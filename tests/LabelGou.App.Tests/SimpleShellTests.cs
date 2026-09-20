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
/// 确定性判据全走 <see cref="SimpleShellFlow"/> 静态方法（活件流档位、抽屉开关、面板摘挂），
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

    // ===== 表格抽屉：顶栏「表格」与抽屉右上角「收起」按的是同一个开关 =====

    [Fact]
    public void ToggleTableDrawer_RoundTripsToTheSameState()
    {
        Assert.True(SimpleShellFlow.ToggleTableDrawer(false));
        Assert.False(SimpleShellFlow.ToggleTableDrawer(true));
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
    public void TheShellWindowConstructsWithAnEmptyIslandAndAClosedDrawer()
        => OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            var shell = new SimpleMainWindow(vm);
            Assert.NotNull(shell.IslandHost);              // 岛在，等主窗把面板搬进来
            Assert.Null(shell.IslandHost.Content);         // 没搬之前不许凭空长出一块面板
            Assert.False(shell.DrawerOpen);                // 抽屉默认关着——看表是瞬时动作，不常驻
            return 0;
        });
}
