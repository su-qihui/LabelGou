using System.IO;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 53 棒形状工具的 Core 底座：闭合曲线的段序列、接缝同步、分裂加点（形状不许变）、
/// 删点护栏、多边形/矩形"转换为曲线"（CDR 同名命令）。
/// </summary>
public class ShapeToolCoreTests
{
    private static TemplateElement ClosedSquare() => new()
    {
        Kind = ElementKind.Line, X = 10, Y = 10, X2 = 10, Y2 = 10,
        Nodes = new()
        {
            new CurveNode(30, 10, 0, 0, 0, 0),
            new CurveNode(30, 30, 0, 0, 0, 0),
            new CurveNode(10, 30, 0, 0, 0, 0),
        },
        Closed = true,
    };

    [Fact]
    public void TheClosingSegmentAppearsOnlyWhenTheSeamIsActuallyOpen()
    {
        // 转出来的闭合曲线首尾重合：返回段 (10,30)→(10,10) 本来就是普通段，不该再补一条零长度收口。
        var closed = CurveGeometry.Segments(ClosedSquare());
        Assert.Equal(4, closed.Count);
        Assert.Equal(10d, closed[^1].Y2);                               // 最后一段自己就回到了起点 (10,10)
        Assert.True(closed.All(s => !(s.IsStraight && Math.Abs(s.X1 - s.X2) < 1e-9 && Math.Abs(s.Y1 - s.Y2) < 1e-9)));

        // 手改 JSON 把终点挪开了（接缝裂口）：这时才补一段收口，出口与外接框都按闭合圈算。
        var cracked = ClosedSquare();
        cracked.X2 = 12; cracked.Y2 = 12;
        var segs = CurveGeometry.Segments(cracked);
        Assert.Equal(5, segs.Count);
        Assert.True(Math.Abs(segs[^1].X2 - 10) < 1e-9 && Math.Abs(segs[^1].Y2 - 10) < 1e-9);
    }

    [Fact]
    public void DraggingTheSeamNodeMovesBothEndsTogether()
    {
        var e = ClosedSquare();
        CurveGeometry.MoveNode(e, 0, 12, 14);                        // 起点＝终点那一个可见点
        Assert.Equal((12d, 14d), (e.X, e.Y));
        Assert.Equal((12d, 14d), (e.X2, e.Y2));                      // 不跟着动，闭合形状当场裂口
    }

    [Fact]
    public void ClosedCurveKeepsAtLeastThreeVisiblePoints()
    {
        var e = ClosedSquare();                                       // 4 个可见点（首尾重合算一个）
        Assert.Equal(5, CurveGeometry.NodesOf(e).Count);             // 含首尾重合
        Assert.True(CurveGeometry.RemoveNode(e, 1));                 // 删到只剩 3 个可见点：允许
        Assert.False(CurveGeometry.RemoveNode(e, 1));                // 再删就成 2 个——一条来回线段，不许
        Assert.Equal(4, CurveGeometry.NodesOf(e).Count);
    }

    [Fact]
    public void AddingANodeSplitsTheCurveWithoutMovingAnyPointOnIt()
    {
        var e = new TemplateElement
        {
            Kind = ElementKind.Line, X = 0, Y = 0, X2 = 40, Y2 = 0,
            StartOut = new CurveHandle(14, -18), EndIn = new CurveHandle(-14, -18),
        };
        var before = CurveGeometry.Segments(e);
        var idx = CurveGeometry.AddNode(e, 20, -12);                 // 弧顶附近双击
        Assert.Equal(1, idx);
        Assert.Equal(3, CurveGeometry.NodesOf(e).Count);             // 起点 + 新点 + 终点
        var after = CurveGeometry.Segments(e);
        Assert.Equal(2, after.Count);
        // 形状没被"加点"改动：原曲线采样 21 个点，每一点到新曲线的距离都要贴到 0.01mm 内。
        for (var k = 0; k <= 20; k++)
        {
            var t = k / 20.0;
            var px = Bezier(before[0].X1, before[0].CX1, before[0].CX2, before[0].X2, t);
            var py = Bezier(before[0].Y1, before[0].CY1, before[0].CY2, before[0].Y2, t);
            Assert.True(CurveGeometry.DistanceMm(e, px, py) < 0.05,
                $"加点后原曲线上的点 ({px:0.##},{py:0.##}) 离新曲线 {CurveGeometry.DistanceMm(e, px, py):0.###} mm——分裂把形状改动了（量具自身是 24 段折线采样，容差按它给）");
        }
        return;
        static double Bezier(double p0, double p1, double p2, double p3, double t)
        {
            var u = 1 - t;
            return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
        }
    }

    [Fact]
    public void APolygonConvertsToAClosedCurveWithItsCurrentVertices()
    {
        var poly = new TemplateElement { Kind = ElementKind.Polygon, X = 20, Y = 10, Width = 40, Height = 40 };
        var verts = ShapeGeometry.PolygonPoints(poly);
        var curve = ShapeGeometry.ToClosedCurve(poly);
        Assert.NotNull(curve);
        Assert.Equal(ElementKind.Line, curve!.Kind);
        Assert.True(curve.Closed);
        Assert.Equal(verts.Count - 2, curve.Nodes!.Count);           // 首尾重合，中间点 = 顶点数 - 2
        Assert.Equal((verts[0].X, verts[0].Y), (curve.X, curve.Y));
        Assert.Equal((verts[^1].X, verts[^1].Y), (curve.X2, curve.Y2));
        Assert.Equal(0d, curve.RotationDeg);                         // 点已是最终位置，角度归零
    }

    [Fact]
    public void ConversionBakesRotationSoTheShapeStaysPut()
    {
        var poly = new TemplateElement { Kind = ElementKind.Polygon, X = 20, Y = 10, Width = 40, Height = 40, RotationDeg = 90 };
        var curve = ShapeGeometry.ToClosedCurve(poly)!;
        // 顺时针转 90° 的正五边形：尖头从朝上变朝右——首点落在盒右边线中点 (60,30)。
        Assert.True(Math.Abs(curve.X - 60) < 0.01 && Math.Abs(curve.Y - 30) < 0.01,
            $"旋转没被烘焙：首点 ({curve.X:0.#},{curve.Y:0.#})");
    }

    [Fact]
    public void ARoundedRectRefusesConversionInsteadOfSilentlyLosingTheCorners()
    {
        var rect = new TemplateElement { Kind = ElementKind.Rect, X = 5, Y = 5, Width = 30, Height = 20 };
        Assert.NotNull(ShapeGeometry.ToClosedCurve(rect));           // 直角矩形：四角闭合曲线
        rect.SetCornerRadii(4, 0, 0, 0);
        Assert.Null(ShapeGeometry.ToClosedCurve(rect));              // 带圆角：拒绝（调用方说原因），不静默丢弧
    }

    [Fact]
    public void ClosedFieldRoundTripsAndOldFilesReadAsOpen()
    {
        var template = new LabelTemplate { Id = "user.c", Name = "闭合", WidthMm = 60, HeightMm = 40, PaddingMm = 2 };
        template.Elements.Add(ClosedSquare());
        var json = Templates.TemplateStore.ToJson(template);
        Assert.Contains("\"closed\": true", json, StringComparison.Ordinal);

        // 老文件没有 closed 字段：读出来必须逐字是开口曲线（旧行为）。
        var legacy = json.Replace(",\r\n      \"closed\": true", "").Replace(",\"closed\":true", "")
                         .Replace("\r\n      \"closed\": true,", "").Replace("\"closed\": true,", "");
        Assert.DoesNotContain("\"closed\"", legacy, StringComparison.Ordinal);
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-closed-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "t.json");
            File.WriteAllText(path, legacy);
            var (back, issues) = new Templates.TemplateStore(dir).ReadFile(path);
            Assert.True(back is not null, "读不回来：" + string.Join(" | ", issues.Select(i => i.Message)));
            Assert.False(back!.Elements[0].Closed);
        }
        finally { Directory.Delete(dir, true); }
    }
}
