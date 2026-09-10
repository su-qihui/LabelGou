namespace LabelGou.Core.Recognition;

/// <summary>
/// AI 一次改动落在哪一类上（阶段 29 第 1 棒）。
/// <para>有类别，才谈得上「只落地被 ✅ 的那几条」。用户 2026-09-10 要的正是逐条确认/取消，
/// 而不是整份点一次了事（旧路只有 <c>这些全都要（一次落地）</c> 一颗按钮）。</para>
/// </summary>
public enum AiChangeKind
{
    /// <summary>列名在原表第几行（含「这张表首行不是列名」那一档）。</summary>
    HeaderRow = 0,

    /// <summary>哪几行不当货印（合计行、备注行）。</summary>
    ExcludedRows = 1,

    /// <summary>标签版式与标签尺寸（AI 出的那一版会存成新模板）。</summary>
    Layout = 2,

    /// <summary>用哪一张纸规。</summary>
    SheetSpec = 3,
}

/// <summary>
/// 软件**此刻**的状态快照（阶段 29 第 1 棒）。
/// <para><strong>为什么必须有它</strong>：要画「原值 → 新值」，就得知道"原值是什么"。
/// 第 21~28 棒的 <c>DescribeItems</c> 只会说「它会改成 X」，说不出「从 Y 改成 X」——
/// 人审不动，所以只能整份点头。用户 2026-09-10 要的改动卡，缺的就是这一份。</para>
/// <para>字段全部可空/带默认：拿不到就不许编一个值出来（宁可显示「还没定」）。</para>
/// </summary>
/// <param name="RawRowCount">原表总行数（1 起口径的边界）。</param>
/// <param name="HeaderRow">软件此刻用的列名行（原表行号，1 起）；null = 还没定。</param>
/// <param name="HasHeader">软件此刻这张表有没有列名行；null = 不知道（那就不拿它去比）。</param>
/// <param name="ExcludedRows">软件此刻剔掉不当货印的原表行号（**1 起，给人看的口径**）。</param>
/// <param name="TemplateName">软件此刻选中的模板名（版式那一条的"原值"靠它说清）。</param>
/// <param name="LabelWidthMm">软件此刻标签宽（毫米）。</param>
/// <param name="LabelHeightMm">软件此刻标签高（毫米）。</param>
/// <param name="SheetSpecName">软件此刻用的纸规名；null = 没选中任何一张。</param>
public sealed record AiChangeContext(
    int RawRowCount = 0,
    int? HeaderRow = null,
    bool? HasHeader = null,
    IReadOnlyList<int>? ExcludedRows = null,
    string? TemplateName = null,
    double? LabelWidthMm = null,
    double? LabelHeightMm = null,
    string? SheetSpecName = null)
{
    /// <summary>什么都不知道的那一份（面板没接上上下文时用；显示成「还没定」而不是编一个数）。</summary>
    public static readonly AiChangeContext Empty = new();
}

/// <summary>
/// 一条「改哪里 + 原值 → 新值」的改动（阶段 29 第 1 棒）。
/// <para><strong>这张卡就是阶段 29 那条红线</strong>：AI 的每一次写都要人点 ✅ 才落地。
/// 口径上它是 <see cref="AiSheetQuestion"/> 那条纪律的放大版——从「4 个白名单动作」扩到「参数级改动」，
/// 仍然不许「点了按钮报成功、其实什么都没改」。</para>
/// </summary>
/// <param name="Kind">落在哪一类（决定点 ✅ 之后走哪条落地路，见 <see cref="AiChangeKind"/>）。</param>
/// <param name="Target">改哪里，一句大白话（如「列名在第几行」）——不出现字段英文名。</param>
/// <param name="Before">软件此刻是什么（原值）。</param>
/// <param name="After">它会改成什么（新值）。</param>
public sealed record AiChange(AiChangeKind Kind, string Target, string Before, string After)
{
    /// <summary>
    /// 两边一样就是没变化。**没变化的项不许出卡**——卡上出现"改成一样的东西"等于逼人白审一条
    /// （与 <see cref="AiSheetQuestion"/> 「不点报成功其实没改」是同一条口径）。
    /// </summary>
    public bool IsNoop => string.Equals(Before, After, StringComparison.Ordinal);

    /// <summary>卡片上那一行「原值 → 新值」。</summary>
    public string DiffText => Before + " → " + After;
}
