using System.IO;
using System.Windows;
using LabelGou.App.Export;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Colors;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 52 棒：旋转三处修复的钉子。
/// <para>① 形状（矩形/椭圆/多边形）的版面项从前根本不带角度——"只有选择框转了、本体没转"就是它；
/// 这里用真渲染的几何界把"本体必须转"钉死。② 文字的旋转锚点从排版盒（隐形行带）中心改成
/// 看得见的墨迹中心——左对齐短字绕带心转会"飞出去"，绕自己中心转才是 CDR 手感；
/// 判据＝转 90° 后墨迹水平中心不许挪位置。</para>
/// </summary>
public sealed class RotationFixTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static LabelTemplate TemplateOf(params TemplateElement[] elements)
    {
        var template = new LabelTemplate
        {
            Id = "user.rot", Name = "旋转测试", WidthMm = 100, HeightMm = 60, PaddingMm = 2, BorderMm = 0,
        };
        template.Elements.AddRange(elements);
        return template;
    }

    /// <summary>用生产画法渲一遍，拿几何界（scale=1 → DIU 与毫米只差 Mm 换算）。</summary>
    private static Rect RenderedBounds(LabelTemplate template)
    {
        var layout = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1));
        var group = new System.Windows.Media.DrawingGroup();
        using (var dc = group.Open())
            LabelRenderer.Draw(dc, layout, scale: 1.0, offsetX: 0, offsetY: 0,
                showGuides: false, pixelsPerDip: 1.0, drawBackground: false);
        var b = group.Bounds;
        return new Rect(Mm.FromDiu(b.X), Mm.FromDiu(b.Y), Mm.FromDiu(b.Width), Mm.FromDiu(b.Height));
    }

    private static TemplateElement Box(double x, double y, double w, double h, double rot) => new()
    {
        Kind = ElementKind.Rect, X = x, Y = y, Width = w, Height = h,
        FillColor = Core.Colors.LabelColor.Black, RotationDeg = rot,
    };

    [Fact]
    public void ARotatedRectangleActuallyTurnsOnThePaper() => OnSta(() =>
    {
        var flat = RenderedBounds(TemplateOf(Box(20, 15, 30, 30, 0)));
        var turned = RenderedBounds(TemplateOf(Box(20, 15, 30, 30, 45)));

        Assert.True(flat.Width < 31, $"没转的框外接宽都 {flat.Width:0.#} 了，夹具不对");   // 30 + 半笔宽 0.35
        // 正方形转 45° 的外接 ≈ 30√2 ≈ 42.4mm——本体没转的话这里量到的还是 30（用户报的正是这个）。
        Assert.True(turned.Width > 40, $"转了 45° 外接宽才 {turned.Width:0.#} mm，矩形本体根本没跟着转");
        // 中心不许跑：绕盒中心转，转完外接仍以 (35,30) 为中心。
        Assert.Equal(35d, turned.X + turned.Width / 2, 1);
        Assert.Equal(30d, turned.Y + turned.Height / 2, 1);
        return true;
    });

    [Fact]
    public void ARotatedEllipseSwapsItsExtents() => OnSta(() =>
    {
        var e = new TemplateElement
        {
            Kind = ElementKind.Ellipse, X = 20, Y = 20, Width = 40, Height = 20,
            FillColor = Core.Colors.LabelColor.Black, RotationDeg = 90,
        };
        var b = RenderedBounds(TemplateOf(e));
        Assert.True(b.Height > b.Width, $"转 90° 后仍是宽 {b.Width:0.#}×高 {b.Height:0.#}，椭圆没跟着转");
        return true;
    });

    [Fact]
    public void ARotatedPolygonMovesItsVertices() => OnSta(() =>
    {
        var p0 = new TemplateElement
        {
            Kind = ElementKind.Polygon, X = 30, Y = 15, Width = 30, Height = 30,
            FillColor = Core.Colors.LabelColor.Black,
        };
        var p90 = new TemplateElement
        {
            Kind = ElementKind.Polygon, X = 30, Y = 15, Width = 30, Height = 30,
            FillColor = Core.Colors.LabelColor.Black, RotationDeg = 90,
        };
        var flat = RenderedBounds(TemplateOf(p0));
        var turned = RenderedBounds(TemplateOf(p90));
        // 五边形首点朝上 vs 转 90° 后首点朝右：顶边高度位置对不上——不一样才证明真转了。
        Assert.True(Math.Abs(flat.Y - turned.Y) > 0.5 || Math.Abs(flat.Height - turned.Height) > 0.5,
            "多边形转 90° 外接一模一样，等于没转");
        return true;
    });

    [Fact]
    public void TextRotatesAboutItsOwnInkCenterNotTheBandCenter() => OnSta(() =>
    {
        // 左对齐短字住在宽行带里：从前绕带心转，字会横着飞出去（用户 2026-09-14 报的偏移）。
        TemplateElement Text(double rot) => new()
        {
            Kind = ElementKind.Text, Text = "AB", X = 6, Y = 10, Width = 80, Height = 12,
            FontSizePt = 14, Align = HorizontalAlign.Left, RotationDeg = rot,
        };
        var flat = RenderedBounds(TemplateOf(Text(0)));
        var turned = RenderedBounds(TemplateOf(Text(90)));

        var flatCx = flat.X + flat.Width / 2;
        var turnedCx = turned.X + turned.Width / 2;
        // 绕墨迹自己中心转：水平中心纹丝不动；绕带心 (46) 转会把它甩到 46+(46-flatCx) 一带。
        Assert.True(Math.Abs(flatCx - turnedCx) < 1.5,
            $"转 90° 后墨迹水平中心从 {flatCx:0.#} 挪到 {turnedCx:0.#}——锚点又回到排版盒中心了");
        // 而这行字的墨迹中心确实不在带心（不然这条测不到任何事）。
        Assert.True(Math.Abs(flatCx - 46) > 10, "夹具没做出【短字在宽行带里】的形状，测不到锚点问题");
        return true;
    });

    [Fact]
    public void TheSvgOutletsCarryTheShapeRotation()
    {
        var rect = Box(20, 15, 30, 30, 45);
        var template = TemplateOf(rect);
        var spec = new SheetSpec
        {
            Id = "test.rot", Name = "旋转小样",
            PaperWidthMm = 120, PaperHeightMm = 80,
            MarginLeftMm = 4, MarginTopMm = 4, MarginRightMm = 4, MarginBottomMm = 4,
            GutterXMm = 2, GutterYMm = 2, CropMarkThicknessMm = 0.15,
            RepeatSameLabelPerPage = false,
        };
        var request = new SheetExportRequest
        {
            Plan = ImpositionEngine.Build(spec, 100, 60, 1),
            Source = new PageContentSource(template, new List<MarkRecord> { SampleRecords.StandardSample() }, "样例.xlsx"),
            PageIndexes = new List<int> { 0 },
            BaseName = "旋转件",
        };
        var xml = Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default)).Xml;
        // 矩形本体必须被包进绕盒中心 (35,30) 的旋转组——串形与 GeometryTransform 的退化分支一致。
        Assert.Contains("translate(35,30) rotate(45) translate(-35,-30)", xml, StringComparison.Ordinal);
    }
}
