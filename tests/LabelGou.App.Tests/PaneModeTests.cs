using System;
using System.IO;
using LabelGou.Core.Docking;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 左右两栏的三态（展开 / 收成窄条 / 关闭）在界面那一侧落地的几笔硬账（M7 第 18 棒）。
/// <para>用户 2026-09-09 的原话：「你做到拼得到之后再把只有两边稍微缩一点然后再可以关闭左边或右边」，
/// 并附了一段别人家的界面当形状参考（面板 header 一个收起键 + 屏幕边缘一条竖排窄栏）。</para>
/// <para><see cref="MainWindow"/> 在单测里造不出来（没有 Application 资源，StaticResource 找不到，§五-70/94），
/// 所以这里钉三样：<strong>形态存得回来而且认不出时退回展开</strong>（VM 层，真跑得起来）、
/// <strong>入口与元件都接好了</strong>（源文件里必须有那些名字）、
/// <strong>列宽只有一个写主</strong>（两处写同一列迟早打架）。</para>
/// <para>三态各自的宽度与「按一下走到哪一档」那条规则在 <c>DockSnapTests</c>，这里不重复验算术。</para>
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

        new ViewModels.MainViewModel(store).SavePaneModes(PaneMode.Narrow, PaneMode.Closed);

        var back = new ViewModels.MainViewModel(store).LoadPaneModes();
        Assert.Equal(PaneMode.Narrow, back.Left);
        Assert.Equal(PaneMode.Closed, back.Right);
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
        Assert.Contains("右侧 AI 栏：关闭", xaml);
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
        // 右栏同理：没真撑开时那条分隔条也不许留着占宽
        Assert.Contains("AiRightSplitCol.Width = new GridLength(rightWidth > 0 ? DockSnap.SplitterDip : 0);", code);
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
