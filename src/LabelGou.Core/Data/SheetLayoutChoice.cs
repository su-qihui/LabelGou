namespace LabelGou.Core.Data;

/// <summary>
/// 「这张表该怎么切」的一份指令——表头在哪一行（或根本没有表头）、哪几行不当数据（合计行）。
/// <para><strong>为什么要把它做成一个能传的参数</strong>（第 21 棒，用户 2026-09-09 定「权限全给 AI」）：
/// 以前切法只有一种——程序自己按别名命中数猜。猜错的两类后果都是印错货：
/// 无表头的工厂表首行被当表头吃掉（少印一张）、表尾「合计 155」被当一条货（多印一张）。
/// 用户的判定是：这类判断交给看得到整张表的 AI（含贴图与批注），软件只负责
/// <strong>照指令重切一遍</strong>并守住几何与数量。</para>
/// <para>第 21 棒当时还写过一句「不再新增写死的启发式去猜」——第 24 棒把它改成可开关的默认档兜底
/// （<see cref="SummaryRowSpotter"/>）：用户点名要把 §六 活账里「合计行不剔」的本地最小改法做掉，
/// 而 AI 那条路要密钥、要等几十秒，不能当唯一出路。这条启发式不抢判定权：剔了什么全程可见、
/// 一键可关，AI/用户的指令永远压过它；递 null 的旧调用方看到的表仍与第 20 棒之前一字不差。</para>
/// </summary>
/// <param name="HeaderRowIndex">表头在原表里的行号（<b>0 起</b>）。null = 由软件按别名命中数自动猜。</param>
/// <param name="HasHeader">这张表有没有表头行。false = 第一行也是数据，表头按「列 A / 列 B」补出来。</param>
/// <param name="ExcludedRawRows">要剔除的<b>原表行号</b>（0 起）：合计行、表尾批注行、空行以外的假数据行。
/// <para>用原表行号而不是"第几条数据"，是因为 AI 报的是原表行号（它看到的就是原表），两套序号并存一定会有人错配。</para></param>
/// <param name="SkipSummaryRows">自动跳过疑似合计/小计行（第 24 棒，§十-A-13 的最小改法）。
/// <para>只在递了指令（含 <see cref="Auto"/>）时生效；命中的行会逐行说出剔了谁、凭什么（见
/// <see cref="DetectionResult.AutoSkippedSummaryRows"/>），不默默删行；关掉即一行不剔。</para></param>
/// <param name="SideBlocks">被认成<strong>右侧块</strong>的列（导入层第 1 棒）：厂方抄在数据区右侧的
/// 表内文字模板、或写给操作者的指令。这些列不参与字段映射、不出唛头，但<strong>原样留着</strong>交给
/// 版式层当参照——它们不是脏数据，删了就等于把「无参照不出模板」的参照物扔了。</param>
/// <param name="ValueRules">值改写规则（导入层第 1 棒补）：把表里<strong>写歪了的内容</strong>在软件里改对，
/// 例如按指令「张数等于件数」把件数列的数填进张数列、删掉货号 <c>*144</c> 这种重复尾巴。
/// <para><strong>只改软件里的这一份视图</strong>：磁盘上用户的 xlsx 原件永远不动（用户 2026-09-13 定的
/// 「只在软件内调整，导出是可选项」）。所以它和 SideBlocks 一样是可撤回的一条指令，不是一次写盘。</para></param>
public sealed record SheetLayoutChoice(
    int? HeaderRowIndex = null,
    bool HasHeader = true,
    IReadOnlyList<int>? ExcludedRawRows = null,
    bool SkipSummaryRows = true,
    IReadOnlyList<SideBlock>? SideBlocks = null,
    IReadOnlyList<ValueRule>? ValueRules = null)
{
    /// <summary>不加任何指令的那一份（自动猜表头、不点名剔行；合计行兜底仍默认开）。</summary>
    public static readonly SheetLayoutChoice Auto = new();

    /// <summary>剔除的行数（界面与日志要说清"剔了几行"，不能让行数悄悄变少；不含自动跳过的合计行）。</summary>
    public int ExcludedCount => ExcludedRawRows?.Count ?? 0;

    /// <summary>被圈出去的右侧列数。</summary>
    public int SideBlockCount => SideBlocks?.Count ?? 0;

    /// <summary>值改写规则条数。</summary>
    public int ValueRuleCount => ValueRules?.Count ?? 0;

    /// <summary>这一列是否被圈成右侧块（映射层与拼版据此跳过它）。</summary>
    public bool IsSideColumn(int column) => SideBlocks?.Any(b => b.Column == column) ?? false;

    /// <summary>这条指令是不是"什么都没点名"（用来决定状态栏要不要多说一句）。</summary>
    public bool IsDefault => HeaderRowIndex is null && HasHeader && ExcludedCount == 0
        && SkipSummaryRows && SideBlockCount == 0 && ValueRuleCount == 0;

    /// <summary>给人看的一行（状态栏与 AI 提案确认窗共用；不说人话的留痕等于没留）。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!HasHeader) parts.Add("按你说的：这张表没有表头行，第一行也当数据");
        else if (HeaderRowIndex is int row) parts.Add($"表头固定在原表第 {row + 1} 行");
        if (ExcludedCount > 0) parts.Add($"剔除 {ExcludedCount} 行不当数据");
        if (!SkipSummaryRows) parts.Add("合计行兜底已关：一行都不自动剔");
        if (SideBlocks is { Count: > 0 } side)
            parts.Add($"右侧 {side.Count} 列不当数据：" +
                      string.Join("、", side.Select(b => $"{HeaderRowDetector.ColumnLetter(b.Column)} 列" +
                                   (b.Kind == SideBlockKind.TextTemplate ? "（表内文字模板，留着给版式当参照）" : "（指令）"))));
        if (ValueRules is { Count: > 0 } rules)
            parts.Add("值改写：" + string.Join("、", rules.Select(ValueRuleText)));
        return parts.Count == 0 ? "切法：软件自动猜表头，不剔行" : "切法：" + string.Join("；", parts);
    }

    /// <summary>一条值改写规则的人话（界面上那句短说得更细一点用）。</summary>
    public static string ValueRuleText(ValueRule rule) => rule.Kind switch
    {
        ValueRuleKind.FillColumnFrom =>
            $"{HeaderRowDetector.ColumnLetter(rule.TargetColumn)} 列的数值改成 {HeaderRowDetector.ColumnLetter(rule.SourceColumn)} 列的数值",
        ValueRuleKind.StripStarTail =>
            $"删掉 {HeaderRowDetector.ColumnLetter(rule.TargetColumn)} 列里 * 号及其后面的部分",
        _ => "一条值改写规则",
    };
}

/// <summary>值改写的种类。</summary>
public enum ValueRuleKind
{
    /// <summary>目标列每行的值改成来源列的值（照指令「张数等于件数」把 B 列的件数填进 D 列）。</summary>
    FillColumnFrom,
    /// <summary>删掉 <c>*</c> 号及其后面那截——<strong>只删判据认得出的重复尾巴</strong>
    /// （数字且同行别处有同值）。品名（<c>OLU4014 *CANDY CLOUDS</c>）与规格（<c>*100ml</c>）不动。</summary>
    StripStarTail,
}

/// <param name="Kind">哪一种改写。</param>
/// <param name="TargetColumn">被改的列（0 起）。</param>
/// <param name="SourceColumn">取值的来源列（仅 <see cref="ValueRuleKind.FillColumnFrom"/> 用）。</param>
/// <param name="Reason">凭什么改（界面上"凭什么"那一句，不许是空的——静默改数据是最坏的一种）。</param>
public sealed record ValueRule(ValueRuleKind Kind, int TargetColumn, int SourceColumn = -1, string Reason = "")
{
    /// <summary>这一列是不是这条规则的目标列。</summary>
    public bool Targets(int column) => TargetColumn == column;
}
