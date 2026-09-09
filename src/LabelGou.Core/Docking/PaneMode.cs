namespace LabelGou.Core.Docking;

/// <summary>
/// 一栏（左边的五步向导 / 右边停靠的 AI）能处在哪三种状态（M7 第 18 棒，用户 2026-09-09 拿录屏比着要：
/// 「再把只有两边稍微缩一点然后再可以关闭左边或右边」）。
/// <para>为什么要三态而不是一个「显示/隐藏」开关：他要的是<strong>先缩</strong>（中间预览大一点，但左边那五步
/// 还在，点一下就回来）<strong>再关</strong>（彻底不要了，把宽度全给预览）。一个开关给不出这两级。</para>
/// <para>为什么枚举落 Core：宽度是几何（<see cref="DockSnap.WidthForPane"/>），而几何规则必须有单测——
/// 尤其「关闭 = 列宽 0」这一条，藏内容不收列宽在 WPF 里等于什么都没让出来（§五-107 同一条教训的反面）。</para>
/// </summary>
public enum PaneMode
{
    /// <summary>展开：正常宽度（左栏 = 用户拖出来的那个宽度，右栏 = 吸附时算出来的那一档）。</summary>
    Open,

    /// <summary>收窄成一条窄边：只放得下展开按钮与五个步骤号（<see cref="DockSnap.RailDip"/>）。</summary>
    Narrow,

    /// <summary>关闭：列宽 0。回来的入口是「视图」菜单（窄条上的按钮在这已经看不见了，所以菜单必须留）。</summary>
    Closed,
}
