using System.IO;
using LabelGou.App.Services.Recognition;
using LabelGou.Core.Agent;

namespace LabelGou.App.Services.Agent;

/// <summary>
/// 拿外部 CLI 当脑子的那条路（今天只有 Codex CLI 可脚本化：实测 WorkBuddy 是桌面 App 没有 CLI，Qoder 只有 IDE 本体）。
/// <para>它只负责<strong>把这次的形状拼对</strong>：参数怎么给、schema 落到哪、答案从哪个文件读、
/// 事件流摊成人话。进程本身怎么起怎么杀归 <see cref="ExternalAgentProcessHost"/>，
/// 沙箱合不合规归 <see cref="AgentSandboxPolicy"/>——三份事各一处，改哪份都只在那份里改。</para>
/// <para>换 runtime 时该动的只有这个类（argv 模板与事件名），界面、闸门、解析都不该跟着动。</para>
/// </summary>
internal sealed class CodexCliRuntime : IAgentRuntime
{
    private readonly AgentSettings _settings;
    private readonly string? _version;

    public CodexCliRuntime(AgentSettings settings, string? version = null)
    {
        _settings = settings;
        _version = string.IsNullOrWhiteSpace(version) ? null : version;
    }

    public string ChannelLabel => _version is null
        ? $"外部 Agent（{_settings.Command}）"
        : $"外部 Agent（{_settings.Command} {_version}）";

    public bool IsAvailable => ExternalAgentProcessHost.LooksInstalled(_settings.Command);

    public string? UnavailableReason => IsAvailable
        ? null
        : $"这台机器上找不到 {_settings.Command} 命令（它只算增强，这条路不走，AI 模式照原来的通道问）";

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IProgress<string>? onEvent,
        CancellationToken token)
    {
        if (!IsAvailable) return AgentRunResult.Failed(UnavailableReason ?? "没有可用的外部 Agent");

        var workDir = ExternalAgentProcessHost.NewWorkDir("read");
        try
        {
            var schemaPath = Path.Combine(workDir, "schema.json");
            var answerPath = Path.Combine(workDir, "answer.json");
            await File.WriteAllTextAsync(schemaPath, request.SchemaJson);
            var imagePaths = await StageImagesAsync(workDir, request.Images);

            var argv = BuildArgv(workDir, schemaPath, answerPath, imagePaths);
            var result = await ExternalAgentProcessHost.RunAsync(
                _settings.Command, argv, workDir, request.Prompt, answerPath,
                ForbiddenRoots(), onLine => Report(onEvent, onLine),
                request.TimeoutSeconds, token);

            if (result.SandboxViolations.Count > 0)
                return AgentRunResult.Failed("参数不合沙箱规矩，没发出去：" + string.Join("；", result.SandboxViolations));
            if (result.Cancelled) return AgentRunResult.Canceled();
            if (result.TimedOut)
                return AgentRunResult.Failed($"外部 Agent 到点没答（{TimeoutPolicy.Describe(request.TimeoutSeconds)}）");

            var answer = Truncate(result.Answer, _settings.MaxAnswerChars);
            if (answer.Length == 0)
                return AgentRunResult.Failed(string.IsNullOrWhiteSpace(result.StdErr)
                    ? "外部 Agent 没留下答案文件（它自己没跑成，或者模型没接这一枪）"
                    : "外部 Agent 没跑成：" + FirstLine(result.StdErr));

            return new AgentRunResult(true, answer);
        }
        finally
        {
            ExternalAgentProcessHost.TryDelete(workDir);
        }
    }

    /// <summary>
    /// 把贴图落到<strong>本轮那间临时目录</strong>里。
    /// <para>为什么要落在这层：沙箱裁判有一条"递给它的图片必须在临时目录内"，而临时目录是本轮才存在的——
    /// 调用方递路径过来要么早就在别处（判据立刻红），要么得由调用方自己猜目录在哪。字节交给我们、名字我们定，
    /// 扩展名按 <see cref="ImageForModel.ExtensionFor"/> 一处算。</para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> StageImagesAsync(
        string workDir, IReadOnlyList<(string Base64, string MimeType)> images)
    {
        var paths = new List<string>(images.Count);
        for (var i = 0; i < images.Count; i++)
        {
            var (base64, mime) = images[i];
            if (string.IsNullOrWhiteSpace(base64)) continue;
            var path = Path.Combine(workDir, $"sheet-{i + 1}{ImageForModel.ExtensionFor(mime)}");
            try
            {
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(base64));
                paths.Add(path);
            }
            catch (Exception)
            {
                // 一张图落不下去不该挡整次读表：它少一张参照，界面那一句"没带图"会如实说。
            }
        }
        return paths;
    }

    /// <summary>
    /// 这次的参数。<strong>每一颗都要经裁判</strong>：只读沙箱、只活本次的会话、不进 git 检查、
    /// 工作目录锁在本轮那间临时目录、必须有 schema 与产物文件两条。提示词是最后一个 <c>-</c>——它走 stdin，
    /// 因为 Windows 命令行约 32K 字符封顶，而一份整表画像能到十万字。
    /// </summary>
    internal static IReadOnlyList<string> BuildArgv(
        string workDir, string schemaPath, string answerPath, IReadOnlyList<string> imageFiles)
    {
        var argv = new List<string>
        {
            "exec", "--json",
            "-s", AgentSandboxPolicy.ReadOnlySandbox,
            "--ephemeral", "--skip-git-repo-check",
            "-C", workDir,
            "--output-schema", schemaPath,
            "-o", answerPath,
        };
        if (imageFiles.Count > 0)
        {
            argv.Add("-i");
            argv.AddRange(imageFiles);
        }
        argv.Add(ExternalAgentProcessHost.PromptFromStdIn);
        return argv;
    }

    /// <summary>禁区：源码仓（那台机器上它能改代码）、程序自己那间、以及用户数据根（模板库与密钥都在里面）。</summary>
    internal static IReadOnlyList<string> ForbiddenRoots() => new[]
    {
        FindRepoRoot(),
        AppContext.BaseDirectory,
        Core.UserPaths.Root,
    };

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LabelGou.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Path.DirectorySeparatorChar.ToString();
    }

    /// <summary>事件流 → 界面上那几行过程话（认不出的安静跳过，见 <see cref="AgentEvent"/>）。</summary>
    private static void Report(IProgress<string>? onEvent, string line)
    {
        if (onEvent is null) return;
        var @event = AgentEvent.Parse(line);
        if (!@event.HasVisibleText) return;
        onEvent.Report(@event.Kind switch
        {
            AgentEventKind.Reasoning => @event.Text,
            AgentEventKind.Notice => "它说：" + @event.Text,
            _ => @event.Text,
        });
    }

    private static string Truncate(string text, int max)
        => max > 0 && text.Length > max ? text[..max] : text;

    private static string FirstLine(string text)
    {
        var trimmed = text.Trim();
        var cut = trimmed.IndexOf('\n');
        return cut < 0 ? trimmed : trimmed[..cut];
    }
}

/// <summary>
/// 没开外部 Agent、或这台机器没装时<strong>常驻</strong>的那一个。
/// <para>为什么要它而不是到处 <c>if (runtime != null)</c>：调用方拿到的永远是一个能问的东西，
/// "问不了"这件事由它回一句人话——到处判空迟早漏掉一处，漏掉的那处就是空指针或者静默不改（§五-123 那一族）。</para>
/// </summary>
internal sealed class NullAgentRuntime : IAgentRuntime
{
    public NullAgentRuntime(string? reason = null) => UnavailableReason = reason;

    public string ChannelLabel => "原通道（HTTP）";

    public bool IsAvailable => false;

    public string? UnavailableReason { get; }

    public Task<AgentRunResult> RunAsync(AgentRunRequest request, IProgress<string>? onEvent, CancellationToken token)
        => Task.FromResult(AgentRunResult.Failed(UnavailableReason ?? "这台机器没有可用的外部 Agent"));
}

/// <summary>
/// 探测"这台机器有没有外部 agent 可用"。
/// <para>两条硬规矩：<strong>绝不在 UI 线程上等它</strong>（<c>--version</c> 走的是进程，慢起来没准），
/// 以及探一次缓存到底——每次点"读这张表"都重探一遍，只会让老板多等一段他看不懂的空白。</para>
/// </summary>
internal static class AgentRuntimeProbe
{
    /// <summary>后台探一次并造出该用的 runtime。开没开、有没有装，都在这里分岔，调用方只看结果。</summary>
    public static async Task<IAgentRuntime> DetectAsync(AgentSettings settings)
    {
        if (!settings.UseExternalAgent)
            return new NullAgentRuntime("外部 Agent 没在设置里打开（AI 模式照原来的通道问）");
        if (!ExternalAgentProcessHost.LooksInstalled(settings.Command))
            return new NullAgentRuntime($"这台机器上找不到 {settings.Command} 命令");

        var (found, text) = await ExternalAgentProcessHost.RunQuietAsync(
            settings.Command, new[] { "--version" }, settings.ProbeTimeoutMs);
        if (!found || text.Length == 0)
            return new NullAgentRuntime($"{settings.Command} 探不到版本，这一阵不走外部 Agent");

        return new CodexCliRuntime(settings, FirstWord(text));
    }

    /// <summary><c>codex-cli 0.144.4</c> 这类输出里最后那颗是版本号；认不出来就整串塞进署名，别瞎猜。</summary>
    internal static string FirstWord(string versionOutput)
    {
        var parts = versionOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? versionOutput : parts[^1];
    }
}
