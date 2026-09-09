using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LabelGou.App.Export;
using LabelGou.App.Services;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 右侧那一整块「拆成独立窗口 / 收回原位」的行为（M7 第 16 棒）。
/// <para>用户 2026-09-08 的原话：「AI 这个窗口做出来后要切换回去才能再看到效果……这个窗口也可以拼到别处」。
/// 拆开后 AI 与预览同时可见，切页签这一步就没必要了。</para>
/// <para>这里钉的是三件会真咬人的事：<strong>拆出去时家里不能再持有那份引用</strong>（两处指一块内容，
/// 谁也不知道现在显示的是哪一个）、<strong>搬的是同一个实例</strong>（页签选中项与会话内容不能丢）、
/// <strong>关掉浮动窗口等于收回而不是把内容一起扔掉</strong>。</para>
/// <para>注：<see cref="MainWindow"/> 本身在单测里造不出来（没有 Application 资源，StaticResource 找不到，
/// §五-70/94），所以 XAML 拓扑那部分由 <c>labelgou-other\checks\check-ui-topology.py</c> 机检，这里只钉这个类。</para>
/// <para>与 <see cref="AiDockingTests"/> 同集合并且不并行：这两个类都真 <c>Show()</c> 顶层窗口，
/// 并着跑会把测试主机跑崩（理由与实测记录写在那里）。</para>
/// <para><strong>每一个真开了窗的用例收尾都必须把窗关掉</strong>（第 18 棒实测）：STA 线程带着一个还活着的 HWND 退出，
/// 那条窗消息会在 <c>MS.Win32.HwndSubclass.SubclassWndProc</c> 里撞上 <c>Thread.CurrentThread == null</c>，
/// 抛一个进不了任何用例的 NullReferenceException → 整个测试主机进程崩。</para>
/// </summary>
[Collection(AiDockingTests.WpfWindowCollection)]
public class DetachablePanelTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static (ContentControl Home, TabControl Tabs) Pair()
    {
        // 拿 TabControl 当被搬的对象只为了验「搬的是同一个实例」（它带选中项这种状态）；
        // 两页 = 主窗口现在预览那块的形状（AI 已经拆出去了，不再挤在一个页签组里）。
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "单标签" });
        tabs.Items.Add(new TabItem { Header = "整版拼版" });
        return (new ContentControl { Content = tabs }, tabs);
    }

    [Fact]
    public void 刚建好时内容在家里()
    {
        OnSta(() =>
        {
            var (home, tabs) = Pair();
            var panel = new DetachablePanel(home, tabs, "测试");

            Assert.False(panel.IsDetached);
            Assert.Null(panel.FloatingWindow);
            Assert.Same(tabs, home.Content);
            return true;
        });
    }

    [Fact]
    public void 拆出去后家里不再持有而浮动窗口持有它()
    {
        OnSta(() =>
        {
            var (home, tabs) = Pair();
            var panel = new DetachablePanel(home, tabs, "测试");
            var hits = 0;
            panel.StateChanged += () => hits++;

            panel.Float();

            Assert.True(panel.IsDetached);
            Assert.Null(home.Content);                       // 不先把家里摘干净，两处就同时指着一块内容
            Assert.Same(tabs, panel.FloatingWindow!.Content);
            Assert.Equal("测试", panel.FloatingWindow.Title);
            Assert.Equal(1, hits);
            panel.Dock();                                    // 收尾关窗：还开着的 HWND 跟着线程退出会跑崩测试主机
            return true;
        });
    }

    [Fact]
    public void 收回后内容回到家里而浮动窗口关掉()
    {
        OnSta(() =>
        {
            var (home, tabs) = Pair();
            var panel = new DetachablePanel(home, tabs, "测试");
            panel.Float();
            var win = panel.FloatingWindow!;

            panel.Dock();

            Assert.False(panel.IsDetached);
            Assert.Null(panel.FloatingWindow);
            Assert.Same(tabs, home.Content);
            Assert.Null(win.Content);
            Assert.False(win.IsVisible);                     // 窗口不能留在桌面上变成一块白板
            return true;
        });
    }

    [Fact]
    public void 直接关掉浮动窗口等于自动收回而不是丢掉内容()
    {
        OnSta(() =>
        {
            var (home, tabs) = Pair();
            var panel = new DetachablePanel(home, tabs, "测试");
            panel.Float();

            panel.FloatingWindow!.Close();                   // 用户点窗口右上角的 ×

            Assert.False(panel.IsDetached);
            Assert.Same(tabs, home.Content);                 // 内容跟着窗口一起消失是不可接受的
            return true;
        });
    }

    [Fact]
    public void 反复拆只会有一个浮动窗口()
    {
        OnSta(() =>
        {
            var (home, tabs) = Pair();
            var panel = new DetachablePanel(home, tabs, "测试");
            var hits = 0;
            panel.StateChanged += () => hits++;

            panel.Float();
            var first = panel.FloatingWindow;
            panel.Float();                                   // 幂等：已经飘着就什么都不做
            panel.Toggle();                                  // 再点 = 收回
            panel.Toggle();                                  // 再点 = 拆出去

            Assert.NotNull(first);
            Assert.NotSame(first, panel.FloatingWindow);     // 收回后再拆是新窗口（旧的已关）
            Assert.Equal(3, hits);                           // 拆、收、拆各报一次
            Assert.True(panel.IsDetached);
            panel.Dock();                                    // 收尾关窗（同上：不留还开着的 HWND）
            return true;
        });
    }

    [Fact]
    public void 主窗还没上屏时拆窗不抛而是当没有主窗()
    {
        OnSta(() =>
        {
            // WPF 不许把浮动窗的 Owner 设给一个从未 Show 过的主窗（当场抛 InvalidOperationException，本轮单测真撞上过）。
            // 正常流程里主窗一定先上屏才可能拆窗，所以这不是“为了测试绕一下”，而是把那条隐含前提变成代码护栏。
            var (home, tabs) = Pair();
            var owner = new Window { Content = home };
            var panel = new DetachablePanel(home, tabs, "测试");
            // 先证「主窗拿得到」，否则下面那句 Owner==null 是白验的（拿不到主窗时根本走不到护栏那一步）。
            Assert.Same(owner, Window.GetWindow(home));

            panel.Float();

            Assert.True(panel.IsDetached);
            Assert.Null(panel.FloatingWindow!.Owner);         // 不认那个没上屏的主窗，但窗照常拆出来
            panel.Dock();
            Assert.Same(tabs, home.Content);
            return true;
        });
    }

    [Fact]
    public void 搬的是同一个实例所以页签选中项与会话不丢()
    {
        OnSta(() =>
        {
            var (home, tabs) = Pair();
            var panel = new DetachablePanel(home, tabs, "测试");
            tabs.SelectedIndex = 1;                          // 用户正停在「整版拼版」那一页

            panel.Float();
            Assert.Same(tabs, panel.FloatingWindow!.Content);
            panel.Dock();

            Assert.Same(tabs, home.Content);
            Assert.Equal(1, tabs.SelectedIndex);             // 复制一份就会在这里变成 0
            return true;
        });
    }
}
