namespace LabelGou.Core.Docking;

/// <summary>
/// 「AI 助手」这一块能待的三个地方（M7 第 17 棒第二版，用户 2026-09-09 原话
/// 「把这个 AI 窗口长按拖动可以拆下来，然后拉到右侧可以吸附」）。
/// <para>为什么只有这三个：本窗口左边是五步向导、上边是菜单与状态栏，停靠到那两条边上
/// 没有可对照的东西，只会增加误停靠（登记在 §八 的取舍里）。</para>
/// </summary>
public enum DockSite
{
    /// <summary>主窗右侧那一列（预览在左、AI 在右，真正并排对照）。</summary>
    Right,

    /// <summary>主窗右下角那一行（第 16 棒第二版以来的原位）。</summary>
    Bottom,

    /// <summary>独立浮动窗口（飘在主窗之上或屏幕任何位置）。</summary>
    Float,
}
