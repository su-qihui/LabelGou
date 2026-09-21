using System.Windows.Controls;

namespace LabelGou.App.Services;

/// <summary>
/// 简洁版壳窗的确定性判据（活件流节点态、左右栏开合与占宽、AI 面板摘挂）。
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

    /// <summary>左右两根栏各只有开与关两态：顶栏那颗钮与栏里自己的「收起」按的是同一个开关。</summary>
    public static bool TogglePane(bool isOpen) => !isOpen;

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

    /// <summary>两根栏可拖宽的范围与默认档（DIP）。左栏是这张表（太窄看不了几列，太宽把画布挤没）。
    /// 右栏下限 420 是第 93 棒在"岛只有 560 高"那一版量出来的（一窄按钮排就折成五行、把对话区挤没）；
    /// 第 95 棒放到 300——右栏现在是顶到底一整根（全屏 ~820 高），折行吃的那点高度装得下。
    /// 这个数**要目检才算数**：拖到 300 看 AI 的问题卡还在不在，不在就把实测值写回来。</summary>
    public const double LeftPaneMinWidth = 360, LeftPaneMaxWidth = 820, LeftPaneDefaultWidth = 560;
    public const double RightPaneMinWidth = 300, RightPaneMaxWidth = 900, RightPaneDefaultWidth = 440;

    /// <summary>拖到的宽度一律夹进范围；坏数（NaN/∞）退回默认那档，不许把 Infinity 写进状态文件（§五-183 同族）。</summary>
    public static double ClampLeftPaneWidth(double wanted)
        => double.IsFinite(wanted) ? Math.Clamp(wanted, LeftPaneMinWidth, LeftPaneMaxWidth) : LeftPaneDefaultWidth;

    public static double ClampRightPaneWidth(double wanted)
        => double.IsFinite(wanted) ? Math.Clamp(wanted, RightPaneMinWidth, RightPaneMaxWidth) : RightPaneDefaultWidth;

    /// <summary>弹入容器比栏本身多留的那点数（栏的外边距 + 卡片投影；裁边时别把它们切掉）。</summary>
    public const double PaneRevealGutter = 12;

    /// <summary>一根栏那一格现在占多宽：收起 = 占 0，中间那格（预览）自己补位——用户 2026-09-21 要的
    /// 「以中间为主导界面，左右可以随时关闭或开启」就是这一句。开着时多留一份投影边。</summary>
    public static double PaneRevealWidth(double paneWidth, bool open)
        => open ? paneWidth + PaneRevealGutter : 0;

    /// <summary>
    /// 答完第 answeredIndex 条问题后，该把哪一条滚进视野（用户 2026-09-21 实测②：答完一条视图弹回顶部，
    /// 要往下滚回来才能选下一条）。还有下一条就指它（返回下标）；全答完返回 -1——那时进第二步，
    /// 新内容在对话区，照常滚对话区底部，这里不再抢方向盘。
    /// </summary>
    public static int NextQuestionIndex(int answeredIndex, int questionCount)
        => answeredIndex + 1 < questionCount ? answeredIndex + 1 : -1;
}
