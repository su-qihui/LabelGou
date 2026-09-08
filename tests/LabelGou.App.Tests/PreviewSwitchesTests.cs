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
    public void 走生产渲染器一页确实画出四枚全同而不是只有左上角有东西()
    {
        // 用户 2026-09-08 拿红框圈的那三个白框：几何里写「每页 4 枚」不算数，必须拿真的 PageRasterizer 渲出来数墨点。
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
            RepeatSameLabelPerPage = true,
        };
        var template = Template("PORT {{DestinationPort}}");
        template.WidthMm = 90;
        template.HeightMm = 60;
        var records = new[] { Record("los angeles"), Record("los angeles"), Record("los angeles"), Record("los angeles"),
                              Record("shanghai"), Record("shanghai"), Record("shanghai"), Record("shanghai") };
        var plan = ImpositionEngine.Build(spec, template.WidthMm, template.HeightMm, records.Length);
        var source = new PageContentSource(template, records, "金沐一开四.csv", MarkTextCase.Upper);

        Assert.True(plan.OneLabelPerPage);
        Assert.Equal(4, plan.PlacementsOnPage(1).Count);
        Assert.All(plan.PlacementsOnPage(1), p => Assert.Equal(1, p.LabelIndex));   // 一页就是同一枚铺满
        Assert.Equal(8, plan.PageCount);

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

        // 四个位置都得有墨 —— 上一版这里右上、左下、右下三格是空的，而那正是用户圈出来的东西。
        Assert.All(darkCounts, dark => Assert.True(dark > 50, $"有一个位置没画出东西：{dark} 个墨点"));
    }

    [Fact]
    public void 后面的标签也一样铺满不是只有第一枚()
    {
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
        var records = new[] { Record("los angeles"), Record("shanghai"), Record("shanghai"), Record("shanghai") };
        var plan = ImpositionEngine.Build(spec, template.WidthMm, template.HeightMm, records.Length);
        var source = new PageContentSource(template, records, "金沐一开四.csv", MarkTextCase.AsSource);

        // 第 4 页 = 第 4 枚独占一页，四格都有墨
        var page4 = plan.PlacementsOnPage(4);
        Assert.Equal(4, page4.Count);
        Assert.All(page4, p => Assert.Equal(4, p.LabelIndex));
        var dark = OnSta(() =>
        {
            var bitmap = PageRasterizer.RenderPage(plan, 4, 150, source.AsProvider(), false, PageRenderPurpose.Image, false);
            return DarkPixelsIn(bitmap, new System.Windows.Int32Rect(bitmap.PixelWidth / 2, 0,
                bitmap.PixelWidth - bitmap.PixelWidth / 2, bitmap.PixelHeight / 2));   // 右上那一格
        });
        Assert.True(dark > 50, $"第 4 页右上那一格是空的：{dark} 个墨点");
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
