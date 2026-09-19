namespace LabelGou.Core.Agent;

/// <summary>
/// 一次「让外部 agent 答一份读表提案」的输入。
/// </summary>
/// <param name="Prompt">已经套好围栏的完整提示词（提示词本体仍由 <c>AiSheetProposalPrompt.BuildRead</c> 造，这里不另起一份）。</param>
/// <param name="SchemaJson">要它按哪份 JSON Schema 交卷（<see cref="AgentReadProposalSchema"/>）。</param>
/// <param name="Images">表里的贴图，<strong>还是字节而不是文件路径</strong>：落盘是 runtime 的活，
/// 因为只有它知道本次那间临时目录在哪。让调用方递路径等于开一条"读任意文件"的通道，
/// <see cref="AgentSandboxPolicy"/> 会当场拦下——拦在发出去之前，不是拦在事后。</param>
/// <param name="TimeoutSeconds">上限秒数（0 = 不设限，口径见 <see cref="TimeoutPolicy"/>）。</param>
public sealed record AgentRunRequest(
    string Prompt,
    string SchemaJson,
    IReadOnlyList<(string Base64, string MimeType)> Images,
    int TimeoutSeconds = 0);

/// <summary>一次跑的结果。</summary>
/// <param name="Ok">真拿到答卷才算成。<strong>退出码 0 不算</strong>——判据是产物文件在不在、里面有没有东西（实测：失败那次根本没写 <c>-o</c> 文件）。</param>
/// <param name="Answer">它交的正文（通常就是那份 JSON），交给既有解析器判。</param>
/// <param name="Error">失败时给人看的那一句，别写成"出错了"。</param>
/// <param name="Cancelled">是用户点停止停的（这种不该回落，他是不想等了）。</param>
public sealed record AgentRunResult(
    bool Ok,
    string? Answer,
    string? Error = null,
    bool Cancelled = false)
{
    public static AgentRunResult Failed(string error) => new(false, null, error);
    public static AgentRunResult Canceled() => new(false, null, "已停止，提案没回来，表与纸规都没动。", Cancelled: true);
}

/// <summary>
/// 外部 agent runtime 的抽象。
/// <para>为什么先立这个口子再写实现：今天只有 Codex CLI 可脚本化（实测：WorkBuddy 是桌面 App 没有 CLI，
/// Qoder 只有 IDE 本体），但把"问谁"与"怎么问"分开之后，换 runtime 只是换一个实现，
/// 而调用方、三道闸、界面那一路一个字不用动。反过来如果界面里直接 <c>Process.Start("codex")</c>，
/// 下一家就得再改一遍界面。</para>
/// <para>实现方在 App 层（要碰进程），接口与数据形状在 Core，所以这套东西照样能单测。</para>
/// </summary>
public interface IAgentRuntime
{
    /// <summary>上屏署名用（例："外部 Agent（codex-cli 0.144.4）"）。</summary>
    string ChannelLabel { get; }

    /// <summary>这台机器上有没有它。<strong>不许在 UI 线程上做探测</strong>——探测是有界的后台活。</summary>
    bool IsAvailable { get; }

    /// <summary>没有它的时候说人话（例："这台机器上没有 codex 命令，或它没装成"）。</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// 跑一次。<paramref name="onEvent"/> 收到的是能上屏的过程话（它在想、它写出几条判断、已等多少秒）。
    /// <paramref name="token"/> 取消时必须真的把子进程杀掉，不能只是"我们不等了"。
    /// </summary>
    Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IProgress<string>? onEvent,
        CancellationToken token);
}
