using System.IO;
using System.Text;
using System.Windows;
using System.Xml.Linq;
using LabelGou.App.Export;
using LabelGou.App.Rendering;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Interop.Svg;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// M5 的矢量侧：底稿上屏、文字排版唯一口径、以及第五个出口（整版 → SVG）。
/// <para>
/// 产物一律用 <see cref="XDocument"/> 独立解析一遍再断言——"自己写的解析器读自己写的写出器"
/// 只算自证，能过 XDocument 才算真合法。落盘留件靠 <c>LABELGOU_TEST_ARTIFACTS</c>（与 M3 复核 PDF 同构）。
/// </para>
/// </summary>
public class SvgInterchangeTests
{
    private const double LabelW = 100;
    private const double LabelH = 80;
    private const double Margin = 8;

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static T OnStaThread<T>(Func<T> work)
        => StaWorker.RunAsync((progress, token) => work(), null, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static SheetSpec Spec(bool registration = false, bool allowRotate = false) => new()
    {
        Id = "test.a4.svg",
        Name = "测试 A4（SVG）",
        PaperWidthMm = 210,
        PaperHeightMm = 297,
        MarginLeftMm = Margin,
        MarginTopMm = Margin,
        MarginRightMm = Margin,
        MarginBottomMm = Margin,
        GutterXMm = 2,
        GutterYMm = 2,
        AllowRotate = allowRotate,
        RegistrationMarks = registration,
        CropMarkThicknessMm = 0.15,
        // 本套件比的是「一枚唛头在 SVG 里画得对不对」（描字/位图/矢量底/旋转），
        // 一页只放一枚才拿得住 Single()；「一枚 = 一张纸、页内四份全同」那档由
        // TextCaseAndSheetRepeatTests 与 PreviewSwitchesTests 守。
        RepeatSameLabelPerPage = false,
    };

    private static LabelTemplate FrameTemplate(params TemplateElement[] extra)
    {
        var template = new LabelTemplate
        {
            Id = "test.svg.frame",
            Name = "SVG 测试框线",
            WidthMm = LabelW,
            HeightMm = LabelH,
            BorderMm = 0.5,
        };
        template.Elements.AddRange(extra);
        return template;
    }

    private static PageContentSource SourceOf(LabelTemplate template, int labelCount = 4)
        => new(template, Enumerable.Range(1, labelCount).Select(_ => SampleRecords.StandardSample()).ToList(), "样例.xlsx");

    private static SheetExportRequest Request(SheetPlan plan, PageContentSource source, int pages = 1)
        => new()
        {
            Plan = plan,
            Source = source,
            PageIndexes = Enumerable.Range(0, pages).ToList(),
            BaseName = "唛头件",
        };

    private static string WriteTemp(string content, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"labelgou-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static XElement Layer(XDocument doc, string id)
        => doc.Root!.Descendants(Svg + "g").Single(g => (string?)g.Attribute("id") == id);

    /// <summary>取一个数值属性并按 double 返回（带精度那重载只吃 double，不吃 double?）。</summary>
    private static double Num(XElement element, string attribute)
        => (double?)element.Attribute(attribute) ?? throw new InvalidOperationException($"{element.Name.LocalName} 上没有 {attribute} 这个属性");

    // ---------- 底稿上屏 ----------

    [Fact]
    public void VectorBackgroundDrawsExactlyIntoItsElementBox() => OnStaThread(() =>
    {
        var path = WriteTemp("""
<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="0 0 100 80">
  <rect x="10" y="10" width="80" height="60" fill="none" stroke="#000000" stroke-width="0.4"/>
</svg>
""", ".svg");
        try
        {
            var plan = SvgDrawableBuilder.Load(path);
            Assert.NotNull(plan);
            Assert.Single(plan!.Paths);

            // 铺进 50×40 的框 = 等比缩到 50%，几何包围盒应当正好落在框里那一段（包含半笔宽的外打）
            var drawing = SvgDrawableBuilder.BuildDrawing(plan, 50, 40, 1.0);
            Assert.NotNull(drawing);
            var bounds = drawing!.Bounds;
            var expected = new Rect(Mm.ToDiu(5), Mm.ToDiu(5), Mm.ToDiu(40), Mm.ToDiu(30));
            Assert.True(Math.Abs(bounds.Left - expected.Left) < 1
                && Math.Abs(bounds.Top - expected.Top) < 1
                && Math.Abs(bounds.Width - expected.Width) < 1
                && Math.Abs(bounds.Height - expected.Height) < 1,
                $"底图包围盒 {bounds} 与期望 {expected} 差太多");
        }
        finally
        {
            File.Delete(path);
        }
        return true;
    });

    [Fact]
    public void CurvesStayCurvesWhenDrawnOnScreen() => OnStaThread(() =>
    {
        var path = WriteTemp("""
<svg xmlns="http://www.w3.org/2000/svg" width="20mm" height="20mm" viewBox="0 0 20 20">
  <circle cx="10" cy="10" r="8" fill="#000000"/>
</svg>
""", ".svg");
        try
        {
            var plan = SvgDrawableBuilder.Load(path);
            var geometry = Assert.Single(plan!.Paths).Geometry as System.Windows.Media.PathGeometry;
            Assert.NotNull(geometry);
            var segments = geometry!.Figures[0].Segments;
            // 圆必须还是贝塞尔，展平成折线就是"底图保真"失守（四个象限 = 四段）
            Assert.Equal(4, segments.Count);
            Assert.All(segments, s => Assert.IsType<System.Windows.Media.BezierSegment>(s));
        }
        finally
        {
            File.Delete(path);
        }
        return true;
    });

    [Fact]
    public void SameBackgroundFileIsParsedOnlyOnce() => OnStaThread(() =>
    {
        var path = WriteTemp("""
<svg xmlns="http://www.w3.org/2000/svg" width="10mm" height="10mm" viewBox="0 0 10 10"><rect width="10" height="10"/></svg>
""", ".svg");
        try
        {
            SvgDrawableBuilder.ClearCache();
            var first = SvgDrawableBuilder.Load(path);
            var countAfterFirst = SvgDrawableBuilder.CachedCount;
            var second = SvgDrawableBuilder.Load(path);
            Assert.Same(first, second);                       // 命中缓存 → 同一个实例
            Assert.Equal(countAfterFirst, SvgDrawableBuilder.CachedCount);
        }
        finally
        {
            SvgDrawableBuilder.ClearCache();
            File.Delete(path);
        }
        return true;
    });

    [Fact]
    public void MissingBackgroundFileDegradesToNullPlan() => OnStaThread(() =>
    {
        Assert.Null(SvgDrawableBuilder.Load(Path.Combine(Path.GetTempPath(), "无此底稿" + Guid.NewGuid().ToString("N") + ".svg")));
        return true;
    });

    // ---------- 文字口径（定案 D10） ----------

    private static TextItem OverflowingText(double fontSizePt = 12) => new(
        "MADE IN CHINA 中华人民共和国海关监管货物", 4, 4, 24, 5, "Microsoft YaHei", fontSizePt,
        false, HorizontalAlign.Left, ShrinkToFit: true, MaxLines: 3);

    [Fact]
    public void FitDecisionDoesNotDependOnZoomLevel() => OnStaThread(() =>
    {
        var text = OverflowingText();
        var atOne = TextFit.Solve(text, 1.0, 1.0);
        var atZoom = TextFit.Solve(text, 3.125, 1.5);     // 屏幕放大 3 倍多

        Assert.NotNull(atOne);
        Assert.NotNull(atZoom);
        Assert.True(atOne!.Shrunk, "这么长的字塞进 24×5mm 应该触发缩字号");
        // 屏幕上看见的字号比例必须等于印出来的比例：只差一个 scale，不许各缩各的
        Assert.Equal(atOne.EmSizeDiu * 3.125, atZoom!.EmSizeDiu, 6);
        Assert.Equal(atOne.ShrinkRatio, atZoom.ShrinkRatio, 6);
        return true;
    });

    [Fact]
    public void ShrinkRespectsTheSixtyTwoPercentFloor() => OnStaThread(() =>
    {
        var fit = TextFit.Solve(OverflowingText() with { Content = new string('国', 200) }, 1.0, 1.0);
        Assert.NotNull(fit);
        Assert.True(fit!.ShrinkRatio >= TextFit.MinEmSizeRatio - 0.005,
            $"缩到 {fit.ShrinkRatio:0.###} 已经低于 62% 下限，宁可截断也不印蚂蚁字");
        return true;
    });

    [Fact]
    public void WithoutShrinkToFitTheRequestedSizeIsKept() => OnStaThread(() =>
    {
        var fit = TextFit.Solve(OverflowingText() with { ShrinkToFit = false }, 1.0, 1.0);
        Assert.NotNull(fit);
        Assert.Equal(1.0, fit!.ShrinkRatio, 9);
        Assert.Equal(TextFit.RequestedEmSizeDiu(12), fit.EmSizeDiu, 9);
        return true;
    });

    [Fact]
    public void ZeroAreaTextElementIsSkippedNotCrashed() => OnStaThread(() =>
    {
        Assert.Null(TextFit.Solve(OverflowingText() with { Width = 0 }, 1.0, 1.0));
        return true;
    });

    // ---------- 几何双向翻译 ----------

    [Fact]
    public void GeometryRoundTripKeepsBoundingBox() => OnStaThread(() =>
    {
        var commands = new List<Core.Interop.Svg.SvgPathCommand>
        {
            new('M', new double[] { 10, 10 }),
            new('C', new double[] { 30, 10, 30, 40, 50, 40 }),
            new('L', new double[] { 50, 60 }),
            new('Z', Array.Empty<double>()),
        };
        var geometry = SvgGeometryConverter.BuildGeometry(commands, evenOdd: false, 96.0 / 25.4);
        var back = SvgGeometryConverter.ToCommands(geometry, 25.4 / 96.0, out var evenOdd);

        Assert.False(evenOdd);
        Assert.Equal(4, back.Count);
        Assert.Equal('Z', back[3].Command);
        Assert.Equal(50, back[2].Args[0], 6);
        Assert.Equal(60, back[2].Args[1], 6);
        return true;
    });

    // ---------- 第五个出口 ----------

    [Fact]
    public void SheetSvgIsMmCanvasWithTheExpectedLayers() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(registration: true), LabelW, LabelH, 4);
        var file = Assert.Single(SheetSvgWriter.WritePage(Request(plan, SourceOf(FrameTemplate()), 1), 0, SvgExportOptions.Default));

        Assert.Equal("唛头件_P01.svg", file.FileName);
        var doc = XDocument.Parse(file.Xml);           // 过不了这一关就是非法 XML
        Assert.EndsWith("mm", (string?)doc.Root!.Attribute("width"));
        Assert.Equal("210mm", (string?)doc.Root.Attribute("width"));
        Assert.Equal("297mm", (string?)doc.Root.Attribute("height"));
        Assert.Equal("0 0 210 297", (string?)doc.Root.Attribute("viewBox"));

        var ids = doc.Root!.Descendants(Svg + "g").Select(g => (string?)g.Attribute("id")).ToList();
        Assert.Contains("registration", ids);
        Assert.Contains("labels", ids);
        Assert.Contains("label-0001", ids);
        Assert.DoesNotContain("notes", ids);           // 默认关
        Assert.DoesNotContain("sheet", ids);
        Assert.NotNull(doc.Descendants(Svg + "metadata").FirstOrDefault());
        // 标签数 = 这一页实际摆了几枚（100×80 在 A4 竖排一列三枚，第四枚已经在下一页）
        Assert.Equal(plan.PlacementsOnPage(1).Count, Layer(doc, "labels").Elements(Svg + "g").Count());
        Assert.InRange(plan.PerPage, 1, 4);
        return true;
    });

    [Fact]
    public void LabelLandsWhereTheImpositionSays() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var placement = plan.PlacementsOnPage(1).First();
        var file = Assert.Single(SheetSvgWriter.WritePage(Request(plan, SourceOf(FrameTemplate())), 0, SvgExportOptions.Default));
        var doc = XDocument.Parse(file.Xml);
        var group = Layer(doc, "labels").Elements(Svg + "g").First();

        Assert.Equal($"translate({placement.X:0.###},{placement.Y:0.###})", (string?)group.Attribute("transform"));
        return true;
    });

    /// <summary>
    /// 第五个出口也照"纸就标签这么大"裁（第 61 棒，用户：「文字超出去时打印的效果是直接截断，在整张纸上也是这样」）。
    /// <para>预览/打印/PDF 那一侧是 <c>LabelRenderer</c> 的一句 PushClip；SVG 交给 CDR 打开，
    /// 不写 clip-path 它就真的一整行收进去——多出来的那一截是印不出的字，不该出现在出片文件里。</para>
    /// </summary>
    [Fact]
    public void EveryLabelGroupIsClippedToTheLabelItself() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var file = Assert.Single(SheetSvgWriter.WritePage(Request(plan, SourceOf(FrameTemplate())), 0, SvgExportOptions.Default));
        var doc = XDocument.Parse(file.Xml);

        var labels = doc.Root!.Descendants(Svg + "g")
            .Where(g => ((string?)g.Attribute("id"))?.StartsWith("label-", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(labels);
        Assert.All(labels, g => Assert.NotNull(g.Attribute("clip-path")));

        var rect = doc.Descendants(Svg + "clipPath").Single().Descendants(Svg + "rect").Single();
        Assert.Equal(LabelW, Num(rect, "width"), 3);
        Assert.Equal(LabelH, Num(rect, "height"), 3);
        return true;
    });

    [Fact]
    public void RotatedLabelUsesTranslateThenRotateLikeThePreview() => OnStaThread(() =>
    {
        // 取 70×30：不转 = 2列×8行=16，转 90° = 6列×3行=18，引擎应选旋转；旋转件必须与 DrawRotated 同一套变换
        var plan = ImpositionEngine.Build(Spec(allowRotate: true), 70, 30, 20);
        var rotated = plan.Placements.First(p => p.Rotated);
        var source = new PageContentSource(FrameTemplate(),
            Enumerable.Range(1, 20).Select(_ => SampleRecords.StandardSample()).ToList(), "样例.xlsx");
        var file = Assert.Single(SheetSvgWriter.WritePage(Request(plan, source), rotated.PageIndex - 1, SvgExportOptions.Default));
        var doc = XDocument.Parse(file.Xml);

        var group = Layer(doc, "labels").Elements(Svg + "g")
            .Single(g => (string?)g.Attribute("id") == $"label-{rotated.LabelIndex:D4}");
        Assert.Equal($"translate({rotated.X + rotated.Height:0.###},{rotated.Y:0.###}) rotate(90)",
            (string?)group.Attribute("transform"));
        return true;
    });

    [Fact]
    public void TextIsOutlinedByDefaultAndEditableOnRequest() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var template = FrameTemplate(new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = "{{DestinationPort}}",
            X = 6,
            Y = 6,
            Width = 60,
            Height = 8,
            FontSizePt = 12,
        });
        var request = Request(plan, SourceOf(template, 1));

        var outlined = Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default));
        var outlinedDoc = XDocument.Parse(outlined.Xml);
        var labelLayer = Layer(outlinedDoc, "labels").Elements(Svg + "g").Single();
        Assert.Contains(labelLayer.Descendants(Svg + "path"), p => ((string?)p.Attribute("d"))!.Length > 40);
        Assert.Empty(labelLayer.Descendants(Svg + "text"));

        var editable = Assert.Single(SheetSvgWriter.WritePage(request, 0,
            new SvgExportOptions { TextAsOutlines = false }));
        var editableDoc = XDocument.Parse(editable.Xml);
        var text = Assert.Single(Layer(editableDoc, "labels").Descendants(Svg + "text"));
        Assert.Equal("LOS ANGELES, USA", (string?)text.Value);
        return true;
    });

    [Fact]
    public void SvgOutletCopiesThePreviewTextFitDecision() => OnStaThread(() =>
    {
        // D10 的试金石：14pt 的单行字只给 4mm 高的框，注定要缩。
        // 预览与第五个出口必须用同一个字号、画出同一份几何，否则就成了“五条出口各画一套”。
        var template = new LabelTemplate
        {
            Id = "test.svg.fitparity",
            Name = "转曲口径对照",
            WidthMm = LabelW,
            HeightMm = LabelH,
            BorderMm = 0,               // 只留一个元素，下面的 Single() 才有意义
        };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = "{{DestinationPort}}",
            X = 4,
            Y = 6,
            Width = 60,
            Height = 4,
            FontSizePt = 14,
        });

        var record = SampleRecords.StandardSample();
        var layout = LayoutEngine.Build(template, record, new LayoutContext(1, 1));
        var text = layout.Items.OfType<TextItem>().Single();
        var fit = TextFit.Solve(text, scale: 1.0, pixelsPerDip: 1.0);
        Assert.NotNull(fit);
        Assert.Equal(1, fit!.LineCount);
        Assert.True(fit.CanonicalEmSizeDiu < TextFit.RequestedEmSizeDiu(14),
            "这份夹具得真的缩了，否则下面比的是两个没被考验过的分支");

        var group = new System.Windows.Media.DrawingGroup();
        using (var dc = group.Open())
        {
            LabelRenderer.Draw(dc, layout, scale: 1.0, offsetX: 0, offsetY: 0,
                showGuides: false, pixelsPerDip: 1.0, drawBackground: false);
        }
        var drawn = group.Bounds;                       // scale=1 → DIU 与毫米只差一个 Mm.ToDiu

        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var source = new PageContentSource(template, new List<MarkRecord> { record }, "样例.xlsx");
        var request = Request(plan, source);

        var outlined = Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default));
        var outlinedDoc = XDocument.Parse(outlined.Xml);
        // 框线那种只描边的 path 不参与：取有填充的那一段，那就是字的轮廓
        var d = (string?)Layer(outlinedDoc, "labels").Descendants(Svg + "path")
            .Single(p => ((string?)p.Attribute("fill")) is { } f && f != "none").Attribute("d");
        var commands = SvgParser.Parse(
                $"""<svg xmlns="http://www.w3.org/2000/svg" width="{LabelW}mm" height="{LabelH}mm" viewBox="0 0 {LabelW} {LabelH}"><path d="{d}"/></svg>""")
            .Document.Paths.Single().Commands;
        var ink = SvgGeometryConverter.BuildGeometry(commands, evenOdd: false, unitPerMm: Mm.ToDiu(1)).Bounds;

        // 两条出口的“边界”语义不一样：预览那个是排版盒（含侧余量），转曲写出来的是墨迹。
        // 但两者都随字号线性缩放，所以行宽按百分比卡就能把“差一档字号”顶出来。
        static void SameInk(double previewDiu, double svgDiu, string what)
        {
            var drift = Math.Abs(previewDiu - svgDiu) / Math.Max(1e-6, Math.Abs(previewDiu));
            Assert.True(drift <= 0.04, $"{what}两条出口差了 {drift:P1}，字号决定已经分叉");
        }

        Assert.True(Math.Abs(Mm.FromDiu(drawn.X) - Mm.FromDiu(ink.X)) <= 0.5, "起笔位置两条出口对不上");
        Assert.True(Math.Abs(Mm.FromDiu(drawn.Y) - Mm.FromDiu(ink.Y)) <= 0.5, "基线位置两条出口对不上");
        SameInk(drawn.Width, ink.Width, "行宽");
        // 高度不能直接比：预览那个边界含行距（ascender+descender），而墨迹只有大写字母那么高。
        // 行宽是随字号线性走的，差一档就超 4%，上面那条已经卡得住；这里只兜一句底：确实画上东西了。
        Assert.True(Mm.FromDiu(ink.Height) > 0.5, "转曲后的墨迹高不到半毫米，等于没画上");

        // 未转曲那条分支：<text> 的字号就是 TextFit 定的那个，不是请求的 14pt
        var editable = Assert.Single(SheetSvgWriter.WritePage(request, 0,
            new SvgExportOptions { TextAsOutlines = false }));
        var textEl = Layer(XDocument.Parse(editable.Xml), "labels").Descendants(Svg + "text").Single();
        var sizeText = (string?)textEl.Attribute("font-size") ?? throw new InvalidOperationException("<text> 上没写 font-size");
        var sizeMm = double.Parse(sizeText, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(sizeMm < Mm.PointToMm(14), "件子里写回的是请求字号，缩字号没传到出口");
        Assert.Equal(fit.EmSizeDiu * (25.4 / 96.0), sizeMm, 3);
        return true;
    });

    [Fact]
    public void RotatedAndStretchedTextCarriesGeometryTransformIntoSvg() => OnStaThread(() =>
    {
        // 第 43 棒：文字带旋转 + 字面拉伸时，SVG 出口必须把那一段包进一个带 transform 的 <g>，
        // 变换串与预览端 PushGeometry 同序同锚点——否则"预览转了、件子没转"就是五出口分叉。
        var template = new LabelTemplate
        {
            Id = "test.svg.geom", Name = "转又拉的一行",
            WidthMm = LabelW, HeightMm = LabelH, BorderMm = 0,
        };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "{{ItemNo}}",
            X = 6, Y = 6, Width = 60, Height = 8, FontSizePt = 12,
            RotationDeg = 30, TextScaleX = 2,
        });
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var request = Request(plan, SourceOf(template, 1));

        // 转曲那条
        var outlined = Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default));
        var group = Layer(XDocument.Parse(outlined.Xml), "labels")
            .Descendants(Svg + "g")
            .Single(g => ((string?)g.Attribute("transform"))?.Contains("rotate(30") == true);

        // 第 52 棒定口径、第 81 棒才真的量得准：旋转绕【拉伸后的墨迹中心】，不再绕排版盒（隐形行带）中心。
        // 参照用编辑器画框那份墨迹（TextInkBox）——从前这里拿 InkLeftDiu + 行盒宽自己再算一遍，
        // 那正是第 81 棒要消掉的第二套算术（而且它漏掉了折行那条路的居中）。钉的是：SVG 的锚点 == 屏幕上那个框的中心。
        var layout0 = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1));
        var item0 = layout0.Items.OfType<TextItem>().Single();
        var ink0 = TextInkBox.Measure(item0)!.Value;
        var anchorX = ink0.X + ink0.Width / 2;
        var anchorY = ink0.Y + ink0.Height / 2;
        Assert.True(Math.Abs(anchorX - 36) > 0.5,
            "锚点没离开排版盒中心——要么这份夹具不左对齐，要么第 52 棒的口径被改回去了");
        Assert.Equal(SvgBuilder.GeometryTransform(36, 10, anchorX, anchorY, 30, 2, 1),
            (string?)group.Attribute("transform"));

        // 未转曲那条也要带同样的变换（<text> 也被包进 <g>）
        var editable = Assert.Single(SheetSvgWriter.WritePage(request, 0,
            new SvgExportOptions { TextAsOutlines = false }));
        var textG = Layer(XDocument.Parse(editable.Xml), "labels").Descendants(Svg + "text").Single();
        var wrap = textG.Ancestors(Svg + "g").First(g => ((string?)g.Attribute("transform"))?.Contains("rotate") == true);
        Assert.Contains("scale(2,1)", (string?)wrap.Attribute("transform"));
        return true;
    });

    [Fact]
    public void PlainTextExportsNoGeometryGroup() => OnStaThread(() =>
    {
        // 回归：没有旋转/拉伸的普通文字【不该】多套一层 transform 组，否则存量件子的形状会变。
        var template = new LabelTemplate
        {
            Id = "test.svg.nogeo", Name = "普通一行",
            WidthMm = LabelW, HeightMm = LabelH, BorderMm = 0,
        };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "{{ItemNo}}",
            X = 6, Y = 6, Width = 60, Height = 8, FontSizePt = 12,
        });
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var file = Assert.Single(SheetSvgWriter.WritePage(
            Request(plan, SourceOf(template, 1)), 0, SvgExportOptions.Default));
        var labelGroup = Layer(XDocument.Parse(file.Xml), "labels").Elements(Svg + "g").Single();
        // 那一枚标签自己的落位组里，不该再有第二层带 rotate/scale 的几何组
        Assert.DoesNotContain(labelGroup.Descendants(Svg + "g"),
            g => ((string?)g.Attribute("transform")) is string t && (t.Contains("rotate") || t.Contains("scale")));
        return true;
    });

    [Fact]
    public void PreviewPushGeometryActuallyMovesTheInk() => OnStaThread(() =>
    {
        // 像素级证据：同一行字，横向抻 2 倍后，预览画出来的墨迹宽要明显变大；
        // 转 90° 后包围盒要接近"宽高互换"。只验 PushGeometry 真的动了画面，不是把变换挂上却没生效。
        static Rect BoundsOf(double rotationDeg, double sx)
        {
            var template = new LabelTemplate
            {
                Id = "test.geo", Name = "几何", WidthMm = LabelW, HeightMm = LabelH, BorderMm = 0,
            };
            template.Elements.Add(new TemplateElement
            {
                Kind = ElementKind.Text, Text = "{{ItemNo}}",
                X = 20, Y = 20, Width = 40, Height = 8, FontSizePt = 20,
                RotationDeg = rotationDeg, TextScaleX = sx,
            });
            var layout = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1));
            var group = new System.Windows.Media.DrawingGroup();
            using (var dc = group.Open())
                LabelRenderer.Draw(dc, layout, 1.0, 0, 0, false, 1.0, drawBackground: false);
            return group.Bounds;
        }

        var plain = BoundsOf(0, 1);
        var stretched = BoundsOf(0, 2);
        Assert.True(stretched.Width > plain.Width * 1.7,
            $"抻两倍后墨迹宽没跟上：plain={plain.Width:F1} stretched={stretched.Width:F1}");
        Assert.True(Math.Abs(stretched.Height - plain.Height) < plain.Height * 0.15 + 1,
            "横向抻不该显著改变行高");

        var turned = BoundsOf(90, 1);
        // 原来扁长的一行转 90° → 变得更高更窄
        Assert.True(turned.Height > turned.Width, $"转 90° 后没变成竖势：{turned.Width:F1}×{turned.Height:F1}");
        return true;
    });

    [Fact]
    public void TruncatedTextCarriesTheRedBackdropOnEverySvgBranch() => OnStaThread(() =>
    {
        // 被省略号截断 = 这一格没印全。预览/打印两者都认（LabelRenderer 认 Flagged || Truncated），
        // SVG 出口以前只认 Flagged，而且未转曲那条分支在画底之前就 return 了 —— 件子上看着完全正常。
        var template = new LabelTemplate
        {
            Id = "test.svg.truncated",
            Name = "注定截断的一格",
            WidthMm = LabelW,
            HeightMm = LabelH,
            BorderMm = 0,               // 只留一个元素，下面的 Single() 才有意义
        };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = "{{DestinationPort}}",
            X = 4,
            Y = 6,
            Width = 12,
            Height = 4,
            FontSizePt = 14,
            MaxLines = 1,               // 单行不许折行：宽度不够只能缩，缩到下限仍装不下才是「被省略号截断」
            WrapWidthMm = 12,           // 第 46 棒：这条路要显式给折行宽度（默认 0 = 永不折行，宽度不再参与缩字）
        });

        var record = SampleRecords.StandardSample();
        var layout = LayoutEngine.Build(template, record, new LayoutContext(1, 1));
        var fit = TextFit.Solve(layout.Items.OfType<TextItem>().Single(), scale: 1.0, pixelsPerDip: 1.0);
        Assert.NotNull(fit);
        Assert.True(fit!.Truncated, "这份夹具没真的截断，那下面比的就是两个没被考验过的分支");

        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var request = Request(plan, new PageContentSource(template, new List<MarkRecord> { record }, "样例.xlsx"));

        static List<XElement> InOrder(XDocument doc) => Layer(doc, "labels").Descendants().ToList();
        static int BackdropIndex(List<XElement> nodes)
            => nodes.FindIndex(e => e.Name == Svg + "rect"
                && ((string?)e.Attribute("fill"))?.StartsWith("#c62828", StringComparison.OrdinalIgnoreCase) == true);

        var outlined = XDocument.Parse(Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default)).Xml);
        Assert.True(BackdropIndex(InOrder(outlined)) >= 0, "转曲那条出口没画淡红底");

        var editable = XDocument.Parse(Assert.Single(SheetSvgWriter.WritePage(request, 0,
            new SvgExportOptions { TextAsOutlines = false })).Xml);
        var nodes = InOrder(editable);
        var backdrop = BackdropIndex(nodes);
        var text = nodes.FindIndex(e => e.Name == Svg + "text");
        Assert.True(backdrop >= 0, "未转曲（单行 <text>）那条出口没画淡红底——只有它会把提示弄丢");
        Assert.True(text >= 0);
        Assert.True(backdrop < text, "淡红底必须画在字之前，否则会把字整块盖住");
        return true;
    });

    [Fact]
    public void ReferenceElementsNeverReachTheSheet() => OnStaThread(() =>
    {
        var asset = WriteTemp("fake", ".png");
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var template = FrameTemplate(new TemplateElement
        {
            Kind = ElementKind.Image,
            ImagePath = asset,
            X = 0,
            Y = 0,
            Width = LabelW,
            Height = LabelH,
            ReferenceOnly = true,
        });
        var file = Assert.Single(SheetSvgWriter.WritePage(Request(plan, SourceOf(template, 1)), 0, SvgExportOptions.Default));

        // 预览里带参考图也得先经 LayoutContext.IncludeReference=true，导出快照默认不含
        Assert.DoesNotContain("<image", file.Xml, StringComparison.Ordinal);
        File.Delete(asset);
        return true;
    });

    [Fact]
    public void VectorBackgroundIsInlinedAsItsOwnGroup() => OnStaThread(() =>
    {
        var asset = WriteTemp("""
<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="0 0 100 80">
  <rect x="4" y="4" width="92" height="72" fill="none" stroke="#000000" stroke-width="0.4"/>
  <text x="6" y="14" font-size="5pt">固定标签文字</text>
</svg>
""", ".svg");
        try
        {
            var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
            var template = FrameTemplate(new TemplateElement
            {
                Kind = ElementKind.Vector,
                ImagePath = asset,
                X = 0,
                Y = 0,
                Width = LabelW,
                Height = LabelH,
            });
            var file = Assert.Single(SheetSvgWriter.WritePage(Request(plan, SourceOf(template, 1)), 0, SvgExportOptions.Default));
            var doc = XDocument.Parse(file.Xml);

            var background = Layer(doc, "background-1");
            Assert.Equal("translate(0,0) scale(1,1)", (string?)background.Attribute("transform"));
            Assert.Contains(background.Descendants(Svg + "path"), p => ((string?)p.Attribute("d"))!.Contains("C"));
            // 底稿里的文字也转曲了：CDR 那台机器没有这款中文字体也不能掉字
            Assert.Empty(background.Descendants(Svg + "text"));
            Assert.Contains(background.Descendants(Svg + "path"), p => ((string?)p.Attribute("d"))!.Length > 30);
        }
        finally
        {
            File.Delete(asset);
        }
        return true;
    });

    [Fact]
    public void DraftThroughImportToSheetSvgKeepsArtworkAndLiveField() => OnStaThread(() =>
    {
        // 全链路（M5 要解决的就是这件事本身）：CorelDRAW 导出的底稿 → 导入 → v2 模板 + 规范化底图 → 拼版 → 矢量件。
        var root = Path.Combine(Path.GetTempPath(), "labelgou-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var draft = Path.Combine(root, "底稿.svg");
        File.WriteAllText(draft, """
<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="0 0 100 80">
  <rect x="3" y="3" width="94" height="74" rx="6" ry="6" fill="none" stroke="#000000" stroke-width="0.6"/>
  <circle cx="50" cy="22" r="8" fill="none" stroke="#000000" stroke-width="0.5"/>
  <text x="10" y="58" font-size="9pt">LOS ANGELES, USA</text>
  <text x="10" y="70" font-size="6pt">FLAMMABLE LIQUID</text>
</svg>
""", new UTF8Encoding(false));
        try
        {
            var plan = TemplateImporter.FromSvgFile(draft);
            Assert.Empty(plan.Issues.ErrorMessages());
            Assert.Equal(LabelW, plan.LabelWidthMm, 1);
            Assert.Equal(LabelH, plan.LabelHeightMm, 1);

            // 三信号：与样例数据撞上的那行自动预勾并绑字段，对不上的留在底图里
            var port = plan.Texts.Single(t => t.Content.Contains("LOS ANGELES"));
            Assert.True(port.Promote);
            Assert.Equal(MarkFieldKey.DestinationPort, port.Field);
            Assert.True(port.Confidence > 0.9);
            Assert.DoesNotContain(plan.Texts, t => t.Content.Contains("FLAMMABLE") && t.Promote);

            var (template, issues) = plan.Build("底稿全链路", new TemplateStore(root));
            Assert.NotNull(template);
            Assert.DoesNotContain(issues, i => i.Severity == IssueLevel.Error);
            Assert.Equal(2, template!.Elements.Count);            // 底图占 1 位 + 提升出来的文字 1 个（D5）
            var vector = template.Elements.Single(e => e.Kind == ElementKind.Vector);
            var asset = Path.Combine(root, vector.ImagePath!);
            Assert.True(File.Exists(asset), "底图资产没落到模板目录里");

            // 被提升的那行必须从底图里剔掉，否则纸上会重影（D6）
            var background = File.ReadAllText(asset);
            Assert.DoesNotContain("LOS ANGELES", background, StringComparison.Ordinal);
            Assert.Contains("FLAMMABLE LIQUID", background, StringComparison.Ordinal);

            // 渲染端的资产根固定是 %APPDATA%\LabelGou\templates（LayoutEngine.ResolveAsset），
            // 单测不能往用户目录写东西，所以这里直接给绝对路径 —— 运行时两者本来就是同一个地方。
            vector.ImagePath = asset;

            var sheet = ImpositionEngine.Build(Spec(), template.WidthMm, template.HeightMm, 1);
            var source = new PageContentSource(template, new List<MarkRecord> { SampleRecords.StandardSample() }, "全链路.xlsx");
            var sheetFile = Assert.Single(SheetSvgWriter.WritePage(Request(sheet, source), 0, SvgExportOptions.Default));
            var doc = XDocument.Parse(sheetFile.Xml);

            var inlined = Layer(doc, "background-1");
            Assert.Contains(inlined.Descendants(Svg + "path"), p => ((string?)p.Attribute("d"))!.Contains("C"));
            Assert.Empty(inlined.Descendants(Svg + "text"));      // 底图里的文字也转曲，对方缺字体也不掉字
            Assert.Single(Layer(doc, "labels").Elements(Svg + "g"));

            var editable = Assert.Single(SheetSvgWriter.WritePage(Request(sheet, source), 0,
                new SvgExportOptions { TextAsOutlines = false }));
            var editableDoc = XDocument.Parse(editable.Xml);
            var live = Assert.Single(Layer(editableDoc, "labels").Descendants(Svg + "text"));
            Assert.Equal("LOS ANGELES, USA", (string?)live.Value);
            Assert.Empty(Layer(editableDoc, "background-1").Descendants(Svg + "text"));

            var keep = Environment.GetEnvironmentVariable("LABELGOU_TEST_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(keep))
            {
                Directory.CreateDirectory(keep);
                File.WriteAllText(Path.Combine(keep, "chain-sheet.svg"), sheetFile.Xml, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(keep, "chain-label-editable.svg"), editable.Xml, new UTF8Encoding(false));
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        return true;
    });

    [Fact]
    public void RasterLogoIsEmbeddedOrReferencedOnRequest() => OnStaThread(() =>
    {
        // 1×1 的合法 PNG，够小，能走内嵌那条路
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
        var asset = Path.Combine(Path.GetTempPath(), $"labelgou-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(asset, png);

        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var template = FrameTemplate(new TemplateElement
        {
            Kind = ElementKind.Image,
            ImagePath = asset,
            X = 10,
            Y = 10,
            Width = 20,
            Height = 20,
        });
        var request = Request(plan, SourceOf(template, 1));

        var embedded = Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default));
        var doc = XDocument.Parse(embedded.Xml);
        var image = Assert.Single(Layer(doc, "labels").Descendants(Svg + "image"));
        Assert.StartsWith("data:image/png;base64,", (string?)image.Attribute("href"));
        Assert.Equal("10", (string?)image.Attribute("x"));

        var linked = Assert.Single(SheetSvgWriter.WritePage(request, 0, new SvgExportOptions { EmbedRasterImages = false }));
        Assert.Contains("file:///", linked.Xml, StringComparison.Ordinal);
        File.Delete(asset);
        return true;
    });

    [Fact]
    public void CropMarkLinesMatchTheImpositionEngine() => OnStaThread(() =>
    {
        var spec = Spec();
        var plan = ImpositionEngine.Build(spec, LabelW, LabelH, 4);
        var file = Assert.Single(SheetSvgWriter.WritePage(Request(plan, SourceOf(FrameTemplate())), 0, SvgExportOptions.Default));
        var doc = XDocument.Parse(file.Xml);

        var expected = ImpositionEngine.BuildMarks(spec, plan, 1).Count(m => m.Kind == SheetMarkKind.CropMark);
        Assert.Equal(expected, Layer(doc, "crop-marks").Elements(Svg + "line").Count());
        Assert.True(expected > 0, "这个纸规本该有角线，一条都没有说明标记层取错了");
        return true;
    });

    [Fact]
    public void PerLabelModeWritesOneUprightFilePerLabel() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var onPage = plan.PlacementsOnPage(1).Count;
        var files = SheetSvgWriter.WritePage(Request(plan, SourceOf(FrameTemplate())), 0,
            new SvgExportOptions { Mode = SvgExportMode.PerLabel });

        Assert.Equal(onPage, files.Count);
        Assert.Equal("唛头件_L0001.svg", files[0].FileName);
        var last = files[^1];
        var doc = XDocument.Parse(last.Xml);
        Assert.Equal("100mm", (string?)doc.Root!.Attribute("width"));
        Assert.Equal("80mm", (string?)doc.Root.Attribute("height"));
        Assert.DoesNotContain("rotate(90)", last.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain(doc.Root.Descendants(Svg + "g"), g => (string?)g.Attribute("id") == "crop-marks");
        // 件子里没有角线，metadata 里也不许报有角线（口径说明必须与产物一致）
        var meta = doc.Root.Descendants(Svg + "metadata").Single().Value;
        Assert.Contains("一枚一图", meta, StringComparison.Ordinal);
        Assert.DoesNotContain("角线", meta, StringComparison.Ordinal);
        Assert.NotNull(Layer(doc, "label"));                // 单枚文件就一个 label 层
        return true;
    });

    [Fact]
    public void NotesLayerShowsFieldTokensWhenAsked() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var template = FrameTemplate(new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = "合同号 {{ContractNo}}",
            X = 6,
            Y = 6,
            Width = 60,
            Height = 8,
        });
        var file = Assert.Single(SheetSvgWriter.WritePage(Request(plan, SourceOf(template, 1)), 0,
            new SvgExportOptions { IncludeNotes = true }));
        var doc = XDocument.Parse(file.Xml);

        Assert.Contains("ContractNo", file.Xml, StringComparison.Ordinal);      // metadata 里的字段清单
        var notes = Layer(doc, "notes");
        Assert.Contains(notes.Descendants(Svg + "rect"), r => (string?)r.Attribute("width") == "60");
        return true;
    });

    [Fact]
    public void ExportSvgWritesFilesAndReportsSummary() => OnStaThread(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "labelgou-svg-" + Guid.NewGuid().ToString("N"));
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var outcome = SheetExportService.ExportSvg(Request(plan, SourceOf(FrameTemplate())), directory,
            SvgExportOptions.Default, null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        var written = Assert.Single(outcome.Files);
        Assert.True(File.Exists(written));
        Assert.Contains("个 SVG", outcome.Summary);
        Assert.Contains("文字转曲", outcome.Summary);
        Directory.Delete(directory, recursive: true);
        return true;
    });

    [Fact]
    public void ExportSvgRefusesTooManyFiles() => OnStaThread(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "labelgou-svg-" + Guid.NewGuid().ToString("N"));
        // 一页 3 枚，要写出 600 个以上就得 200 页往后；上限判的是“真会写出的文件数”
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, (SheetExportService.MaxSvgFiles + 20) * 3);
        var outcome = SheetExportService.ExportSvg(
            Request(plan, SourceOf(FrameTemplate(), plan.LabelCount), plan.PageCount), directory,
            new SvgExportOptions { Mode = SvgExportMode.PerLabel }, null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("上限", outcome.Error);
        Assert.False(Directory.Exists(directory));
        return true;
    });

    [Fact]
    public void ExportSvgCountsTheFilesItActuallyWritesNotTheWholeBatch() => OnStaThread(() =>
    {
        // 整批 650 枚（远超上限）但只导第一页：上一版按 LabelCount 算，把这单合法请求也拦下了
        var directory = Path.Combine(Path.GetTempPath(), "labelgou-svg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, SheetExportService.MaxSvgFiles + 50);
            var outcome = SheetExportService.ExportSvg(Request(plan, SourceOf(FrameTemplate()), 1), directory,
                new SvgExportOptions { Mode = SvgExportMode.PerLabel }, null, CancellationToken.None);

            Assert.True(outcome.Success, outcome.Error);
            Assert.Equal(plan.PlacementsOnPage(1).Count, Directory.GetFiles(directory, "*.svg").Length);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
        return true;
    });

    // ---------- 条码（第 17 棒）：矢量口与绘制口拿的是同一份毫米 ----------

    private static MarkRecord RecordWithBarcodeColumn(string value) => MarkRecord.Builder()
        .SetRow(1, "样例.xlsx")
        .Set(MarkFieldKey.DestinationPort, "LOS ANGELES")
        .SetCustom("col:条码", value)
        .Build();

    private static LabelTemplate BarcodeTemplate(BarcodeSymbology symbology, bool showText)
        => FrameTemplate(new TemplateElement
        {
            Kind = ElementKind.Barcode,
            Text = "{{col:条码}}",
            Symbology = symbology,
            ShowBarcodeText = showText,
            FontSizePt = 7,
            X = 4, Y = 52, Width = 92, Height = 22,
        });

    [Fact]
    public void 条码的每一根条都是一个落在毫米坐标上的矩形() => OnStaThread(() =>
    {
        var template = BarcodeTemplate(BarcodeSymbology.Code128, showText: true);
        var record = RecordWithBarcodeColumn("BOX-000123");
        var bar = Assert.Single(LayoutEngine.Build(template, record, new LayoutContext(1, 1))
            .Items.OfType<BarcodeItem>());
        Assert.True(bar.Bars.Count > 20, "这份夹具得真的够密，否则下面比的是两个都没被考验过的分支");

        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        var file = Assert.Single(SheetSvgWriter.WritePage(
            Request(plan, new PageContentSource(template, new List<MarkRecord> { record }, "样例.xlsx")), 0, SvgExportOptions.Default));
        var doc = XDocument.Parse(file.Xml);

        // 标签层里那些黑填充矩形 == Core 算出来的条数：渲染端一根不多、一根不少（预览能扫印出来也能扫）
        var rects = Layer(doc, "labels").Descendants(Svg + "rect")
            .Where(r => (string?)r.Attribute("fill") == "#000000")
            .ToList();
        Assert.Equal(bar.Bars.Count, rects.Count);
        Assert.Equal(bar.Bars[0].X, Num(rects[0], "x"), 3);
        Assert.Equal(bar.Bars[0].Width, Num(rects[0], "width"), 3);
        Assert.Equal(bar.BarsY, Num(rects[0], "y"), 3);
        Assert.Equal(bar.BarsHeight, Num(rects[0], "height"), 3);

        // 可读那串数字也不能少：它是轮廓 path（不是矩形），所以上面那条计数不会把它算进去
        Assert.Contains(Layer(doc, "labels").Descendants(Svg + "path"),
            p => ((string?)p.Attribute("fill")) == "#000000");
        return true;
    });

    [Fact]
    public void 条码画到屏幕上不出元素框也不越标签() => OnStaThread(() =>
    {
        var template = BarcodeTemplate(BarcodeSymbology.Ean13, showText: true);
        template.BorderMm = 0;                                  // 只要条码那一个元素，边界才是干净的
        var record = RecordWithBarcodeColumn("400638133393");   // 12 位 → 自动补校验位
        var layout = LayoutEngine.Build(template, record, new LayoutContext(1, 1));
        var bar = Assert.Single(layout.Items.OfType<BarcodeItem>());
        Assert.Equal("4006381333931", bar.Data);

        var group = new System.Windows.Media.DrawingGroup();
        using (var dc = group.Open())
        {
            LabelRenderer.Draw(dc, layout, scale: 1.0, offsetX: 0, offsetY: 0,
                showGuides: false, pixelsPerDip: 1.0, drawBackground: false);
        }

        var ink = group.Bounds;
        var epsilon = 0.5;      // 半个像素：抗锯齿外沿
        Assert.True(ink.Left >= Mm.ToDiu(4) - epsilon, "条推出元素框左边了");
        Assert.True(ink.Right <= Mm.ToDiu(4 + 92) + epsilon, $"静区没算进框内：右沿 {ink.Right} DIU");
        Assert.True(ink.Top >= Mm.ToDiu(52) - epsilon);
        Assert.True(ink.Bottom <= Mm.ToDiu(52 + 22) + epsilon, "可读数字那一刀算重了，字掉到框外");
        return true;
    });

    [Fact]
    public void 编不出来的码在SVG里占一个红框而不是一片空白() => OnStaThread(() =>
    {
        var template = BarcodeTemplate(BarcodeSymbology.Ean13, showText: true);
        var record = RecordWithBarcodeColumn("6901234567890");      // 13 位但末位校验不对
        var file = Assert.Single(SheetSvgWriter.WritePage(
            Request(ImpositionEngine.Build(Spec(), LabelW, LabelH, 1),
                new PageContentSource(template, new List<MarkRecord> { record }, "样例.xlsx")), 0, SvgExportOptions.Default));

        Assert.Contains("条码编不出来", file.Xml, StringComparison.Ordinal);
        // 半成品码绝对不能出现：宁可占一个红框让人去改数据
        var doc = XDocument.Parse(file.Xml);                       // 同时这也是「仍是一份合法 SVG」
        Assert.DoesNotContain(Layer(doc, "labels").Descendants(Svg + "rect"), r => (string?)r.Attribute("fill") == "#000000");
        return true;
    });

    // ---------- 产物留件：肉眼与 python 复核用 ----------

    [Fact]
    public void KeepReviewableArtifactsWhenEnvVarIsSet() => OnStaThread(() =>
    {
        var keep = Environment.GetEnvironmentVariable("LABELGOU_TEST_ARTIFACTS");
        if (string.IsNullOrEmpty(keep)) return true;

        Directory.CreateDirectory(keep);
        var plan = ImpositionEngine.Build(Spec(registration: true), LabelW, LabelH, 9);
        var source = new PageContentSource(BuiltInTemplates.GetById(BuiltInTemplates.IdStandard)!,
            Enumerable.Range(1, 9).Select(_ => SampleRecords.StandardSample()).ToList(), "E:/唛头样例数据_50条.xlsx");
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = source,
            PageIndexes = new[] { 0 },
            BaseName = "复核件_M5",
        };

        foreach (var file in SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default))
        {
            File.WriteAllText(Path.Combine(keep, file.FileName), file.Xml, new UTF8Encoding(false));
        }
        foreach (var file in SheetSvgWriter.WritePage(request, 0, new SvgExportOptions { Mode = SvgExportMode.PerLabel }))
        {
            File.WriteAllText(Path.Combine(keep, "perlabel_" + file.FileName), file.Xml, new UTF8Encoding(false));
        }
        return true;
    });
}
