using System.Windows.Controls;

namespace LabelGou.App.Services;

/// <summary>
/// 简洁版壳窗的确定性判据（活件流节点态、表格抽屉开合、AI 面板摘挂）。
/// <para>为什么收在这里而不是写在窗口里：`SimpleMainWindow` 在测试进程里造不出来
/// （和 ⑤ 步那条「判据只能问静态方法」同一个原因，见第 89 棒 `PreviewArrows`），
/// 手势与视觉留在窗口，「该显示成什么样」的账在这里，能被单测直接读。</para>
/// </summary>
public static class SimpleShellFlow
{
    /// <summary>活件流的一个节点现在处在哪一档。</summary>
    public enum NodeState
    {
        /// <summary>还没轮到。</summary>
        Next,
        /// <summary>正在这一档。</summary>
        Now,
        /// <summary>已经过了。</summary>
        Done,
    }

    /// <summary>活件流四节点：读表 → 拍板 → 排版 → 出纸。</summary>
    public const int NodeCount = 4;

    /// <summary>
    /// 五步向导（0..4）折算到活件流四节点：① 导入＝读表，② 连字段＝拍板，
    /// ③ 选模板与 ④ 拼版编号都算排版（对简洁版用户是同一件事——"版长出来了没有"），⑤ 核对输出＝出纸。
    /// <para>越界的步号先夹进 0..4 再折算：这里不许静默吃进一个坏数。</para>
    /// </summary>
    public static int NodeOfStep(int stepIndex) => Math.Clamp(stepIndex, 0, 4) switch
    {
        0 => 0,
        1 => 1,
        4 => 3,
        _ => 2,
    };

    /// <summary>第 node 个节点在第 stepIndex 步的档位。节点号越界当场抛（那是调用方写错，不是运行时状态）。</summary>
    public static NodeState StateOf(int stepIndex, int node)
    {
        if (node is < 0 or >= NodeCount)
            throw new ArgumentOutOfRangeException(nameof(node), $"活件流只有 {NodeCount} 个节点，第 {node} 个不存在");
        var now = NodeOfStep(stepIndex);
        return node < now ? NodeState.Done : node == now ? NodeState.Now : NodeState.Next;
    }

    /// <summary>表格抽屉只有开与关两态：顶栏那颗「表格」与抽屉自己右上角的「收起」按的是同一个开关。</summary>
    public static bool ToggleTableDrawer(bool isOpen) => !isOpen;

    /// <summary>
    /// 把 AI 面板从它现在的泊位摘下来挂进壳窗宿主（**同一个实例搬走**，绝不 new 第二块——
    /// 两块面板同时发请求就是双份模型钱，主窗构造里钉着的规矩）。
    /// <para>调用方负责先保证面板不在浮动窗里（飘着时内容归浮动窗持有，摘法不一样）。</para>
    /// </summary>
    public static void Park(DetachablePanel panel, ContentControl host)
    {
        var home = panel.HostAt(panel.Site);
        if (home is not null) home.Content = null;
        host.Content = panel.Content;
    }

    /// <summary>回专业版：先把岛上那份引用摘干净，再把面板挂回它登记的泊位（与 <see cref="Park"/> 严格互为逆操作——
    /// 不先摘岛就挂回家，等于两处同时指着一块内容，那正是这个类族要拦的事）。</summary>
    public static void TakeBack(DetachablePanel panel, ContentControl host)
    {
        host.Content = null;
        var home = panel.HostAt(panel.Site);
        if (home is not null) home.Content = panel.Content;
    }

    /// <summary>抽屉可拖宽的范围（DIP）：太窄看不了几列，太宽就把画布挤没了——让位之后画布至少还留得下一张纸。</summary>
    public const double DrawerMinWidth = 360;
    public const double DrawerMaxWidth = 820;

    /// <summary>抽屉拖到的宽度一律夹进范围；坏数（NaN/∞）退回默认那档，不许把 Infinity 写进状态文件（§五-183 同族）。</summary>
    public static double ClampDrawerWidth(double wanted)
        => double.IsFinite(wanted) ? Math.Clamp(wanted, DrawerMinWidth, DrawerMaxWidth) : 560;

    /// <summary>指令岛可拉伸的范围（DIP）。**下限不是拍脑袋**：第 93 棒实测把岛收到 320×380，
    /// 面板里那块内容区（Star 行）被固定行挤到看不见——AI 问的三条题一个字都读不出来，
    /// 那比"不能收缩"更糟。420 宽让按钮排只折两行，520 高让对话区还剩得下问题卡。
    /// 上限不越过壳窗常见尺寸，免得拉到看不见角。</summary>
    public const double IslandMinWidth = 420, IslandMaxWidth = 900;
    public const double IslandMinHeight = 520, IslandMaxHeight = 820;

    /// <summary>岛拉到的宽高各自夹住；坏数退回默认 440×560。</summary>
    public static (double Width, double Height) ClampIslandSize(double wantedWidth, double wantedHeight)
        => (double.IsFinite(wantedWidth) ? Math.Clamp(wantedWidth, IslandMinWidth, IslandMaxWidth) : 440,
            double.IsFinite(wantedHeight) ? Math.Clamp(wantedHeight, IslandMinHeight, IslandMaxHeight) : 560);

    /// <summary>
    /// 答完第 answeredIndex 条问题后，该把哪一条滚进视野（用户 2026-09-21 实测②：答完一条视图弹回顶部，
    /// 要往下滚回来才能选下一条）。还有下一条就指它（返回下标）；全答完返回 -1——那时进第二步，
    /// 新内容在对话区，照常滚对话区底部，这里不再抢方向盘。
    /// </summary>
    public static int NextQuestionIndex(int answeredIndex, int questionCount)
        => answeredIndex + 1 < questionCount ? answeredIndex + 1 : -1;
}
