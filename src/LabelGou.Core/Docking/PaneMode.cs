namespace LabelGou.Core.Docking;

/// <summary>
/// 一栏能处在哪几种状态（M7 第 18 棒，用户 2026-09-09 拿录屏比着要：「再把只有两边稍微缩一点然后再可以关闭左边或右边」）。
/// <para>为什么要多态而不是一个「显示/隐藏」开关：他要的是<strong>先缩</strong>（中间预览大一点，但左边那五步
/// 还在，点一下就回来）<strong>再关</strong>（彻底不要了，把宽度全给预览）。一个开关给不出这两级。</para>
/// <para><strong>哪些栏用得上哪几态（第 19 棒，用户纠正我上一棒自己发明的那条规矩）</strong>：
/// 左栏（五步向导）三态都用；<strong>右栏只用 <see cref="Open"/> 与 <see cref="Narrow"/> 两态</strong>——
/// 他原话「<em>就是因为用完可以收起到右侧啊</em>」，收成窄条就是他要的「收起」，而<strong>「关闭右栏」在新规矩下没有合法去处</strong>：
/// AI 不住别的地方，关掉它住的那一栏等于把内容弄丢（上一棒我就是这么做的，被他否了）。</para>
/// <para>为什么枚举落 Core：宽度是几何（<see cref="DockSnap.WidthForPane"/>），而几何规则必须有单测——
/// 尤其「关闭 = 列宽 0」这一条，藏内容不收列宽在 WPF 里等于什么都没让出来（§五-107 同一条教训的反面）。</para>
/// </summary>
public enum PaneMode
{
    /// <summary>展开：正常宽度（左栏 = 用户拖出来的那个宽度，右栏 = 吸附时算出来的那一档）。</summary>
    Open,

    /// <summary>
    /// 收窄成一条窄边：只放得下展开按钮与竖排标题（<see cref="DockSnap.RailDip"/>）。
    /// <para><strong>右栏的这一态不等于「内容搬走了」</strong>：AI 的家还在这块列里，只是不给看
    /// （第 19 棒：他要么「用完收到右侧」，上一棒我把 AI 踢回底部那一行，等于把这个动作做废了）。</para>
    /// </summary>
    Narrow,

    /// <summary>
    /// 关闭：列宽 0。回来的入口是「视图」菜单（窄条上的按钮在这已经看不见了，所以菜单必须留）。
    /// <para><strong>只有左栏有这一档</strong>：右栏不许走到这（<see cref="DockSnap.CollapseStep"/> 的
    /// <c>mayClose: false</c>），旧状态文件里那句 <c>"Closed"</c> 由 <see cref="DockSnap.ReconcileRightPane"/> 收成展开。</para>
    /// </summary>
    Closed,
}
