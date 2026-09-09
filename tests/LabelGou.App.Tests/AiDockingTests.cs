using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.Core.Docking;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「拖到右侧可以吸附」在对象层的那几笔硬账（M7 第 17 棒第二版）。
/// <para>用户 2026-09-09 第三次纠正：「把这个 AI 窗口长按拖动可以拆下来，然后拉到右侧可以吸附」。
/// 手势本身（按下-拖动-松手）在单测里造不出来（§五-70），但吸附的<strong>后果</strong>全是对象状态：
/// 内容从底部宿主搬到右栏宿主、任何时刻只有一个宿主持有它、吸不到就原地不动。
/// 这几条一旦写歪，界面上表现为「AI 消失」或「出现两份 AI」，都是不可回退的错。</para>
/// <para>判据（哪一下算吸附）在 <c>DockSnapTests</c>；这里钉的是搬移与持久化。</para>
/// <para><strong>为什么与 <c>DetachablePanelTests</c> 同一个集合</strong>（第 18 棒实测）：这两个类会真 <c>Show()</c> 出顶层窗口，
/// xUnit 默认按集合并行 → 两个 STA 线程同时开/关 HWND 会把测试主机进程跑崩（单独跑 AiDockingTests 不崩，
/// 与 DetachablePanelTests 一起跑必崩）。排到同一集合 = 这两类串行，别的类照常并行。</para>
/// </summary>
[Collection(WpfWindowCollection)]
public class AiDockingTests
{
    /// <summary>凡会真开出顶层窗口的测试类都使用这个集合名（见 <see cref="DetachablePanelTests"/>）。</summary>
    public const string WpfWindowCollection = "真开窗口的测试不并行";

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src"))) dir = Path.GetDirectoryName(dir);
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, Path.Combine(parts));
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到源文件 {path}");
        return File.ReadAllText(path);
    }

    private static (ContentControl Bottom, ContentControl Right, TabControl Body) TwoSites()
    {
        // 拿 TabControl 当被搬的对象，只为了验「搬的是同一个实例」（它带选中项这种状态）。
        var body = new TabControl();
        body.Items.Add(new TabItem { Header = "单标签" });
        body.Items.Add(new TabItem { Header = "整版拼版" });
        return (new ContentControl { Content = body }, new ContentControl(), body);
    }

    [Fact]
    public void 登记右栏泊位不会让同一块内容有两个宿主()
    {
        OnSta(() =>
        {
            var (bottom, right, body) = TwoSites();
            var panel = new DetachablePanel(bottom, body, "测试");
            panel.AddDockSite(DockSite.Right, right);

            Assert.Same(body, bottom.Content);      // 现在停在底部
            Assert.Null(right.Content);             // 右栏只是登记，不许顺手也拿一份
            return true;
        });
    }

    [Fact]
    public void 同一个泊位登记两次当场拒而不是留两块家()
    {
        OnSta(() =>
        {
            var (bottom, _, body) = TwoSites();
            var panel = new DetachablePanel(bottom, body, "测试");
            Assert.Throws<InvalidOperationException>(() => panel.AddDockSite(DockSite.Bottom, new ContentControl()));
            Assert.Throws<ArgumentException>(() => panel.AddDockSite(DockSite.Float, new ContentControl()));
            return true;
        });
    }

    [Fact]
    public void 吸到右栏后底部那一格必须空着()
    {
        OnSta(() =>
        {
            var (bottom, right, body) = TwoSites();
            var panel = new DetachablePanel(bottom, body, "测试").AddDockSite(DockSite.Right, right);

            Assert.True(panel.Dock(DockSite.Right));

            Assert.Equal(DockSite.Right, panel.Site);
            Assert.Same(body, right.Content);
            Assert.Null(bottom.Content);            // 两处同时指一块内容 = 谁也不知道现在显示的是哪一个
            return true;
        });
    }

    [Fact]
    public void 吸到没登记的泊位时原地不动而不是被丢到别处()
    {
        OnSta(() =>
        {
            var (bottom, _, body) = TwoSites();
            var panel = new DetachablePanel(bottom, body, "测试");     // 只有底部这一个家

            Assert.False(panel.Dock(DockSite.Right));

            Assert.Equal(DockSite.Bottom, panel.Site);
            Assert.Same(body, bottom.Content);      // 不静默换地方：换地方等于替他做了选择
            return true;
        });
    }

    [Fact]
    public void 从右栏拆出去后关掉窗口收回右栏而不是底部()
    {
        OnSta(() =>
        {
            var (bottom, right, body) = TwoSites();
            var panel = new DetachablePanel(bottom, body, "测试").AddDockSite(DockSite.Right, right);
            panel.Dock(DockSite.Right);
            panel.Float();
            Assert.True(panel.IsDetached);

            panel.FloatingWindow!.Close();          // 用户点窗口右上角的 ×

            Assert.False(panel.IsDetached);
            Assert.Equal(DockSite.Right, panel.Site);
            Assert.Same(body, right.Content);
            Assert.Null(bottom.Content);
            return true;
        });
    }

    [Fact]
    public void 按住拖出去时窗口落在手给的位置而不是默认摆位()
    {
        OnSta(() =>
        {
            var (bottom, _, body) = TwoSites();
            var panel = new DetachablePanel(bottom, body, "测试");

            panel.FloatAt((321, 345));              // 拖拽控制器算出来的那一格（光标 − 抓握偏移）

            var g = panel.FloatingGeometry;
            Assert.NotNull(g);
            Assert.Equal(321d, g!.Value.Left, 3);
            Assert.Equal(345d, g.Value.Top, 3);
            DockableCleanup(panel);                        // 收尾关窗：不留还活着的 HWND 给已死的线程
            return true;
        });
    }

    [Fact]
    public void 两个泊位之间来回搬拿回来的还是同一份会话()
    {
        OnSta(() =>
        {
            var (bottom, right, body) = TwoSites();
            var panel = new DetachablePanel(bottom, body, "测试").AddDockSite(DockSite.Right, right);
            body.SelectedIndex = 1;                 // 用户正停在「整版拼版」那一页

            panel.Dock(DockSite.Right);
            panel.Float();
            panel.Dock(DockSite.Bottom);
            panel.Dock(DockSite.Right);

            Assert.Same(body, right.Content);
            Assert.Equal(1, body.SelectedIndex);    // 复制一份就会在这里变成 0
            return true;
        });
    }

    [Fact]
    public void 握把真的挂在面板上而且就在第零行()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel();
            var grid = Assert.IsType<Grid>(panel.Content);
            var grip = panel.DragGrip;
            return (inTree: grid.Children.Cast<UIElement>().Contains(grip),
                    row: Grid.GetRow(grip),
                    movesCursor: grip.Cursor is not null,
                    explains: (grip.Child as StackPanel)?.Children.OfType<TextBlock>().Count() ?? 0);
        });

        Assert.True(probe.inTree);                  // 造出来却没挂上树 = 界面上根本没有那个把手
        Assert.Equal(0, probe.row);                 // 挂在最上面，拆出去之后也还是第一样能抓的东西
        Assert.True(probe.movesCursor);             // 十字光标：不用读说明也知道这里能拖
        Assert.Equal(2, probe.explains);            // 「≡ AI 助手」+ 一句怎么用
    }

    [Fact]
    public void 主窗口把两个泊位与拖拽都接好了只有一套搬移实现()
    {
        var xaml = RepoFile("src", "LabelGou.App", "MainWindow.xaml");
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        Assert.Contains("x:Name=\"AiHostRight\"", xaml);          // 右栏那个宿主在
        Assert.Contains("x:Name=\"DockPreviewRight\"", xaml);     // 两块落点预览在
        Assert.Contains("x:Name=\"DockPreviewBottom\"", xaml);
        Assert.Contains("<ColumnDefinition x:Name=\"AiRightCol\" Width=\"0\" />", xaml);   // 平时宽 0 = 旧布局一格没动
        Assert.Contains("Click=\"OnAiDockBottomClick\"", xaml);   // 右栏里那个「回底部」入口
        Assert.Contains("_aiPanel.AddDockSite(DockSite.Right, AiHostRight)", code);
        Assert.Contains("new Services.PanelDragController(this, _aiPanel, ai.DragGrip", code);
        // 反向：搬移只有一套实现，拖拽与按钮都指它
        Assert.Equal(1, code.Split("new Services.DetachablePanel(").Length - 1);
    }

    [Fact]
    public void 上次停在哪个泊位与右栏宽度都存得回来()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        var vm = new ViewModels.MainViewModel(store);

        vm.SaveAiDock(DockSite.Right, 500);

        var back = new ViewModels.MainViewModel(store).LoadAiDock();
        Assert.Equal(DockSite.Right, back.Site);
        Assert.Equal(500d, back.RightWidth, 3);
    }

    [Fact]
    public void 浮动不写盘而认不出的旧状态退回右栏()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        var vm = new ViewModels.MainViewModel(store);

        vm.SaveAiDock(DockSite.Right, 460);
        vm.SaveAiDock(DockSite.Float, 460);         // 关软件时 AI 正飘着：不该下次启动多开一个窗
        Assert.Equal(DockSite.Right, vm.LoadAiDock().Site);

        var state = store.Load();
        state.AiDockSite = "Left";                  // 根本没有的泊位（旧文件/手改/新版本）
        store.Save(state);
        // 第 19 棒：认不出时退回的是右栏（用户：默认打开软件「右栏是 AI」），不再是底部那一行
        Assert.Equal(DockSite.Right, new ViewModels.MainViewModel(store).LoadAiDock().Site);
    }

    [Fact]
    public void 没记过状态时停在右栏且右栏宽度为零()
    {
        var loaded = new ViewModels.MainViewModel(TestEnvironment.NewTempUiStateStore()).LoadAiDock();

        // 默认就是产品：他 2026-09-09 要的三栏布局是「左向导 / 中预览 / 右 AI」，
        // 旧默认（底部那一行）正好把 AI 放在他说不想要的那个位置。
        Assert.Equal(DockSite.Right, loaded.Site);
        Assert.Equal(0d, loaded.RightWidth);        // 0 = 没记过，界面上会退回 DockSnap 的默认档
    }

    // ===== 第 18 棒：拖「标题条」也要吸得上 =====
    // 用户真拖以后反馈「拼不到右边」（原件：录屏 10-36-58.MP4 第 32 帧，窗已挂到屏右缘外）。
    // 根因：他拆下来之后是抓浮动窗自己的标题条拖的，那一路由操作系统接管，WPF 一个鼠标事件都不报，
    // 于是上一版只靠光标的那套判据永远没人跑。下面这几条钉的是：只看窗的位置 + 物理左键状态就能吸上去。
    // 这也是本机目前唯一能验这条链的口径（合成鼠标输入进不了 WPF，§五-109）。

    /// <summary>一条「主窗 + 一块可拆面板 + 一个拖拽器 + 两块预览」的最小现场。</summary>
    private sealed record RigParts(Window Owner, DetachablePanel Panel, PanelDragController Drag,
        ContentControl Bottom, ContentControl Right, TabControl Body,
        System.Windows.Shapes.Rectangle Pr, System.Windows.Shapes.Rectangle Pb);

    private static RigParts Rig(Func<bool> leftButtonPressed)
    {
        var owner = new Window { Width = 1400, Height = 900, Left = 60, Top = 40, Content = new Grid() };
        var (bottom, right, body) = TwoSites();
        // 把两个泊位真挂到主窗的树上（跟 MainWindow.xaml 里同一个形状）。不挂的话 Window.GetWindow(宿主)
        // 在 FloatAt 里拿不到主窗，“内容真在主窗树里”那一句也就没东西可验（见 LogicalAncestorOf）。
        var root = (Grid)owner.Content;
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(bottom, 1);
        root.Children.Add(bottom);
        root.Children.Add(right);
        // 主窗不上屏：一是本棒要验的那几个数 Measure/Arrange 就能给齐（判据那边拿不到 Actual 时会退到 Width），
        // 二是上一棒在这里 <c>Show()</c> 过一次，收尾没关掉就撞上了「开着窗退出 STA 线程→测试主机当场崩」（见 DockableCleanup）。
        owner.Measure(new Size(1400, 900));
        owner.Arrange(new Rect(0, 0, 1400, 900));
        var panel = new DetachablePanel(bottom, body, "测试").AddDockSite(DockSite.Right, right);
        var pr = new System.Windows.Shapes.Rectangle();
        var pb = new System.Windows.Shapes.Rectangle();
        var drag = new PanelDragController(owner, panel, new Border(), pr, pb,
            () => 900, () => 420, leftButtonPressed);
        return new RigParts(owner, panel, drag, bottom, right, body, pr, pb);
    }

    /// <summary>沿逻辑树往上找（<c>IsDescendantOf</c> 要看过布局才行，而未上屏的窗拿不到真树）。</summary>
    private static bool LogicalAncestorOf(DependencyObject ancestor, DependencyObject child)
    {
        for (object? p = LogicalTreeHelper.GetParent(child); p is not null; p = LogicalTreeHelper.GetParent((DependencyObject)p))
        {
            if (ReferenceEquals(p, ancestor)) return true;
        }
        return false;
    }

    /// <summary>
    /// 每个用这条夹具的测试收尾都必须把窗关掉：STA 线程带着一个还开着的 HWND 退出，那条窗消息会在
    /// <c>HwndSubclass.SubclassWndProc</c> 里撞上 <c>Thread.CurrentThread == null</c>，整个测试主机进程当场崩（本轮实测）。
    /// </summary>
    private static void DockableCleanup(DetachablePanel panel)
    {
        panel.FloatingWindow?.Close();      // 关窗自己就会收回上次那个泊位
        panel.Dock(DockSite.Bottom);        // 兜底：断言中途抛了也不留窗
    }

    /// <summary>在 STA 线程上跑一段夹具代码，并且<strong>无论断言成败都在同一个线程上</strong>把开出去的窗收掉。</summary>
    private static void OnRig(Func<bool> leftButtonPressed, Action<RigParts> body)
        => OnSta(() =>
        {
            var r = Rig(leftButtonPressed);
            try
            {
                body(r);
                return true;
            }
            finally
            {
                DockableCleanup(r.Panel);
            }
        });

    [Fact]
    public void 拆出去后用标题条拖到右缘一松手就吸成右栏()
        => OnRig(() => false, r =>
        {
            r.Panel.FloatAt((200, 200));
            var win = r.Panel.FloatingWindow!;

            // 模拟野生的那一下拖动：直接摆窗的位置（操作系统搬窗就是改这几个数），不碰鼠标。
            // 用 Owner.Width 而不是 ActualWidth：单测里没正经布局过，只有前者是确定的数（判据那边也按这个顺序退）。
            win.Left = r.Owner.Left + r.Owner.Width + 20;
            win.Top = r.Owner.Top + 120;
            r.Drag.PollFloatingTarget();

            Assert.False(r.Panel.IsDetached);              // 真吸上了，不是只画了个预览
            Assert.Equal(DockSite.Right, r.Panel.Site);
            Assert.Same(r.Body, r.Right.Content);
            Assert.True(LogicalAncestorOf(r.Owner, r.Right), "右栏泊位没挂在主窗树上——吸上去的内容会掉进一个界面上看不见的孤儿宿主");
            Assert.Null(r.Bottom.Content);
            Assert.False(win.IsVisible);                   // 浮动窗随之关掉
        });

    [Fact]
    public void 还按着左键时只预告不落_免得拖的路上被抢窗()
        => OnRig(() => true, r =>
        {
            r.Panel.FloatAt((200, 200));                   // 手指还按在标题条上
            var win = r.Panel.FloatingWindow!;
            win.Left = r.Owner.Left + r.Owner.Width - 30;
            win.Top = r.Owner.Top + 120;

            r.Drag.PollFloatingTarget();

            Assert.True(r.Panel.IsDetached);               // 不提前抢：松手才算
            Assert.Equal(DockSite.Right, r.Drag.PendingSite);
            Assert.Equal(Visibility.Visible, r.Pr.Visibility);
            Assert.Contains("松手吸成右栏", win.Title);      // 预览被自己盖住了，所以那句话得写在标题条上
        });

    [Fact]
    public void 飘在中间时轮询什么都不改_不会把用户放好的位置抢走()
        => OnRig(() => false, r =>
        {
            r.Panel.FloatAt((200, 200));
            var win = r.Panel.FloatingWindow!;
            // 故意摆得高一点：那块窗默认 700 高，而主窗高 900，位置稍微往下就真贴上下缘了（那就不该算「中间」）。
            win.Left = r.Owner.Left + 60;
            win.Top = r.Owner.Top + 20;

            r.Drag.PollFloatingTarget();

            Assert.True(r.Panel.IsDetached);
            Assert.Equal(DockSite.Float, r.Drag.PendingSite);
            Assert.Equal(Visibility.Collapsed, r.Pr.Visibility);
            Assert.Equal("测试", win.Title);                 // 没预告就不许在标题上多写字
        });

    [Fact]
    public void 吸上之后标题上那句预告要擦干净()
        => OnRig(() => false, r =>
        {
            r.Panel.FloatAt((200, 200));
            var win = r.Panel.FloatingWindow!;
            win.Left = r.Owner.Left + r.Owner.Width - 30;
            win.Top = r.Owner.Top + 120;
            r.Drag.PollFloatingTarget();

            Assert.False(r.Panel.IsDetached);
            Assert.Equal(Visibility.Collapsed, r.Pr.Visibility);
            Assert.Equal(DockSite.Float, r.Drag.PendingSite);
            // 预告是在关窗之前擦掉的：漏下一步，下次拆出去标题上还挂着上一轮那个箭头。
            Assert.Equal("测试", win.Title);
        });
}
