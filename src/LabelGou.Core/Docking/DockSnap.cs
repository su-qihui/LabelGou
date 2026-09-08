namespace LabelGou.Core.Docking;

/// <summary>
/// 拖拽停靠的**判据**：光标停在哪算「要吸附到右缘 / 下缘」，以及右栏能给多宽。
/// <para>为什么这套算术放在 Core（纯逻辑、零依赖、可单测）：它是规则而不是画法。
/// 「离边多少算吸附」「两条边都够得着时听谁的」「右栏最宽能给到多少才不把预览挤没」——
/// 这三条判据若散在 WPF 事件处理里，就只有把窗口拖来拖去才能验（而单测造不出 <c>MainWindow</c>，§五-70/94）。
/// App 侧只负责「拿到光标位置、挪窗口、画预览」，判据一律回来问这里。</para>
/// <para><strong>单位统一用 DIP（WPF 设备无关像素，1/96 英寸）</strong>：窗口 <c>Left/Top/Width/Height</c>
/// 与 <c>GetPosition()</c> 都是 DIP，混进物理像素会在 150% 缩放的屏幕上偏 1.5 倍（用 <see cref="DeviceToDip"/> 换算）。</para>
/// </summary>
public static class DockSnap
{
    /// <summary>吸附带宽度：光标进入主窗边线内侧这么远以内，就算「奔着这条边去」。</summary>
    public const double EdgeBandDip = 56;

    /// <summary>允许超出主窗边线多远还算吸附（拖到窗外一点点是很自然的手势，太严格会吸不上）。</summary>
    public const double OutsideSlackDip = 96;

    /// <summary>右栏最小宽度：低于这个数对话区又开始横向被挤（§五-107 同一条教训换个方向）。</summary>
    public const double MinRightColumnDip = 320;

    /// <summary>右栏默认宽度（第一次吸附、或状态里没记过时用）。</summary>
    public const double DefaultRightColumnDip = 420;

    /// <summary>预览区下限：右栏再宽也不许把预览挤到比这更窄——预览是这次对照的另一半，不能为了一边牺牲另一边。</summary>
    public const double MinPreviewDip = 360;

    /// <summary>右栏与预览之间那条可拖分隔条占的宽度。</summary>
    public const double SplitterDip = 6;

    /// <summary>
    /// 光标现在指向哪个落点。
    /// <para>右下角两条带会重叠（既靠近右缘又靠近下缘）：这时<strong>听更近的那条边</strong>，
    /// 距离也相同则优先右栏（用户这次点名的正是「拉到右侧」）。除此之外一律 <see cref="DockSite.Float"/>
    /// ——中间区域不许吸附，否则拖到半路就回不到手上了。</para>
    /// </summary>
    public static DockSite Decide(
        double cursorX, double cursorY,
        double ownerLeft, double ownerTop, double ownerWidth, double ownerHeight,
        double bandDip = EdgeBandDip, double outsideSlackDip = OutsideSlackDip)
    {
        if (ownerWidth <= 0 || ownerHeight <= 0) return DockSite.Float;

        var right = ownerLeft + ownerWidth;
        var bottom = ownerTop + ownerHeight;

        // 「靠近右缘」还得「纵向大致还在主窗范围内」：光标跑到屏幕最左边时不该吸到右缘去。
        var inVerticalSpan = cursorY >= ownerTop - outsideSlackDip && cursorY <= bottom + outsideSlackDip;
        var inHorizontalSpan = cursorX >= ownerLeft - outsideSlackDip && cursorX <= right + outsideSlackDip;

        var nearRight = inVerticalSpan && cursorX >= right - bandDip && cursorX <= right + outsideSlackDip;
        var nearBottom = inHorizontalSpan && cursorY >= bottom - bandDip && cursorY <= bottom + outsideSlackDip;

        if (nearRight && nearBottom)
            return Math.Abs(right - cursorX) <= Math.Abs(bottom - cursorY) ? DockSite.Right : DockSite.Bottom;
        if (nearRight) return DockSite.Right;
        if (nearBottom) return DockSite.Bottom;
        return DockSite.Float;
    }

    /// <summary>这块总宽里到底挤不挤得出一个合法的右栏（挤不出来就别承诺吸附，松手继续浮动）。</summary>
    public static bool CanDockRight(double splitRegionWidthDip)
        => MaxRightColumnDip(splitRegionWidthDip) >= MinRightColumnDip;

    /// <summary>右栏上限 = 总宽 − 预览下限 − 分隔条；不够本棒最小值时返回 0（= 这次不给吸）。</summary>
    public static double MaxRightColumnDip(double splitRegionWidthDip)
    {
        var room = splitRegionWidthDip - MinPreviewDip - SplitterDip;
        return room < MinRightColumnDip ? 0 : room;
    }

    /// <summary>
    /// 把想要的右栏宽度夹进合法区间。返回 0 表示这块地方放不下右栏
    /// （调用方必须据此退回浮动，而不是硬塞一个负数或把预览挤成一条缝）。
    /// </summary>
    public static double ClampRightColumnDip(double wantedDip, double splitRegionWidthDip)
    {
        var max = MaxRightColumnDip(splitRegionWidthDip);
        if (max <= 0) return 0;
        var wanted = wantedDip <= 0 ? DefaultRightColumnDip : wantedDip;
        return Math.Clamp(wanted, MinRightColumnDip, max);
    }

    /// <summary>物理像素 → DIP（<c>PointToScreen</c> 给的是物理像素，而窗口坐标是 DIP）。缩放数非法时原样返回，不假装换算过。</summary>
    public static double DeviceToDip(double deviceValue, double dpiScale)
        => dpiScale > 0 ? deviceValue / dpiScale : deviceValue;
}
