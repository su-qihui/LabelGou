using System.Linq;
using LabelGou.App.Export;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 13 棒 App 侧：大小写开关要真的走到预览与导出快照，预览虚线判据要有机检口子，
/// ④ 步那个勾必须绑在 VM 上（直接绑纸规字段就是「改了没反应」那一类）。
/// </summary>
public class PreviewSwitchesTests
{
    /// <summary>WPF 渲染对象只能在 STA 线程上造（与 OutputPipelineTests 同一个做法）。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static LabelTemplate Template(string text) => new()
    {
        Id = "test.app-text",
        Name = "出口测试",
        WidthMm = 100,
        HeightMm = 60,
        BorderMm = 0,
        Elements =
        {
            new TemplateElement
            {
                Kind = ElementKind.Text, Text = text,
                X = 4, Y = 4, Width = 90, Height = 10,
            },
        },
    };

    private static MarkRecord Record(string port = "los angeles") => MarkRecord.Builder()
        .SetRow(1, "样例.xlsx")
        .Set(MarkFieldKey.DestinationPort, port)
        .Build();

    private static string Only(PageContentSource source)
        => source.BuildAt(1)!.Items.OfType<TextItem>().Single().Content;

    // ---------- 导出/打印快照带口径 ----------

    [Fact]
    public void 快照没传口径时按表格里的印()
    {
        var source = new PageContentSource(Template("PORT {{DestinationPort}}"), new[] { Record() }, "样例.xlsx");
        Assert.Equal("PORT los angeles", Only(source));
    }

    [Fact]
    public void 快照带上大写口径后整批都走大写()
    {
        var source = new PageContentSource(Template("PORT {{DestinationPort}}"), new[] { Record() }, "样例.xlsx",
            MarkTextCase.Upper);
        Assert.Equal("PORT LOS ANGELES", Only(source));
    }

    [Fact]
    public void 口径随快照固定而不是每次现读界面()
    {
        // 后台线程跑到一半界面改了档，也不能让同一批 PDF 前几页大写、后几页小写。
        var records = new[] { Record(), Record() };
        var source = new PageContentSource(Template("PORT {{DestinationPort}}"), records, "样例.xlsx", MarkTextCase.Upper);
        Assert.All(records.Select((_, i) => source.BuildAt(i + 1)!),
            layout => Assert.Equal("PORT LOS ANGELES", layout.Items.OfType<TextItem>().Single().Content));
    }

    // ---------- 单标签预览读同一个开关 ----------

    [Fact]
    public void 界面改大小写档预览立刻跟着换()
    {
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        vm.TextCase = MarkTextCase.Lower;

        Assert.Contains(vm.CurrentLayout!.Items.OfType<TextItem>(), t => t.Content.Contains("yoga-pant-ss"));
    }

    [Fact]
    public void 默认档下预览仍是表里那串原样()
    {
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        Assert.Equal(MarkTextCase.AsSource, vm.TextCase);
        Assert.Contains(vm.CurrentLayout!.Items.OfType<TextItem>(), t => t.Content.Contains("YOGA-PANT-SS"));
    }

    [Fact]
    public void 大小写档存住重启还在()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        var vm = new MainViewModel(store);
        vm.SelectedTextCase = vm.TextCaseOptions.Single(o => o.Value == MarkTextCase.Upper);

        Assert.Equal(MarkTextCase.Upper, new MainViewModel(store).TextCase);   // 新实例 = 重启
    }

    [Fact]
    public void 下拉三项的名字来自核心而不是界面手打()
    {
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        Assert.Equal(new[] { "按表格里的", "全部大写", "全部小写" }, vm.TextCaseOptions.Select(o => o.Label).ToArray());
    }

    // ---------- ④ 步那个勾绑在 VM 上 ----------

    [Fact]
    public void 一页只排同一枚默认开着而且改得动纸规()
    {
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        Assert.NotNull(vm.Sheet.Working);
        Assert.True(vm.Sheet.RepeatSameLabelPerPage);

        vm.Sheet.RepeatSameLabelPerPage = false;
        Assert.False(vm.Sheet.Working.RepeatSameLabelPerPage);
    }

    [Fact]
    public void 换纸规后要通知界面回读那个勾()
    {
        // SheetSpec 不带变更通知，换档时不 Raise 一下，勾就会停在上一个纸规的样子。
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        var raised = new System.Collections.Generic.List<string>();
        vm.Sheet.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        var next = vm.Sheet.SheetOptions.FirstOrDefault(o => !ReferenceEquals(o, vm.Sheet.SelectedSheetOption));
        Assert.NotNull(next);

        vm.Sheet.SelectedSheetOption = next!;
        Assert.Contains(nameof(ImpositionViewModel.RepeatSameLabelPerPage), raised);
    }

    // ---------- 预览虚线 / 上纸实线 ----------

    [Fact]
    public void 走生产渲染器分组那页确实画了四枚而不是只写在计划里()
    {
        // §五-70 那条欠账的一小半：几何对了不代表渲染真画出来了，这里拿真的 PageRasterizer 渲一页，
        // 数四个象限各自的墨点，防止「计划里四枚、纸上只有一枚」这种只算没炸的错。
        var spec = new SheetSpec
        {
            Name = "一开四",
            PaperWidthMm = 200,
            PaperHeightMm = 140,
            MarginLeftMm = 5,
            MarginTopMm = 5,
            MarginRightMm = 5,
            MarginBottomMm = 5,
            GutterXMm = 2,
            GutterYMm = 2,
            AllowRotate = false,
            RegistrationMarks = false,
        };
        var template = Template("PORT {{DestinationPort}}");
        template.WidthMm = 90;
        template.HeightMm = 60;
        var records = new[] { Record("los angeles"), Record("los angeles"), Record("los angeles"), Record("los angeles"),
                              Record("shanghai"), Record("shanghai"), Record("shanghai"), Record("shanghai") };
        var groups = new[] { 1, 1, 1, 1, 2, 2, 2, 2 };
        var plan = ImpositionEngine.Build(spec, template.WidthMm, template.HeightMm, records.Length, groups);
        var source = new PageContentSource(template, records, "金沐一开四.csv", MarkTextCase.Upper);

        Assert.Equal(4, plan.PlacementsOnPage(1).Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, plan.PlacementsOnPage(1).Select(p => p.LabelIndex).ToArray());
        Assert.Equal(2, plan.PageCount);

        var darkCounts = OnSta(() =>
        {
            var bitmap = PageRasterizer.RenderPage(plan, 1, 150, source.AsProvider(), false, PageRenderPurpose.Image, false);
            var halfW = bitmap.PixelWidth / 2;
            var halfH = bitmap.PixelHeight / 2;
            return new[]
            {
                new System.Windows.Int32Rect(0, 0, halfW, halfH),
                new System.Windows.Int32Rect(halfW, 0, bitmap.PixelWidth - halfW, halfH),
                new System.Windows.Int32Rect(0, halfH, halfW, bitmap.PixelHeight - halfH),
                new System.Windows.Int32Rect(halfW, halfH, bitmap.PixelWidth - halfW, bitmap.PixelHeight - halfH),
            }.Select(q => DarkPixelsIn(bitmap, q)).ToArray();
        });

        // 四个位置都得有墨，且都是同一款的大写 PORT LOS ANGELES（不是第二款的 SHANGHAI）。
        Assert.All(darkCounts, dark => Assert.True(dark > 50, $"有一个位置没画出东西：{dark} 个墨点"));
        Assert.All(records.Take(4), r => Assert.Equal("los angeles", r.GetText(MarkFieldKey.DestinationPort)));
    }

    private static int DarkPixelsIn(System.Windows.Media.Imaging.RenderTargetBitmap bmp, System.Windows.Int32Rect area)
    {
        var stride = area.Width * 4;
        var data = new byte[area.Height * stride];
        bmp.CopyPixels(area, data, stride, 0);
        var dark = 0;
        for (var i = 0; i + 3 < data.Length; i += 4)
        {
            if (data[i + 3] > 0 && data[i] < 128 && data[i + 1] < 128 && data[i + 2] < 128) dark++;
        }
        return dark;
    }

    [Fact]
    public void 预览里角线与套准十字走虚线()
    {
        Assert.NotNull(SheetRenderer.DashStyleFor(SheetMarkKind.CropMark, RenderTarget.Screen));
        Assert.NotNull(SheetRenderer.DashStyleFor(SheetMarkKind.RegistrationMark, RenderTarget.Screen));
    }

    [Fact]
    public void 打印与位图出口这些线必须是实线()
    {
        // 虚线是给人看的示意；真要上纸的墨不能是虚的。
        Assert.Null(SheetRenderer.DashStyleFor(SheetMarkKind.CropMark, RenderTarget.Printer));
        Assert.Null(SheetRenderer.DashStyleFor(SheetMarkKind.RegistrationMark, RenderTarget.Printer));
        Assert.Null(SheetRenderer.DashStyleFor(SheetMarkKind.CropMark, RenderTarget.Bitmap));
    }

    [Fact]
    public void 刀模示意线在任何去处都是点线()
    {
        Assert.NotNull(SheetRenderer.DashStyleFor(SheetMarkKind.LabelOutline, RenderTarget.Screen));
        Assert.NotNull(SheetRenderer.DashStyleFor(SheetMarkKind.LabelOutline, RenderTarget.Bitmap));
    }
}
