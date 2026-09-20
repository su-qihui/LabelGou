using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core;
using LabelGou.Core.Data;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 90 棒②③：① 步导进来之后读的是软件自己那份副本（原件从此不再被碰），
/// 以及打印／导出还在跑就要关窗时先问那一句。
/// <para>②那半边钉三件事：显示与元数据里的文件名不许变（下游全取 <c>GetFileName</c>）、
/// 重开同一张表不许被当成新表（那会把他手工钉过的切法悄悄清回自动）、原件没了这张表照样读得动
/// （证明下游没有一条路回头去开它）。</para>
/// <para>③那半边钉的是文案与接线：主窗口在测试进程里造不出来，所以能问的都收在
/// <see cref="ExportViewModel.ComposeExitDuringJobText"/> 这一句纯函数里，剩下的用源码文本判据。</para>
/// </summary>
public sealed class ImportStagingAndExitGuardTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-b90");

    /// <summary>MainViewModel 会碰 WPF 类型，必须在 STA 线程上造。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private string WriteCsv(string name, int rows = 3)
    {
        var path = Path.Combine(_dir, name);
        var text = string.Join(",", "货号 ITEM NO:", "件数 CTN") + Environment.NewLine;
        for (var i = 0; i < rows; i++) text += $"olu830-{i}," + (i + 1) + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private static MainViewModel Fresh() => new(TestEnvironment.NewTempUiStateStore()) { Mode = RunMode.Offline };

    [Fact]
    public void 导入之后读的是软件自己那份副本_但界面说的还是那个文件名()
    {
        var result = OnSta(() =>
        {
            var vm = Fresh();
            var original = WriteCsv("爆朵唛头 -9.20.csv");
            vm.LoadSource(original, null);
            return (Source: vm.SourcePath!, Original: original, Caption: vm.HeaderInfoText);
        });

        Assert.True(ImportCache.IsCached(result.Source),
            $"① 步之后该读缓存里那份副本，实际读的是 {result.Source}");
        Assert.NotEqual(Path.GetFullPath(result.Original), result.Source);
        Assert.Equal("爆朵唛头 -9.20.csv", Path.GetFileName(result.Source));
        Assert.Contains("爆朵唛头 -9.20.csv", result.Caption);   // 他看到的那句不许变成一串缓存路径
    }

    [Fact]
    public void 重开同一张表不算换表_他手工钉过的列名行不许被清回自动()
    {
        var result = OnSta(() =>
        {
            var vm = Fresh();
            var original = WriteCsv("同一张.csv");
            vm.LoadSource(original, null);
            vm.SelectedHeaderRowOption = vm.HeaderRowOptions.First(o => o.HeaderIndex is not null);
            var pinned = vm.CurrentChoice.HeaderRowIndex;
            var rows = vm.RawRowCount;
            vm.LoadSource(original, null);                       // 走文件对话框那条路：递的是原件路径
            return (pinned, After: vm.CurrentChoice.HeaderRowIndex, Rows: vm.RawRowCount, Same: rows);
        });

        Assert.NotNull(result.pinned);
        Assert.Equal(result.pinned, result.After);
        Assert.Equal(4, result.Rows);         // 一张表头 + 三行货
        Assert.Equal(result.Same, result.Rows);
    }

    [Fact]
    public void 换一张表才叫新表_切法回到自动()
    {
        var result = OnSta(() =>
        {
            var vm = Fresh();
            var first = WriteCsv("第一张.csv");
            vm.LoadSource(first, null);
            vm.SelectedHeaderRowOption = vm.HeaderRowOptions.First(o => o.HeaderIndex is not null);
            var pinned = vm.CurrentChoice.HeaderRowIndex;
            vm.LoadSource(WriteCsv("第二张.csv"), null);
            return (pinned, After: vm.CurrentChoice.HeaderRowIndex);
        });

        Assert.NotNull(result.pinned);
        Assert.Null(result.After);      // 新表就该重新猜，拿上一张的「第 2 行」切新表就是切错行
    }

    [Fact]
    public void 他在WPS里存过盘之后_软件这一轮读的还是进来那一份()
    {
        // 这一条才是暂存真正的理由（比"别锁文件"更要紧）：AI 报的是原表行号。
        // 他在这边切着表、那边 WPS 加了一行，内存里那张表和磁盘上那份就错位了——
        // 行号一错就是剔错行、少印货。读自己那份副本，"中途换表"这件事从根上没有。
        var result = OnSta(() =>
        {
            var vm = Fresh();
            var original = WriteCsv("两边改.csv");
            vm.LoadSource(original, null);
            var before = vm.RawRowCount;
            File.AppendAllText(original, "olu830-新增,9" + Environment.NewLine);
            vm.ReloadSheetCommand.Execute(null);                 // 内部重读：换工作表 / 改切法 / 撤回都走这一条
            var afterReread = vm.RawRowCount;
            vm.LoadSource(original, null);                       // 他从 ① 步重新点一次这个文件 = 明确要拿新的
            return (Before: before, AfterReread: afterReread, AfterReopen: vm.RawRowCount);
        });

        Assert.Equal(4, result.Before);
        Assert.Equal(4, result.AfterReread);                     // 中途不跟着磁盘变
        Assert.Equal(5, result.AfterReopen);                     // 重新点一次才换，而且换得过来
    }

    [Fact]
    public void 原件删掉了_这张表照样重读得动()
    {
        // 这一条是"不再碰原件"的正证：副本在软件手里，下游没有一条路回头去开那个原件。
        var result = OnSta(() =>
        {
            var vm = Fresh();
            var original = WriteCsv("删得掉.csv");
            vm.LoadSource(original, null);
            var before = vm.RawRowCount;
            File.Delete(original);
            vm.ReloadSheetCommand.Execute(null);
            return (Before: before, After: vm.RawRowCount, Caption: vm.HeaderInfoText);
        });

        Assert.Equal(4, result.Before);
        Assert.Equal(4, result.After);
        Assert.Contains("删得掉.csv", result.Caption);
    }

    [Fact]
    public void 输出进行中要退出_那一句问话说清纸收不回来()
    {
        var printing = ExportViewModel.ComposeExitDuringJobText("打印");
        Assert.Contains("正在打印", printing);
        Assert.Contains("已经送进打印机的页会继续打完", printing);
        Assert.Contains("「确定」= 停下这个任务并退出软件", printing);
        Assert.Contains("「取消」= 留下", printing);
        Assert.DoesNotContain("不能退出", printing);           // 不是拦着他，是问一句

        // 任务名没给也要成句，不许露出"正在，现在退出"这种半截话
        Assert.Contains("正在输出", ExportViewModel.ComposeExitDuringJobText(null));
        Assert.Contains("正在导出 PNG", ExportViewModel.ComposeExitDuringJobText("导出 PNG"));
    }

    [Fact]
    public void 确认框接在主窗口关闭那一步_没在输出时不多问一句()
    {
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        var guard = code.IndexOf("protected override void OnClosing", StringComparison.Ordinal);
        var busy = code.IndexOf("_viewModel.Export.IsBusy", StringComparison.Ordinal);
        var cancelClose = code.IndexOf("e.Cancel = true", StringComparison.Ordinal);
        var stopJob = code.IndexOf("_viewModel.Export.Cancel()", StringComparison.Ordinal);
        var raise = code.IndexOf("base.OnClosing(e)", StringComparison.Ordinal);
        Assert.True(guard > 0 && busy > guard, "关闭那一步没问「正在输出」这一态");
        Assert.True(cancelClose > busy && stopJob > cancelClose, "确认之后没去停当前任务");
        // 「取消」这一条路必须压根不 raise Closing：那条 SaveAiDock 只在真要关窗时该写盘
        Assert.True(raise > stopJob, "base.OnClosing 排在了确认之前？那等于留下也把停靠宽度写了盘");
        Assert.Contains("MessageBoxButton.OKCancel", code);      // 他要的就是 确定／取消 这一对

        var app = RepoFile("src", "LabelGou.App", "App.xaml.cs");
        Assert.Contains("ImportCache.Clear()", app);             // 退出清缓存（用户点名的另一半）
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src"))) dir = Path.GetDirectoryName(dir);
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, Path.Combine(parts));
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到源文件 {path}");
        return File.ReadAllText(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
