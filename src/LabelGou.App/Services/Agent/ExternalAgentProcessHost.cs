using System.Diagnostics;
using System.IO;
using System.Text;
using LabelGou.Core.Agent;

namespace LabelGou.App.Services.Agent;

/// <summary>一次子进程的结果。<see cref="Ok"/> 的判据是"有产物"，不是"退出码 0"（实测：失败那次 <c>-o</c> 文件根本没写）。</summary>
internal sealed record HostRunResult(
    bool Started,
    int ExitCode,
    string Answer,
    string StdErr,
    IReadOnlyList<string> SandboxViolations,
    bool TimedOut,
    bool Cancelled,
    TimeSpan Elapsed)
{
    public bool Ok => Started && SandboxViolations.Count == 0 && !TimedOut && !Cancelled
                      && Answer.Length > 0;
}

/// <summary>
/// 全项目<strong>唯一</strong>跑子进程的地方（第 86 棒）。
/// <para>为什么要收在一处：这条路上每一件容易出事的事都跟进程有关——命令行长度上限、<c>.cmd</c> 不能直接 CreateProcess、
/// stdin 不关它就卡在"读 stdin"（实测卡在 <c>Reading additional input from stdin...</c> 上白等 87 秒）、
/// 以及<strong>取消时必须真把整棵进程树杀掉</strong>（只"我们不等了"会留一个还在跑还能写文件的 agent 在后台）。</para>
/// <para>参数怎么拼、结果怎么解读都不在这层（那是 <see cref="CodexCliRuntime"/> 的事）；这层只负责
/// "把它发出去、把它关掉、把字节拿回来"，外加<strong>发之前先让 <see cref="AgentSandboxPolicy"/> 审一遍真参数</strong>。</para>
/// </summary>
internal static class ExternalAgentProcessHost
{
    /// <summary>stdout 收多少封顶：它开始自说自话时不该把我们内存吃掉。</summary>
    const int MaxEventChars = 8_000_000;

    const int MaxStdErrChars = 200_000;

    /// <summary>提示词走 stdin 时的落点：<c>codex exec -</c> 里那个 <c>-</c>。</summary>
    public const string PromptFromStdIn = "-";

    /// <summary>
    /// 本次那间临时目录。故意不放 <c>Path.GetTempPath()</c> 根上：那等于把整台机器的暂存区交给它。
    /// 名字带 pid 与一个自增序号，两轮并发也撞不上。
    /// </summary>
    public static string NewWorkDir(string kind)
    {
        var seq = Interlocked.Increment(ref _seq);
        var dir = Path.Combine(Path.GetTempPath(), "LabelGou", $"agent-{kind}-{Environment.ProcessId}-{seq}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int _seq;

    public static void TryDelete(string workDir)
    {
        try
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
        catch (Exception)
        {
            // 删不掉只是留一间空壳在 %TEMP%，不值得为它打断出纸这条路。日志里留一句就够。
        }
    }

    /// <summary>
    /// 跑一次。<paramref name="prompt"/> 从 stdin 递进去并<strong>立刻关掉</strong>——Windows 命令行约 32K 字符封顶，
    /// 而一份整表画像能到十万字，走参数会在 CreateProcess 那一步直接失败。
    /// </summary>
    public static async Task<HostRunResult> RunAsync(
        string program,
        IReadOnlyList<string> argv,
        string workDir,
        string prompt,
        string answerPath,
        IReadOnlyList<string> forbiddenRoots,
        Action<string>? onEventLine,
        int timeoutSeconds,
        CancellationToken userToken)
    {
        var started = DateTime.UtcNow;

        var resolved = ResolveCommand(program);
        if (resolved is null)
            return new HostRunResult(false, -1, string.Empty, $"这台机器上找不到 {program}", Array.Empty<string>(), false, false,
                DateTime.UtcNow - started);

        using var timeout = new CancellationTokenSource();
        var ourTimeout = timeoutSeconds > 0;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, timeout.Token);

        var full = new List<string>(resolved.Value.PrefixArgs.Count + argv.Count);
        full.AddRange(resolved.Value.PrefixArgs);
        full.AddRange(argv);

        var psi = new ProcessStartInfo
        {
            FileName = resolved.Value.FileName,
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in full) psi.ArgumentList.Add(arg);
        // 密钥不该跟着一条"只读问答"出去：这里先真剥掉，下面那一行审的就是剥完剩下的那份。
        psi.Environment.Remove("LABELGOU_LLM_KEY");

        // 先发出去的东西必须自己先审：喂给裁判的是真 argv 与真要带进子进程的环境，不是"我以为我拼的那份"。
        var violations = AgentSandboxPolicy.Validate(argv, workDir, forbiddenRoots, psi.Environment.Keys.ToList());
        if (violations.Count > 0)
            return new HostRunResult(false, -1, string.Empty, string.Empty, violations, false, false, DateTime.UtcNow - started);

        Process? process = null;
        var events = new StringBuilder();
        var stdErr = new StringBuilder();
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("没能起这个进程");
            using (linked.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch { /* 已经没了 */ } }))
            {
                if (ourTimeout) timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                var readOut = PumpAsync(process.StandardOutput, events, onEventLine, MaxEventChars);
                var readErr = PumpAsync(process.StandardError, stdErr, null, MaxStdErrChars);

                await process.StandardInput.WriteAsync(prompt);
                process.StandardInput.Close();   // 不关：它会一直等stdin，我们一直等它（实测 87 秒那次就是这形状）

                await process.WaitForExitAsync();
                await Task.WhenAll(readOut, readErr);
            }
        }
        catch (Exception ex)
        {
            return new HostRunResult(false, -1, string.Empty, $"{ex.GetType().Name}：{ex.Message}",
                Array.Empty<string>(), false, false, DateTime.UtcNow - started);
        }
        finally
        {
            TryKill(process);
            process?.Dispose();
            linked.Dispose();
        }

        var cancelled = userToken.IsCancellationRequested;
        var timedOut = !cancelled && ourTimeout && timeout.IsCancellationRequested;
        var answer = ReadAnswer(answerPath);
        return new HostRunResult(true, SafeExitCode(process), answer, stdErr.ToString(), Array.Empty<string>(),
            timedOut, cancelled, DateTime.UtcNow - started);
    }

    /// <summary>探测版本用的小命令：<c>codex --version</c>。不建工作目录、不进沙箱审判（它不含 exec，也就碰不到文件与命令）。</summary>
    public static async Task<(bool Found, string Text)> RunQuietAsync(string program, IReadOnlyList<string> argv, int timeoutMs)
    {
        var resolved = ResolveCommand(program);
        if (resolved is null) return (false, string.Empty);

        var psi = new ProcessStartInfo
        {
            FileName = resolved.Value.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in resolved.Value.PrefixArgs.Concat(argv)) psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return (false, string.Empty);
            using var timeout = new CancellationTokenSource(timeoutMs);
            var read = process.StandardOutput.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return (true, string.Empty);   // 找到了但答不上来：按"没探到"处理，别让它挂着界面
            }
            return (true, (await read).Trim());
        }
        catch (Exception)
        {
            return (false, string.Empty);
        }
    }

    public static bool LooksInstalled(string program) => ResolveCommand(program) is not null;

    /// <summary>
    /// 把命令名解析成一个 <c>CreateProcess</c> 真能起的东西。
    /// <para>实测形状：npm 在 <c>%APPDATA%\npm</c> 放的是 <c>codex.cmd</c>（外加一个给 bash 用的无扩展名 shim），
    /// 而 <c>CreateProcess</c> 不跑批处理——直接把它当 exe 名递过去只会得到"找不到关联程序"。
    /// 所以 <c>.exe</c> 直接用，<c>.cmd/.bat</c> 经 <c>cmd.exe /d /c</c> 包一层（杀掉时要靠
    /// <c>Kill(entireProcessTree)</c> 把孙进程一起收，本文件已经这么做了）。</para>
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> PrefixArgs)? ResolveCommand(string program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        if (Path.IsPathRooted(program))
        {
            if (!File.Exists(program)) return null;
            // 写全路径也可能指到一个批处理：那一样不能当 exe 递给 CreateProcess（下面那条规矩同款）。
            return NeedsCmdWrapper(program)
                ? ("cmd.exe", new[] { "/d", "/s", "/c", "\"" + program + "\"" })
                : (program, Array.Empty<string>());
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = Path.Combine(dir.Trim('"'), program + ".exe");
            if (File.Exists(exe)) return (exe, Array.Empty<string>());

            var batch = Path.Combine(dir.Trim('"'), program + ".cmd");
            if (File.Exists(batch))
                return ("cmd.exe", new[] { "/d", "/s", "/c", "\"" + batch + "\"" });

            batch = Path.Combine(dir.Trim('"'), program + ".bat");
            if (File.Exists(batch))
                return ("cmd.exe", new[] { "/d", "/s", "/c", "\"" + batch + "\"" });
        }

        // 带扩展名的写法（有人把 command 填成 "codex.cmd"）：仍然要包一层 cmd，别当 exe 递出去。
        if (NeedsCmdWrapper(program))
            return ("cmd.exe", new[] { "/d", "/s", "/c", "\"" + program + "\"" });

        return null;
    }

    private static bool NeedsCmdWrapper(string program)
        => program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
           || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static string ReadAnswer(string answerPath)
    {
        try
        {
            return File.Exists(answerPath) ? File.ReadAllText(answerPath) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, Action<string>? onLine, int cap)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (sink.Length < cap) sink.Append(line).Append('\n');
            if (line.Length == 0) continue;
            try
            {
                onLine?.Invoke(line);
            }
            catch (Exception)
            {
                // 上屏那一段炸了不该让一次读表失败：它还有产物文件这条判据在。
            }
        }
    }

    private static int SafeExitCode(Process? process)
    {
        try
        {
            return process?.HasExited == true ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void TryKill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 它可能正好自己退了——不是错。
        }
    }
}
