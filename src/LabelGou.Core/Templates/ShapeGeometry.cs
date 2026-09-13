using LabelGou.Core.Editing;

namespace LabelGou.Core.Templates;

/// <summary>
/// 椭圆与正多边形的几何——<strong>全项目只有这一份"顶点怎么数"</strong>（第 51 棒）。
/// <para>多边形是内切于外接盒的那个圆经"盒比例拉伸"后的正多边形：首点朝正上（CorelDRAW 的五边形
/// 起手就是尖朝上），顺时针排开。WPF 画布、SVG 出口、跨出口逐格比对读的都是这一份点表，
/// 两处各算一遍迟早长歪（§五-62 那族）。</para>
/// <para>纯算术、纯毫米；渲染端只照着连点，不许自己再推角度。</para>
/// </summary>
public static class ShapeGeometry
{
    /// <summary>最少几条边——少于三条就不叫多边形。</summary>
    public const int MinSides = 3;

    /// <summary>最多几条边——再多画出来就是圆，还白吃顶点数。</summary>
    public const int MaxSides = 100;

    /// <summary>边数一律从这里取：缺字段 = 默认五边，再夹进 <see cref="MinSides"/>~<see cref="MaxSides"/>，坏文件也不崩。</summary>
    public static int SidesOf(TemplateElement element)
        => Math.Clamp(element.PolygonSides ?? TemplateElement.DefaultPolygonSides, MinSides, MaxSides);

    /// <summary>
    /// 正多边形顶点（毫米，绝对坐标，首点朝上、顺时针）。外接盒由元素的 X/Y/Width/Height 说话，
    /// 与椭圆、矩形同一套摆位口径。
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> PolygonPoints(TemplateElement element)
    {
        var sides = SidesOf(element);
        var cx = element.X + element.Width / 2;
        var cy = element.Y + element.Height / 2;
        var rx = element.Width / 2;
        var ry = element.Height / 2;
        var points = new List<(double, double)>(sides);
        for (var i = 0; i < sides; i++)
        {
            // -90° 起手＝首点正上方；拉伸到盒比例，非正方盒里的多边形与椭圆同一族仿射形状。
            var angle = -Math.PI / 2 + i * 2 * Math.PI / sides;
            points.Add((cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle)));
        }
        return points;
    }

    /// <summary>这元素是不是"有外观可谈"的盒状形状（矩形/椭圆/多边形共用填充、描边那几件字段）。</summary>
    public static bool IsBoxShape(TemplateElement element)
        => element.Kind is ElementKind.Rect or ElementKind.Ellipse or ElementKind.Polygon;

    /// <summary>
    /// 把多边形/矩形<strong>转成闭合曲线</strong>（第 53 棒，CorelDRAW 的「转换为曲线」——它自带文案写得很明白：
    /// 「将选定对象转换为曲线，以便进行更灵活的编辑」）。转完就能用形状工具加点、删点、拖点。
    /// <para>规则：顶点按当前形状现算（多边形含旋转烘焙——转完 <c>RotationDeg</c> 归零，因为点已是最终位置）；
    /// 首尾重合 + <c>Closed</c>；填充 / 描边 / 线宽 / 笔色原样带走。<strong>带圆角的矩形拒绝转</strong>
    /// （返回 null，调用方说人话）——圆角是四段弧，直转成四个尖点会静默丢圆角，那比不给转更坏。</para>
    /// </summary>
    public static TemplateElement? ToClosedCurve(TemplateElement element)
    {
        var pts = element.Kind switch
        {
            ElementKind.Polygon => PolygonPoints(element).ToList(),
            ElementKind.Rect => RectCorners(element),
            _ => null,
        };
        if (pts is null || pts.Count < 3) return null;
        var radii = element.CornerRadii();
        if (element.Kind == ElementKind.Rect
            && (radii.TopLeft > 1e-9 || radii.TopRight > 1e-9 || radii.BottomRight > 1e-9 || radii.BottomLeft > 1e-9))
            return null;

        var baked = BakeRotation(element, pts);
        var closed = element.CloneTemplate();
        closed.Kind = ElementKind.Line;
        closed.X = baked[0].X;
        closed.Y = baked[0].Y;
        closed.X2 = baked[^1].X;
        closed.Y2 = baked[^1].Y;
        closed.Nodes = baked.Skip(1).Take(baked.Count - 2)
            .Select(p => new CurveNode(p.X, p.Y, 0, 0, 0, 0)).ToList();
        closed.StartOut = null;
        closed.EndIn = null;
        closed.Closed = true;
        closed.RotationDeg = 0;                     // 点已是最终位置，再带着角度就是转两遍
        closed.Width = Math.Max(EditGeometry.MinSideMm, baked.Max(p => p.X) - baked.Min(p => p.X));
        closed.Height = Math.Max(EditGeometry.MinSideMm, baked.Max(p => p.Y) - baked.Min(p => p.Y));
        return closed;
    }

    private static List<(double X, double Y)> RectCorners(TemplateElement e) => new()
    {
        (e.X, e.Y), (e.X + e.Width, e.Y), (e.X + e.Width, e.Y + e.Height), (e.X, e.Y + e.Height),
    };

    /// <summary>元素带着旋转角时，转曲线要把每个点绕盒中心转到最终位置——曲线不认 RotationDeg（端点即形状，43 棒口径）。</summary>
    private static List<(double X, double Y)> BakeRotation(TemplateElement e, List<(double X, double Y)> pts)
    {
        if (Math.Abs(e.RotationDeg) <= 1e-9) return pts;
        var cx = e.X + e.Width / 2;
        var cy = e.Y + e.Height / 2;
        var rad = e.RotationDeg * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return pts.Select(p =>
        {
            var dx = p.X - cx;
            var dy = p.Y - cy;
            return (cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
        }).ToList();
    }
}
