namespace LabelGou.Core.Templates;

/// <summary>
/// 贝塞尔曲线的一个<strong>中间节点</strong>：绝对落点（毫米）+ 两根控制柄的相对偏移（毫米）。
/// <para>
/// 只存中间节点是有意的：起点与终点仍然住在 <see cref="TemplateElement.X"/>/<see cref="Y"/> 与
/// <see cref="TemplateElement.X2"/>/<see cref="Y2"/> 里（老字段、老代码、老模板文件一个字都不改），
/// 两端各留一根柄在元素上（<see cref="TemplateElement.StartOut"/> 与 <see cref="EndIn"/>）。
/// 这样"同一个点存两处、拖一下就不一样"的破事从结构上就没有发生机会。
/// </para>
/// </summary>
/// <param name="X">节点横坐标（毫米，绝对）。</param>
/// <param name="Y">节点纵坐标（毫米，绝对）。</param>
/// <param name="InX">进柄相对节点的横向偏移（毫米）。0 表示这一侧是尖角。</param>
/// <param name="InY">进柄纵向偏移。</param>
/// <param name="OutX">出柄横向偏移。</param>
/// <param name="OutY">出柄纵向偏移。</param>
public sealed record CurveNode(double X, double Y, double InX, double InY, double OutX, double OutY)
{
    /// <summary>两侧都没有柄＝尖角（Shift 拖出来的直线段两头就是这个）。</summary>
    public bool IsCorner => IsZero(InX) && IsZero(InY) && IsZero(OutX) && IsZero(OutY);

    /// <summary>挪动一段距离（拖节点用；柄跟着走，形状不变）。</summary>
    public CurveNode Moved(double dxMm, double dyMm) => this with { X = X + dxMm, Y = Y + dyMm };

    /// <summary>换掉一根柄（拖控制柄用，节点不动）。</summary>
    public CurveNode WithIn(double dxMm, double dyMm) => this with { InX = dxMm, InY = dyMm };

    public CurveNode WithOut(double dxMm, double dyMm) => this with { OutX = dxMm, OutY = dyMm };

    private static bool IsZero(double v) => Math.Abs(v) < 1e-6;

    private static double Round(double v) => Math.Round(v, 3);

    /// <summary>存盘写法：六个数四舍五入到 0.001mm（唛头的精度到不了这一档，但读文件的人要能看懂）。</summary>
    public override string ToString() =>
        $"({Round(X)},{Round(Y)}) in({Round(InX)},{Round(InY)}) out({Round(OutX)},{Round(OutY)})";
}

/// <summary>端点上那根柄的相对偏移（毫米）。</summary>
public sealed record CurveHandle(double DX, double DY)
{
    public bool IsZero => Math.Abs(DX) < 1e-6 && Math.Abs(DY) < 1e-6;
}

/// <summary>
/// 一段三次贝塞尔（从 <c>(X1,Y1)</c> 到 <c>(X2,Y2)</c>）。<see cref="CurveGeometry"/> 把元素拆成这些段，
/// 渲染、命中、外接框、SVG 的 <c>d</c> 全从这一种结构算——只此一份画法。
/// </summary>
public sealed record CurveSegment(
    double X1, double Y1, double CX1, double CY1, double CX2, double CY2, double X2, double Y2)
{
    /// <summary>两根控制柄都落在端点上＝直线段（SVG 出口据此写 <c>L</c> 而不是 <c>C</c>）。</summary>
    public bool IsStraight =>
        Math.Abs(CX1 - X1) < 1e-6 && Math.Abs(CY1 - Y1) < 1e-6
        && Math.Abs(CX2 - X2) < 1e-6 && Math.Abs(CY2 - Y2) < 1e-6;
}

/// <summary>画布上点中了曲线的哪一部分。</summary>
public enum CurvePart
{
    /// <summary>节点本体（挪它＝整段弯度跟着平移）。</summary>
    Node = 0,

    /// <summary>进柄的柄头（只改这一侧的切线方向）。</summary>
    In = 1,

    /// <summary>出柄的柄头。</summary>
    Out = 2,
}

/// <summary>
/// 一次命中的结果：<paramref name="Index"/> 是 <see cref="CurveGeometry.NodesOf"/> 那张全节点表里的下标
/// （0 = 起点，最后一格 = 终点），<paramref name="Part"/> 是抓到的是节点还是哪一根柄。
/// </summary>
public sealed record CurveHit(int Index, CurvePart Part);

/// <summary>
/// 曲线元素的算法本体：<strong>元素 → 段序列</strong>、段序列 → 精确外接框 / 路径串 / 变换。
/// <para>
/// 全在 Core：预览、位图、打印、PDF、SVG 五个出口都问这里要同一份段序列（47 棒"五出口读同一支笔"
/// 的几何版）。坐标一律毫米，与模板文件同口径。
/// </para>
/// </summary>
public static class CurveGeometry
{
    /// <summary>一条曲线最多几个中间节点。给 AI 生成的模板兜个底，也防止手滑拖出上百个点。</summary>
    public const int MaxNodes = 64;

    /// <summary>这条元素是不是曲线（有中间节点或任一端有柄）。</summary>
    public static bool IsCurved(TemplateElement element) =>
        (element.Nodes is { Count: > 0 }) || (element.StartOut is { IsZero: false }) || (element.EndIn is { IsZero: false });

    /// <summary>
    /// 这个节点是不是<strong>平滑</strong>的：两根柄都在、且方向大致成一条直线。
    /// <para>拖柄时靠它决定"另一侧跟不跟着镜像"——平滑节点跟着（这是 CDR 里调切线最常用的那半下），
    /// 尖角节点只动这一根（否则单侧切线就调不动了）。角度容差 2°，比人眼在屏幕上的判断还宽一点。</para>
    /// </summary>
    public static bool IsSmooth(CurveNode n)
    {
        if (Math.Abs(n.InX) < 1e-6 && Math.Abs(n.InY) < 1e-6) return false;
        if (Math.Abs(n.OutX) < 1e-6 && Math.Abs(n.OutY) < 1e-6) return false;
        var cross = n.InX * n.OutY - n.InY * n.OutX;
        var dot = n.InX * n.OutX + n.InY * n.OutY;
        if (dot >= 0) return false;                       // 同向＝两根柄在节点的同一侧，那是尖角
        var lenIn = Math.Sqrt(n.InX * n.InX + n.InY * n.InY);
        var lenOut = Math.Sqrt(n.OutX * n.OutX + n.OutY * n.OutY);
        return Math.Abs(cross) / (lenIn * lenOut) < 0.035;
    }

    /// <summary>
    /// 元素的<strong>全节点表</strong>：下标 0 = 起点，最后一格 = 终点，中间是 <see cref="TemplateElement.Nodes"/>。
    /// <para>命中、拖动、渲染、SVG 全都问这一份，索引也就只有这一套（0..末）。</para>
    /// </summary>
    public static List<CurveNode> NodesOf(TemplateElement element)
    {
        var pts = new List<CurveNode>
        {
            new(element.X, element.Y, 0, 0, element.StartOut?.DX ?? 0, element.StartOut?.DY ?? 0),
        };
        if (element.Nodes is { Count: > 0 } nodes)
        {
            foreach (var node in nodes)
            {
                if (node is not null) pts.Add(node);
            }
        }
        var endIn = element.EndIn;
        pts.Add(new CurveNode(element.X2, element.Y2, endIn?.DX ?? 0, endIn?.DY ?? 0, 0, 0));
        return pts;
    }

    /// <summary>
    /// 把全节点表写回元素（<see cref="NodesOf"/> 的逆向，唯一被允许的写入口）。
    /// <para><strong>退化成直线时三个曲线字段一律清空</strong>——不是"存一份长度为 0 的柄"：
    /// 用曲线工具拖一条直线出来，存的必须与从前那条直线逐字同形，否则同一个动作在新老文件里长两个样子。</para>
    /// </summary>
    public static void ApplyNodes(TemplateElement element, IReadOnlyList<CurveNode> pts)
    {
        if (pts.Count < 2) return;                       // 一个点画不出东西：保持原样，不猜
        var first = pts[0];
        var last = pts[^1];
        element.X = Round(first.X);
        element.Y = Round(first.Y);
        element.X2 = Round(last.X);
        element.Y2 = Round(last.Y);
        element.StartOut = Keep(first.OutX, first.OutY);
        element.EndIn = Keep(last.InX, last.InY);
        var middle = pts.Skip(1).Take(pts.Count - 2)
            .Select(n => n with { X = Round(n.X), Y = Round(n.Y), InX = Round(n.InX), InY = Round(n.InY), OutX = Round(n.OutX), OutY = Round(n.OutY) })
            .ToList();
        element.Nodes = middle.Count == 0 ? null : middle;
        if (element.Nodes is null && element.StartOut is null && element.EndIn is null) return;   // 直线：三个字段都空，与老文件同形

        /// <summary>零柄不留字段（0 与"没填"在几何上同义，但存出来是两个样子）。</summary>
        static CurveHandle? Keep(double dx, double dy)
            => Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6 ? null : new CurveHandle(Round(dx), Round(dy));
    }

    /// <summary>
    /// 节点/控制柄的命中（画布上点中谁）。<strong>先比节点再比柄</strong>：柄头小、节点是落点，
    /// 两个都能抓时人想动的多半是节点。半径由调用方给（屏幕像素换算成毫米，与句柄那套同一个数）。
    /// </summary>
    public static CurveHit? HandleAt(TemplateElement element, double xMm, double yMm, double radiusMm)
    {
        var pts = NodesOf(element);
        CurveHit? best = null;
        var bestGap = double.MaxValue;
        for (var i = 0; i < pts.Count; i++)
        {
            var n = pts[i];
            Consider(i, CurvePart.Node, n.X, n.Y);
            if (i > 0) Consider(i, CurvePart.In, n.X + n.InX, n.Y + n.InY);
            if (i < pts.Count - 1) Consider(i, CurvePart.Out, n.X + n.OutX, n.Y + n.OutY);
        }
        return bestGap <= radiusMm ? best : null;

        void Consider(int index, CurvePart part, double px, double py)
        {
            var gap = Math.Max(Math.Abs(px - xMm), Math.Abs(py - yMm));
            if (part != CurvePart.Node) gap += 1e-9;     // 同距离时节点优先（浮点上比大小，不是给柄加权）
            if (gap < bestGap) { bestGap = gap; best = new CurveHit(index, part); }
        }
    }

    /// <summary>把某个节点挪到绝对坐标处（柄跟着走，弯度不变）。</summary>
    public static void MoveNode(TemplateElement element, int index, double xMm, double yMm)
    {
        var pts = NodesOf(element);
        if (index < 0 || index >= pts.Count) return;
        var n = pts[index];
        pts[index] = n with { X = xMm, Y = yMm };
        ApplyNodes(element, pts);
    }

    /// <summary>拖某根控制柄（节点不动）。起点没有进柄、终点没有出柄，这两格会被忽略。</summary>
    public static void SetHandle(TemplateElement element, CurveHit hit, double dxMm, double dyMm)
    {
        var pts = NodesOf(element);
        if (hit.Index < 0 || hit.Index >= pts.Count) return;
        var n = pts[hit.Index];
        pts[hit.Index] = hit.Part switch
        {
            CurvePart.In when hit.Index > 0 => n.WithIn(dxMm, dyMm),
            CurvePart.Out when hit.Index < pts.Count - 1 => n.WithOut(dxMm, dyMm),
            _ => n,
        };
        ApplyNodes(element, pts);
    }

    /// <summary>删掉一个中间节点（首尾不许删——那条线就没方向了，也让"至少两个点"这条不变式成立）。</summary>
    public static bool RemoveNode(TemplateElement element, int index)
    {
        var pts = NodesOf(element);
        if (index <= 0 || index >= pts.Count - 1) return false;
        pts.RemoveAt(index);
        ApplyNodes(element, pts);
        return true;
    }

    /// <summary>
    /// 拉直：所有柄归零，节点位置不动（拖歪了想退回直线时用，比删了重画快）。
    /// <para>注意它<em>不删</em>中间节点——节点还在，只是每段都成了直线段；要真回到"两个点的直线"，
    /// 属性面板那颗「只留两端」走的是 <see cref="ApplyNodes"/> 传首尾两点。</para>
    /// </summary>
    public static void Straighten(TemplateElement element)
    {
        var pts = NodesOf(element).Select(n => n with { InX = 0d, InY = 0d, OutX = 0d, OutY = 0d }).ToList();
        ApplyNodes(element, pts);
    }

    /// <summary>
    /// 拆成段序列。没填曲线字段时交回<strong>恰好一段直线</strong>——所以调用方不必再分"直线/曲线"两套代码，
    /// 老的线条元素走的也是这条路，画法与从前逐字一致。
    /// </summary>
    public static IReadOnlyList<CurveSegment> Segments(TemplateElement element)
    {
        var pts = NodesOf(element);
        var segments = new List<CurveSegment>(pts.Count - 1);
        for (var i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            segments.Add(new CurveSegment(
                a.X, a.Y, a.X + a.OutX, a.Y + a.OutY,
                b.X + b.InX, b.Y + b.InY, b.X, b.Y));
        }
        return segments;
    }

    /// <summary>
    /// 点到这条曲线的距离（毫米）。每段采 <see cref="HitSamples"/> 段折线取最小——
    /// 命中容差是 0.6mm 一档（<c>EditGeometry.HitToleranceMm</c>），24 段采样带来的误差远在其下，
    /// 所以不必上解析解。<strong>只有这一份量法</strong>：画布上点得中的弧，与选择框、越界校验是同一份段序列。
    /// </summary>
    public static double DistanceMm(TemplateElement element, double xMm, double yMm)
    {
        var best = double.MaxValue;
        foreach (var s in Segments(element))
        {
            double px = s.X1, py = s.Y1;
            for (var i = 1; i <= HitSamples; i++)
            {
                var t = (double)i / HitSamples;
                var qx = Point(s.X1, s.CX1, s.CX2, s.X2, t);
                var qy = Point(s.Y1, s.CY1, s.CY2, s.Y2, t);
                best = Math.Min(best, SegmentDistance(xMm, yMm, px, py, qx, qy));
                px = qx;
                py = qy;
            }
        }
        return best;
    }

    /// <summary>命中采样的段数（一条弧按 24 段折线量）。</summary>
    public const int HitSamples = 24;

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lenSq = dx * dx + dy * dy;
        if (lenSq <= 1e-12) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        var t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0, 1);
        var cx = ax + t * dx;
        var cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    /// <summary>
    /// 精确外接框（毫米）。<strong>不是"控制点围起来的框"</strong>——那个框在弯得厉害的弧上会明显比看得见
    /// 的线大，选中框就会虚胖。这里对每一段解一维导数的根（标准做法），把极值点算进去。
    /// </summary>
    public static (double X, double Y, double Width, double Height) BoundsMm(TemplateElement element)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var s in Segments(element))
        {
            foreach (var t in Extrema(s.X1, s.CX1, s.CX2, s.X2))
            {
                var v = Point(s.X1, s.CX1, s.CX2, s.X2, t);
                minX = Math.Min(minX, v);
                maxX = Math.Max(maxX, v);
            }
            foreach (var t in Extrema(s.Y1, s.CY1, s.CY2, s.Y2))
            {
                var v = Point(s.Y1, s.CY1, s.CY2, s.Y2, t);
                minY = Math.Min(minY, v);
                maxY = Math.Max(maxY, v);
            }
        }
        if (minX > maxX || minY > maxY) return (element.X, element.Y, 0, 0);
        return (minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>整条平移（拖曲线本体时端点、中间节点一起动；柄是相对量，不用动）。</summary>
    public static void Translate(TemplateElement element, double dxMm, double dyMm)
    {
        if (element.Nodes is not { Count: > 0 } nodes) return;
        for (var i = 0; i < nodes.Count; i++) nodes[i] = nodes[i].Moved(dxMm, dyMm);
    }

    /// <summary>曲线字段的四舍五入与去噪（存盘前调用，避免拖一次鼠标留下 17 位小数）。</summary>
    public static void SnapForStorage(TemplateElement element)
    {
        if (element.Nodes is not { Count: > 0 } nodes) return;
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            nodes[i] = n with
            {
                X = Round(n.X), Y = Round(n.Y),
                InX = Round(n.InX), InY = Round(n.InY),
                OutX = Round(n.OutX), OutY = Round(n.OutY),
            };
        }
    }

    private static double Round(double v) => Math.Round(v, 3);


    /// <summary>三次曲线在一根轴上的取值（de Casteljau 展开式）。</summary>
    private static double Point(double p0, double p1, double p2, double p3, double t)
    {
        var u = 1 - t;
        return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
    }

    /// <summary>导数根的闭式解：端点 0/1 之外，落在 (0,1) 内的极值点才是外接框要认的点。</summary>
    private static IEnumerable<double> Extrema(double p0, double p1, double p2, double p3)
    {
        yield return 0;
        yield return 1;
        var a = 3 * (-p0 + 3 * p1 - 3 * p2 + p3);
        var b = 6 * (p0 - 2 * p1 + p2);
        var c = 3 * (p1 - p0);
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12)
            {
                var t = -c / b;
                if (t > 0 && t < 1) yield return t;
            }
            yield break;
        }
        var disc = b * b - 4 * a * c;
        if (disc < 0) yield break;
        var root = Math.Sqrt(disc);
        foreach (var t in new[] { (-b + root) / (2 * a), (-b - root) / (2 * a) })
        {
            if (t > 0 && t < 1) yield return t;
        }
    }
}
