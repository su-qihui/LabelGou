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
}
