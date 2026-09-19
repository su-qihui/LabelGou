using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LabelGou.App.Services.Agent;
using LabelGou.App.Services.Recognition;
using LabelGou.Core.Agent;
using LabelGou.Core.Recognition;

namespace LabelGou.App.Services;

/// <summary>
/// <see cref="AiChatPanel"/> 的外部 Agent 那半边（第 86 棒）。
/// <para>为什么拆成第二个文件而不是往那份 1651 行的文件里继续堆：这一整块（探测、起进程、回落、署名）
/// 与界面机械没有一件共享，混在一起只会让那份已经Acknowledged臃肿的文件更读不动。</para>
/// <para>本棒的口径是<strong>只换读表那一步</strong>：聊天、"出一版排版"、第二步排版仍走原来的 HTTP 通道，
/// 一个字不变。外部 agent 不在场（没开、没装、没答）时也走原路——它是增强，不是前提。</para>
/// </summary>
public sealed partial class AiChatPanel
{
    private IAgentRuntime _external = new NullAgentRuntime();
    private readonly StringBuilder _externalThinking = new();

    /// <summary>
    /// 面板一上屏就自己在后台探一次（第 86 棒）。
    /// <para>为什么挂在面板而不是 <c>MainWindow.WireAi</c>：那条线是共用文件，本棒另一位同事正在同一份上改
    /// ① 步预览列的事——我把启动点收在自己这半个类里，两只手不用碰同一处。
    /// 浮动窗那份面板也走同一条（它是同一个 UserControl 类），不用另接。</para>
    /// </summary>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        AgentServeHost.Attach(this);   // 谁是当前这份工具面，登记在这（进程里可能有第二份面板，见 AgentServeHost）
        Loaded += OnLoadedOnce;
        Unloaded += (_, _) => AgentServeHost.Detach(this);
        _ = AdoptExternalAgentAsync();
    }

    private void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        // 浮动窗那份面板也会走这一条：后上屏的算"当前这份"，前面那份退场时自己交还。
        AgentServeHost.Attach(this);
    }

    private async Task AdoptExternalAgentAsync()
    {
        try
        {
            var (settings, readFailure) = AgentSettings.Load();
            if (readFailure is not null) Services.AppLog.Warning(readFailure);
            Services.AppLog.Info(await AdoptExternalRuntimeAsync(settings));
            if (settings.ServeExternalClients && !AgentServeHost.IsServing)
            {
                var line = AgentServeHost.Start(settings);
                Append(line);
                Services.AppLog.Info(line);
            }
        }
        catch (Exception ex)
        {
            // 探测或开监听本身炸了都不该影响界面：这条路就当没开，原通道照常。
            Services.AppLog.Error($"外部 Agent 这条路没起来，AI 模式走原通道：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 外部客户端连进来时看见的那几颗工具。<strong>只在这一处</strong>决定写权限给不给，
    /// 且给出去的还是面板既有那两条口（<c>ApplyProposal</c> 落快照、<c>GetLayoutContext</c> 读当前表）。
    /// </summary>
    internal LabelGouToolCatalog BuildToolCatalog(AgentSettings settings)
    {
        Func<string, (bool Ok, string Message)>? apply = null;
        if (settings.AllowWriteToolsForExternalClient)
            apply = Land;
        return new LabelGouToolCatalog(() => SnapshotNow, Check, apply, settings.AllowWriteToolsForExternalClient);
    }

    /// <summary>工具调用要回到 UI 线程执行：VM 与集合都是线程亲和的（§五-91）。</summary>
    internal T RunOnUi<T>(Func<T> read) => Dispatcher.CheckAccess() ? read() : Dispatcher.Invoke(read);

    /// <summary>外部递来的提案要落地：先按<strong>读表那一段</strong>解析（rows 与纸规照丢），再走唯一那条落地口。</summary>
    private (bool Ok, string Message) Land(string json)
    {
        var proposal = ParseFrom(json);
        if (proposal is null) return (false, "现在落不了：他还没走到 ① 导入数据，或者这份 JSON 读不成提案。");
        return ApplyProposal?.Invoke(proposal) ?? (false, "这台没接落地那一条口。");
    }

    private LabelGouToolCatalog.Snapshot? SnapshotNow
    {
        get
        {
            var ctx = GetLayoutContext?.Invoke();
            if (ctx is null) return null;
            var now = GetChangeContext?.Invoke();
            return new LabelGouToolCatalog.Snapshot(
                ctx.Portrait ?? "（没拿到整张表画像）",
                ctx.RawRowCount,
                ctx.CurrentHeaderRow,
                now?.TemplateName,
                now?.SheetSpecName);
        }
    }

    /// <summary>
    /// 递进来判一份提案：走的是<strong>既有那台解析器</strong>（读表阶段照丢 rows 与纸规）。
    /// 这一颗一个字都不落——落地只有 <c>apply_proposal</c> 那一条口，而且它走的还是 <c>ApplyProposal</c>。
    /// </summary>
    private LabelGouToolCatalog.CheckOutcome Check(string json)
    {
        var proposal = ParseFrom(json);
        if (proposal is null) return new(false, "现在问不了：他还没走到 ① 导入数据。");
        return new(true, $"读了：字段绑定 {proposal.Mappings.Count} 条，要老板拍板 {proposal.Questions.Count} 条。"
                         + (proposal.Notes.Count > 0 ? "软件自己记下的：" + string.Join("；", proposal.Notes) : ""));
    }

    private AiSheetProposal? ParseFrom(string json)
    {
        var ctx = GetLayoutContext?.Invoke();
        if (ctx is null) return null;
        return AiSheetProposal.Parse(json, ctx.Columns, ctx.RawRowCount, AiProposalStage.Read,
            ctx.SheetSpecNames, ctx.CellFormats);
    }

    /// <summary>上一次是不是退回过原通道，以及为什么（那句必须上屏，别让它悄悄发生）。</summary>
    private string? _externalNote;

    /// <summary>
    /// 开没开、装没装，探一次缓存到底。返回人话，给调用方写日志与状态。
    /// <para><strong>必须在 UI 线程上点它</strong>（<c>MainWindow.WireAi</c> 那一条）：探测本身丢后台线程跑，
    /// 而 <c>await</c> 之后会回到调用方的同步上下文，所以末尾那句 <see cref="RefreshChannel"/> 仍然踩在 UI 线程上。
    /// 反过来若从 <c>Task.Run</c> 里调它，续跑就没有 UI 上下文，刷通道会变成跨线程碰控件（§五-91 那一族）。</para>
    /// </summary>
    public async Task<string> AdoptExternalRuntimeAsync(AgentSettings? settings = null)
    {
        _external = await Task.Run(() => AgentRuntimeProbe.DetectAsync(settings ?? AgentSettings.Load().Settings));
        ProposalTransport = _external.IsAvailable ? AskExternalAsync : null;
        _externalNote = null;
        RefreshChannel();
        return _external.IsAvailable
            ? $"外部 Agent 已接上：{_external.ChannelLabel}"
            : $"外部 Agent 没接上：{_external.UnavailableReason ?? "未知原因"}";
    }

    /// <summary>
    /// 读表那一枪交给外部 agent 答。
    /// <para><strong>只在这一层回落</strong>，而且只回落"传输级"的失败（没装、起不来、没留答案文件、超时、沙箱审不过）。
    /// 解析级失败——它答了但答得不合结构——<strong>绝不回落</strong>：那是要看清"它到底读成什么样"的时刻，
    /// 偷偷再问一次原通道只会把两家的答案搅在一起，老板永远不知道屏幕上这份是谁答的（§五-123 同族）。</para>
    /// </summary>
    private async Task<ChatOutcome> AskExternalAsync(
        List<AiChatTurn> payload,
        List<(string Base64, string MimeType)>? images,
        CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var settings = AgentSettings.Load().Settings;
        var request = new AgentRunRequest(
            string.Join("\n\n", payload.Select(t => t.Text)),
            AgentReadProposalSchema.Json(),
            images?.Select(i => (i.Base64, i.MimeType)).ToList() ?? new List<(string, string)>(),
            settings.EffectiveRunTimeoutSeconds);

        // Progress<T> 在构造它的线程（UI）上回调，所以这段与 HTTP 那一路共用同一条"攒 150ms 再刷"的纪律。
        var progress = new Progress<string>(piece =>
        {
            _externalThinking.Append(piece);
            AppendThinking(piece);
        });

        var result = await _external.RunAsync(request, progress, token);
        var elapsed = DateTime.UtcNow - started;

        if (result.Ok)
        {
            _externalNote = null;
            return new ChatOutcome { Text = result.Answer, Reasoning = _externalThinking.ToString(), Elapsed = elapsed };
        }
        if (result.Cancelled)
            return new ChatOutcome { Error = result.Error, Elapsed = elapsed };

        // 到这里是"这条路上没拿到答案"——退回原通道，并把退的原因写在通道行上（他看得见，不是悄悄换脑）。
        _externalNote = $"已退回原通道：{result.Error}";
        Services.AppLog.Warning($"外部 Agent 没答上（{_external.ChannelLabel}）：{result.Error}");
        Append($"外部 Agent 这次没成（{result.Error}），我按原来的通道又问了一遍。");
        return await SendStreamingAsync(payload, images, token);
    }

    /// <summary>
    /// 只在外部那一路给画像套围栏：表是工厂发来的不可信输入，而这条路对面是个<strong>能执行命令</strong>的程序。
    /// 原 HTTP 那一路<strong>逐字不变</strong>——那是已经验收过的行为，不该被本棒顺手动（改默认值＝改行为，§五-116）。
    /// </summary>
    private string FenceForTransport(string portrait)
        => ProposalTransport is null ? portrait : AgentPromptFence.Create(portrait).Render();

    /// <summary>通道那一行的外部部分：接上了署名是谁，退回过写为什么退。</summary>
    private string ExternalChannelSuffix => ProposalTransport is null
        ? string.Empty
        : "　" + _external.ChannelLabel + (_externalNote is null ? string.Empty : "（" + _externalNote + "）");
}
