namespace LabelGou.Core.Data;

/// <summary>
/// 「这张表该怎么切」的一份指令——表头在哪一行（或根本没有表头）、哪几行不当数据（合计行）。
/// <para><strong>为什么要把它做成一个能传的参数</strong>（第 21 棒，用户 2026-09-09 定「权限全给 AI」）：
/// 以前切法只有一种——程序自己按别名命中数猜。猜错的两类后果都是印错货：
/// 无表头的工厂表首行被当表头吃掉（少印一张）、表尾「合计 155」被当一条货（多印一张）。
/// 用户的判定是：这类判断交给看得到整张表的 AI（含贴图与批注），程序只负责
/// <strong>照指令重切一遍</strong>并守住几何与数量，<strong>不再新增写死的启发式去猜</strong>。</para>
/// <para>默认值 = 与第 20 棒之前完全一致的行为（自动猜、有表头、不剔行），
/// 所以谁不传这个参数，谁看到的表就还是老样子。</para>
/// </summary>
/// <param name="HeaderRowIndex">表头在原表里的行号（<b>0 起</b>）。null = 由软件按别名命中数自动猜。</param>
/// <param name="HasHeader">这张表有没有表头行。false = 第一行也是数据，表头按「列 A / 列 B」补出来。</param>
/// <param name="ExcludedRawRows">要剔除的<b>原表行号</b>（0 起）：合计行、表尾批注行、空行以外的假数据行。
/// <para>用原表行号而不是"第几条数据"，是因为 AI 看到与报出来的都是原表行号，两套序号并存一定会有人错配。</para></param>
public sealed record SheetLayoutChoice(
    int? HeaderRowIndex = null,
    bool HasHeader = true,
    IReadOnlyList<int>? ExcludedRawRows = null)
{
    /// <summary>不加任何指令的那一份（等价于旧行为）。</summary>
    public static readonly SheetLayoutChoice Auto = new();

    /// <summary>剔除的行数（界面与日志要说清"剔了几行"，不能让行数悄悄变少）。</summary>
    public int ExcludedCount => ExcludedRawRows?.Count ?? 0;

    /// <summary>这条指令是不是"什么都没改"（用来决定状态栏要不要多说一句）。</summary>
    public bool IsDefault => HeaderRowIndex is null && HasHeader && ExcludedCount == 0;

    /// <summary>给人看的一行（状态栏与 AI 提案确认窗共用；不说人话的留痕等于没留）。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!HasHeader) parts.Add("按你说的：这张表没有表头行，第一行也当数据");
        else if (HeaderRowIndex is int row) parts.Add($"表头固定在原表第 {row + 1} 行");
        if (ExcludedCount > 0) parts.Add($"剔除 {ExcludedCount} 行不当数据");
        return parts.Count == 0 ? "切法：软件自动猜表头，不剔行" : "切法：" + string.Join("；", parts);
    }
}
