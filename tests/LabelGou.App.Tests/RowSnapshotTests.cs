using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core;
using LabelGou.Core.Export;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 73 棒 · 单张定稿（「编辑这一张…」只改那一张）。
/// <para>用户 2026-09-16 的判词：「我理解的单张编辑是对那一张进行更改编辑而不是全部，目前就是我在单张
/// 编辑更改后还是进行全部跟随了」，并补了两句边界：「也不能导致出现对应的还是多出一次等问题」。</para>
/// <para>所以这一组判据盯四件事：① 只改那一行的那几张，别的行<strong>逐字不动</strong>；
/// ② 张数、页数、③ 步模板列表一条都不许多出来；③ 屏幕那一路与出纸那一路读到<strong>同一份</strong>定稿；
/// ④ 件号不许被冻住（一行五箱印成同一个号就是重号）。</para>
/// </summary>
public sealed class RowSnapshotTests : IDisposable
{
    private const string Marker = "@@定稿@@";

    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-b73");

    /// <summary>MainViewModel 会碰 WPF 类型，必须在 STA 线程上造（§五-91）。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>两行货：一行 5 箱、一行 3 箱，另有一列「打印张数」决定一行出几张纸。</summary>
    private string WriteCsv()
    {
        var path = Path.Combine(_dir, "定稿表.csv");
        var text = string.Join(",", "货号 ITEM NO:", "件数 CTN", "打印张数") + Environment.NewLine +
                   "olu830-35,1,5" + Environment.NewLine +
                   "olu830-70,1,3" + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private MainViewModel LoadInto()
    {
        // 离线模式：导入即自动连线，货号才有值（AI 模式故意先不绑，那一行标题就写成「（无货号）」）。
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore()) { Mode = RunMode.Offline };
        vm.LoadSource(WriteCsv(), null);
        vm.Sheet.SelectedSheetCount = vm.Sheet.SheetCountOptions.First(o => o.Value == ImpositionViewModel.SheetCountByColumn);
        vm.Sheet.SelectedExpandColumn = vm.Sheet.ExpandColumnOptions.First(o => o.Value == "打印张数");
        vm.RowCheck = true;
        Assert.Equal(8, vm.Sheet.Labels.Count);       // 5 + 3：一行多张才是这一棒的主场景
        return vm;
    }

    /// <summary>给这一行的定稿加一个只有它才有的记号元素（不依赖模板里到底有哪些令牌）。</summary>
    private static void Mark(MainViewModel.RowSnapshotTarget target) => target.Template.Elements.Add(new TemplateElement
    {
        Kind = ElementKind.Text, Text = Marker, X = 6, Y = 6, Width = 60, Height = 8, FontSizePt = 10,
    });

    private static List<string> TextsOf(PageContentSource source, int labelIndex)
        => source.BuildAt(labelIndex)?.Items.OfType<TextItem>().Select(i => i.Content).ToList() ?? new List<string>();

    private static bool HasMarker(PageContentSource source, int labelIndex)
        => TextsOf(source, labelIndex).Any(t => t.Contains(Marker));

    /// <summary>模板库里有几份 json：一条都没存过时那个目录还不存在，不算违规。</summary>
    private static int CountTemplateFiles(MainViewModel vm)
        => Directory.Exists(vm.Templates.UserDirectory)
            ? Directory.GetFiles(vm.Templates.UserDirectory, "*.json").Length
            : 0;

    // ---------- ① 只改那一张，别的行逐字不动 ----------

    [Fact]
    public void 定稿只改那一行的五张_其他三张逐字不动()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var before = Enumerable.Range(1, 8).Select(i => TextsOf(vm.CreatePageSource()!, i)).ToList();

            var target = vm.BeginRowSnapshot(1)!;
            Mark(target);
            vm.CommitRowSnapshot(target, target.Template);

            var after = vm.CreatePageSource()!;
            return (before,
                After: Enumerable.Range(1, 8).Select(i => TextsOf(after, i)).ToList(),
                Marked: Enumerable.Range(1, 8).Select(i => HasMarker(after, i)).ToList());
        });

        Assert.Equal(new[] { true, true, true, true, true, false, false, false }, result.Marked);
        // 第 6~8 张（第二行）连文字带顺序逐字不变——"其他没影响"要能逐字节证
        for (var i = 5; i < 8; i++) Assert.Equal(result.before[i], result.After[i]);
    }

    /// <summary>
    /// 模板里内容含某个词的那个元素。
    /// <para>按元素而不是按版面项找：渲染时空值整条会隐藏，下标就错开了（第 9 棒批次一-11 那一族）。</para>
    /// </summary>
    private static TemplateElement ElementWith(LabelTemplate template, string word)
        => template.Elements.First(e => (e.Text ?? string.Empty).Contains(word));

    /// <summary>模板里所有"不是内置计算量"的占位符：定稿里只剩编号引擎的推算量才算切干净了。</summary>
    private static List<string> NonBuiltInTokens(LabelTemplate template)
        => template.Elements
            .SelectMany(e => TemplateTokenizer.EnumerateTokens(e.Text ?? string.Empty))
            .Where(t => !TemplateTokenizer.IsBuiltInToken(t))
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void 表里的值写死而推算量仍由软件逐张算()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var baseTemplate = vm.SelectedTemplate!.Template;
            var target = vm.BeginRowSnapshot(1)!;
            vm.CommitRowSnapshot(target, target.Template);

            return (
                ItemBaked: ElementWith(target.Template, "Item no").Text,
                ItemWasToken: ElementWith(baseTemplate, "Item no").Text,
                CtnsAlive: ElementWith(target.Template, "Ctns").Text,
                TokensLeft: NonBuiltInTokens(target.Template),
                BaseKeepsTokens: baseTemplate.Elements.Any(e => (e.Text ?? string.Empty).Contains("{{")));
        });

        Assert.Equal("Item no：olu830-35", result.ItemBaked);          // 表里的值：与那张纸切开，写死
        Assert.Contains("{{ItemNo}}", result.ItemWasToken);            // 库里那份模板没被改脏
        Assert.True(result.BaseKeepsTokens);
        Assert.Contains("{{col:本行箱数}}", result.CtnsAlive);         // 引擎算的量：留活，逐张/逐行重算
        Assert.Equal(new[] { "col:本行箱数" }, result.TokensLeft);     // 定稿里只剩这一个推算量
    }

    // ---------- ② 不许多出一次 ----------

    [Fact]
    public void 定稿不动张数页数与模板列表()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var labels = vm.Sheet.Labels.Count;
            var pages = vm.Sheet.Plan!.PageCount;
            var options = vm.TemplateOptions.Count;
            var files = CountTemplateFiles(vm);
            Assert.Equal(0, vm.RowSnapshotCount);

            var target = vm.BeginRowSnapshot(1)!;
            Mark(target);
            vm.CommitRowSnapshot(target, target.Template);

            return (labels, pages, options, files,
                After: (Labels: vm.Sheet.Labels.Count, Pages: vm.Sheet.Plan!.PageCount, Options: vm.TemplateOptions.Count,
                    Files: CountTemplateFiles(vm)),
                BaseStillClean: vm.SelectedTemplate!.Template.Elements.All(e => !(e.Text ?? string.Empty).Contains(Marker)),
                Count: vm.RowSnapshotCount);
        });

        Assert.Equal(result.labels, result.After.Labels);        // 张数一根手指没动
        Assert.Equal(result.pages, result.After.Pages);          // 页数不变
        Assert.Equal(result.options, result.After.Options);      // ③ 步下拉不会多出一份
        Assert.Equal(result.files, result.After.Files);          // 模板库里一个字都没写
        Assert.True(result.BaseStillClean);                      // 那份模板本身没被改脏
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void 定稿里宽高被钉回基准模板_拼版不跟着跑()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var pages = vm.Sheet.Plan!.PageCount;
            var width = vm.SelectedTemplate!.Template.WidthMm;
            var height = vm.SelectedTemplate!.Template.HeightMm;

            var target = vm.BeginRowSnapshot(1)!;
            target.Template.WidthMm = width + 900;      // 绕开界面直接塞一个荒唐尺寸
            target.Template.HeightMm = height + 900;
            vm.CommitRowSnapshot(target, target.Template);

            var source = vm.CreatePageSource()!;
            return (pages, Plan: vm.Sheet.Plan!.PageCount, Width: source.TemplateAt(1).WidthMm,
                Height: source.TemplateAt(1).HeightMm, OtherWidth: source.TemplateAt(8).WidthMm);
        });

        Assert.Equal(result.Width, result.OtherWidth);   // 整批只有一套尺寸，格子才排得对
        Assert.True(result.Width < 600, $"荒唐尺寸没被钉回去：{result.Width} mm");
        Assert.True(result.Height < 600, $"荒唐尺寸没被钉回去：{result.Height} mm");
        Assert.Equal(result.pages, result.Plan);
    }

    // ---------- ③ 屏幕与出纸同一个答案 ----------

    [Fact]
    public void 屏幕那一路与出纸那一路读到同一份定稿()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var target = vm.BeginRowSnapshot(1)!;
            Mark(target);
            vm.CommitRowSnapshot(target, target.Template);

            var source = vm.CreatePageSource()!;
            // vm.Sheet.LayoutFor 走 MainViewModel.BuildLayoutFor（单标签与整版预览那一路）
            var screen = Enumerable.Range(1, 8)
                .Select(i => vm.Sheet.LayoutFor(i)?.Items.OfType<TextItem>().Any(t => t.Content.Contains(Marker)) ?? false)
                .ToList();
            var print = Enumerable.Range(1, 8).Select(i => HasMarker(source, i)).ToList();
            return (screen, print);
        });

        Assert.Equal(result.print, result.screen);         // 两边不一致就是"屏幕一支、纸上一支"
        Assert.Equal(new[] { true, true, true, true, true, false, false, false }, result.screen);
    }

    // ---------- ④ 结构一动就当面作废 ----------

    [Fact]
    public void 换模板把定稿当面作废_并说出作废的是哪一行()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var target = vm.BeginRowSnapshot(1)!;
            Mark(target);
            vm.CommitRowSnapshot(target, target.Template);

            var other = vm.TemplateOptions.First(o => o.Id != vm.SelectedTemplate!.Id);
            var before = HasMarker(vm.CreatePageSource()!, 1);      // 先证这一步真定过稿，否则下面的"没标记"可能是假绿
            vm.SelectedTemplate = other;
            return (vm.RowSnapshotCount, vm.RowSnapshotLostNotice, before, Marked: HasMarker(vm.CreatePageSource()!, 1));
        });

        Assert.Equal(0, result.RowSnapshotCount);
        Assert.True(result.before);
        Assert.NotNull(result.RowSnapshotLostNotice);
        Assert.Contains("换了模板", result.RowSnapshotLostNotice);
        Assert.Contains("作废", result.RowSnapshotLostNotice);
        Assert.False(result.Marked);
    }

    [Fact]
    public void 表重切一次定稿就作废_不留旧字挂到别的行()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            var target = vm.BeginRowSnapshot(1)!;
            Mark(target);
            vm.CommitRowSnapshot(target, target.Template);

            var first = 1;                                  // 这份夹具里第一行的第一张就是第 1 张（与换模板那条同写法）
            var before = HasMarker(vm.CreatePageSource()!, first);

            vm.ResetSheetChoice();                       // 「恢复自动切表」＝重读一遍这张表
            return (vm.RowSnapshotCount, vm.RowSnapshotLostNotice, before, Marked: HasMarker(vm.CreatePageSource()!, first));
        });

        Assert.Equal(0, result.RowSnapshotCount);
        Assert.True(result.before);
        Assert.NotNull(result.RowSnapshotLostNotice);
        Assert.Contains("这张表重新切过", result.RowSnapshotLostNotice);
        Assert.False(result.Marked);
    }

    // ---------- ⑤ 一览角标与恢复 ----------

    [Fact]
    public void 一览那一格标出已单独定稿_恢复这一张就退回模板()
    {
        var result = OnSta(() =>
        {
            var vm = LoadInto();
            vm.RowCheckAll = true;
            var before = vm.RowThumbs.Select(t => t.HasSnapshot).ToList();
            Assert.Equal(new[] { false, false }, before);

            var target = vm.BeginRowSnapshot(1)!;
            Mark(target);
            vm.CommitRowSnapshot(target, target.Template);
            var marked = vm.RowThumbs.Select(t => t.HasSnapshot).ToList();
            var info = vm.RecordInfoText;
            var summary = vm.RowSnapshotSummary;

            var first = vm.RestoreRowSnapshot(1);
            var second = vm.RestoreRowSnapshot(1);
            return (marked, info, summary, first, second,
                After: vm.RowThumbs.Select(t => t.HasSnapshot).ToList());
        });

        Assert.Equal(new[] { true, false }, result.marked);          // 只有那一格挂角标
        Assert.Contains("已单独定稿", result.info);
        Assert.Contains("单独定稿", result.summary);
        Assert.True(result.first);                                    // 真撤掉了东西才报"已恢复"
        Assert.False(result.second);                                  // 没定过稿不许装作办了事
        Assert.Equal(new[] { false, false }, result.After);
    }

    // ---------- ⑥ 出纸闸与 SVG 说明 ----------

    [Fact]
    public void 定稿把待核值烤成死字_出纸闸仍数得着那一张()
    {
        var result = OnSta(() =>
        {
            var template = BuiltInTemplates.GetById(BuiltInTemplates.IdStandard)!;
            var record = MarkRecord.Builder().SetRow(1, "样例.xlsx").Set(MarkFieldKey.ItemNo, "olu830-35").Build();
            var plain = new PageContentSource(template, new[] { record, record }, "样例.xlsx");
            // 第二张带着定稿，而定稿记得"这张上有 1 个值没核过"（烤平成死字后版面项自己不再挂 NeedsReview）
            var baked = template.CloneAsUserCopy(template.Name);
            var withSnapshot = new PageContentSource(template, new[] { record, record }, "样例.xlsx",
                snapshotFor: r => new LabelSnapshot(baked, 1));
            return (Plain: plain.UnconfirmedLabelCount, WithSnapshot: withSnapshot.UnconfirmedLabelCount);
        });

        Assert.Equal(0, result.Plain);
        Assert.Equal(2, result.WithSnapshot);   // 两条记录都归那一定稿 → 两张都算
    }

    [Fact]
    public void 导出的SVG元数据里点名哪几枚是定稿()
    {
        var xml = OnSta(() =>
        {
            var vm = LoadInto();
            var target = vm.BeginRowSnapshot(2)!;
            Mark(target);
            vm.CommitRowSnapshot(target, target.Template);

            var request = new SheetExportRequest
            {
                Plan = vm.Sheet.Plan!,
                Source = vm.CreatePageSource()!,
                PageIndexes = new[] { 0 },
                BaseName = "定稿批次",
                Dpi = 150,
            };
            return Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default)).Xml;
        });

        Assert.Contains("单张定稿", xml);
        Assert.Contains("olu830-70", xml);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 测试产物删不掉不影响结论 */ }
    }
}
