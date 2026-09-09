using LabelGou.Core.Docking;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 拖拽停靠的判据（M7 第 17 棒第二版）。
/// <para>用户 2026-09-09 第三次纠正后要的东西：「把这个 AI 窗口长按拖动可以拆下来，然后拉到右侧可以吸附」。
/// 「跟手移动」那段是 WPF 事件，单测造不出主窗口（§五-70/94）；但<strong>哪一下算吸附</strong>是纯算术，
/// 这一页钉的就是它——包括右下角两条吸附带重叠时听谁的、以及右栏不许把预览挤没。</para>
/// </summary>
public class DockSnapTests
{
    // 主窗：左上 (100, 60)，1600 × 900 → 右缘 1700、下缘 960。
    const double L = 100, T = 60, W = 1600, H = 900;
    const double Right = L + W;    // 1700
    const double Bottom = T + H;   // 960

    static DockSite At(double x, double y) => DockSnap.Decide(x, y, L, T, W, H);

    [Fact]
    public void 光标在主窗中间不算吸附()
        => Assert.Equal(DockSite.Float, At(L + W / 2, T + H / 2));

    [Fact]
    public void 贴进右缘吸附带就是右栏()
        => Assert.Equal(DockSite.Right, At(Right - 20, T + H / 2));

    [Fact]
    public void 拖出右缘一点点仍然吸得住()
        => Assert.Equal(DockSite.Right, At(Right + 40, T + H / 2));

    [Fact]
    public void 拖出右缘太远就不吸了()
        => Assert.Equal(DockSite.Float, At(Right + DockSnap.OutsideSlackDip + 1, T + H / 2));

    [Fact]
    public void 贴下缘吸回底部那一行()
        => Assert.Equal(DockSite.Bottom, At(L + W / 2, Bottom - 20));

    [Fact]
    public void 吸附带边界是闭区间_差一丝就不算()
    {
        Assert.Equal(DockSite.Right, At(Right - DockSnap.EdgeBandDip, T + H / 2));
        Assert.Equal(DockSite.Float, At(Right - DockSnap.EdgeBandDip - 0.01, T + H / 2));
    }

    [Fact]
    public void 右下角两条带重叠时听更近的那条边()
    {
        // 距右缘 10、距下缘 50 → 右；反过来 → 下。
        Assert.Equal(DockSite.Right, At(Right - 10, Bottom - 50));
        Assert.Equal(DockSite.Bottom, At(Right - 50, Bottom - 10));
    }

    [Fact]
    public void 两条边一样近时优先右栏()
        => Assert.Equal(DockSite.Right, At(Right - 30, Bottom - 30));

    [Fact]
    public void 光标跑到屏幕最左边时不许吸右缘()
        => Assert.Equal(DockSite.Float, At(L - 900, T + H / 2));

    [Fact]
    public void 主窗还没量出尺寸时一律不吸()
    {
        Assert.Equal(DockSite.Float, DockSnap.Decide(0, 0, 0, 0, 0, 0));
        Assert.Equal(DockSite.Float, DockSnap.Decide(10, 10, 100, 100, 1600, -1));
    }

    [Fact]
    public void 右栏默认宽度就是那四百二十()
        => Assert.Equal(DockSnap.DefaultRightColumnDip, DockSnap.ClampRightColumnDip(0, 1600));

    [Fact]
    public void 右栏太窄会被夹到下限_太宽会被夹到上限()
    {
        Assert.Equal(DockSnap.MinRightColumnDip, DockSnap.ClampRightColumnDip(100, 1600));
        Assert.Equal(1600 - DockSnap.MinPreviewDip - DockSnap.SplitterDip,
            DockSnap.ClampRightColumnDip(9999, 1600));
    }

    [Fact]
    public void 地方不够时宁可不给吸附()
    {
        // 360（预览下限）+ 6（分隔条）+ 320（右栏下限）= 686：比这更窄就挤不出合法右栏。
        Assert.False(DockSnap.CanDockRight(680));
        Assert.Equal(0, DockSnap.ClampRightColumnDip(420, 680));
        Assert.True(DockSnap.CanDockRight(686));
        Assert.Equal(DockSnap.MinRightColumnDip, DockSnap.ClampRightColumnDip(420, 686));
    }

    [Fact]
    public void 右栏不许把预览挤到下限以下()
    {
        var width = DockSnap.ClampRightColumnDip(9999, 1400);
        Assert.True(1400 - width - DockSnap.SplitterDip >= DockSnap.MinPreviewDip);
    }

    [Fact]
    public void 物理像素换算成DIP按缩放数除_缩放数坏了就原样返回()
    {
        Assert.Equal(1920, DockSnap.DeviceToDip(2880, 1.5), 6);
        Assert.Equal(2880, DockSnap.DeviceToDip(2880, 0), 6);
        Assert.Equal(2880, DockSnap.DeviceToDip(2880, -1), 6);
    }

    [Fact]
    public void 那几个数不许悄悄退回旧值()
    {
        // 420 = 用户要的「右侧那一栏」够放对话区的宽度；320 是下限；360 是预览的命根子。
        Assert.Equal(420, DockSnap.DefaultRightColumnDip);
        Assert.Equal(320, DockSnap.MinRightColumnDip);
        Assert.Equal(360, DockSnap.MinPreviewDip);
        Assert.Equal(56, DockSnap.EdgeBandDip);
    }

    // ===== 第 18 棒：拿「浮动窗自己的矩形」判落点 =====
    // 用户真拖之后反馈「拼不到右边」：他拆下来之后是拖那块窗的标题条，那一路由操作系统接管，
    // 光标版的判据根本没人跑。所以下面这些用例钉的是「只看几何能不能得出同样的结论」。

    /// <summary>浮动窗：宽 900、高 700，左上角递进来。</summary>
    static DockSite AtRect(double fl, double ft) => DockSnap.DecideFromWindowRect(fl, ft, fl + 900, ft + 700, L, T, W, H);

    [Fact]
    public void 浮动窗右缘越到主窗右缘上就吸右栏()
        => Assert.Equal(DockSite.Right, AtRect(Right - 400, T + 100));

    [Fact]
    public void 浮动窗拖到屏右外面仍然吸右栏_这是用户真正做的那个手势()
    {
        // 录屏里那一帧：窗已挂到屏右缘之外（左缘在主窗内、右缘超出主窗右缘）。
        Assert.Equal(DockSite.Right, AtRect(Right - 100, T + 100));
        Assert.Equal(DockSite.Right, AtRect(Right + 40, T + 100));
    }

    [Fact]
    public void 只差一丝没进带就不吸()
        => Assert.Equal(DockSite.Float, AtRect(Right - 900 - DockSnap.EdgeBandDip - 0.01, T + 100));

    [Fact]
    public void 窗与主窗纵向不重叠时不许吸右栏()
    {
        // 拖到主窗上方老远再往右靠：右缘进了带，但两块矩形纵向不叠 → 吸上去只会挡住顶部菜单。
        Assert.Equal(DockSite.Float, AtRect(Right - 100, T - 800));
    }

    [Fact]
    public void 窗左缘跑得太远也不吸()
        => Assert.Equal(DockSite.Float, AtRect(Right + DockSnap.OutsideSlackDip + 1, T + 100));

    [Fact]
    public void 拖到下缘吸回底部那一行()
        => Assert.Equal(DockSite.Bottom, AtRect(L + 200, Bottom - 600));

    [Fact]
    public void 右下角重叠时听进带更深的那条边()
    {
        // 右缘进了带 800、下缘只进了 20 → 右；反过来 → 下。
        Assert.Equal(DockSite.Right, AtRect(Right - 100, Bottom - 680));
        Assert.Equal(DockSite.Bottom, AtRect(L, Bottom - 100));
    }

    [Fact]
    public void 主窗或浮动窗尺寸非法时一律不吸()
    {
        Assert.Equal(DockSite.Float, DockSnap.DecideFromWindowRect(0, 0, 100, 100, 0, 0, 0, 0));
        Assert.Equal(DockSite.Float, DockSnap.DecideFromWindowRect(0, 0, 0, 0, L, T, W, H));
        Assert.Equal(DockSite.Float, DockSnap.DecideFromWindowRect(100, 100, 50, 50, L, T, W, H));   // 宽高为负（还没量出来）
    }

    [Fact]
    public void 一栏三态的宽度_展开给正经宽_收窄给窄条_关闭给零()
    {
        Assert.Equal(470, DockSnap.WidthForPane(PaneMode.Open, 470), 6);
        Assert.Equal(DockSnap.RailDip, DockSnap.WidthForPane(PaneMode.Narrow, 470), 6);
        Assert.Equal(0, DockSnap.WidthForPane(PaneMode.Closed, 470), 6);
    }

    [Fact]
    public void 展开态递个零宽度当场报错而不是默默藏起来()
        => Assert.Throws<ArgumentOutOfRangeException>(() => DockSnap.WidthForPane(PaneMode.Open, 0));

    [Fact]
    public void 同一个收起按钮按三下刚好绕回展开()
    {
        // 左栏（向导）要的顺序：先缩一点（Open→Narrow），再能关掉（Narrow→Closed），然后再按一下得回来。
        Assert.Equal(PaneMode.Narrow, DockSnap.CollapseStep(PaneMode.Open));
        Assert.Equal(PaneMode.Closed, DockSnap.CollapseStep(PaneMode.Narrow));
        Assert.Equal(PaneMode.Open, DockSnap.CollapseStep(PaneMode.Closed));
    }

    [Fact]
    public void 右栏不给关闭那一档_按一下只在展开与窄条之间来回()
    {
        // 第 19 棒（用户 2026-09-09）：「我没有说 AI 不许住在一个被收掉的列里?? 就是因为用完可以收起到右侧啊??」
        // 右栏住的是 AI，而 AI 没有别的家——关掉它住的那一栏等于把内容弄丢，所以这一档根本不给它。
        Assert.Equal(PaneMode.Narrow, DockSnap.CollapseStep(PaneMode.Open, mayClose: false));
        Assert.Equal(PaneMode.Open, DockSnap.CollapseStep(PaneMode.Narrow, mayClose: false));
        // 旧状态文件里那句 Closed 走到这里也得回得来，不能卡在收不掉也展不开的那一档
        Assert.Equal(PaneMode.Open, DockSnap.CollapseStep(PaneMode.Closed, mayClose: false));
        // 而默认（左栏）那一档不变：向导确实可以整个不要
        Assert.Equal(PaneMode.Closed, DockSnap.CollapseStep(PaneMode.Narrow, mayClose: true));
    }

    [Fact]
    public void 旧状态里那句AI在底部而右栏是窄条会搬到右栏展开()
    {
        // 新规矩下 AI 不住右栏时那根窄条根本不该存在（搬走时会复位成展开），
        // 所以这种组合只可能是旧版默认写出来的——而用户这次直说了默认要「右栏是 AI」。
        var (site, pane) = DockSnap.ReconcileRightPane(DockSite.Bottom, PaneMode.Narrow);
        Assert.Equal(DockSite.Right, site);
        Assert.Equal(PaneMode.Open, pane);
    }

    [Fact]
    public void 右栏那句Closed认出来就展开()
    {
        var (site, pane) = DockSnap.ReconcileRightPane(DockSite.Right, PaneMode.Closed);
        Assert.Equal(DockSite.Right, site);
        Assert.Equal(PaneMode.Open, pane);
    }

    [Fact]
    public void 本来就合法的组合一律原样不动()
    {
        // 他主动把 AI 放回底部那一行（App 侧会把右栏复位成展开）：这条记录合法，不许再搬一次
        Assert.Equal((DockSite.Bottom, PaneMode.Open), DockSnap.ReconcileRightPane(DockSite.Bottom, PaneMode.Open));
        // 上次把 AI 收在右栏的窄条里：这就是他要的那个「用完收到右侧」，必须留住
        Assert.Equal((DockSite.Right, PaneMode.Narrow), DockSnap.ReconcileRightPane(DockSite.Right, PaneMode.Narrow));
        Assert.Equal((DockSite.Right, PaneMode.Open), DockSnap.ReconcileRightPane(DockSite.Right, PaneMode.Open));
    }

    [Fact]
    public void 收窄那一档比展开窄但装得下一个按钮()
    {
        Assert.True(DockSnap.RailDip < DockSnap.MinRightColumnDip);   // 不收窄就不算另一档
        Assert.True(DockSnap.RailDip >= 40);                           // 再窄就只剩一条线，看不出那里还有东西
    }
}
