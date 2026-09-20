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
}
