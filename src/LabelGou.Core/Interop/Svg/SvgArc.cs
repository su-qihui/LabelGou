namespace LabelGou.Core.Interop.Svg;

/// <summary>
/// 端点参数化圆弧的展开工具。<strong>读入侧与写出侧共用这一份</strong>：
/// <c>SvgParser</c> 用它把 <c>path@d</c> 里的 <c>A</c> 归一成三次贝塞尔，
/// App 层的几何翻译用它把 WPF 的 <c>ArcSegment</c> 写回 SVG。
/// 两边各写一套的话，同一个圆在"导进来"和"导出去"会长得不一样。
/// </summary>
public static class SvgArc
{
    /// <summary>
    /// 端点参数化圆弧 → 三次贝塞尔（SVG 2 附录 E.6 / impl 4.6）。
    /// 超过 90° 的弧先分段，保证每段的贝塞尔近似误差在印刷可接受范围内。
    /// </summary>
    public static List<(double C1X, double C1Y, double C2X, double C2Y, double X, double Y)> ToBeziers(
        double x1, double y1, double rxIn, double ryIn, double phiDeg, bool largeArc, bool sweepFlag, double x2, double y2)
    {
        var result = new List<(double, double, double, double, double, double)>();
        var phi = phiDeg / 180.0 * Math.PI;
        var cosPhi = Math.Cos(phi);
        var sinPhi = Math.Sin(phi);

        // 1) 端点参数化 → 圆心参数化
        var dx = (x1 - x2) / 2.0;
        var dy = (y1 - y2) / 2.0;
        var x1p = cosPhi * dx + sinPhi * dy;
        var y1p = -sinPhi * dx + cosPhi * dy;

        var rx = Math.Abs(rxIn);
        var ry = Math.Abs(ryIn);
        var lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
        if (lambda > 1)
        {
            // 半径太小，画不出这条弧：规范是等比放大到刚好能画
            var scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        var rx2 = rx * rx;
        var ry2 = ry * ry;
        var num = rx2 * ry2 - rx2 * y1p * y1p - ry2 * x1p * x1p;
        var den = rx2 * y1p * y1p + ry2 * x1p * x1p;
        var factor = num <= 0 || den <= 0 ? 0 : Math.Sqrt(Math.Abs(num / den));
        if (double.IsNaN(factor)) factor = 0;
        if (largeArc == sweepFlag) factor = -factor;

        var cxp = factor * rx * y1p / ry;
        var cyp = -factor * ry * x1p / rx;
        var cx = cosPhi * cxp - sinPhi * cyp + (x1 + x2) / 2.0;
        var cy = sinPhi * cxp + cosPhi * cyp + (y1 + y2) / 2.0;

        double Angle(double ux, double uy, double vx, double vy)
        {
            var dot = ux * vx + uy * vy;
            var len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            var a = len <= 0 ? 0 : Math.Clamp(dot / len, -1, 1);
            var ang = Math.Acos(a);
            return ux * vy - uy * vx < 0 ? -ang : ang;
        }

        var theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        var delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweepFlag && delta > 0) delta -= 2 * Math.PI;
        else if (sweepFlag && delta < 0) delta += 2 * Math.PI;

        // 2) 分段（每段 ≤ 90°）并逐段转贝塞尔
        var segments = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2));
        if (segments <= 0) segments = 1;
        var per = delta / segments;
        var alpha = Math.Sin(per) * (Math.Sqrt(4 + 3 * Math.Tan(per / 2) * Math.Tan(per / 2)) - 1) / 3;
        if (double.IsNaN(alpha)) alpha = 0.5522847498 * Math.Sign(per);

        for (var i = 0; i < segments; i++)
        {
            var t0 = theta1 + per * i;
            var t1 = theta1 + per * (i + 1);

            (double X, double Y) On(double t) => (
                cx + cosPhi * rx * Math.Cos(t) - sinPhi * ry * Math.Sin(t),
                cy + sinPhi * rx * Math.Cos(t) + cosPhi * ry * Math.Sin(t));

            (double DX, double DY) Deriv(double t) => (
                -cosPhi * rx * Math.Sin(t) - sinPhi * ry * Math.Cos(t),
                -sinPhi * rx * Math.Sin(t) + cosPhi * ry * Math.Cos(t));

            var p0 = On(t0);
            var p3 = On(t1);
            var d0 = Deriv(t0);
            var d1 = Deriv(t1);
            result.Add((p0.X + alpha * d0.DX, p0.Y + alpha * d0.DY,
                p3.X - alpha * d1.DX, p3.Y - alpha * d1.DY, p3.X, p3.Y));
        }
        return result;
    }
}
