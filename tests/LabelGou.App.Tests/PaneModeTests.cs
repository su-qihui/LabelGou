using System;
using System.IO;
using LabelGou.Core.Docking;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 左右两栏的形态在界面那一侧落地的几笔硬账（M7 第 18 棒立起来，第 19 棒按用户的话改了形状）。
/// <para>第 18 棒他原话：「你做到拼得到之后再把只有两边稍微缩一点然后再可以关闭左边或右边」，
/// 并附了一段别人家的界面当形状参考（面板 header 一个收起键 + 屏幕边缘一条竖排窄栏）。</para>
/// <para><strong>第 19 棒他否掉了我加的那条规矩</strong>：「我没有说 AI 不许住在一个被收掉的列里?? 就是因为用完可以收起到右侧啊??」
/// ——于是形状变成<strong>左栏三态（展开 / 窄条 / 关闭）、右栏两态（展开 / 窄条）</strong>，
/// 而右栏收窄时 AI 的家留在这一栏里（只是不给看），不再被踢回底部那一行。</para>
/// <para><see cref="MainWindow"/> 在单测里造不出来（没有 Application 资源，StaticResource 找不到，§五-70/94），
/// 所以这里钉三样：<strong>形态存得回来而且认不出时退回展开</strong>（VM 层，真跑得起来）、
/// <strong>入口与元件都接好了</strong>（源文件里必须有那些名字）、
/// <strong>列宽只有一个写主</strong>（两处写同一列迟早打架）。</para>
/// <para>三态各自的宽度、「按一下走到哪一档」、旧状态那份矛盾的化简都在 <c>DockSnapTests</c>，这里不重复验算术。</para>
/// </summary>
public class PaneModeTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src"))) dir = Path.GetDirectoryName(dir);
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, Path.Combine(parts));
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到源文件 {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void 两栏各自停在哪一态存得回来下一次启动照用()
    {
        var store = TestEnvironment.NewTempUiStateStore();

        new ViewModels.MainViewModel(store).SavePaneModes(PaneMode.Closed, PaneMode.Narrow);

        var back = new ViewModels.MainViewModel(store).LoadPaneModes();
        Assert.Equal(PaneMode.Closed, back.Left);       // 左栏（向导）可以整个关掉
        Assert.Equal(PaneMode.Narrow, back.Right);      // 右栏最收就只有窄条：它没有 Closed 那一档
    }

    [Fact]
    public void 没记过状态时两栏都展开()
    {
        var loaded = new ViewModels.MainViewModel(TestEnvironment.NewTempUiStateStore()).LoadPaneModes();

        // 旧状态文件根本没有这两个字段：缺字段必须等于什么都没变，不然一升级界面就自己缩起来了。
        Assert.Equal(PaneMode.Open, loaded.Left);
        Assert.Equal(PaneMode.Open, loaded.Right);
    }

    [Fact]
    public void 状态里是认不出的名字或越界数字时退回展开而不是照用()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        var state = store.Load();
        state.LeftPaneMode = "99";            // Enum.TryParse 对这个也返回 true（越界的枚举值要用时才炸）
        state.RightPaneMode = "Sideways";     // 手改的 / 新版本写的
        store.Save(state);

        var back = new ViewModels.MainViewModel(store).LoadPaneModes();
        Assert.Equal(PaneMode.Open, back.Left);
        Assert.Equal(PaneMode.Open, back.Right);
    }

    [Fact]
    public void 只改一栏时不会把另一栏那一格抹掉()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        var vm = new ViewModels.MainViewModel(store);
        vm.SavePaneModes(PaneMode.Closed, PaneMode.Narrow);

        vm.SavePaneModes(PaneMode.Open, PaneMode.Narrow);       // 只动左栏

        var back = new ViewModels.MainViewModel(store).LoadPaneModes();
        Assert.Equal(PaneMode.Open, back.Left);
        Assert.Equal(PaneMode.Narrow, back.Right);
    }

    [Fact]
    public void 主窗口把三态的元件与入口都接好了()
    {
        var xaml = RepoFile("src", "LabelGou.App", "MainWindow.xaml");
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        // 三样东西各就各位：整块向导（要真藏掉的那一块）、两条窄边、两条被一起收掉的分隔条列
        Assert.Contains("<Grid Grid.Column=\"0\" x:Name=\"WizardBody\">", xaml);
        Assert.Contains("x:Name=\"WizardRail\"", xaml);
        Assert.Contains("x:Name=\"AiRightRail\"", xaml);
        Assert.Contains("<ColumnDefinition x:Name=\"WizardSplitCol\" Width=\"6\" />", xaml);
        // 入口：展开那一态有收起键，菜单里三档都明写（不拿一个按钮让人猜「再按一下走到哪」）
        Assert.Contains("Content=\"收起 «\" Click=\"OnLeftPaneCollapseClick\"", xaml);
        Assert.Contains("Content=\"收窄 «\" Click=\"OnRightPaneCollapseClick\"", xaml);
        Assert.Contains("左侧向导栏：收成窄条", xaml);
        Assert.Contains("左侧向导栏：关闭", xaml);
        // 第 19 棒：右栏只两态（AI 的家不收），菜单里那一项「关闭」得消失，而它得说清收窄后 AI 还在这一栏
        Assert.Contains("右侧 AI 栏：收成窄条（AI 留在右栏，只是不给看）", xaml);
        Assert.DoesNotContain("右侧 AI 栏：关闭", xaml);
        // 底部那一行还在（他选的「只是不再默认」），所以得有一个不靠拖就能回去的入口
        Assert.Contains("AI 助手：放回底部那一行", xaml);
        // 窄条上必须留一个回展开的键，否则收成窄条后就再也出不来了
        Assert.Contains("Click=\"OnLeftPaneExpandClick\"", xaml);
        Assert.Contains("Click=\"OnRightPaneExpandClick\"", xaml);
        Assert.Contains("ApplyPaneModes();", code);
        Assert.Contains("LoadPaneModes()", code);
    }

    [Fact]
    public void 收窄与关闭都得把下限和分隔条一起收掉()
    {
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        // ColumnDefinition.MinWidth 会把列钉住：不抹成 0，Width 设多小都收不动（那就是假收窄）。
        Assert.Contains("WizardCol.MinWidth = leftOpen ? LeftPaneOpenMinWidthDip : 0;", code);
        Assert.Contains("WizardSplitCol.Width = new GridLength(leftOpen ? DockSnap.SplitterDip : 0);", code);
        // 右栏同理：只有真展开着才给那条分隔条（窄条那一档没什么可调的）
        Assert.Contains("AiRightSplitCol.Width = new GridLength(rightOpen ? DockSnap.SplitterDip : 0);", code);
    }

    [Fact]
    public void 右栏收窄时不搬内容而左栏收窄时藏掉整块()
    {
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        // 第 19 棒的主账：收窄右栏只改这一栏的宽与可见性，AI 的家还在这里。
        // 上一版这里是 Dock(DockSite.Bottom)——那条「AI 不许住在一个被收掉的列里」是我自己发明的，被他否了。
        Assert.DoesNotContain("_aiPanel.Dock(DockSite.Bottom);", code);
        Assert.Contains("AiRightBox.Visibility = rightOpen ? Visibility.Visible : Visibility.Collapsed;", code);
        Assert.Contains("DockSnap.CollapseStep(_rightPane, mayClose: false)", code);
        // 而左栏收成窄条时整块向导必须真藏掉（那一栏里除了窄条没别的东西要留）
        Assert.Contains("WizardBody.Visibility = leftOpen ? Visibility.Visible : Visibility.Collapsed;", code);
    }

    [Fact]
    public void AI不在右栏时不留一根没人住的窄条而启动那次不许强制展开()
    {
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        // 搬去底部行/飘在窗外 = 那根窄条没主人了，复位成展开（下次吸回来是开着的）
        Assert.Contains("if (site != DockSite.Right) _rightPane = PaneMode.Open;", code);
        // 但启动那一次不许强制展开：否则「上次收着」这个状态根本存不住（拿上次泊位作凭，null = 启动）
        Assert.Contains("_lastAiSite is { } prev && prev != DockSite.Right", code);
    }

    [Fact]
    public void 默认泊位是右栏而不是底部那一行()
    {
        var vm = RepoFile("src", "LabelGou.App", "ViewModels", "MainViewModel.cs");
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        // 用户 2026-09-09：默认打开软件是「左向导 / 中预览 / 右 AI」。没记过与认不出都走右栏，
        // 而关窗时那个「上次停在哪个泊位」的兜底值也是右栏（两处都是默认，都得钉）。
        Assert.Contains(": DockSite.Right;", vm);
        Assert.DoesNotContain(": DockSite.Bottom;", vm);
        Assert.Contains("?? DockSite.Right", code);
        // 旧状态文件里那句自相矛盾的「AI 在底部 + 右栏窄条」由 Core 化简，而它真接在启动路径上
        Assert.Contains("DockSnap.ReconcileRightPane(recorded.Site, _rightPane)", code);
    }

    [Fact]
    public void 两列的列宽各只有一个写主()
    {
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        // 两处写同一列 = 谁后跑谁赢，界面上就是一栏忽宽忽窄（本棒把右栏那段从 SyncAiPanelState 搬进 ApplyPaneModes 就是这个原因）。
        Assert.Equal(1, code.Split("AiRightCol.Width = new GridLength(").Length - 1);
        Assert.Equal(1, code.Split("WizardCol.Width = new GridLength(").Length - 1);
    }

    [Fact]
    public void 右栏没收展时那个宽度不许被当成记忆存走()
    {
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        // 不收口：窄条那一档的 44 会被存成「上次右栏的宽度」，下次展开就开成一根柱子。
        Assert.Contains("RightColumnWidthWorthRemembering()", code);
        Assert.DoesNotContain("AiRightCol.ActualWidth > 0 ? AiRightCol.ActualWidth", code);
    }
}
