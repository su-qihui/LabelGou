using System.IO;
using System.Windows;
using System.Windows.Media;
using LabelGou.App.Export;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Editing;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 85 棒③（画布与面板这一层）：用户报的那一串手势要一路走得通。
/// <para>他的原话是"多边形工具使用对角编辑时可能存在删除角无法进行拉伸扭曲问题"。Core 那三条
/// （接缝 twin、删点护栏、盒句柄）在 <c>CurveBoxScaleCoreTests</c> 里各钉各的，这里钉的是<strong>顺序</strong>：
/// 拖角缩放 → 抓顶点（那一下会静默转曲线）→ 再拖角。<strong>全仓从前没有任何一条测试走过这个顺序</strong>，
/// 所以第 53 棒把"转曲线"接进编辑工具时，句柄消失这件事没人看得见。</para>
/// </summary>
public sealed class CurveBoxScaleFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "labelgou-b85curve-" + Guid.NewGuid().ToString("N")[..6]);

    public CurveBoxScaleFlowTests() => Directory.CreateDirectory(_dir);

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
            Id = "user.b85curve", Name = "曲线缩放", WidthMm = 140, HeightMm = 100, PaddingMm = 2, BorderMm = 0,
        };
        return new TemplateEditorViewModel(template, new TemplateStore(_dir));
    }

    private static TemplateElement Pentagon() => new()
    {
        Kind = ElementKind.Polygon, X = 20, Y = 20, Width = 40, Height = 40,
    };

    /// <summary>拖角缩放 → 转曲线 → 再拖角：两下都得真的把整只形状放大，且第一步退得回去。</summary>
    [Fact]
    public void ScalingThenConvertingThenScalingAgainStillScales() => OnSta(() =>
    {
        var vm = Open();
        var poly = Pentagon();
        vm.Template.Elements.Add(poly);
        vm.RefreshElements();
        vm.SelectedRow = vm.Elements[0];
        vm.IsNodeTool = true;

        var before = EditGeometry.VisualBoxOf(poly);
        Assert.True(vm.BeginDrag(before.X, before.Y, 2) == TemplateEditorViewModel.DragMode.Resize, "多边形拖角没进缩放档");
        vm.DragTo(before.X - 8, before.Y - 8);
        vm.EndDrag();
        var scaledOnce = EditGeometry.VisualBoxOf(poly);
        Assert.True(scaledOnce.Width > before.Width + 1, "第一下拖角没把形状放大");

        var converted = Assert.Single(vm.Template.Elements);
        Assert.True(vm.TryConvertToCurve(out converted), "转曲线没成");
        var box = EditGeometry.VisualBoxOf(converted);
        Assert.True(vm.BeginDrag(box.X + box.Width, box.Y + box.Height, 2) == TemplateEditorViewModel.DragMode.Resize,
            "转曲线后拖角不再缩放 = 用户报的「删角后拉不动」还在");
        vm.DragTo(box.X + box.Width + 14, box.Y + box.Height + 14);
        vm.EndDrag();
        var scaledTwice = EditGeometry.VisualBoxOf(converted);
        Assert.True(scaledTwice.Width > box.Width + 1,
            $"第二下拖角只把宽从 {box.Width:0.#} 拖到 {scaledTwice.Width:0.#}：句柄抓到了却没缩成");
        Assert.True(scaledTwice.Height > box.Height + 1);

        vm.Undo();
        // 撤销会换掉元素对象（第 62 棒那条"CopyInto 换对象"），所以要从模板里重新拿那一只来量，
        // 不能拿手上那个引用——它可能已经不是表里的那个了。
        var back = EditGeometry.VisualBoxOf(vm.Template.Elements[0]);
        Assert.True(Math.Abs(back.Width - box.Width) < 0.6,
            $"撤销后宽是 {back.Width:0.#}mm，该退回 {box.Width:0.#}mm");
        return true;
    });

    /// <summary>属性面板那两格对转曲线后的形状也要真的动它（从前写进 <c>Width</c> 是空操作）。</summary>
    [Fact]
    public void ThePanelWidthStillMovesAConvertedShape() => OnSta(() =>
    {
        var vm = Open();
        var poly = Pentagon();
        vm.Template.Elements.Add(poly);
        vm.RefreshElements();
        vm.SelectedRow = vm.Elements[0];
        Assert.True(vm.TryConvertToCurve(out var converted));

        var Panel = vm.Editing ?? throw new InvalidOperationException("面板没跟上选中行");
        var before = EditGeometry.VisualBoxOf(converted);
        Assert.True(Panel.HasBox, "转曲线后「宽(mm)/高(mm)」那两格不见了 = 精确改尺寸的路也断了");
        Panel.Width = before.Width * 2;

        var after = EditGeometry.VisualBoxOf(converted);
        Assert.Equal(before.Width * 2, after.Width, 1);
        Assert.Equal(before.Height, after.Height, 1);      // 只给宽 = 单轴，不许顺手把高等比放大
        return true;
    });

    /// <summary>转完曲线，「边数」那一格留在原地但灰掉，并把原因说在格子上（⑥ 拍板：不做回转，但要讲清）。</summary>
    [Fact]
    public void TheSidesRowStaysButGoesGreyAfterConverting() => OnSta(() =>
    {
        var vm = Open();
        vm.Template.Elements.Add(Pentagon());
        vm.RefreshElements();
        vm.SelectedRow = vm.Elements[0];
        var Panel = vm.Editing ?? throw new InvalidOperationException("面板没跟上选中行");
        Assert.True(Panel.ShowsPolygonSides);
        Assert.True(Panel.IsPolygon, "多边形那一下「边数」该可改");

        Assert.True(vm.TryConvertToCurve(out _));
        // 转换会重建图层行（第 62 棒那条"CopyInto 换对象"同一族）——面板要重新取一次，
        // 拿旧引用断言等于在问"上一只元素"。
        Panel = vm.Editing ?? throw new InvalidOperationException("转完曲线面板反而没了");
        Assert.True(Panel.ShowsPolygonSides, "边数格直接消失 = 用户只看见「它没了」，看不见为什么");
        Assert.False(Panel.IsPolygon, "转完还判成可改边数，就是给了个按不动的旋钮");
        Assert.Contains("闭合曲线", Panel.PolygonSidesHint, StringComparison.Ordinal);
        Assert.Contains("已转为曲线", vm.StatusText, StringComparison.Ordinal);
        return true;
    });

    /// <summary>一条普通直线不许被这次改动带上盒句柄（它吃两个端点，第 49 棒口径）。</summary>
    [Fact]
    public void APlainLineStillOnlyGrabsItsTwoEnds() => OnSta(() =>
    {
        var vm = Open();
        var line = new TemplateElement { Kind = ElementKind.Line, X = 10, Y = 10, X2 = 60, Y2 = 40 };
        vm.Template.Elements.Add(line);
        vm.RefreshElements();
        vm.SelectedRow = vm.Elements[0];
        var Panel = vm.Editing ?? throw new InvalidOperationException("面板没跟上选中行");
        Assert.False(Panel.HasBox, "直线不该有整只框（它的缩放就是拖端点）");
        Assert.Equal(ResizeHandle.LineStart, EditGeometry.HandleAt(line, 10, 10, 2));
        return true;
    });

    /// <summary>
    /// 画布这一层要真的把八向句柄画出来：Core 算得对而屏幕上看不见，用户照样"没法拉伸"
    /// （第 50 棒那条教训——VM 十六全绿、画布那一层压根进不去）。
    /// </summary>
    [Fact]
    public void TheCanvasDrawsEightBoxHandlesOnTopOfTheNodes() => OnSta(() =>
    {
        var template = new LabelTemplate
        {
            Id = "user.b85canvas", Name = "画布句柄", WidthMm = 140, HeightMm = 100, PaddingMm = 2, BorderMm = 0,
        };
        template.Elements.Add(Pentagon());
        var vm = new TemplateEditorViewModel(template, new TemplateStore(_dir));
        vm.RefreshElements();
        vm.SelectedRow = vm.Elements[0];
        Assert.True(vm.TryConvertToCurve(out _));

        var window = new TemplateEditorWindow(vm);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1180, 760));
        root.Arrange(new Rect(0, 0, 1180, 760));
        window.UpdateLayout();

        var grips = CountGrips(FrameOf(window.EditorCanvas));
        // 5 个节点 + 8 个整框句柄。退回第 85 棒之前这一帧只有 5 个（曲线那支提前 return 了）。
        Assert.True(grips >= 13, $"这一帧只画了 {grips} 个抓手，该是 5 个节点 + 8 个句柄：句柄没画出来=用户看不见能拖哪");
        return true;
    });

    private static DrawingGroup FrameOf(TemplateEditorControl canvas)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
            canvas.RenderForTests(dc);
        return group;
    }

    /// <summary>抓手方块的画法只有一种：白底 + 选中那支蓝笔（节点与句柄共用，所以一起数）。</summary>
    private static int CountGrips(Drawing? drawing) => drawing switch
    {
        null => 0,
        DrawingGroup group => group.Children.Sum(CountGrips),
        GeometryDrawing g when g.Brush is SolidColorBrush fill && fill.Color == Colors.White
            && g.Pen?.Brush is SolidColorBrush line && line.Color == Color.FromRgb(0, 120, 215) => 1,
        _ => 0,
    };
}
