using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using LabelGou.App.Export;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Colors;
using LabelGou.Core.Editing;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 51 棒：椭圆与多边形。基本功逻辑照矩形那条链——按下拖出、只点给默认尺寸、Esc 连元素撤、
/// 一步撤销、拖中画布活刷、两支墨（填充/描边）与逐角无关的外观字段共用。
/// <para>钉四件：① 分派点真能走到椭圆/多边形那两条（§五-151 同族，VM 全绿救不了画布层）；
/// ② CorelDRAW 的修饰键语义（Ctrl＝限制为圆/正方、Shift＝从中心绘制，文案照它资源串）；
/// ③ 多边形顶点表只有一份（Core 数点，SVG 出口照抄，逐点比对）；④ 边数默认 5 不写盘、坏边数校验器说话。</para>
/// </summary>
public sealed class ShapeToolTests : IDisposable
{
    private readonly string _dir;

    public ShapeToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "labelgou-shapes-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录留给人看一次就够了 */ }
    }

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private TemplateEditorViewModel Open()
    {
        var template = new LabelTemplate
        {
            Id = "user.shapes", Name = "形状测试", WidthMm = 100, HeightMm = 60, PaddingMm = 2, BorderMm = 0,
        };
        return new TemplateEditorViewModel(template, new TemplateStore(_dir));
    }

    // ---------- 拖动机械（与矩形同一份） ----------

    [Fact]
    public void DraggingOutAnEllipseDropsAnEllipseElement() => OnSta(() =>
    {
        var vm = Open();
        vm.IsEllipseTool = true;

        Assert.True(vm.BeginShape(TemplateEditorViewModel.EditorTool.Ellipse, 10, 12));
        vm.DragShape(50, 32);
        var element = Assert.Single(vm.Template.Elements);
        Assert.Equal(ElementKind.Ellipse, element.Kind);
        Assert.Equal((10d, 12d, 40d, 20d), (element.X, element.Y, element.Width, element.Height));

        // 本体与覆盖层同一帧刷新（§五-148）：没松手时画布吃的样例版面就得是这只椭圆。
        var item = Assert.Single(vm.SampleLayout.Items.OfType<EllipseItem>());
        Assert.Equal((40d, 20d), (item.Width, item.Height));

        vm.EndShape();
        Assert.False(vm.IsDrawingShape);
        return true;
    });

    [Fact]
    public void DraggingOutAPolygonDropsItWithTheCoreComputedPoints() => OnSta(() =>
    {
        var vm = Open();
        vm.IsPolygonTool = true;
        vm.BeginShape(TemplateEditorViewModel.EditorTool.Polygon, 20, 10);
        vm.DragShape(60, 50);                       // 40×40 的盒，五边形

        var element = Assert.Single(vm.Template.Elements);
        Assert.Equal(ElementKind.Polygon, element.Kind);
        Assert.Null(element.PolygonSides);                      // 默认五边 = 缺字段，不占一行

        var item = Assert.Single(vm.SampleLayout.Items.OfType<PolygonItem>());
        var wanted = ShapeGeometry.PolygonPoints(element);
        Assert.Equal(wanted.Count, item.Points.Count);
        for (var i = 0; i < wanted.Count; i++)
        {
            Assert.Equal(wanted[i].X, item.Points[i].X, 6);
            Assert.Equal(wanted[i].Y, item.Points[i].Y, 6);
        }
        // 首点朝正上（CorelDRAW 起手一致）：盒顶中点。
        Assert.Equal(40d, wanted[0].X, 6);
        Assert.Equal(10d, wanted[0].Y, 6);
        return true;
    });

    [Fact]
    public void CtrlConstrainsToACircleAndShiftDrawsFromCenter() => OnSta(() =>
    {
        // 「按住 Ctrl 键拖动可限制为圆形」：两轴取更大的拖幅，正圆。
        var circle = Open();
        circle.IsEllipseTool = true;
        circle.BeginShape(TemplateEditorViewModel.EditorTool.Ellipse, 10, 10, fromCenter: false, square: true);
        circle.DragShape(70, 30);
        var e = Assert.Single(circle.Template.Elements);
        Assert.Equal(e.Width, e.Height, 6);                       // 正圆：两轴同幅
        Assert.Equal(60d, e.Width, 6);                            // 取拖得更远的那一轴（x 拖了 60）

        // 「按住 Shift 键并拖动可从中心绘制」：按下的点是中心，四向对称张开。
        var centered = Open();
        centered.IsEllipseTool = true;
        centered.BeginShape(TemplateEditorViewModel.EditorTool.Ellipse, 50, 30, fromCenter: true);
        centered.DragShape(60, 36);
        var c = Assert.Single(centered.Template.Elements);
        Assert.Equal((40d, 24d, 20d, 12d), (c.X, c.Y, c.Width, c.Height));
        return true;
    });

    [Fact]
    public void AClickWithoutDraggingGivesAVisibleCircleNotAnInvisibleOne() => OnSta(() =>
    {
        var vm = Open();
        vm.IsPolygonTool = true;
        vm.BeginShape(TemplateEditorViewModel.EditorTool.Polygon, 12, 12);
        vm.DragShape(12.4, 12.4);
        vm.EndShape();

        var element = Assert.Single(vm.Template.Elements);
        Assert.Equal((TemplateEditorViewModel.DefaultShapeSideMm, TemplateEditorViewModel.DefaultShapeSideMm),
            (element.Width, element.Height));
        return true;
    });

    [Fact]
    public void EscapeThrowsAwayTheShapeInProgressAndOneDragIsOneUndoStep() => OnSta(() =>
    {
        var vm = Open();
        vm.IsEllipseTool = true;
        vm.BeginShape(TemplateEditorViewModel.EditorTool.Ellipse, 20, 20);
        vm.DragShape(40, 40);
        Assert.Single(vm.Template.Elements);
        vm.CancelShape();
        Assert.Empty(vm.Template.Elements);                       // 元素一起撤，不是留一只空壳

        vm.BeginShape(TemplateEditorViewModel.EditorTool.Ellipse, 20, 20);
        vm.DragShape(40, 40);
        vm.EndShape();
        vm.Undo();
        Assert.Empty(vm.Template.Elements);                       // 画一只＝一步撤销
        return true;
    });

    // ---------- 按下分派（§五-151：这一层必须被测试读到） ----------

    [Fact]
    public void TheEllipseAndPolygonToolsActuallyReachTheShapeBranch() => OnSta(() =>
    {
        var vm = Open();
        vm.IsEllipseTool = true;
        Assert.False(vm.IsRectTool);                             // 互斥由同一个 Tool 字段保证——分派必须按工具 switch
        Assert.Equal(TemplateEditorControl.ToolDown.Drag,
            TemplateEditorControl.TryToolDown(vm, 10, 10, doubleClick: false, ctrl: false));
        Assert.Equal(ElementKind.Ellipse, Assert.Single(vm.Template.Elements).Kind);

        var vm2 = Open();
        vm2.IsPolygonTool = true;
        Assert.Equal(TemplateEditorControl.ToolDown.Drag,
            TemplateEditorControl.TryToolDown(vm2, 10, 10, doubleClick: false, ctrl: false, shift: true));
        var poly = Assert.Single(vm2.Template.Elements);
        Assert.Equal(ElementKind.Polygon, poly.Kind);
        return true;
    });

    // ---------- 外观与文件 ----------

    [Fact]
    public void ShapesShareTheRectAppearanceFieldsAndSlashStates() => OnSta(() =>
    {
        var ellipse = new TemplateElement { Kind = ElementKind.Ellipse, X = 10, Y = 10, Width = 20, Height = 20 };
        var vm = Open();
        vm.Template.Elements.Add(ellipse);
        vm.RefreshElements();
        vm.SelectedRow = vm.Elements[0];
        var panel = vm.Editing!;

        Assert.True(panel.HasShapeAppearance);
        Assert.True(panel.FillShowsSlash);                        // 没填充＝斜杠块，椭圆也一样
        Assert.False(panel.IsRect);                               // 圆角那一块不该出现在椭圆上
        panel.OpenFillEditorCommand.Execute(null);
        panel.InkC = 100;
        Assert.NotNull(ellipse.FillColor);
        Assert.False(panel.FillShowsSlash);
        return true;
    });

    [Fact]
    public void DefaultSidesWritesNothingAndBadSidesMakesTheValidatorSpeak()
    {
        var poly = new TemplateElement { Kind = ElementKind.Polygon, X = 10, Y = 10, Width = 20, Height = 20 };
        var template = new LabelTemplate { Id = "user.poly", Name = "多边形", WidthMm = 60, HeightMm = 40, PaddingMm = 2 };
        template.Elements.Add(poly);

        var json = TemplateStore.ToJson(template);
        Assert.DoesNotContain("\"polygonSides\"", json, StringComparison.Ordinal);   // 缺字段 = 默认 5，不上盘

        poly.PolygonSides = 7;
        Assert.Contains("\"polygonSides\": 7", TemplateStore.ToJson(template), StringComparison.Ordinal);
        poly.PolygonSides = 2;
        Assert.Contains(TemplateValidator.Validate(template),
            i => i.Severity == IssueLevel.Error && i.Message.Contains("边数 2", StringComparison.Ordinal));
        poly.PolygonSides = null;
        Assert.DoesNotContain(TemplateValidator.Validate(template),
            i => i.Message.Contains("边数", StringComparison.Ordinal));
    }

    [Fact]
    public void AShapeThatDrawsNothingSaysSo()
    {
        var ellipse = new TemplateElement { Kind = ElementKind.Ellipse, X = 5, Y = 5, Width = 20, Height = 20, Stroked = false };
        var template = new LabelTemplate { Id = "user.e", Name = "隐形椭圆", WidthMm = 60, HeightMm = 40, PaddingMm = 2 };
        template.Elements.Add(ellipse);
        Assert.Contains(TemplateValidator.Validate(template),
            i => i.Message.Contains("既不描边也不填充", StringComparison.Ordinal));
    }

    // ---------- 跨出口：SVG 抄的是同一份点/盒 ----------

    private static LabelTemplate TemplateOf(TemplateElement element)
    {
        var template = new LabelTemplate
        {
            Id = "user.shape", Name = "形状出口", WidthMm = 60, HeightMm = 30, PaddingMm = 2, BorderMm = 0,
        };
        template.Elements.Add(element);
        return template;
    }

    private static string SvgOf(TemplateElement element)
    {
        var spec = new SheetSpec
        {
            Id = "test.shape", Name = "形状小样",
            PaperWidthMm = 80, PaperHeightMm = 40,
            MarginLeftMm = 4, MarginTopMm = 4, MarginRightMm = 4, MarginBottomMm = 4,
            GutterXMm = 2, GutterYMm = 2, CropMarkThicknessMm = 0.15,
            RepeatSameLabelPerPage = false,
        };
        var request = new SheetExportRequest
        {
            Plan = ImpositionEngine.Build(spec, 60, 30, 1),
            Source = new PageContentSource(TemplateOf(element), new List<MarkRecord> { SampleRecords.StandardSample() }, "样例.xlsx"),
            PageIndexes = new List<int> { 0 },
            BaseName = "形状件",
        };
        return Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default)).Xml;
    }

    [Fact]
    public void TheSvgEllipseCarriesTheSameBoxAndThePolygonTheSamePoints()
    {
        var ellipse = new TemplateElement { Kind = ElementKind.Ellipse, X = 10, Y = 6, Width = 40, Height = 16 };
        var xml = XDocument.Parse(SvgOf(ellipse));
        var el = xml.Descendants().Single(e => e.Name.LocalName == "ellipse");
        Assert.Equal("30", el.Attribute("cx")!.Value);            // 圆心/半径由同一个外接盒说话：10+40/2
        Assert.Equal("14", el.Attribute("cy")!.Value);
        Assert.Equal("20", el.Attribute("rx")!.Value);
        Assert.Equal("8", el.Attribute("ry")!.Value);

        var poly = new TemplateElement { Kind = ElementKind.Polygon, X = 10, Y = 6, Width = 40, Height = 16 };
        var points = ShapeGeometry.PolygonPoints(poly);
        var polygon = XDocument.Parse(SvgOf(poly)).Descendants().Single(e => e.Name.LocalName == "polygon");
        var written = polygon.Attribute("points")!.Value.Split(' ')
            .Select(p => p.Split(','))
            .Select(xy => (double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture)))
            .ToList();
        Assert.Equal(points.Count, written.Count);
        for (var i = 0; i < points.Count; i++)
        {
            // SVG 里的是夹到 3 位小数的同一份数——差不得超过打印精度内的 0.001mm。
            Assert.True(Math.Abs(points[i].X - written[i].Item1) < 0.002);
            Assert.True(Math.Abs(points[i].Y - written[i].Item2) < 0.002);
        }
    }

    [Fact]
    public void TheWpfPolygonGeometryMatchesThePointTable() => OnSta(() =>
    {
        var poly = new TemplateElement { Kind = ElementKind.Polygon, X = 10, Y = 6, Width = 40, Height = 16 };
        var points = ShapeGeometry.PolygonPoints(poly);
        var geometry = LabelRenderer.PolygonGeometry(points, scale: 1.0);      // scale=1 仍是 96DPI 设备单位（§五-61）
        // 首点朝上落在几何里；中心在内、盒角在外（五边形内切于盒，角上没有东西）。
        Assert.True(geometry.FillContains(new Point(Mm.ToDiu(30), Mm.ToDiu(14))));
        Assert.False(geometry.FillContains(new Point(Mm.ToDiu(10.2), Mm.ToDiu(6.2))));
        return true;
    });
}
