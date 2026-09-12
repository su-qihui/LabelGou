using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using LabelGou.App.Export;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Colors;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 47 棒：元素自己那支墨，从属性面板一路走到五个出口。
/// <para>
/// 这一层要证的只有一件事——<strong>五出口读的是同一支笔</strong>。
/// 项目里已经栽过两次"某个出口自己手写了一串 <c>#000000</c>"（§五-62：预览蓝十字、件上黑十字），
/// 元素一旦可以带色，那种漂移就从"看着别扭"升级成"印出来的颜色不对"，所以每个出口都要各自钉一条。
/// </para>
/// </summary>
public class InkColourTests
{
    private const double LabelW = 100;
    private const double LabelH = 80;

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static LabelTemplate Template(params TemplateElement[] elements)
    {
        var template = new LabelTemplate
        {
            Id = "user.ink", Name = "墨色测试", WidthMm = LabelW, HeightMm = LabelH,
            PaddingMm = 4,
            BorderMm = 0,                                     // 外框不属于任何元素，留着它会把"这一层没有黑"那条断言搅浑
        };
        template.Elements.AddRange(elements);
        return template;
    }

    private static SheetSpec Spec() => new()
    {
        Id = "test.a4.ink",
        Name = "测试 A4（墨色）",
        PaperWidthMm = 210,
        PaperHeightMm = 297,
        MarginLeftMm = 8,
        MarginTopMm = 8,
        MarginRightMm = 8,
        MarginBottomMm = 8,
        GutterXMm = 2,
        GutterYMm = 2,
        CropMarkThicknessMm = 0.15,
        RepeatSameLabelPerPage = false,
    };

    private static string WritePageSvg(LabelTemplate template, MarkRecord? record = null)
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = new PageContentSource(template, new List<MarkRecord> { record ?? SampleRecords.StandardSample() }, "样例.xlsx"),
            PageIndexes = new List<int> { 0 },
            BaseName = "墨件套",
        };
        var file = Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default));
        return file.Xml;
    }

    private static List<string> FillsAndStrokes(string xml)
    {
        var doc = XDocument.Parse(xml);
        var layer = doc.Root!.Descendants(Svg + "g").Single(g => (string?)g.Attribute("id") == "labels");
        return layer.Descendants()
            .SelectMany(e => new[] { (string?)e.Attribute("fill"), (string?)e.Attribute("stroke") })
            .Where(c => c is not null && c != "none")
            .Select(c => c!)
            .ToList();
    }

    // ---------- 出口侧 ----------

    [Fact]
    public void TextCarriesItsOwnInkIntoTheSvgBranch() => OnSta(() =>
    {
        var xml = WritePageSvg(Template(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "MADE IN CHINA", X = 10, Y = 10, Width = 60, Height = 8,
            FontSizePt = 12, InkColor = LabelColor.FromCmyk(0, 100, 100, 0),
        }));
        var inks = FillsAndStrokes(xml);
        Assert.Contains("#ff0000", inks);
        Assert.DoesNotContain("#000000", inks);
        return true;
    });

    [Fact]
    public void LineAndRectBranchesUseTheSameInk() => OnSta(() =>
    {
        var xml = WritePageSvg(Template(
            new TemplateElement { Kind = ElementKind.Line, X = 10, Y = 20, X2 = 60, Y2 = 20, ThicknessMm = 0.35, InkColor = LabelColor.FromCmyk(100, 100, 0, 0) },
            new TemplateElement { Kind = ElementKind.Rect, X = 10, Y = 30, Width = 40, Height = 12, InkColor = LabelColor.FromCmyk(100, 100, 0, 0) }));
        var inks = FillsAndStrokes(xml);
        var blue = inks.Count(c => c == "#0000ff");
        Assert.True(blue == 2, $"线与框各该有一笔蓝，实际数到 {blue} 笔——少一笔就是有一处出口还在自己写黑色");
        Assert.DoesNotContain("#000000", inks);
        return true;
    });

    [Fact]
    public void BarcodeBarsAndTheirReadableLineShareOneInk() => OnSta(() =>
    {
        var template = Template(new TemplateElement
        {
            Kind = ElementKind.Barcode, Text = "{{col:条码}}", Symbology = BarcodeSymbology.Code128,
            ShowBarcodeText = true, X = 4, Y = 52, Width = 92, Height = 22, FontSizePt = 7,
            InkColor = LabelColor.FromCmyk(0, 100, 100, 0),
        });
        var record = MarkRecord.Builder()
            .SetRow(1, "样例.xlsx")
            .Set(MarkFieldKey.DestinationPort, "LOS ANGELES")
            .SetCustom("col:条码", "BOX-000123")
            .Build();
        var layout = LayoutEngine.Build(template, record, new LayoutContext(1, 1));
        var bar = Assert.Single(layout.Items.OfType<BarcodeItem>());
        Assert.Null(bar.Error);

        // 预览侧：条与下面那串数字必须同一支笔（那串数字是被包成 TextItem 复用的，最容易各画各的）
        var glyphs = LabelRenderer.ReadableLine(bar);
        Assert.Equal(bar.Ink, glyphs.Ink);
        Assert.Equal(RenderRules.InkOf(bar.Ink), TextFit.ForegroundFor(glyphs));

        // 出口侧：一根条一个矩形，填充都是那支红
        var inks = FillsAndStrokes(WritePageSvg(template, record));
        Assert.True(inks.Count(c => c == "#ff0000") > 20, $"条码那一片条该整排都是红的，实际只数到 {inks.Count(c => c == "#ff0000")} 笔");
        return true;
    });

    [Fact]
    public void UncolouredElementsStillDrawExactlyBlack() => OnSta(() =>
    {
        // 「模板文件暂时不用更新」的出口侧含义：没填颜色的元素，SVG 里逐字还是从前那一串。
        var xml = WritePageSvg(Template(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "CONTRACT NO.", X = 10, Y = 10, Width = 60, Height = 8, FontSizePt = 12,
        }));
        Assert.Contains("#000000", FillsAndStrokes(xml));
        return true;
    });

    // ---------- 警示优先那一侧 ----------

    [Fact]
    public void WarningRedOutranksTheElementOwnInk() => OnSta(() =>
    {
        var red = LabelColor.FromCmyk(0, 100, 100, 0);
        var plain = new TextItem("A", 0, 0, 10, 5, TemplateElement.DefaultFont, 8, false, HorizontalAlign.Left, true, 1, Ink: red);
        Assert.Same(RenderRules.InkOf(red), TextFit.ForegroundFor(plain));

        var flagged = plain with { Flagged = true };
        Assert.Same(RenderRules.FlagInk, TextFit.ForegroundFor(flagged));

        // 没填颜色 = 那个黑常量本身（不是"另一个也是黑的 brush"），撤销与对比都不会多出差异
        Assert.Same(RenderRules.Ink, TextFit.ForegroundFor(plain with { Ink = null }));
        return true;
    });

    // ---------- 面板侧 ----------

    [Fact]
    public void PanelChannelsEditOneInkThatUndoesAsOneStep() => OnSta(() =>
    {
        var vm = new TemplateEditorViewModel(Template(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "{{Consignee}}", X = 10, Y = 10, Width = 40, Height = 8, FontSizePt = 12,
        }), new TemplateStore(Path.Combine(Path.GetTempPath(), "labelgou-ink-" + Guid.NewGuid().ToString("N")[..6])));
        vm.SelectedRow = vm.Elements[0];
        var element = vm.Template.Elements[0];
        var panel = vm.Editing ?? throw new InvalidOperationException("选中行没建起来，面板不成立");

        Assert.Null(element.InkColor);                                  // 没填就是没填，面板显示"黑（默认）"
        Assert.Equal("黑（默认）", panel.InkSummary);

        // 手打四格：从黑（K=100）一路改成那支红，全程只该有一步撤销
        panel.InkM = 100;
        panel.InkY = 100;
        panel.InkK = 0;
        Assert.Equal((0, 100, 100, 0), (element.InkColor!.C, element.InkColor.M, element.InkColor.Y, element.InkColor.K));
        Assert.Equal(ColorEntrySpace.Cmyk, element.InkColor.Entry);
        Assert.Equal(255, panel.InkR);                                  // RGB 档跟着显示同一支墨的近似

        vm.UndoCommand.Execute(null);
        Assert.Null(vm.Template.Elements[0].InkColor);                   // 撤销后模板里真的没了
        Assert.Null(vm.Editing!.InkColor);                               // 面板也跟着刷回真值（RaiseAll 漏一格就是一格数字在骗人）
        return true;
    });

    [Fact]
    public void PresetAndResetCommandsDoWhatTheySay() => OnSta(() =>
    {
        var vm = new TemplateEditorViewModel(Template(new TemplateElement
        {
            Kind = ElementKind.Rect, X = 10, Y = 10, Width = 40, Height = 8,
        }), new TemplateStore(Path.GetTempPath() + "labelgou-ink-" + Guid.NewGuid().ToString("N")[..6]));
        vm.SelectedRow = vm.Elements[0];
        var element = vm.Template.Elements[0];
        var panel = vm.Editing ?? throw new InvalidOperationException("选中行没建起来，面板不成立");

        Assert.False(panel.ResetInkCommand.CanExecute(null));            // 已经黑着，不给一颗"点了什么都没发生"的按钮
        panel.ApplyInkPresetCommand.Execute(EditableElement.InkPresets[1]);
        Assert.Equal("#ff0000", element.InkColor!.ToHex());
        Assert.True(panel.ResetInkCommand.CanExecute(null));

        panel.ResetInkCommand.Execute(null);
        Assert.Null(element.InkColor);
        return true;
    });

    [Fact]
    public void HexBoxAcceptsDeviceCmykAndIgnoresHalfTypedText() => OnSta(() =>
    {
        var vm = new TemplateEditorViewModel(Template(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "X", X = 10, Y = 10, Width = 40, Height = 8,
        }), new TemplateStore(Path.GetTempPath() + "labelgou-ink-" + Guid.NewGuid().ToString("N")[..6]));
        vm.SelectedRow = vm.Elements[0];
        var element = vm.Template.Elements[0];

        vm.Editing!.InkHex = "device-cmyk(0 .9 .9 0)";
        Assert.Equal((0, 90, 90, 0), (element.InkColor!.C, element.InkColor.M, element.InkColor.Y, element.InkColor.K));

        vm.Editing!.InkHex = "cmyk(1";                                   // 打字打一半：先不当真，也不许清空
        Assert.Equal((0, 90, 90, 0), (element.InkColor!.C, element.InkColor.M, element.InkColor.Y, element.InkColor.K));
        return true;
    });

    [Fact]
    public void ArtworkThatCarriesItsOwnColoursIsNotOfferedAPen()
    {
        // 图片与矢量底图自带颜色，给它们一支笔色只会让人以为能改。
        Assert.False(new EditableElement(new TemplateElement { Kind = ElementKind.Image }, () => { }, () => { }).CanTintInk);
        Assert.False(new EditableElement(new TemplateElement { Kind = ElementKind.Vector }, () => { }, () => { }).CanTintInk);
        Assert.True(new EditableElement(new TemplateElement { Kind = ElementKind.Text }, () => { }, () => { }).CanTintInk);
        Assert.True(new EditableElement(new TemplateElement { Kind = ElementKind.Barcode }, () => { }, () => { }).CanTintInk);
    }

    [Fact]
    public void DegradedArtworkColourIsReportedOnExportNotOnlyOnImport()
    {
        // 底稿里的降级以前只在导入那一次说过：plan.Issues 全工程没人读，之后每次出片都悄悄用着降级结果。
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-ink-art-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bg.svg");
        File.WriteAllText(path, """
<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="0 0 100 80">
  <rect x="10" y="10" width="40" height="20" fill="url(#fade)"/>
</svg>
""");

        var template = Template(new TemplateElement
        {
            Kind = ElementKind.Vector, ImagePath = path, X = 10, Y = 10, Width = 50, Height = 40,
        });
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = new PageContentSource(template, new List<MarkRecord> { SampleRecords.StandardSample() }, "样例.xlsx"),
            PageIndexes = new List<int> { 0 },
            BaseName = "底稿件",
        };

        var file = Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default));
        Assert.Contains(file.Notes, n => n.Contains("渐变", StringComparison.Ordinal) && n.Contains("bg.svg", StringComparison.Ordinal));
    }
}
