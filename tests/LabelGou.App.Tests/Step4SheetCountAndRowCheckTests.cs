using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core;
using LabelGou.Core.Marks;
using LabelGou.Core.Numbering;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 69 棒：④ 步那两段（一行打几张纸 / 件号怎么编）与单标签页的「行检查 + 全部行」。
/// <para>用户 2026-09-14：「第四部分这个写的挺乱的，我都看不懂了。改成张数就好了，默认是一张，
/// 也可以绑定 x 列……中间那栏就是预览添加一个模块（行检查）就把张数缩略了，还有开关（折叠）
/// 可以全部检查和单个检查，你提到的标题也加入」。</para>
/// <para>两条不能破的：① 张数与件号只是把原来那个三档下拉拆成两档看，<strong>引擎真源仍是 Mode</strong>；
/// ② 行检查只改预览怎么看，<strong>出片的张数一根手指都不许动</strong>（那条判据在最后一组）。</para>
/// </summary>
public sealed class Step4SheetCountAndRowCheckTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-b69");

    /// <summary>MainViewModel 会碰 WPF 类型，必须在 STA 线程上造。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>件数都写 1，另开一列「打印张数」写 5 / 3 —— 只有那一列能分出"一行几张纸"。</summary>
    private string WriteCsv()
    {
        var path = Path.Combine(_dir, "张数表.csv");
        var text = string.Join(",", "货号 ITEM NO:", "件数 CTN", "打印张数") + Environment.NewLine +
                   "olu830-35,1,5" + Environment.NewLine +
                   "olu830-70,1,3" + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private MainViewModel LoadInto()
    {
        // 离线模式：导入即自动连线，货号才有值（AI 模式故意先不绑，那时标题会老实写「（无货号）」）。
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore()) { Mode = RunMode.Offline };
        vm.LoadSource(WriteCsv(), null);
        return vm;
    }

    private static ChoiceOption<string> CountOption(MainViewModel vm, string value)
        => vm.Sheet.SheetCountOptions.First(o => o.Value == value);

    // ---------- ④ 步：张数与件号拆成两档 ----------

    [Fact]
    public void 张数默认一张_选按列才切到展开档_切回一张就回原档()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var start = vm.Sheet.SelectedSheetCount?.Value;
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountByColumn);
            var expanded = (Mode: vm.Sheet.Mode, StyleEnabled: vm.Sheet.IsNumberStyleEnabled, Shown: vm.Sheet.SelectedNumberStyle?.Value);
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountOne);
            return (start, expanded, Back: vm.Sheet.Mode);
        });

        Assert.Equal(ImpositionViewModel.SheetCountOne, result.start);           // 默认一张
        Assert.Equal(NumberingMode.ExpandByCartonTotal, result.expanded.Mode);    // 按列 = 展开档
        Assert.False(result.expanded.StyleEnabled);                               // 展开时件号那颗锁住
        Assert.Equal(NumberingMode.ForceSequence, result.expanded.Shown);         // 并如实显示强制重排
        Assert.Equal(NumberingMode.KeepData, result.Back);                        // 切回来仍是他原来那档
    }

    [Fact]
    public void 件号选了强制重排_按列展开后再切回一张_还是强制重排()
    {
        var mode = OnSta(() =>
        {
            var vm = LoadInto();
            vm.Sheet.NumberStyle = NumberingMode.ForceSequence;
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountByColumn);
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountOne);
            return vm.Sheet.Mode;
        });

        Assert.Equal(NumberingMode.ForceSequence, mode);
    }

    [Fact]
    public void AI点名张数列_张数那一档要显示成按列()
    {
        // 第 40 棒那类病：摘要里说接上了、界面那档却还停在「1 张」，用户看不出软件到底按什么算。
        var (mode, shown) = OnSta(() =>
        {
            var vm = LoadInto();
            vm.Sheet.ApplyQtyColumn("打印张数");
            return (vm.Sheet.Mode, vm.Sheet.SelectedSheetCount?.Value);
        });

        Assert.Equal(NumberingMode.ExpandByCartonTotal, mode);
        Assert.Equal(ImpositionViewModel.SheetCountByColumn, shown);
    }

    // ---------- 单标签：行检查 + 全部行 ----------

    [Fact]
    public void 行检查_一行五张纸只算一行_标题写本行五张()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountByColumn);
            vm.Sheet.SelectedExpandColumn = vm.Sheet.ExpandColumnOptions.First(o => o.Value == "打印张数");
            var labels = vm.Sheet.Labels.Count;
            vm.RowCheck = true;
            return (labels, vm.RecordTotal, vm.RecordInfoText);
        });

        Assert.Equal(8, result.labels);                 // 5 + 3
        Assert.Equal(2, result.RecordTotal);            // 行检查下按行走
        Assert.Contains("第 1 / 2 行", result.RecordInfoText);
        Assert.Contains("olu830-35", result.RecordInfoText);
        Assert.Contains("本行 5 张", result.RecordInfoText);
    }

    [Fact]
    public void 行检查翻页按行走_关掉回到逐张()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountByColumn);
            vm.Sheet.SelectedExpandColumn = vm.Sheet.ExpandColumnOptions.First(o => o.Value == "打印张数");
            vm.RowCheck = true;
            vm.NextRecordCommand.Execute(null);
            vm.NextRecordCommand.Execute(null);         // 越界要夹在最后一行，不是夹在第八张
            var lastRow = (Index: vm.CurrentIndex, Info: vm.RecordInfoText);
            vm.RowCheck = false;
            vm.LastRecordCommand.Execute(null);
            return (LastRow: lastRow, PerSheetIndex: vm.CurrentIndex, PerSheetTotal: vm.RecordTotal);
        });

        Assert.Equal(2, result.LastRow.Index);                                    // 夹在最后一行，不是夹在第八张
        Assert.Contains("olu830-70", result.LastRow.Info);
        Assert.Contains("本行 3 张", result.LastRow.Info);
        Assert.Equal(8, result.PerSheetIndex);                                    // 关掉后逐张：末件是第 8 张
        Assert.Equal(8, result.PerSheetTotal);
    }

    [Fact]
    public void 全部行_每行一张缩略_标题与单张那一句同源()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountByColumn);
            vm.Sheet.SelectedExpandColumn = vm.Sheet.ExpandColumnOptions.First(o => o.Value == "打印张数");
            vm.RowCheck = true;
            vm.RowCheckAll = true;
            vm.CurrentIndex = 2;
            return (Titles: vm.RowThumbs.Select(t => t.Title).ToList(), Info: vm.RecordInfoText,
                Layouts: vm.RowThumbs.Count(t => t.Layout is not null));
        });

        Assert.Equal(2, result.Titles.Count);
        Assert.Contains("olu830-35，本行 5 张", result.Titles[0]);
        Assert.Contains("olu830-70，本行 3 张", result.Titles[1]);
        Assert.Equal(result.Titles[1], result.Info);              // 一览与单张那句是同一个出处
        Assert.Equal(2, result.Layouts);                          // 每格都真有版面，不是空标题
    }

    [Fact]
    public void 没开行检查就没有缩略内容_开了又关掉也要清空()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var off = vm.RowThumbs.Count;
            vm.RowCheck = true;
            vm.RowCheckAll = true;
            var on = vm.RowThumbs.Count;
            vm.RowCheckAll = false;
            return (Off: off, On: on, After: vm.RowThumbs.Count);
        });

        Assert.Equal(0, result.Off);
        Assert.Equal(2, result.On);
        Assert.Equal(0, result.After);    // 关掉就清空：不留一排旧版面被人当成新的看
    }

    /// <summary>
    /// 出片口径不许被预览动一根手指：行检查 + 全部行开着，交给打印/导出的仍是整份标签。
    /// <para>这条是本棒唯一真正危险的口子——缩略一览少画一张，他就是少贴一箱货。</para>
    /// </summary>
    [Fact]
    public void 行检查与全部行都不改出片的张数()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountByColumn);
            vm.Sheet.SelectedExpandColumn = vm.Sheet.ExpandColumnOptions.First(o => o.Value == "打印张数");
            var before = vm.CreatePageSource()?.LabelCount;
            vm.RowCheck = true;
            vm.RowCheckAll = true;
            var after = vm.CreatePageSource()?.LabelCount;
            vm.RowCheck = false;
            return (Before: before, During: after, AfterOff: vm.CreatePageSource()?.LabelCount);
        });

        Assert.Equal(8, result.Before);
        Assert.Equal(8, result.During);       // 行检查 + 全部行开着，出片仍是 8 张
        Assert.Equal(8, result.AfterOff);
    }

    // ---------- 第 70 棒：一览排十格、点格跳转、编辑这一张 ----------

    [Fact]
    public void 点缩略一格_跳到那一行并收起一览()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountByColumn);
            vm.Sheet.SelectedExpandColumn = vm.Sheet.ExpandColumnOptions.First(o => o.Value == "打印张数");
            vm.RowCheck = true;
            vm.RowCheckAll = true;
            Assert.Equal(1, vm.RowThumbs[0].Ordinal);         // 格上带着行号，跳转不靠集合下标猜
            vm.SelectRowThumb(vm.RowThumbs[0]);               // 故意点第一格：跳成"最后一行"这种错才抓得住
            return (Index: vm.CurrentIndex, All: vm.RowCheckAll, Thumbs: vm.RowThumbs.Count, Info: vm.RecordInfoText);
        });

        Assert.Equal(1, result.Index);                  // 跳到他点的那一行，不是最后一行
        Assert.False(result.All);                       // 一览收起，回到单张
        Assert.Equal(0, result.Thumbs);
        Assert.Contains("olu830-35", result.Info);      // 看见的就是刚点的那一张
    }

    [Fact]
    public void 缩略一览十格一排_格宽按预览区可用宽算()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.SetPreviewViewport(1528);                // 预览区量到多宽，就按那个宽 ÷ 10 分格
            return (Cell: vm.RowThumbItemWidth, PerRow: vm.RowThumbCellsPerRow,
                Used: vm.RowThumbItemWidth * vm.RowThumbCellsPerRow);
        });

        Assert.Equal(148.4, result.Cell, 1);
        Assert.Equal(10, result.PerRow);
        // 十格加上留给内边距与竖向滚动条的余量，必须还在视口之内：宽一点 WrapPanel 就只排得下九格
        Assert.True(result.Used + 44 <= 1528, $"十格 {result.Used:0} DIP 放不下 1528 的预览区");
    }

    [Fact]
    public void 没量到视口宽时格宽不为零()
    {
        var cell = OnSta(() => LoadInto().RowThumbItemWidth);
        Assert.True(cell > 0, "首帧兜底格宽不能是 0，否则一览整片空白");
    }

    [Fact]
    public void 行检查时编辑器照眼前这一行的数据画()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.Sheet.SelectedSheetCount = CountOption(vm, ImpositionViewModel.SheetCountByColumn);
            vm.Sheet.SelectedExpandColumn = vm.Sheet.ExpandColumnOptions.First(o => o.Value == "打印张数");
            var off = vm.EditorPreviewRecord.GetText(MarkFieldKey.ItemNo);
            vm.RowCheck = true;
            vm.CurrentIndex = 2;
            return (off, vm.EditorPreviewRecord.GetText(MarkFieldKey.ItemNo));
        });

        Assert.Equal("olu830-35", result.Item1);        // 关着仍是第 1 张（第 65 棒③ 的口径不变）
        Assert.Equal("olu830-70", result.Item2);        // 开着就是他点的那一行——改越界看得见现场
    }

    [Fact]
    public void 一览排版与入口都在XAML里()
    {
        var xaml = RepoFile("src", "LabelGou.App", "MainWindow.xaml");

        Assert.Contains("ItemWidth=\"{Binding DataContext.RowThumbItemWidth", xaml);   // 十格一排靠它
        Assert.Contains("MouseLeftButtonUp=\"OnRowThumbClick\"", xaml);                // 点格 = 鼠标事件，不靠每格一条命令
        Assert.DoesNotContain("Command=\"{Binding SelectCommand}\"", xaml);            // 那条"点了没反应"的老写法不许回来
        // 横向滚动一开着，WrapPanel 拿到的就是无限宽 —— 永远不换行、只能往右拖（他报的「没按一行排 10 个往下排」）
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\" VerticalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("<Viewbox Stretch=\"Uniform\">", xaml);                         // 每格按格宽自适应，不切不满也不溢出
        Assert.Contains("编辑这一张…", xaml);                                       // 从预览直接进编辑器（仍走 ③ 步那一个入口）
        Assert.Contains("Click=\"OnEditTemplateClick\"", xaml);

        // 他报的「太小、显示不全」真因在这两行的顺序上：格宽刷新必须站在「自动适应」那颗勾之外 ——
        // 一览开着时那排按钮（含这颗勾）整排是藏起来的，挂进去格宽就永远停在兜底值。
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");
        var viewport = code.IndexOf("SetPreviewViewport(PreviewHost.ActualWidth)", StringComparison.Ordinal);
        var autoFit = code.IndexOf("if (AutoFitBox.IsChecked == true) FitNow();", StringComparison.Ordinal);
        Assert.True(viewport > 0 && autoFit > viewport && autoFit - viewport < 140,
            "格宽刷新得站在「自动适应」那颗勾之前（挪进 if 里就红）");
    }

    // ---------- 界面拓扑（XAML 文本判据） ----------

    [Fact]
    public void 四步面板拆成两段_更多默认折叠_报警列表不折进去()
    {
        var xaml = RepoFile("src", "LabelGou.App", "MainWindow.xaml");

        Assert.Contains("一行打几张纸", xaml);
        Assert.Contains("件号 No.x / y 怎么编", xaml);
        Assert.DoesNotContain("件号 No.x / y 规则", xaml);          // 那个把两件事混在一起的老标题不再回来
        Assert.DoesNotContain("怎么编：", xaml);                    // 三档下拉的标签也一起退场
        Assert.Contains("IsExpanded=\"False\"", xaml);              // 常年不动的那几格默认收起
        Assert.Contains("每件贴几张", xaml);
        Assert.DoesNotContain("每张份数", xaml);                    // 与「张数」撞名的老词退场

        // 报警列表必须留在折叠之外：折进「更多」等于把告警藏起来
        var expander = xaml.IndexOf("<Expander", StringComparison.Ordinal);
        var issues = xaml.IndexOf("Sheet.NumberingIssues", StringComparison.Ordinal);
        Assert.True(expander > 0 && issues > expander, "编号告警列表被折进「更多」之前了？告警不许藏");
    }

    [Fact]
    public void 单标签页有两颗检查开关_全部行时收起缩放那一组()
    {
        var xaml = RepoFile("src", "LabelGou.App", "MainWindow.xaml");

        Assert.Contains("行检查（一行一张）", xaml);
        Assert.Contains("全部行", xaml);
        Assert.Contains("RowThumbs", xaml);
        Assert.Contains("{Binding RowCheckAll, Converter={StaticResource InvBoolVis}}", xaml);   // 那一态藏掉 +/-
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
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }
}
