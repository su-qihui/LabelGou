using System.IO;
using System.Windows;
using System.Xml.Linq;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Colors;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 49 棒：贝塞尔工具与节点编辑（类 CorelDRAW）。
/// <para>
/// 这里钉的是<strong>手感与结构</strong>：逐点画出来的确实是一条曲线元素（不是 N 个元素）；
/// Shift 那一下必须落回"从前那条直线"的存储形状；拖节点只动那个点、而且连拖两次不会越拖越远
/// （<c>DragTo</c> 是每帧从快照重算的写法，恢复漏一个字段就会累积）；曲线与直线在 SVG 出口各写各的。
/// </para>
/// </summary>
public class CurveToolTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static TemplateEditorViewModel Open(params TemplateElement[] elements)
    {
        var template = new LabelTemplate
        {
            Id = "user.bezier", Name = "贝塞尔测试", WidthMm = 100, HeightMm = 60, PaddingMm = 2, BorderMm = 0,
        };
        template.Elements.AddRange(elements);
        return new TemplateEditorViewModel(template,
            new TemplateStore(Path.Combine(Path.GetTempPath(), "labelgou-bezier-" + Guid.NewGuid().ToString("N")[..6])));
    }

    private static TemplateElement Line(double x1, double y1, double x2, double y2, params CurveNode[] middle)
    {
        var element = new TemplateElement
        {
            Kind = ElementKind.Line, X = x1, Y = y1, X2 = x2, Y2 = y2,
            Width = Math.Abs(x2 - x1), Height = Math.Abs(y2 - y1), ThicknessMm = 0.35,
        };
        if (middle.Length > 0) element.Nodes = middle.ToList();
        return element;
    }

    /// <summary>点三下画一条两点一拱再收尾的曲线（N 个点 = N-1 段）。</summary>
    private static void DrawAnArc(TemplateEditorViewModel vm, double x1, double y1, double xm, double ym, double x2, double y2)
    {
        vm.BeginPath(x1, y1);
        vm.EndPathSegment();                                      // 第一下只点不拖：起点是尖角
        vm.BeginPath(xm, ym);
        vm.DragPath(xm - 12, ym + 12);                            // 拖第二下：弯的是刚画出来的 1→2 那一段
        vm.EndPathSegment();
        vm.BeginPath(x2, y2);
        vm.EndPathSegment();
        vm.FinishPath();
    }

    [Fact]
    public void ThreeClicksMakeOneCurveElementNotThreeElements() => OnSta(() =>
    {
        var vm = Open();
        vm.IsBezierTool = true;
        DrawAnArc(vm, 10, 40, 45, 20, 80, 40);

        var element = Assert.Single(vm.Template.Elements);
        Assert.Equal(ElementKind.Line, element.Kind);                       // 还是那类元素，只是会弯了
        Assert.True(CurveGeometry.IsCurved(element));
        Assert.Equal(2, CurveGeometry.Segments(element).Count);             // 三个点 = 两段
        Assert.False(vm.IsDrawingPath);

        // 预览/导出吃的是版面项里那份段序列，不是"两端连一条弦"。
        var arc = Assert.Single(vm.SampleLayout.Items.OfType<LineItem>()).Arc;
        Assert.NotNull(arc);
        Assert.Equal(2, arc!.Count);
        return true;
    });

    [Fact]
    public void ClicksWithoutDraggingWriteExactlyTheOldStraightLine() => OnSta(() =>
    {
        // 只点不拖＝尖角＝直线段（用户看图更正后的口径：不按 Shift 本来就是直线）。
        // 直线就该存成从前那个样子——三个曲线字段一个都不写。
        var vm = Open();
        vm.IsBezierTool = true;
        vm.BeginPath(10, 30);
        vm.EndPathSegment();
        vm.BeginPath(90, 30);
        vm.EndPathSegment();
        vm.FinishPath();

        var element = Assert.Single(vm.Template.Elements);
        Assert.False(CurveGeometry.IsCurved(element));
        Assert.Null(element.Nodes);
        Assert.Null(element.StartOut);
        Assert.Null(element.EndIn);
        Assert.Equal((10d, 30d, 90d, 30d), (element.X, element.Y, element.X2, element.Y2));
        Assert.DoesNotContain("\"nodes\"", TemplateStore.ToJson(vm.Template), StringComparison.OrdinalIgnoreCase);
        return true;
    });

    [Fact]
    public void EscapeWhileDrawingLeavesNothingBehind() => OnSta(() =>
    {
        var vm = Open();
        vm.IsBezierTool = true;
        vm.BeginPath(20, 20);
        vm.DragPath(30, 10);
        Assert.Single(vm.Template.Elements);                                // 第一个点就落地了（撤销栈要能整步退掉）

        vm.CancelPath();
        Assert.Empty(vm.Template.Elements);
        Assert.False(vm.IsDrawingPath);
        return true;
    });

    [Fact]
    public void OneDrawnCurveUndoesAsOneStep() => OnSta(() =>
    {
        var vm = Open();
        vm.IsBezierTool = true;
        DrawAnArc(vm, 10, 40, 45, 20, 80, 40);
        Assert.Single(vm.Template.Elements);

        vm.Undo();
        Assert.Empty(vm.Template.Elements);                                 // 画一条线不该要人按五次撤销
        return true;
    });

    [Fact]
    public void SwitchingToolsFinishesTheLineInProgress() => OnSta(() =>
    {
        // 切走之前不收尾，画布上就留着一条"半截曲线"，而人已经去点别的东西了。
        var vm = Open();
        vm.IsBezierTool = true;
        vm.BeginPath(15, 45);
        vm.DragPath(35, 25);
        vm.EndPathSegment();
        vm.BeginPath(75, 45);
        vm.EndPathSegment();

        vm.IsBezierTool = false;
        Assert.False(vm.IsDrawingPath);
        var element = Assert.Single(vm.Template.Elements);
        Assert.True(CurveGeometry.IsCurved(element));
        var pts = CurveGeometry.NodesOf(element);
        Assert.Equal(2, pts.Count);
        Assert.Equal((element.X, element.Y), (pts[0].X, pts[0].Y));                        // 端点只有一个出处
        Assert.Equal((element.X2, element.Y2), (pts[^1].X, pts[^1].Y));
        Assert.NotEqual(0, element.StartOut!.DX);  // 第一下拖出的出柄还在：它决定 1→2 怎么离开起点
        return true;
    });

    [Fact]
    public void DraggingTheWholeCurveMovesTheArcBodyByExactlyTheSameAmount() => OnSta(() =>
    {
        // DragTo 是"每帧从按下时的快照重算"。RestoreGeometry 少恢复节点，弧身就会逐帧累加、跑得比端点远。
        var element = Line(10, 50, 90, 50, new CurveNode(50, 20, -8, 0, 8, 0));
        var vm = Open(element);
        vm.SelectedRow = vm.Elements[0];

        var before = (X: element.X, Y: element.Y, NodeX: element.Nodes![0].X, NodeY: element.Nodes[0].Y);
        Assert.Equal(TemplateEditorViewModel.DragMode.Move, vm.BeginDrag(27, 35, 1.5));   // 弧身上的一点（不是节点）
        vm.DragTo(25, 33);
        vm.DragTo(23, 31);                                 // 同一笔里再走一帧：位移是相对按下时算的
        vm.EndDrag();

        Assert.Equal(element.X - before.X, element.Nodes[0].X - before.Item3);             // 弧身与端点挪得一样多
        Assert.Equal(element.Y - before.Y, element.Nodes[0].Y - before.Item4);
        Assert.Equal((86d, 46d), (element.X2, element.Y2));                                // 另一端同样跟着
        Assert.Equal((-8d, 0d), (element.Nodes[0].InX, element.Nodes[0].InY));             // 弯度一点没变
        return true;
    });

    [Fact]
    public void DraggingTheSecondNodeBendsTheSegmentAlreadyDrawn() => OnSta(() =>
    {
        // 这条钉子就是本次纠正的正身：拖当前点改的是【刚画出来的那一段】，不是下一段。
        // 之前实现成"改下一段"，用户看到的就是第二下之后线被钉死、怎么拖都不弯。
        var vm = Open();
        vm.IsBezierTool = true;
        vm.BeginPath(10, 10);
        vm.EndPathSegment();
        vm.BeginPath(90, 10);
        var element = Assert.Single(vm.Template.Elements);
        Assert.True(CurveGeometry.Segments(element)[0].IsStraight);              // 第二下还没拖：这一段是直的

        vm.DragPath(70, 40);                                                     // 往左下方拖这一点
        var seg = CurveGeometry.Segments(element)[0];
        Assert.False(seg.IsStraight);
        Assert.Equal((70d, 40d), (seg.CX2, seg.CY2));                            // 控制点正落在拖的方向上
        vm.EndPathSegment();
        vm.BeginPath(150, 40);                                                   // 第三下：第二点从"终点"变成中间节点
        var mid = CurveGeometry.NodesOf(element)[1];
        Assert.Equal((-20d, 30d), (mid.InX, mid.InY));                            // 刚拖的那根进柄存住了
        Assert.Equal((20d, -30d), (mid.OutX, mid.OutY));                          // 出柄是它的反向＝平滑延续，下一段不会突然折
        return true;
    });

    [Fact]
    public void HoldingCtrlConstrainsTheSegmentToHorizontalOrVertical() => OnSta(() =>
    {
        // CorelDRAW 的贝塞尔工具自带文案：「按住 Ctrl 键单击可限制线条」（VGCoreIntl.dll）。
        // 用户自己更正过：Shift 不是这个键，所以这里钉的是 Ctrl 那一条。
        var vm = Open();
        vm.IsBezierTool = true;
        vm.BeginPath(10, 10);
        vm.EndPathSegment();
        vm.BeginPath(90, 60, constrain: true);        // 横向走得更多 → 这一笔被夹成水平

        var element = Assert.Single(vm.Template.Elements);
        Assert.Equal(90, element.X2);
        Assert.Equal(10, element.Y2);                 // 不是 60：斜的那一下不给落

        vm.DragPath(60, 40, constrain: true);         // 拖柄也一样：只留水平那根分量
        var node = CurveGeometry.NodesOf(element)[1];
        Assert.Equal((-30d, 0d), (node.InX, node.InY));
        return true;
    });

    [Fact]
    public void DraggingANodeMovesOnlyThatNodeAndDoesNotAccumulate() => OnSta(() =>
    {
        var element = Line(10, 50, 90, 50, new CurveNode(50, 20, -8, 0, 8, 0));
        var vm = Open(element);
        vm.SelectedRow = vm.Elements[0];

        var mode = vm.BeginDrag(50, 20, 1.5);                              // 抓中间那个节点
        Assert.Equal(TemplateEditorViewModel.DragMode.Node, mode);

        vm.DragTo(54, 24);
        vm.EndDrag();
        Assert.Equal((54d, 24d), (element.Nodes![0].X, element.Nodes[0].Y));
        Assert.Equal((10d, 50d, 90d, 50d), (element.X, element.Y, element.X2, element.Y2));

        // 同一处再拖一次：结果必须还是 (54,24)。每帧从快照重算的写法漏恢复一个字段，这里就会跑到 (58,28)。
        Assert.Equal(TemplateEditorViewModel.DragMode.Node, vm.BeginDrag(54, 24, 1.5));
        vm.DragTo(54, 24);
        vm.EndDrag();
        Assert.Equal((54d, 24d), (element.Nodes[0].X, element.Nodes[0].Y));
        return true;
    });

    [Fact]
    public void DraggingAHandleOnASmoothNodeMirrorsTheOtherSide() => OnSta(() =>
    {
        var element = Line(10, 50, 90, 50, new CurveNode(50, 20, -8, 0, 8, 0));
        var vm = Open(element);
        vm.SelectedRow = vm.Elements[0];

        Assert.Equal(TemplateEditorViewModel.DragMode.Handle, vm.BeginDrag(58, 20, 1.5));   // 出柄的柄头
        vm.DragTo(60, 24);
        vm.EndDrag();

        Assert.Equal((10d, 4d), (element.Nodes![0].OutX, element.Nodes[0].OutY));
        Assert.Equal((-10d, -4d), (element.Nodes[0].InX, element.Nodes[0].InY));            // 平滑：另一侧跟着反向
        Assert.Equal((50d, 20d), (element.Nodes[0].X, element.Nodes[0].Y));                 // 节点本身不动
        return true;
    });

    [Fact]
    public void DraggingAHandleOnACornerNodeLeavesTheOtherSideAlone() => OnSta(() =>
    {
        // 尖角节点（只有一根柄）必须能单独调这一侧，不然"把一侧拉直"就做不到了。
        var element = Line(10, 50, 90, 50, new CurveNode(50, 20, 0, 0, 8, 0));
        var vm = Open(element);
        vm.SelectedRow = vm.Elements[0];

        vm.BeginDrag(58, 20, 1.5);
        vm.DragTo(62, 26);
        vm.EndDrag();

        Assert.Equal((12d, 6d), (element.Nodes![0].OutX, element.Nodes[0].OutY));
        Assert.Equal((0d, 0d), (element.Nodes[0].InX, element.Nodes[0].InY));
        return true;
    });

    [Fact]
    public void StraightenAndFlattenDoWhatTheButtonsSay() => OnSta(() =>
    {
        var element = Line(10, 50, 90, 50, new CurveNode(50, 20, -8, 0, 8, 0));
        var vm = Open(element);
        vm.SelectedRow = vm.Elements[0];
        var panel = vm.Editing!;

        panel.StraightenCommand.Execute(null);
        Assert.Equal(3, CurveGeometry.NodesOf(element).Count);              // 节点还在
        Assert.All(CurveGeometry.Segments(element), s => Assert.True(s.IsStraight));

        panel.FlattenToEndsCommand.Execute(null);
        Assert.Null(element.Nodes);                                        // 只留两端：回到两点的直线
        Assert.Equal((10d, 50d, 90d, 50d), (element.X, element.Y, element.X2, element.Y2));
        return true;
    });

    [Fact]
    public void SvgOutletWritesAPathForTheCurveAndALineForTheStraightOne() => OnSta(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-bezier-svg-" + Guid.NewGuid().ToString("N")[..6]);
        var plan = ImpositionEngine.Build(Spec(), 100, 60, 1);
        var curved = Open(Line(10, 50, 90, 50, new CurveNode(50, 20, -20, -10, 20, -10)));
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = new PageContentSource(curved.Template, new List<MarkRecord> { SampleRecords.StandardSample() }, "样例.xlsx"),
            PageIndexes = new List<int> { 0 },
            BaseName = "曲线件",
        };
        var file = Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default));
        var xml = file.Xml;

        Assert.Contains("<path d=\"M 10 50 C", xml, StringComparison.Ordinal);   // 弧走 path
        Assert.DoesNotContain("<line x1=\"10\"", xml, StringComparison.Ordinal);  // 不再是两端连线
        XDocument.Parse(xml);                                                    // 手写串最容易坏在转义上，能解析才算数

        var straight = Open(Line(10, 50, 90, 50));
        var straightRequest = new SheetExportRequest
        {
            Plan = plan,
            Source = new PageContentSource(straight.Template, new List<MarkRecord> { SampleRecords.StandardSample() }, "样例.xlsx"),
            PageIndexes = new List<int> { 0 },
            BaseName = "直线件",
        };
        Assert.Contains("<line ", Assert.Single(SheetSvgWriter.WritePage(straightRequest, 0, SvgExportOptions.Default)).Xml,
            StringComparison.Ordinal);                                           // 直线仍写 line：老出口逐字不变
        return true;
    });

    [Fact]
    public void TheCurvePrintsOnThePlatesItWasGiven() => OnSta(() =>
    {
        // 48 棒的分色与曲线得合得上：填 C100 M0 Y100 K0 的曲线，青版与黄版该有墨、品红版该干净。
        var element = Line(10, 20, 90, 20, new CurveNode(50, 5, -20, 0, 20, 0));
        element.ThicknessMm = 1.2;                                        // 细线在高 DPI 下采样不足，量不出"有没有墨"这件事
        element.InkColor = LabelColor.FromCmyk(100, 0, 100, 0);
        var template = Open(element).Template;
        var plan = ImpositionEngine.Build(Spec(), 100, 60, 1);
        var source = new PageContentSource(template, new List<MarkRecord> { SampleRecords.StandardSample() }, "样例.xlsx");
        var page = PageRasterizer.RenderCmykPage(plan, 1, 300, source.AsPlateProvider(), false);
        var planes = page.ToPlanes();

        // 判据只看纸内：最外一圈在 300DPI 下有栅格化余数（空版也一样到 96），拿整页 Max() 比会被它骗。
        static byte Peak(byte[] plane, int w, int h)
        {
            var peak = (byte)0;
            for (var y = 3; y < h - 3; y++)
                for (var x = 3; x < w - 3; x++) peak = Math.Max(peak, plane[y * w + x]);
            return peak;
        }

        var cyan = Peak(planes[0], page.Width, page.Height);
        var magenta = Peak(planes[1], page.Width, page.Height);
        var yellow = Peak(planes[2], page.Width, page.Height);
        Assert.True(cyan > 200 && yellow > 200, $"青版与黄版没墨，曲线没画进分色：C{cyan} M{magenta} Y{yellow}");
        Assert.True(magenta <= 6, $"品红版上出现了不该有的墨：峰值 {magenta}");
        return true;
    });

    private static SheetSpec Spec() => new()
    {
        Id = "test.bezier", Name = "贝塞尔小样",
        // 纸必须比"标签 + 页边"大：100×60 的标签塞进 100×60 的纸再加 2mm 页边，PerPage=0，
        // 后面所有"画没画出来"的断言都会因为"根本没排版出东西"而假失败。
        PaperWidthMm = 140, PaperHeightMm = 90,
        MarginLeftMm = 2, MarginTopMm = 2, MarginRightMm = 2, MarginBottomMm = 2,
        GutterXMm = 2, GutterYMm = 2, CropMarkThicknessMm = 0.15,
        RepeatSameLabelPerPage = false,
    };
}
