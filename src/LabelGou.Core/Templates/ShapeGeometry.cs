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
}
