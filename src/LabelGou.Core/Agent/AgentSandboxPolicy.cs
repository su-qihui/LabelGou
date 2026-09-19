namespace LabelGou.Core.Agent;

/// <summary>
/// 外部 agent 那条路的<strong>沙箱裁判</strong>：把"我以为我传了只读"变成"验过真传了只读"。
/// <para>为什么要有这么一个类：外部 runtime 是个能执行命令的程序，一旦我们忘了带 <c>-s read-only</c>、
/// 或者工作目录指到了源码仓，它就有可能改我们的代码、或顺着 <c>--add-dir</c> 拿到全盘写权限。
/// 这类事编译与既有单测都照不出来（§五-151 那一族：机器测不到的那一层）。</para>
/// <para>所以规则是：<c>AgentSandboxPolicy</c> 是唯一裁判，宿主必须把<strong>真正要发出去的那份 argv
/// 与真正建出来的工作目录</strong>交给它，violations 非空就不发。裁判不做 IO、不抛异常，因此能在 Core 逐条钉死。</para>
/// </summary>
public static class AgentSandboxPolicy
{
    /// <summary>只给读这一档。其余档位（能写工作区、能全盘）一律不许出现。</summary>
    public const string ReadOnlySandbox = "read-only";

    /// <summary>少了任何一样都不发：没有 <c>--output-schema</c> 就是没有结构约束，没有 <c>-o</c> 就没有"它真答了"的产物判据。</summary>
    public static IReadOnlyList<string> RequiredTokens { get; } =
    [
        "exec", "--json", "--ephemeral", "--skip-git-repo-check", "--output-schema", "-o", "-C",
    ];

    /// <summary>出现即拒的开关。凡是 <c>--dangerously</c> 开头的（包括以后新加的）也一并拒，见 <see cref="Validate"/>。</summary>
    public static IReadOnlyList<string> ForbiddenTokens { get; } =
    [
        "--dangerously-bypass-approvals-and-sandbox",
        "--dangerously-bypass-hook-trust",
        "--add-dir",
    ];

    /// <summary>不许带进子进程环境的变量名（密钥不该跟着一条"只读问答"出去）。</summary>
    public static IReadOnlyList<string> ForbiddenEnvironmentVariables { get; } =
    [
        "LABELGOU_LLM_KEY",
    ];

    /// <summary>
    /// 审一遍真发出去的东西，返回人话违规清单（空 = 可以发）。
    /// </summary>
    /// <param name="argv">子进程的完整参数（含 <c>exec</c> 那颗）。</param>
    /// <param name="workDir">真建出来的工作目录。</param>
    /// <param name="forbiddenRoots">绝不能当工作目录的根：源码仓、程序自身目录、用户数据根。</param>
    /// <param name="environmentVariableNames">准备带进子进程的环境变量名（可空 = 不审这一项）。</param>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<string> argv,
        string? workDir,
        IReadOnlyList<string>? forbiddenRoots = null,
        IReadOnlyList<string>? environmentVariableNames = null)
    {
        var violations = new List<string>();
        if (argv is null || argv.Count == 0)
        {
            violations.Add("一条参数都没有，不知道要让它干什么");
            return violations;
        }

        foreach (var token in RequiredTokens)
            if (!argv.Contains(token, StringComparer.Ordinal))
                violations.Add($"少了 {token}");

        foreach (var token in ForbiddenTokens)
            if (argv.Contains(token, StringComparer.Ordinal))
                violations.Add($"出现了禁止的开关 {token}");

        // --dangerously 开头的先拦下来，免得以后它新加一颗"更危险"而我们以为还在管着。
        foreach (var token in argv.Where(t => t.StartsWith("--dangerously", StringComparison.Ordinal)))
            if (!ForbiddenTokens.Contains(token, StringComparer.Ordinal))
                violations.Add($"出现了没登记过的危险开关 {token}");

        var sandbox = ValueAfter(argv, "-s", "--sandbox");
        if (sandbox is null) violations.Add("没带 -s read-only，等于没限权限");
        else if (!string.Equals(sandbox, ReadOnlySandbox, StringComparison.OrdinalIgnoreCase))
            violations.Add($"沙箱档位是 {sandbox}，只许 read-only");

        var cd = ValueAfter(argv, "-C", "--cd");
        if (cd is null) violations.Add("没带 -C，它会用调用方的当前目录");
        else if (string.IsNullOrWhiteSpace(workDir) || !Same(cd, workDir))
            violations.Add("-C 指的地方不是本次那间临时目录");

        violations.AddRange(CheckWorkDir(workDir, forbiddenRoots));
        violations.AddRange(CheckImages(argv, workDir));

        if (environmentVariableNames is not null)
            foreach (var name in environmentVariableNames)
                if (ForbiddenEnvironmentVariables.Contains(name, StringComparer.OrdinalIgnoreCase))
                    violations.Add($"环境变量里带着 {name}");

        return violations;
    }

    /// <summary>这间临时目录能不能用：不能是空、不能落在任何一条禁区里（含它自己就是禁区）。</summary>
    private static IEnumerable<string> CheckWorkDir(string? workDir, IReadOnlyList<string>? forbiddenRoots)
    {
        if (string.IsNullOrWhiteSpace(workDir))
        {
            yield return "没有工作目录";
            yield break;
        }
        if (!Path.IsPathRooted(workDir)) yield return "工作目录不是绝对路径";
        if (forbiddenRoots is null) yield break;
        foreach (var root in forbiddenRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (IsUnderOrSame(workDir, root))
                yield return $"工作目录落在禁区里：{root}（外部程序能执行命令，给它写权限等于让它改我们的东西）";
        }
    }

    /// <summary>递出去的图片必须在临时目录里：否则等于给了它一条读任意路径的通道。</summary>
    private static IEnumerable<string> CheckImages(IReadOnlyList<string> argv, string? workDir)
    {
        if (string.IsNullOrWhiteSpace(workDir)) yield break;
        for (var i = 0; i < argv.Count; i++)
        {
            if (!string.Equals(argv[i], "-i", StringComparison.Ordinal)
                && !string.Equals(argv[i], "--image", StringComparison.Ordinal))
                continue;
            // 这颗开关后面可以跟多张图（可变参数），吃到下一个开关为止。
            for (var j = i + 1; j < argv.Count && !argv[j].StartsWith("-", StringComparison.Ordinal); j++)
            {
                if (!IsUnderOrSame(argv[j], workDir))
                    yield return $"递给它的图片不在临时目录里：{argv[j]}";
            }
        }
    }

    /// <summary>取 <c>-s read-only</c> 或 <c>--sandbox=read-only</c> 两种写法里的值。</summary>
    private static string? ValueAfter(IReadOnlyList<string> argv, params string[] flags)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            foreach (var flag in flags)
            {
                if (string.Equals(argv[i], flag, StringComparison.Ordinal) && i + 1 < argv.Count)
                    return argv[i + 1];
                if (argv[i].StartsWith(flag + "=", StringComparison.Ordinal))
                    return argv[i][(flag.Length + 1)..];
            }
        }
        return null;
    }

    private static bool Same(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), PathComparison);

    private static bool IsUnderOrSame(string candidate, string root)
    {
        var child = Normalize(candidate);
        var parent = Normalize(root);
        if (child.Length < parent.Length) return false;
        if (child.Length == parent.Length)
            return string.Equals(child, parent, PathComparison);
        var separator = child[parent.Length];
        return child.StartsWith(parent, PathComparison)
               && (separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            // 路径本身就不合法（含非法字符）：原样比，别把裁判弄炸——违规照样由别的条报出来。
            return path;
        }
    }

    // Windows 路径不分大小写；非 Windows 上 Ordinal 更保守（宁可多报一条违规，也不放过）。
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
