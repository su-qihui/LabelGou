using System.IO;
using LabelGou.Core;
using LabelGou.Core.Agent;

namespace LabelGou.App.Services.Agent;

/// <summary>
/// 外部 Agent 这条路的设置（<c>%APPDATA%\LabelGou\agent.json</c>）。
/// <para>为什么另开一份而不塞进 <c>recognition.json</c>：那份管的是"我们自己去问哪个端点"，
/// 这份管的是"让别人的 agent 来答"——两套通道各有开关与凭据形状，混在一格里迟早出现
/// "改了那格这半边不跳"（§五-122 那一族：同一份数据在界面上出现两次）。</para>
/// <para><c>UserPaths.Root</c> 是唯一出口（§五-95）：<see cref="Core.UserPaths.SetRootForTests"/>
/// 一换就整份跟着进临时目录，单测不碰用户真机。RecognitionSettings 与 SecretStore 那两处自拼
/// %APPDATA% 的旧账记在 §十-A-25，本棒不再制造第三个。</para>
/// </summary>
public sealed class AgentSettings
{
    /// <summary>外部 agent 只算增强：<strong>默认关</strong>。开着探测到了也不自己换脑——那是老板在设置里点的决定。</summary>
    public const bool DefaultUseExternalAgent = false;

    /// <summary>今天唯一可脚本化的 runtime（实测：WorkBuddy 没有 CLI，Qoder 只有 IDE 本体）。名字可换， argv 形状由 runtime 自己造。</summary>
    public const string DefaultCommand = "codex";

    /// <summary>探测的天花板。<c>--version</c> 正常是几十毫秒，1.5 秒还没回就当中途没人理。</summary>
    public const int DefaultProbeTimeoutMs = 1500;

    /// <summary>答案文件读多少封顶（一份读表提案 JSON 也就几 KB，40 万是"它开始胡说"的界）。</summary>
    public const int DefaultMaxAnswerChars = 400_000;

    /// <summary>开不开这条路。关着的时候 AI 模式与今天逐字相同。</summary>
    public bool UseExternalAgent { get; set; } = DefaultUseExternalAgent;

    public string Command { get; set; } = DefaultCommand;

    public int ProbeTimeoutMs { get; set; } = DefaultProbeTimeoutMs;

    /// <summary>0 = 不设限（<see cref="TimeoutPolicy"/>，与第 27 棒那条口径同源）。</summary>
    public int RunTimeoutSeconds { get; set; }

    public int MaxAnswerChars { get; set; } = DefaultMaxAnswerChars;

    /// <summary>
    /// 外部 agent 连着我们的时候，允不允许它下"写"的指令（落地提案）。
    /// <para>关着 = 只读面（报能力、读当前表、递一份提案来要我们判）。这是默认，
    /// 因为"外部会说话的东西能改我们的数据"这件事得老板亲口点头；
    /// 开着也只是让它走 <c>ApplyAiProposal</c> 那唯一一条口——快照照压、可撤照撤，不是绕过闸门。</para>
    /// </summary>
    public bool AllowWriteToolsForExternalClient { get; set; }

    /// <summary>开不开"给外部 agent 连"的那扇本地门。关着就没有监听的 socket。</summary>
    public bool ServeExternalClients { get; set; }

    public int EffectiveRunTimeoutSeconds => TimeoutPolicy.Effective(RunTimeoutSeconds);

    public static string FilePath => Path.Combine(UserPaths.Root, "agent.json");

    /// <summary>读设置。没有这个文件 = 全默认（默认关）；文件坏了也说人话，不静默换默认。</summary>
    public static (AgentSettings Settings, string? ReadFailure) Load() => LoadFrom(FilePath);

    public static (AgentSettings Settings, string? ReadFailure) LoadFrom(string path)
    {
        if (!File.Exists(path)) return (new AgentSettings(), null);
        try
        {
            var json = File.ReadAllText(path);
            var read = System.Text.Json.JsonSerializer.Deserialize<AgentSettings>(json, Json);
            if (read is null) return (new AgentSettings(), "agent.json 读不出内容，这一阵按默认（外部 Agent 关着）走");
            return (read, null);
        }
        catch (Exception ex)
        {
            return (new AgentSettings(), $"agent.json 读不了：{ex.GetType().Name}，这一阵按默认走");
        }
    }

    public void Save() => SaveTo(FilePath);

    public void SaveTo(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // File.WriteAllText 在 .NET 8 默认就是不带 BOM 的 UTF-8（别拿 PowerShell 的 -Encoding UTF8 那套来比，§五 记过）。
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(this, Json));
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
