using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using LabelGou.Core;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 89 棒：③/④ 步预览这一块的三件（用户 2026-09-20 一张圈了红框的图 + 三条口径）。
/// <para>① 选模板要立刻在右侧看到那张标签长什么样——他圈的那片是<strong>空的</strong>。查出来不是"没做预览"，
/// 而是「行检查」开着又还没导数据时 <c>RebuildLayout</c> 拿不到那一行，直接把整块预览清成 null；
/// ② 行检查开着时 ←/→ 就是上一张/下一张，再加一颗「幻灯片检查」逐行自动过；
/// ③ 「全部行」那一屏要能放大——一行摆几格由他定，格数越少图越大。</para>
/// <para>两条老规矩照旧钉着：预览怎么看不许动出片的张数；快捷键不许把文本框、下拉、表格里的箭头抢走。</para>
/// </summary>
public sealed class RowCheckPagingAndSlideshowTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-b89");

    /// <summary>MainViewModel 会碰 WPF 类型，必须在 STA 线程上造。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>三行货，够翻页与幻灯片走几拍。</summary>
    private string WriteCsv()
    {
        var path = Path.Combine(_dir, "三行表.csv");
        var text = string.Join(",", "货号 ITEM NO:", "件数 CTN") + Environment.NewLine +
                   "olu830-35,1" + Environment.NewLine +
                   "olu830-70,1" + Environment.NewLine +
                   "olu830-99,1" + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private MainViewModel Fresh() => new(TestEnvironment.NewTempUiStateStore()) { Mode = RunMode.Offline };

    private MainViewModel LoadInto()
    {
        var vm = Fresh();
        vm.LoadSource(WriteCsv(), null);
        return vm;
    }

    // ---------- 件①：③ 步选模板，右侧那块预览不能是空的 ----------

    [Fact]
    public void 行检查开着但还没导数据_预览画的是选中模板的示意版式()
    {
        var result = OnSta(() =>
        {
            var vm = Fresh();
            vm.RowCheck = true;                     // 他图上那颗勾就是开着的
            var option = vm.SelectedTemplate!;
            return (Has: vm.CurrentLayout is not null,
                W: vm.CurrentLayout?.WidthMm ?? 0, H: vm.CurrentLayout?.HeightMm ?? 0,
                TW: option.Template.WidthMm, TH: option.Template.HeightMm,
                Info: vm.RecordInfoText);
        });

        Assert.True(result.Has, "行检查开着又没数据时预览整块空白——用户圈的那片红框就是这个");
        Assert.Equal(result.TW, result.W);          // 画的就是他选中的那份模板
        Assert.Equal(result.TH, result.H);
        Assert.Contains("示意预览", result.Info);    // 并且说实话：这是示意，不是哪一行
    }

    [Fact]
    public void 行检查开着换模板_预览立刻跟着换成新版式()
    {
        var result = OnSta(() =>
        {
            var vm = Fresh();
            vm.RowCheck = true;
            var first = vm.SelectedTemplate!;
            // 内置模板里 140×100 与 160×120 都有：换一份尺寸不同的，画布必须跟着换
            var other = vm.TemplateOptions.First(o => o.Template.WidthMm != first.Template.WidthMm
                                                   || o.Template.HeightMm != first.Template.HeightMm);
            vm.SelectedTemplate = other;
            return (OldW: first.Template.WidthMm, NewW: other.Template.WidthMm, NewH: other.Template.HeightMm,
                LayoutW: vm.CurrentLayout?.WidthMm ?? 0, LayoutH: vm.CurrentLayout?.HeightMm ?? 0);
        });

        Assert.NotEqual(result.OldW, result.NewW);
        Assert.Equal(result.NewW, result.LayoutW);
        Assert.Equal(result.NewH, result.LayoutH);
    }

    [Fact]
    public void 导了数据行检查照旧画那一行的真值_不会被示意版式顶掉()
    {
        // 件①那条兜底只在"没有这一行"时兜；有数据时画的必须还是真数据，否则核对就是假的。
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.RowCheck = true;
            vm.CurrentIndex = 2;
            return (RowIndex: vm.CurrentLayout!.RecordRowIndex, Info: vm.RecordInfoText);
        });

        Assert.True(result.RowIndex > 0, "画的又是示意版式？行检查开着有数据时必须画真那一行");
        Assert.Contains("olu830-70", result.Info);
    }

    // ---------- 件②：行检查开着，←/→ 就是上一张/下一张 ----------

    [Fact]
    public void 行检查开着_箭头翻页生效_一览摊开时不接管()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.RowCheck = true;
            vm.CurrentIndex = 1;
            var right = PreviewArrows.TryPage(vm, null, Key.Right);
            var afterRight = vm.CurrentIndex;       // 当场取：放到 return 的元组里取的是"翻完两下之后"的数
            var left = PreviewArrows.TryPage(vm, null, Key.Left);
            var afterLeft = vm.CurrentIndex;
            vm.RowCheckAll = true;                      // 全部行摊开：满屏都是，没有"下一张"可翻
            var inGrid = PreviewArrows.TryPage(vm, null, Key.Right);
            return (Right: right, IndexAfterRight: afterRight, Left: left, IndexAfterLeft: afterLeft,
                InGrid: inGrid, GridIndex: vm.CurrentIndex);
        });

        Assert.True(result.Right);
        Assert.Equal(2, result.IndexAfterRight);
        Assert.True(result.Left);
        Assert.Equal(1, result.IndexAfterLeft);
        Assert.False(result.InGrid);
        Assert.Equal(1, result.GridIndex);
    }

    [Fact]
    public void 箭头让开文本框下拉与表格_但不让开步骤条()
    {
        OnSta(() =>
        {
            var vm = LoadInto();
            vm.RowCheck = true;
            vm.CurrentIndex = 1;
            // 这几处箭头另有本职：光标、换选项、单元格移动。抢了它们就是"打字翻不了页、翻页打不了字"。
            Assert.False(PreviewArrows.TryPage(vm, new TextBox(), Key.Right));
            Assert.False(PreviewArrows.TryPage(vm, new PasswordBox(), Key.Right));
            Assert.False(PreviewArrows.TryPage(vm, new ComboBox(), Key.Right));
            Assert.False(PreviewArrows.TryPage(vm, new DataGrid(), Key.Right));
            Assert.Equal(1, vm.CurrentIndex);
            // 顶部步骤条是 ListBox，而用户点完步骤后焦点就落在那儿——让开它这条快捷键等于永远不生效。
            Assert.True(PreviewArrows.TryPage(vm, new ListBox(), Key.Right));
            Assert.Equal(2, vm.CurrentIndex);
            // 行检查关着时不许接管：那时逐张翻，左右键仍归原来的控件。
            vm.RowCheck = false;
            Assert.False(PreviewArrows.TryPage(vm, null, Key.Right));
            return true;
        });
    }

    [Fact]
    public void 幻灯片检查_自动开行检查并从第一行逐张走到末行就停()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            Assert.False(vm.RowCheck);
            vm.ToggleSlideshow();
            var start = (RowCheck: vm.RowCheck, Playing: vm.SlideshowPlaying,
                Index: vm.CurrentIndex, Label: vm.SlideshowButtonLabel);
            vm.SlideshowTick();
            var mid = (Index: vm.CurrentIndex, Playing: vm.SlideshowPlaying);
            vm.SlideshowTick();                 // 走到第 3 行（共 3 行）
            var last = (Index: vm.CurrentIndex, Playing: vm.SlideshowPlaying);
            vm.SlideshowTick();                 // 再一拍就该自己停下
            return (start, mid, last,
                After: (Index: vm.CurrentIndex, Playing: vm.SlideshowPlaying, Label: vm.SlideshowButtonLabel));
        });

        Assert.True(result.start.RowCheck);                     // 幻灯片就是"一行一张地过"
        Assert.True(result.start.Playing);
        Assert.Equal(1, result.start.Index);
        Assert.Contains("停止", result.start.Label);            // 正在放的时候那颗要写"停止"
        Assert.Equal(2, result.mid.Index);
        Assert.True(result.mid.Playing);
        Assert.Equal(3, result.last.Index);
        // 走到最后一行的那一拍就得停：等下一拍才停的话，他站在最后一行时那颗按钮还写着"停止"，
        // 状态栏也还挂着"正在放"——差一拍也是骗人。
        Assert.False(result.last.Playing);
        Assert.Equal(3, result.After.Index);                    // 不越界、不绕回第一行
        Assert.False(result.After.Playing);                     // 放到最后一行自己停，不许绕回去糊弄
        Assert.Contains("幻灯片", result.After.Label);
    }

    [Fact]
    public void 手动翻页就停下幻灯片_不跟用户抢方向盘()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.ToggleSlideshow();
            vm.NextRecordCommand.Execute(null);
            var byButton = vm.SlideshowPlaying;
            vm.ToggleSlideshow();                               // 再放
            PreviewArrows.TryPage(vm, null, Key.Right);         // 用箭头手动翻
            return (byButton, vm.SlideshowPlaying, vm.RecordInfoText);
        });

        Assert.False(result.byButton);
        Assert.False(result.SlideshowPlaying);
    }

    [Fact]
    public void 幻灯片间隔可调_越界夹住_填错数字退回原值()
    {
        var result = OnSta(() =>
        {
            var vm = Fresh();
            var defaults = (Seconds: vm.SlideshowSeconds, Text: vm.SlideshowIntervalText);
            vm.SlideshowSeconds = 99;
            var tooSlow = vm.SlideshowSeconds;
            vm.SlideshowSeconds = 0.01;
            var tooFast = vm.SlideshowSeconds;
            vm.SlideshowSeconds = 4;
            var four = vm.SlideshowSeconds;         // 当场取，理由同上（后面还要改两次）
            vm.SlideshowIntervalText = "2.5";
            var typed = vm.SlideshowSeconds;
            vm.SlideshowIntervalText = "乱填";
            return (defaults, tooSlow, tooFast, Four: four, typed, BadText: vm.SlideshowIntervalText);
        });

        Assert.Equal(2, result.defaults.Seconds);               // 默认每 2 秒一张
        Assert.Equal("2", result.defaults.Text);
        Assert.Equal(10, result.tooSlow);                       // 上限：再慢就不是检查是打瞌睡
        Assert.Equal(0.5, result.tooFast);
        Assert.Equal(4, result.Four);
        Assert.Equal(2.5, result.typed);
        Assert.Equal("2.5", result.BadText);                    // 填不进就不改，文本框要弹回原值
    }

    [Fact]
    public void 关掉行检查时幻灯片跟着停()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.ToggleSlideshow();
            vm.RowCheck = false;
            return (vm.SlideshowPlaying, vm.RecordInfoText);
        });

        Assert.False(result.SlideshowPlaying);
    }

    [Fact]
    public void 幻灯片与箭头都不改出片的张数()
    {
        // 这一条是本棒唯一危险的口子：预览翻到哪一行都不许动交给打印/导出的那份标签集。
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var before = vm.CreatePageSource()?.LabelCount;
            vm.ToggleSlideshow();
            vm.SlideshowTick();
            PreviewArrows.TryPage(vm, null, Key.Right);
            var during = vm.CreatePageSource()?.LabelCount;
            vm.RowCheck = false;
            return (before, during, vm.CreatePageSource()?.LabelCount);
        });

        Assert.Equal(3, result.before);
        Assert.Equal(3, result.during);
        Assert.Equal(3, result.Item3);
    }

    // ---------- 件③：全部行那一屏，一行摆几格他说了算 ----------

    [Fact]
    public void 一览每行格数可调_改小就是把缩略放大()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.SetPreviewViewport(1528);
            var ten = (Cells: vm.RowThumbCellsPerRow, Width: vm.RowThumbItemWidth);
            vm.RowThumbCellsPerRow = 4;                 // 一行四格 = 每格比十格大一倍半
            var four = vm.RowThumbItemWidth;
            vm.RowThumbCellsText = "2";                 // 直接填数字这条路必须留着
            var two = (Cells: vm.RowThumbCellsPerRow, Width: vm.RowThumbItemWidth);
            vm.RowThumbCellsPerRow = 0;
            var clampedLow = vm.RowThumbCellsPerRow;
            vm.RowThumbCellsPerRow = 99;
            var clampedHigh = (Cells: vm.RowThumbCellsPerRow, Text: vm.RowThumbCellsText);
            return (ten, four, two, clampedLow, clampedHigh);
        });

        Assert.Equal(10, result.ten.Cells);             // 默认仍是十格一排（第 70 棒那条口径不动）
        Assert.Equal(148.4, result.ten.Width, 1);
        Assert.Equal(371, result.four, 1);
        Assert.Equal(2, result.two.Cells);
        Assert.Equal(742, result.two.Width, 1);
        Assert.True(result.four > result.ten.Width, "格数改小，每格必须变大");
        Assert.Equal(1, result.clampedLow);
        Assert.Equal(10, result.clampedHigh.Cells);
        Assert.Equal("10", result.clampedHigh.Text);    // 填 99 夹回 10，框里也得跟着弹回来
    }

    [Fact]
    public void 一览格数与幻灯片那颗都在XAML里_说明文案不许还写着固定十格()
    {
        var xaml = RepoFile("src", "LabelGou.App", "MainWindow.xaml");

        Assert.Contains("幻灯片检查", xaml);
        Assert.Contains("{Binding SlideshowButtonLabel}", xaml);
        Assert.Contains("{Binding ToggleSlideshowCommand}", xaml);
        Assert.Contains("RowThumbCellsText", xaml);                       // 填数字那条路
        Assert.Contains("RowThumbCellsFewerCommand", xaml);
        Assert.Contains("RowThumbCellsMoreCommand", xaml);
        Assert.Contains("每行格数", xaml);
        // 老文案写死"一行摆 10 格"，现在这数归他定，说明得跟着改口（否则又是一处"界面与说法不符"）
        Assert.DoesNotContain("一行摆 10 格", xaml);
        Assert.Contains("← / →", xaml);                                   // 快捷键要写在脸上，不许让人猜

        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");
        var arrows = code.IndexOf("PreviewArrows.TryPage", StringComparison.Ordinal);
        var altArrows = code.IndexOf("Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)", StringComparison.Ordinal);
        Assert.True(arrows > 0, "窗口没接这条箭头快捷键");
        Assert.True(altArrows > 0, "Alt+←/→ 那条老快捷键被删了？两条要并存");
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
