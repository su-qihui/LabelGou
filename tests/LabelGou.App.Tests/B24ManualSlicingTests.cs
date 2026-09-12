using System.IO;
using System.Text;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using LabelGou.Core;
using LabelGou.Core.Editing;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 24 棒界面半边：① 步手动切法（列名行/没列名/合计行兜底开关）与 ⑤ 闸门的字面量告警。
/// <para>判据本体在 Core（<c>B24SlicingAndLiteralTests</c> 守），这里只守「界面到指令」那一段——
/// 它此前没有入口：切表只能靠 AI 提案（§十-A-33②），字面量没人对（活账 A-1）。</para>
/// </summary>
public sealed class B24ManualSlicingTests : IDisposable
{
    /// <summary>
    /// 第 30 棒：默认运行模式已改成 **AI 模式**（导入后先不绑定，交 AI 读完整张表再由它绑）。
    /// 这一类测的是**离线模式**那条路（导入即自动连线，模板告警才有"这批是谁的货"可比），
    /// 所以模式显式写死，别让它跟着默认值漂。
    /// </summary>
    private static MainViewModel OfflineVm() => new(TestEnvironment.NewTempUiStateStore()) { Mode = RunMode.Offline };

    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-b24");

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    private string WriteCsv(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private const string WithSummaryTail = "货号 ITEM NO,件数 CTN\nolu830-35,5\nolu830-70,7\n,12\n";

    [Fact]
    public void 导入即兜底剔合计行_状态栏逐行说清剔了谁()
    {
        var (recordsOff, recordsOn, info) = OnSta(() =>
        {
            var vm = OfflineVm();
            vm.LoadSource(WriteCsv("tail.csv", WithSummaryTail), null);
            vm.ApplyMappingCommand.Execute(null);
            var on = vm.RecordTotal;
            var header = vm.HeaderInfoText;
            vm.SkipSummaryRowsChecked = false;
            vm.ApplyMappingCommand.Execute(null);
            return (vm.RecordTotal, on, header);
        });

        Assert.Equal(2, recordsOn);                       // 「,12」那行没混进来
        Assert.Equal(3, recordsOff);                      // 关掉兜底 → 一行不剔，刚才跳过的回来了
        Assert.Contains("自动跳过 1 行疑似合计", info);
        Assert.Contains("原表第 4 行", info);
    }

    [Fact]
    public void 开关能来回切_且切完状态如实()
    {
        var (backOn, msg) = OnSta(() =>
        {
            var vm = OfflineVm();
            vm.LoadSource(WriteCsv("tail.csv", WithSummaryTail), null);
            vm.SkipSummaryRowsChecked = false;
            vm.SkipSummaryRowsChecked = true;
            return (vm.CurrentChoice.SkipSummaryRows, vm.StatusMessage);
        });

        Assert.True(backOn);
        Assert.Contains("合计行兜底", msg);
    }

    [Fact]
    public void 被兜底剔着的行_点名要印时判据认得()
    {
        var (flaggedWhileOn, flaggedWhileOff) = OnSta(() =>
        {
            var vm = OfflineVm();
            vm.LoadSource(WriteCsv("tail.csv", WithSummaryTail), null);
            var on = vm.RowAutoSkippedByHeuristic(3);     // 原表第 4 行（0 起 = 3）
            vm.SkipSummaryRowsChecked = false;
            return (on, vm.RowAutoSkippedByHeuristic(3));
        });

        Assert.True(flaggedWhileOn);
        Assert.False(flaggedWhileOff);
    }

    [Fact]
    public void 选没列名_首行回到切法里_状态栏说实话()
    {
        var (hasHeaderAfter, info) = OnSta(() =>
        {
            var vm = OfflineVm();
            vm.LoadSource(WriteCsv("nohead.csv", "AJ7,3\nAJ8,5\n"), null);
            var noHeader = vm.HeaderRowOptions.First(o => !o.HasHeader);
            vm.SelectedHeaderRowOption = noHeader;
            return (vm.CurrentChoice.HasHeader, vm.HeaderInfoText);
        });

        Assert.False(hasHeaderAfter);
        Assert.Contains("没有表头行", info);
    }

    [Fact]
    public void 下拉列出自动猜与每一行可选项()
    {
        var (count, autoLabel, hasRow3) = OnSta(() =>
        {
            var vm = OfflineVm();
            vm.LoadSource(WriteCsv("tail.csv", WithSummaryTail), null);
            var opts = vm.HeaderRowOptions;
            return (opts.Count, opts[0].Label, opts.Any(o => o.HeaderIndex == 2));
        });

        Assert.Equal(5, count);                   // 自动猜 + 没列名 + 原表第 1、2、3 行（表头底下至少留一行货 → RawRowCount-1=3）
        Assert.Contains("软件自动猜", autoLabel);
        Assert.True(hasRow3);
    }

    [Fact]
    public void 自定义模板带着别家客户名_告警进TemplateCautions与橙色区()
    {
        var (cautions, inIssues) = OnSta(() =>
        {
            var vm = OfflineVm();
            var t = TemplateFactory.Blank("别家留下的模板");
            t.Elements.Add(TemplateFactory.NewText("BOLAROM", 6, 60, 88, 12, 20));
            Assert.True(vm.Templates.Save(t).Saved);
            vm.LoadSource(WriteCsv("kimu.csv", "客户 CONSIGNEE,货号 ITEM NO\n金沐,olu830-35\n"), null);
            vm.ReloadTemplates();                 // 新存的模板要先进下拉才谈得上选中（列表只在构造与这里重列）
            var option = vm.TemplateOptions.First(o => o.Template.Id == t.Id);
            vm.SelectedTemplate = option;
            vm.ApplyMappingCommand.Execute(null);
            return (vm.TemplateCautions, vm.IssueLines.Any(l => l.Contains("BOLAROM")));
        });

        var line = Assert.Single(cautions);
        Assert.Contains("BOLAROM", line);
        Assert.Contains("金沐", line);            // 说清这批是谁的货，不让人自己去猜
        Assert.True(inIssues);
    }

    [Fact]
    public void 内置四行模板配正常表_一声不吭()
    {
        var cautions = OnSta(() =>
        {
            var vm = OfflineVm();
            vm.LoadSource(WriteCsv("kimu.csv", "客户 CONSIGNEE,货号 ITEM NO\n金沐,olu830-35\n"), null);
            vm.ApplyMappingCommand.Execute(null);
            return vm.TemplateCautions;
        });

        Assert.Empty(cautions);                   // 客户名走 {{Consignee}}，字面量只有标签词——不该被喊
    }

    // ---------- ⑤ 闸门文案（静态函数，不需要 STA） ----------

    [Fact]
    public void 纯字面量告警也能触发一次闸门确认()
    {
        var text = ExportViewModel.ComposeGateMessage(10, 0, 0, new[] { "模板整行写死的「BOLAROM」……" });

        Assert.NotNull(text);
        Assert.Contains("BOLAROM", text);
        Assert.Contains("对不上号", text);
        Assert.Contains("确认这些已经人工过目了吗", text);
    }

    [Fact]
    public void 没告警没红旗时闸门照旧放行_有红旗时告警拼进同一段()
    {
        Assert.Null(ExportViewModel.ComposeGateMessage(10, 0, 0));
        Assert.Null(ExportViewModel.ComposeGateMessage(10, 0, 0, Array.Empty<string>()));

        var both = ExportViewModel.ComposeGateMessage(10, 3, 0, new[] { "告警一条" });
        Assert.NotNull(both);
        Assert.Contains("3 张含「需人工核对」", both!);
        Assert.Contains("告警一条", both);
    }

    /// <summary>第 46 棒：文本改成永不折行后，"字排到纸外"由出纸前这道闸接手。</summary>
    [Fact]
    public void 墨迹出纸单独也能触发复核_并说清怎么修()
    {
        Assert.Null(ExportViewModel.ComposeGateMessage(10, 0, 0, null, 0));      // 没越界照旧放行

        var text = ExportViewModel.ComposeGateMessage(10, 0, 0, null, 3);
        Assert.NotNull(text);
        Assert.Contains("3 张标签的文字排到了纸边外", text!);
        Assert.Contains("缩回纸内", text);

        var sampled = ExportViewModel.ComposeGateMessage(25731, 0, 0, null, 2, 2000);
        Assert.Contains("只抽查了前 2000 张", sampled!);   // 被上限截过就不许说成"全量量过"
    }
}
