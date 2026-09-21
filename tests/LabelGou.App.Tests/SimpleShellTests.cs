using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 简洁版壳窗（第 91 棒 · 阶段一第一刀）的判据。
/// <para>窗口在测试进程里造不出完整交互（§五-112 那族），所以判据分两层：
/// 确定性判据全走 <see cref="SimpleShellFlow"/> 静态方法（活件流档位、左右栏开关与占宽、面板摘挂），
/// 壳窗本体只钉「XAML 加载得动 + 初始态诚实」这一条——它已经是本仓最贵的一课（第 88 棒的崩溃回归同款形状）。</para>
/// </summary>
public class SimpleShellTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    // ===== 活件流：五步向导折算成四节点 =====

    [Theory]
    [InlineData(0, 0)]   // ① 导入 = 读表
    [InlineData(1, 1)]   // ② 连字段 = 拍板
    [InlineData(2, 2)]   // ③ 选模板 = 排版
    [InlineData(3, 2)]   // ④ 拼版编号 也算排版（对简洁版用户是同一件事）
    [InlineData(4, 3)]   // ⑤ 核对输出 = 出纸
    [InlineData(-3, 0)]  // 越界先夹住，不许静默吃坏数
    [InlineData(9, 3)]
    public void NodeOfStep_FoldsFiveWizardStepsIntoFourNodes(int step, int expectedNode)
        => Assert.Equal(expectedNode, SimpleShellFlow.NodeOfStep(step));

    [Fact]
    public void StateOf_MarksPassedNodesDoneAndLaterOnesNext()
    {
        Assert.Equal(SimpleShellFlow.NodeState.Done, SimpleShellFlow.StateOf(1, 0));   // 拍板时，读表已过
        Assert.Equal(SimpleShellFlow.NodeState.Now, SimpleShellFlow.StateOf(1, 1));
        Assert.Equal(SimpleShellFlow.NodeState.Next, SimpleShellFlow.StateOf(1, 2));
        Assert.Equal(SimpleShellFlow.NodeState.Next, SimpleShellFlow.StateOf(1, 3));
    }

    [Fact]
    public void StateOf_RejectsNodeNumberThatDoesNotExist()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SimpleShellFlow.StateOf(0, 4));

    // ===== 左右两根栏：顶栏那颗钮与栏里自己的「收起」按的是同一个开关 =====

    [Fact]
    public void TogglePane_RoundTripsToTheSameState()
    {
        Assert.True(SimpleShellFlow.TogglePane(false));
        Assert.False(SimpleShellFlow.TogglePane(true));
    }

    // ===== 第 93/94 棒：栏宽夹取、收起占多少、答题后滚到下一条 =====

    [Theory]
    [InlineData(100, 100, 360, 420)]    // 太小 → 各自的下限（左 360、右 420：右栏再窄 AI 的问题区就被挤没）
    [InlineData(5000, 5000, 820, 900)]  // 太大 → 各自的上限（左 820、右 900）
    [InlineData(640, 640, 640, 640)]    // 区间内原样
    public void ClampPaneWidth_KeepsSizesInsideSaneBands(double left, double right, double wantLeft, double wantRight)
    {
        Assert.Equal(wantLeft, SimpleShellFlow.ClampLeftPaneWidth(left));
        Assert.Equal(wantRight, SimpleShellFlow.ClampRightPaneWidth(right));
    }

    [Fact]
    public void ClampPaneWidth_RejectsBadNumbersInsteadOfStoringThem()
    {
        // NaN/∞ 拖不进状态文件（§五-183 同族）：坏数一律退回默认档
        Assert.Equal(560, SimpleShellFlow.ClampLeftPaneWidth(double.NaN));
        Assert.Equal(440, SimpleShellFlow.ClampRightPaneWidth(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(560, true, 560)]   // 开着 = 占自己那份宽
    [InlineData(560, false, 0)]    // 收起 = 占 0，中间那格（预览）自己补位——用户要的是"左右随时开关，中间主导"
    public void PaneSlotWidth_CollapsedPaneTakesNoRoom(double width, bool open, double expected)
        => Assert.Equal(expected, SimpleShellFlow.PaneSlotWidth(width, open));

    [Theory]
    [InlineData(0, 3, 1)]    // 答完第 1 条 → 把第 2 条滚进视野
    [InlineData(1, 3, 2)]
    [InlineData(2, 3, -1)]   // 最后一条答完 → 不抢方向盘（进第二步，新内容在对话区）
    public void NextQuestionIndex_PointsAtTheOneToAnswerOrHandsBack(int answered, int count, int expected)
        => Assert.Equal(expected, SimpleShellFlow.NextQuestionIndex(answered, count));

    [Fact]
    public void ShellPanes_RoundTripThroughTheStateFile()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        var vm = OnSta(() => new MainViewModel(store));
        // 旧状态文件缺这几格 = 没记过：宽度 0（壳窗退回默认档）、开关 null（左关右开）
        var p0 = vm.LoadShellPanes();
        Assert.Equal((0d, 0d), (p0.LeftWidth, p0.RightWidth));
        Assert.Null(p0.LeftOpen);
        Assert.Null(p0.RightOpen);
        OnSta(() => { vm.SaveShellPanes(500, 620, true, false); return 0; });
        var p1 = vm.LoadShellPanes();
        Assert.Equal((500d, 620d), (p1.LeftWidth, p1.RightWidth));
        Assert.True(p1.LeftOpen);
        Assert.False(p1.RightOpen);
    }

    // ===== AI 面板摘挂：全程只有一个实例，两处宿主不同时指它 =====

    [Fact]
    public void ParkAndTakeBackMoveTheOnePanelWithoutDuplicatingIt()
        => OnSta(() =>
        {
            var content = new Border();
            var home = new ContentControl { Content = content };
            var panel = new DetachablePanel(home, content, "测试面板");
            var island = new ContentControl();

            SimpleShellFlow.Park(panel, island);
            Assert.Same(content, island.Content);          // 岛拿到了
            Assert.Null(home.Content);                     // 家里必须摘干净（两处同时指着就是双面板）

            SimpleShellFlow.TakeBack(panel, island);
            Assert.Same(content, home.Content);            // 回迁
            Assert.Null(island.Content);                   // 岛上那份必须先摘干净——两处同时指着就是双面板
            return 0;
        });

    // ===== 壳窗本体：XAML 加载得动 + 初始态诚实（不 Show，§五-112/113 那族坑绕开） =====

    [Fact]
    public void TheShellWindowConstructsWithAnEmptyRightPaneAndTheLeftPaneClosed()
        => OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            var shell = new SimpleMainWindow(vm);
            Assert.NotNull(shell.IslandHost);              // 右栏宿主在，等主窗把面板搬进来
            Assert.Null(shell.IslandHost.Content);         // 没搬之前不许凭空长出一块面板
            Assert.False(shell.LeftPaneOpen);              // 左栏默认收起——中间预览才是主角（用户 2026-09-21）
            Assert.True(shell.RightPaneOpen);              // 右栏 AI 默认开着（AI 模式第一步就要用它）
            Assert.False(shell.SwitchingToPro);            // 刚造出来的窗不许自认"正在回专业版"，否则关窗会把软件留着
            return 0;
        });

    /// <summary>
    /// 第 92 棒：岛内面板与抽屉表格的换装靠**窗级隐式样式**（字典只合并进壳窗，专业版拿不到）。
    /// 这条钉的是"隐式键真的挂在壳窗资源上、并且岛里的裸控件命中的就是它"——
    /// 写了没挂上，界面上就是用户那句「部分地方仍是旧界面的 UI」。
    /// </summary>
    [Fact]
    public void TheShellWindowCarriesImplicitControlStylesThatRestyleTheHostedPanel()
        => OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            var shell = new SimpleMainWindow(vm);
            foreach (var kind in new[] { typeof(System.Windows.Controls.Button), typeof(System.Windows.Controls.TextBox),
                                         typeof(System.Windows.Controls.ComboBox), typeof(System.Windows.Controls.DataGrid) })
                Assert.NotNull(shell.TryFindResource(kind));   // 隐式键 = 控件类型本身，四类都得在

            // 正向证据：岛里放一颗裸按钮，样式解析必须命中壳窗那份隐式样式（不是主题默认那身灰）
            var probe = new System.Windows.Controls.Button();
            shell.IslandHost.Content = probe;
            Assert.Same(shell.FindResource(typeof(System.Windows.Controls.Button)), probe.Style);
            shell.IslandHost.Content = null;
            return 0;
        });
}
