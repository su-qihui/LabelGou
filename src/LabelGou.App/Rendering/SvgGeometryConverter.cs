using System.Windows;
using System.Windows.Media;
using LabelGou.Core.Interop.Svg;

namespace LabelGou.App.Rendering;

/// <summary>
/// <see cref="SvgPathCommand"/>（毫米）与 WPF <see cref="Geometry"/>（设备单位）之间的双向翻译。
/// <para>
/// 两个方向本棒都要用：<strong>读进来</strong>——把 CDR 导出的 SVG 底图画到屏幕上
/// （<c>SvgDrawableBuilder</c>）；<strong>写出去</strong>——把 <c>FormattedText.BuildGeometry()</c>
/// 的转曲轮廓写成 <c>&lt;path d="…"&gt;</c>（<c>SheetSvgWriter</c>，定案「导出默认转曲」）。
/// </para>
/// <para>
/// 本类只做两件事：坐标乘一个比例、曲线种类保持。它不认识 dpi，也不认识磅。
/// <strong>贝塞尔绝不展平成折线</strong>（底图保真的底线）；二次贝塞尔按 2/3 规则升三次，
/// WPF 可能给的圆弧走 <see cref="SvgArc.ToBeziers"/>——与解析器读入 <c>A</c> 时同一份实现，
/// 所以同一个圆导进来再导出去不会变样。
/// </para>
/// </summary>
public static class SvgGeometryConverter
{
    /// <summary>
    /// 毫米命令表 → WPF 几何。<paramref name="unitPerMm"/> 传 <c>96/25.4</c> 得到 DIU 域几何。
    /// </summary>
    public static Geometry BuildGeometry(IReadOnlyList<SvgPathCommand> commands, bool evenOdd, double unitPerMm)
    {
        var figures = new PathFigureCollection();
        PathFigure? figure = null;

        void EndFigure()
        {
            if (figure is { Segments.Count: > 0 }) figures.Add(figure);
            figure = null;
        }

        foreach (var cmd in commands)
        {
            var args = cmd.Args;
            switch (cmd.Command)
            {
                case 'M':
                {
                    EndFigure();
                    if (args.Count < 2) break;
                    figure = new PathFigure
                    {
                        StartPoint = At(args[0], args[1], unitPerMm),
                        IsFilled = true,
                        IsClosed = false,
                    };
                    // 一对以上是隐式 lineto：解析器已逐个拆开，这里防御手写的命令表
                    for (var i = 2; i + 1 < args.Count; i += 2)
                        figure.Segments.Add(new LineSegment(At(args[i], args[i + 1], unitPerMm), true));
                    break;
                }
                case 'L':
                {
                    if (figure is null) break;
                    for (var i = 0; i + 1 < args.Count; i += 2)
                        figure.Segments.Add(new LineSegment(At(args[i], args[i + 1], unitPerMm), true));
                    break;
                }
                case 'C':
                {
                    if (figure is null) break;
                    for (var i = 0; i + 5 < args.Count; i += 6)
                        figure.Segments.Add(new BezierSegment(
                            At(args[i], args[i + 1], unitPerMm),
                            At(args[i + 2], args[i + 3], unitPerMm),
                            At(args[i + 4], args[i + 5], unitPerMm), true));
                    break;
                }
                case 'Q':
                {
                    if (figure is null) break;
                    for (var i = 0; i + 3 < args.Count; i += 4)
                        figure.Segments.Add(new QuadraticBezierSegment(
                            At(args[i], args[i + 1], unitPerMm),
                            At(args[i + 2], args[i + 3], unitPerMm), true));
                    break;
                }
                case 'Z':
                {
                    if (figure is null) break;
                    figure.IsClosed = true;
                    EndFigure();   // 闭合的子路径同样要存进集合，否则圆角框会整块消失
                    break;
                }
            }
        }
        EndFigure();

        var geometry = new PathGeometry(figures) { FillRule = evenOdd ? FillRule.EvenOdd : FillRule.Nonzero };
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// WPF 几何 → 毫米命令表。<paramref name="unitToMm"/> 传 <c>25.4/96</c>（即 <c>Mm.FromDiu</c> 的系数）。
    /// <para><paramref name="evenOdd"/> 必须带出去：字形轮廓靠 evenodd 才能在“O、回”这类字里挖出洞，
    /// 写丢了就是一个实心黑块。</para>
    /// <para>遇到无法用贝塞尔表达的输入（非 <see cref="PathGeometry"/> 的怪几何）会展平并往
    /// <paramref name="notes"/> 里记一条，绝不静默降质。</para>
    /// </summary>
    public static List<SvgPathCommand> ToCommands(Geometry geometry, double unitToMm, out bool evenOdd, List<string>? notes = null)
    {
        evenOdd = FillRuleOf(geometry) == FillRule.EvenOdd;
        var emitter = new Emitter(unitToMm, notes);
        emitter.Emit(geometry, transform: null);
        return emitter.Commands;
    }
    
    /// <summary>取几何的填充规则（嵌套组取最外层的，与 WPF 自己的渲染口径一致）。</summary>
    private static FillRule FillRuleOf(Geometry geometry) => geometry switch
    {
        PathGeometry path => path.FillRule,
        GeometryGroup group => group.FillRule,
        _ => FillRule.EvenOdd,   // 字形轮廓几乎都是 evenodd；展平得到的几何也归到这里
    };

    /// <summary>
    /// 一个往外吐命令的累加器。<see cref="Cursor"/> 必须跟着子路径与嵌套几何走，
    /// 少传一步就会把后续段连到错的地方去。
    /// </summary>
    private sealed class Emitter
    {
        public Emitter(double unitToMm, List<string>? notes)
        {
            UnitToMm = unitToMm;
            Notes = notes;
        }

        public List<SvgPathCommand> Commands { get; } = new();

        private double UnitToMm { get; }

        private List<string>? Notes { get; }

        /// <summary>当前笔位（设备单位，未经变换）。圆弧与二次贝塞尔都要拿它当上一段的终点。</summary>
        private Point Cursor { get; set; }

        public void Emit(Geometry geometry, Transform? transform)
        {
            switch (geometry)
            {
                case PathGeometry path:
                    EmitPath(path, Combine(transform, path.Transform));
                    break;
                case GeometryGroup group:
                    foreach (var child in group.Children)
                    {
                        if (child is not null) Emit(child, Combine(transform, child.Transform));
                    }
                    break;
                default:
                    Notes?.Add("有一处几何不是路径/几何组，已按约 0.005mm 容差展平成折线。");
                    EmitPath(geometry.GetFlattenedPathGeometry(0.02, ToleranceType.Absolute), transform);
                    break;
            }
        }

        private void EmitPath(PathGeometry path, Transform? transform)
        {
            foreach (var figure in path.Figures)
            {
                if (figure is null) continue;
                Cursor = figure.StartPoint;
                Commands.Add(new SvgPathCommand('M', Pair(transform, figure.StartPoint)));

                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            EmitLine(transform, line.Point);
                            break;

                        case PolyLineSegment poly:
                            foreach (var point in poly.Points) EmitLine(transform, point);
                            break;

                        case BezierSegment cubic:
                            Commands.Add(new SvgPathCommand('C', Six(
                                Pair(transform, cubic.Point1),
                                Pair(transform, cubic.Point2),
                                Pair(transform, cubic.Point3))));
                            Cursor = cubic.Point3;
                            break;

                        case PolyBezierSegment polyCubic:
                        {
                            var points = polyCubic.Points;
                            for (var i = 0; i + 2 < points.Count; i += 3)
                            {
                                Commands.Add(new SvgPathCommand('C', Six(
                                    Pair(transform, points[i]),
                                    Pair(transform, points[i + 1]),
                                    Pair(transform, points[i + 2]))));
                                Cursor = points[i + 2];
                            }
                            break;
                        }

                        case QuadraticBezierSegment quad:
                        {
                            // 二次升三次：c1 = p0 + 2/3(q-p0)，c2 = p1 + 2/3(q-p1)
                            Commands.Add(new SvgPathCommand('C', Six(
                                Pair(transform, Lerp(Cursor, quad.Point1, 2.0 / 3.0)),
                                Pair(transform, Lerp(quad.Point2, quad.Point1, 2.0 / 3.0)),
                                Pair(transform, quad.Point2))));
                            Cursor = quad.Point2;
                            break;
                        }

                        case ArcSegment arc:
                            EmitArc(arc, transform);
                            break;
                    }
                }

                if (figure.IsClosed) Commands.Add(new SvgPathCommand('Z', Array.Empty<double>()));
            }
        }

        /// <summary>
        /// 圆弧先按局部坐标展开成三次贝塞尔（用 <see cref="SvgArc.ToBeziers"/>，与读入侧同一份），
        /// 再把控制点过一遍变换。对"缩放+平移"（本项目的实际情形）这是精确的。
        /// </summary>
        private void EmitArc(ArcSegment arc, Transform? transform)
        {
            // WPF 的椭圆弧参数是"半径"，SVG 的 A 也是；中心点由两端点反解（SvgArc 内部完成）
            foreach (var bez in SvgArc.ToBeziers(
                         Cursor.X, Cursor.Y,
                         Math.Abs(arc.Size.Width / 2), Math.Abs(arc.Size.Height / 2),
                         arc.RotationAngle,
                         arc.IsLargeArc,
                         arc.SweepDirection == SweepDirection.Clockwise,
                         arc.Point.X, arc.Point.Y))
            {
                Commands.Add(new SvgPathCommand('C', Six(
                    Pair(transform, new Point(bez.C1X, bez.C1Y)),
                    Pair(transform, new Point(bez.C2X, bez.C2Y)),
                    Pair(transform, new Point(bez.X, bez.Y)))));
                Cursor = new Point(bez.X, bez.Y);
            }
        }

        private void EmitLine(Transform? transform, Point point)
        {
            Commands.Add(new SvgPathCommand('L', Pair(transform, point)));
            Cursor = point;
        }

        /// <summary>设备单位点 → 毫米的一对坐标（命令的参数是平铺的，所以直接返两个数）。</summary>
        private double[] Pair(Transform? transform, Point unit)
        {
            var p = transform is null ? unit : transform.Value.Transform(unit);
            return new[] { p.X * UnitToMm, p.Y * UnitToMm };
        }

        private static double[] Six(double[] a, double[] b, double[] c)
            => new[] { a[0], a[1], b[0], b[1], c[0], c[1] };

        private static Point Lerp(Point from, Point to, double t)
            => new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

        private static Transform? Combine(Transform? outer, Transform? inner)
        {
            if (outer is null) return inner;
            if (inner is null) return outer;
            return new TransformGroup { Children = { outer, inner } };
        }
    }

    private static Point At(double xMm, double yMm, double unitPerMm) => new(xMm * unitPerMm, yMm * unitPerMm);
}
