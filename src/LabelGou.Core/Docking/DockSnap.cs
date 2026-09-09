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

    /// <summary>「收窄」那一档的宽度（第 18 棒：用户拿录屏比着要的形状 = 两边收成一条窄边，还能各关各的）。
    /// <para>44 DIP 是「放得下一个展开按钮 + 五个步骤号」的下限；再窄就只剩一条线，看不出那里还有东西。</para></summary>
    public const double RailDip = 44;

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

    /// <summary>
    /// 那块<strong>浮动窗口自己的矩形</strong>指向哪个落点（第 18 棒：用户真拖出来的缺陷）。
    /// <para>上一版的判据只看光标。可用户拆下 AI 之后，是抓那块浮动窗的<strong>标题条</strong>拖到右缘的
    /// （录屏 <c>10-36-58.MP4</c> 第 32 帧：窗已挂到屏右缘外，仍没吸上去）——
    /// 标题条的拖动由操作系统接管，我们的鼠标捕获与 Move 事件一个都收不到，于是永远不报「可吸附」。
    /// 所以「能不能吸」必须能<strong>只看几何、不看光标</strong>就判出来（拖把手那一路仍走 <see cref="Decide"/>，
    /// 因为那时光标就在手指下，而刚拆出去的窗底边本来就贴着主窗下缘，拿矩形判会当场想把他弹回去）。</para>
    /// <para>与光标版的不同：这里「越线」是好事（把窗拖过半出屏是很自然的手势），所以不设上限，
    /// 只要求<strong>两块矩形在方向上真重叠</strong>（把窗拖到主窗上方再往右挪，不该吸右栏），
    /// 且窗的左/上边还没跑得太远（<paramref name="outsideSlackDip"/>）。右下角重叠时听“进带更深”的那条边，
    /// 一样深则优先右栏（用户点名的就是右侧）。</para>
    /// </summary>
    public static DockSite DecideFromWindowRect(
        double floatLeft, double floatTop, double floatRight, double floatBottom,
        double ownerLeft, double ownerTop, double ownerWidth, double ownerHeight,
        double bandDip = EdgeBandDip, double outsideSlackDip = OutsideSlackDip)
    {
        if (ownerWidth <= 0 || ownerHeight <= 0) return DockSite.Float;
        if (floatRight <= floatLeft || floatBottom <= floatTop) return DockSite.Float;

        var right = ownerLeft + ownerWidth;
        var bottom = ownerTop + ownerHeight;

        // 方向上得真叠着：否则「右缘」只是坐标接近（窗在主窗上方老远也算贴右缘）。
        var overlapY = Math.Min(floatBottom, bottom) - Math.Max(floatTop, ownerTop);
        var overlapX = Math.Min(floatRight, right) - Math.Max(floatLeft, ownerLeft);

        // 进带多深：窗的右缘越过「主窗右缘 − 带」多远（负数 = 还没到）。
        var rightDepth = floatRight - (right - bandDip);
        var bottomDepth = floatBottom - (bottom - bandDip);

        var nearRight = overlapY > 0 && rightDepth >= 0 && floatLeft < right + outsideSlackDip;
        var nearBottom = overlapX > 0 && bottomDepth >= 0 && floatTop < bottom + outsideSlackDip;

        if (nearRight && nearBottom) return rightDepth >= bottomDepth ? DockSite.Right : DockSite.Bottom;
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

    /// <summary>
    /// 某一栏在某个状态下到底给多宽（第 18 棒第二件：展开 / 收窄 / 关闭）。
    /// <para>关闭给 0 而不是给一个负数，也不靠 <c>Visibility=Collapsed</c> 冒充收回——
    /// <strong>Grid 列宽是「预留空间」</strong>（上一棒那条反向机检钉的就是它）：只藏内容不收列宽，
    /// 中间那块预览不会真变大。</para>
    /// </summary>
    public static double WidthForPane(PaneMode mode, double openDip, double railDip = RailDip)
        => mode switch
        {
            PaneMode.Open => openDip <= 0 ? throw new ArgumentOutOfRangeException(nameof(openDip), "展开态得给一个正经宽度") : openDip,
            PaneMode.Narrow => railDip,
            _ => 0,
        };

    /// <summary>
    /// 那个「收起 / 展开」按钮按一下走到哪一档（第 18 棒：用户要的是「先稍微缩一点，然后还能关掉」）。
    /// <para>三档绕一圈而不是给两个独立开关：一个按钮按下去就是「再小一点」，回到展开只要再按两下；
    /// 左栏与右栏用同一个规则，两边手感一致。</para>
    /// </summary>
    public static PaneMode CollapseStep(PaneMode mode) => mode switch
    {
        PaneMode.Open => PaneMode.Narrow,
        PaneMode.Narrow => PaneMode.Closed,
        _ => PaneMode.Open,
    };

    /// <summary>物理像素 → DIP（<c>PointToScreen</c> 给的是物理像素，而窗口坐标是 DIP）。缩放数非法时原样返回，不假装换算过。</summary>
    public static double DeviceToDip(double deviceValue, double dpiScale)
        => dpiScale > 0 ? deviceValue / dpiScale : deviceValue;
}
