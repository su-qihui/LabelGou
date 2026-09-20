namespace LabelGou.Core.Agent;

/// <summary>
/// 这次调用来自哪种宿主。它决定工具面上看得见哪几颗——<strong>不是权限装饰，是可达性</strong>：
/// 判据与实现都在这一处，界面与传输层换法都不许另开一份。
/// </summary>
public enum AgentHostKind
{
    /// <summary>界面进程里跑（LabelGou 自己当宿主，或外部 agent 连着开着的窗口）。</summary>
    WithUi,

    /// <summary>没有界面的那一路（头less 起一个服务给外部 agent 连）。
    /// <para>这一档<strong>天生没有</strong> <c>MainWindow</c> 与真 <c>ConfirmGate</c>，而那边的出纸闸是
    /// <c>ConfirmGate?.Invoke() ?? true</c>——宿主不在就等于闸门自己消失。所以这一档不许看见任何会出纸的东西。</para></summary>
    Headless,
}

/// <summary>一颗工具对外部 agent 的信任档。</summary>
public enum AgentToolTrust
{
    /// <summary>只读：不改数据、不出纸，两种宿主都给。</summary>
    ReadOnly,

    /// <summary>要活界面才给：它写的是<strong>当前这份会话状态</strong>，头less 那一路没有这个东西可写。</summary>
    NeedsUiHost,

    /// <summary>永不开成工具：打印、导出、存模板、驱动设置。
    /// <para>唛头打错货要赔钱，这三样必须留在人点的那一下，而不是让任何会说话的东西能点名。</para></summary>
    NeverExposed,
}

/// <summary>
/// 一颗工具的声明。
/// </summary>
/// <param name="Name">MCP 上的工具名，全库唯一。</param>
/// <param name="Description">给模型看的说明——写清楚"什么时候该调它"，光写"读表"它就不会用。</param>
/// <param name="InputSchemaJson">入参的 JSON Schema 文本（注册时验它是合法 JSON 对象，别等客户端报错才发现）。</param>
/// <param name="Trust">信任档，决定它对哪种宿主可见。</param>
public sealed record AgentToolSpec(
    string Name,
    string Description,
    string InputSchemaJson,
    AgentToolTrust Trust);
