using LabelGou.Core.Editing;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 85 棒③：「转为曲线」之后，那只形状<strong>还得能整只拉伸</strong>。
/// <para>用户 2026-09-19 报的是"多边形用对角编辑时，删除角后无法拉伸扭曲"。挖到根上是三件事叠在一起：</para>
/// <list type="number">
/// <item><c>ToClosedCurve</c> 把 <c>Kind</c> 改成 <c>Line</c>，而 <c>HandleAt</c> 对 Line 只给两个端点 →
/// 八向句柄一个都不剩、属性面板宽高那格也跟着消失（<c>HasBox =&gt; Kind != Line</c>）；</item>
/// <item>接缝同步假设"首尾是同一个可见点"，可转曲线产的是<strong>首尾不重合 + 收口段</strong> →
/// 拖第一个顶点会把末点叠到同一坐标，形状当场塌一角（他说的"扭曲"）；</item>
/// <item>删点护栏按 <c>pts.Count - 1</c> 数可见点 → 转曲线后的五边形只删得掉一个角、四边形一个都删不掉。</item>
/// </list>
/// <para>两种表示并存是既成事实（画到末点双击闭合的那条确实首尾重合），所以函数必须<strong>认</strong>，
/// 不能继续假设。每条判据都配了反面的老形状，防止"修这边修塌那边"。</para>
/// </summary>
public class CurveBoxScaleCoreTests
{
    private static TemplateElement Pentagon() => new()
    {
        Kind = ElementKind.Polygon, X = 20, Y = 10, Width = 40, Height = 40,
    };

    private static TemplateElement ConvertedPentagon() => ShapeGeometry.ToClosedCurve(Pentagon())!;

    /// <summary>手工搭一条"首尾真重合"的闭合曲线（画到末点双击闭合产的就是这种）。</summary>
    private static TemplateElement SharedSeamSquare()
    {
        var e = new TemplateElement { Kind = ElementKind.Line, Closed = true };
        CurveGeometry.ApplyNodes(e, new List<CurveNode>
        {
            new(0, 0, 0, 0, 0, 0), new(20, 0, 0, 0, 0, 0), new(20, 20, 0, 0, 0, 0), new(0, 20, 0, 0, 0, 0),
            new(0, 0, 0, 0, 0, 0),
        });
        return e;
    }

    private static LabelTemplate One(TemplateElement element)
    {
        var template = new LabelTemplate { Id = "user.curve", Name = "曲线测试", WidthMm = 140, HeightMm = 100, BuiltIn = false };
        template.Elements.Add(element);
        return template;
    }

    [Fact]
    public void AConvertedShapeDoesNotShareItsSeam()
    {
        var converted = ConvertedPentagon();
        Assert.True(CurveGeometry.IsClosed(converted));
        Assert.False(CurveGeometry.SeamCoincides(converted),
            "「转为曲线」产的首尾是两个不同的角，判成重合就会把末点叠过来");
        Assert.Equal(5, CurveGeometry.VisiblePointCount(converted));

        var shared = SharedSeamSquare();
        Assert.True(CurveGeometry.SeamCoincides(shared));
        Assert.Equal(4, CurveGeometry.VisiblePointCount(shared));      // 五格表里首尾是同一个点
    }

    [Fact]
    public void DraggingTheFirstCornerOfAConvertedShapeLeavesTheLastAlone()
    {
        var converted = ConvertedPentagon();
        var (firstX, firstY) = (converted.X, converted.Y);
        var (lastX, lastY) = (converted.X2, converted.Y2);
        CurveGeometry.MoveNode(converted, 0, firstX - 6, firstY + 6);

        Assert.True(Math.Abs(converted.X - (firstX - 6)) < 0.5, "拖的那一点没走");
        Assert.Equal(lastX, converted.X2, 3);
        Assert.Equal(lastY, converted.Y2, 3);
    }

    [Fact]
    public void DraggingEitherEndOfASharedSeamStillMovesBoth()
    {
        var shared = SharedSeamSquare();
        CurveGeometry.MoveNode(shared, 0, -4, -4);
        Assert.Equal((-4, -4), (shared.X, shared.Y));
        Assert.Equal((-4, -4), (shared.X2, shared.Y2));        // 同一个可见点：另一端跟着走（第 53 棒口径）

        CurveGeometry.MoveNode(shared, 4, -8, -8);
        Assert.Equal((-8, -8), (shared.X, shared.Y));
    }

    [Fact]
    public void AConvertedQuadrilateralCanLoseExactlyOneCorner()
    {
        var rect = ShapeGeometry.ToClosedCurve(new TemplateElement
        {
            Kind = ElementKind.Rect, X = 10, Y = 10, Width = 40, Height = 24,
        })!;
        Assert.Equal(4, CurveGeometry.VisiblePointCount(rect));
        Assert.True(CurveGeometry.RemoveNode(rect, 1), "转曲线后的四边形一个角都删不掉（护栏按首尾重合数点了）");
        Assert.Equal(3, CurveGeometry.VisiblePointCount(rect));
        Assert.False(CurveGeometry.RemoveNode(rect, 1), "只剩三个可见点，再删就成一条来回的线了");
    }

    [Fact]
    public void AConvertedPentagonAllowsTwoCornersGoneAndRefusesTheThird()
    {
        var penta = ConvertedPentagon();
        Assert.True(CurveGeometry.RemoveNode(penta, 1));
        Assert.True(CurveGeometry.RemoveNode(penta, 1));
        Assert.Equal(3, CurveGeometry.VisiblePointCount(penta));
        Assert.False(CurveGeometry.RemoveNode(penta, 1));
    }

    [Fact]
    public void ScalingACurveScalesTheHandlesTheSameWay()
    {
        var e = new TemplateElement { Kind = ElementKind.Line };
        CurveGeometry.ApplyNodes(e, new List<CurveNode>
        {
            new(10, 10, 0, 0, 6, 0), new(40, 10, -6, 0, 6, 0), new(40, 30, 0, 0, 0, 0),
        });
        CurveGeometry.ScaleBy(e, 2, 1.5, 0, 0);

        var pts = CurveGeometry.NodesOf(e);
        Assert.Equal(20, pts[0].X, 3);
        Assert.Equal(15, pts[0].Y, 3);
        Assert.Equal(12, pts[0].OutX, 3);       // 柄也按同轴乘：只挪点不挪柄会把弯度改样
        Assert.Equal(0, pts[0].OutY, 3);
        Assert.Equal(80, pts[1].X, 3);
        Assert.Equal(-12, pts[1].InX, 3);
        Assert.Equal(45, pts[2].Y, 3);
    }

    [Fact]
    public void APlainStraightLineIsNotScaledByTheBox()
    {
        var line = new TemplateElement { Kind = ElementKind.Line, X = 10, Y = 10, X2 = 50, Y2 = 30 };
        CurveGeometry.ScaleBy(line, 2, 2, 0, 0);
        Assert.Equal((10d, 10d, 50d, 30d), (line.X, line.Y, line.X2, line.Y2));
    }

    [Fact]
    public void TheBoxHandlesComeBackForAClosedCurve()
    {
        var converted = ConvertedPentagon();
        var box = EditGeometry.VisualBoxOf(converted);
        var handle = EditGeometry.HandleAt(converted, box.X, box.Y, 2);
        Assert.True(handle.HasFlag(ResizeHandle.Left) && handle.HasFlag(ResizeHandle.Top),
            $"盒左上角抓不到缩放句柄（拿到 {handle}）= 转曲线后仍然没法拉伸");

        var line = new TemplateElement { Kind = ElementKind.Line, X = 10, Y = 10, X2 = 50, Y2 = 30 };
        Assert.Equal(ResizeHandle.None, EditGeometry.HandleAt(line, 0, 0, 2));   // 直线不吃盒句柄
    }

    [Fact]
    public void DraggingACornerScalesTheWholeShapeAndHoldsTheOppositeCorner()
    {
        var converted = ConvertedPentagon();
        var template = One(converted);
        var before = CurveGeometry.BoundsMm(converted);
        var heldX = before.X;                       // 拖右下角 → 左上角该钉住
        var heldY = before.Y;

        EditGeometry.ResizeBy(template, 0, ResizeHandle.BottomRight, 20, 12);

        var after = CurveGeometry.BoundsMm(converted);
        Assert.True(after.Width > before.Width + 1, "拖角没把整只形状放大");
        Assert.True(after.Height > before.Height + 1);
        Assert.Equal(heldX, after.X, 1);
        Assert.Equal(heldY, after.Y, 1);
    }

    [Fact]
    public void SettingThePanelWidthScalesACurveInsteadOfDoingNothing()
    {
        var converted = ConvertedPentagon();
        var before = CurveGeometry.BoundsMm(converted);

        EditGeometry.SetBoxSize(converted, before.Width * 2, null);

        var after = CurveGeometry.BoundsMm(converted);
        Assert.Equal(before.Width * 2, after.Width, 1);
        Assert.Equal(before.Height, after.Height, 1);     // 只给宽 = 单轴
        Assert.Equal(before.X, after.X, 1);               // 左上角不动，与"X/Y 不变"那条老直觉一致
    }

    /// <summary>
    /// 放宽只放宽<strong>盒的那一圈边</strong>（句柄长在那儿），盒的空白内部不算命中：
    /// 认了整只盒，一只大形状就会把它空白处下面那一只的点选抢走（第 83 棒"宽容不越权"同一条）。
    /// </summary>
    [Fact]
    public void TheBboxBorderIsGrabbableButTheHollowInteriorIsNot()
    {
        var converted = ConvertedPentagon();
        var box = EditGeometry.VisualBoxOf(converted);
        Assert.True(EditGeometry.HitTest(converted, box.X, box.Y + box.Height / 2, 2),
            "盒边中点抓不到 = 上下两只句柄画出来却够不着");
        Assert.False(EditGeometry.HitTest(converted, box.X + box.Width * 0.42, box.Y + box.Height * 0.62, 0.4),
            "盒的空白内部也算命中 = 大形状会抢走压在它下面那一只的点选");
    }

    [Fact]
    public void ARectangleStillWritesItsOwnWidth()
    {
        var rect = new TemplateElement { Kind = ElementKind.Rect, X = 5, Y = 5, Width = 30, Height = 20 };
        EditGeometry.SetBoxSize(rect, 60, 40);
        Assert.Equal(60, rect.Width, 3);
        Assert.Equal(40, rect.Height, 3);
    }
}
