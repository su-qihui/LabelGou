using LabelGou.Core.Templates;

namespace LabelGou.Core.Editing;

/// <summary>缩放手柄。<see cref="TopLeft"/> 等组合值由四个方向位拼出来，判断用 <c>HasFlag</c>。</summary>
[Flags]
public enum ResizeHandle
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
    TopLeft = Top | Left,
    TopRight = Top | Right,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right,
    LineStart = 16,
    LineEnd = 32,
}

public enum GuideSource
{
    /// <summary>网格线。</summary>
    Grid = 0,
    /// <summary>标签四边。</summary>
    LabelEdge = 1,
    /// <summary>标签中心线。</summary>
    LabelCenter = 2,
    /// <summary>内边距线。</summary>
    Padding = 3,
    /// <summary>别的元素的边或中心线。</summary>
    Neighbor = 4,
}

/// <summary>
/// 水平对齐基准。对齐是相对标签算的，不是元素间——唛头大多要的是「整行贴左」「标题居中」，
/// 元素间等距分布本阶段不做（见 §八 M4 范围末条）。
/// </summary>
public enum AlignHorizontal
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>垂直对齐基准（相对标签）。</summary>
public enum AlignVertical
{
    Top = 0,
    Middle = 1,
    Bottom = 2,
}

/// <summary>吸附命中后要在画布上显示的辅助线（毫米坐标，UI 只负责换算成像素画出来）。</summary>
public sealed record GuideLine(GuideSource Source, bool Vertical, double PositionMm, double FromMm, double ToMm);

/// <summary>吸附参数。网格步长默认 1mm——不干胶上 0.5mm 已经肉眼难分，再细没意义。</summary>
public sealed record SnapOptions
{
    public bool Enabled { get; init; } = true;
    public bool SnapToGrid { get; init; } = true;
    public double GridStepMm { get; init; } = 1.0;
    public double ToleranceMm { get; init; } = 0.6;
    public bool SnapNeighbors { get; init; } = true;

    public static readonly SnapOptions None = new() { Enabled = false };
}

/// <summary>一次吸附的结果：修正后的位置 + 命中的辅助线。</summary>
public sealed record SnapResult(double X, double Y, IReadOnlyList<GuideLine> Guides)
{
    public double OffsetX => X - OriginalX;
    public double OffsetY => Y - OriginalY;
    public double OriginalX { get; init; }
    public double OriginalY { get; init; }
}

/// <summary>
/// B 类模板编辑器的几何层：<strong>全部按毫米算，UI 只把结果乘倍率画出来</strong>。
/// <para>
/// 之所以放在 Core：这些规则决定"拖完是否还印得出"（越界、压边、句柄命中），必须能单测；
/// 放进控件里就变成只有手点才能发现的 bug。
/// </para>
/// </summary>
public static class EditGeometry
{
    /// <summary>元素最小边长（毫米）：比这更小已经看不见，缩放到此为止。</summary>
    public const double MinSideMm = 0.8;

    /// <summary>点选容差（毫米）：0.5mm 内算点到，手指粗也能点中细线。</summary>
    public const double HitToleranceMm = 0.5;

    // ---------- 包围盒与命中 ----------

    /// <summary>元素的毫米包围盒（Line 由两端点算出来）。</summary>
    public static (double X, double Y, double Width, double Height) BoxOf(TemplateElement element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (element.Kind == ElementKind.Line)
        {
            var x = Math.Min(element.X, element.X2);
            var y = Math.Min(element.Y, element.Y2);
            return (x, y, Math.Abs(element.X2 - element.X), Math.Abs(element.Y2 - element.Y));
        }
        return (element.X, element.Y, Math.Max(0, element.Width), Math.Max(0, element.Height));
    }

    public static bool HitTest(TemplateElement element, double xMm, double yMm, double toleranceMm = HitToleranceMm)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (element.Kind == ElementKind.Line)
        {
            var tol = Math.Max(toleranceMm, element.ThicknessMm);
            return DistanceToSegment(xMm, yMm, element.X, element.Y, element.X2, element.Y2) <= tol;
        }

        var box = BoxOf(element);
        return xMm >= box.X - toleranceMm && xMm <= box.X + box.Width + toleranceMm
            && yMm >= box.Y - toleranceMm && yMm <= box.Y + box.Height + toleranceMm;
    }

    /// <summary>命中最上层元素的下标（列表末尾=画在最上面）；没命中返回 -1。</summary>
    public static int TopmostAt(LabelTemplate template, double xMm, double yMm, double toleranceMm = HitToleranceMm)
    {
        for (var i = template.Elements.Count - 1; i >= 0; i--)
        {
            var element = template.Elements[i];
            if (element.Visible && HitTest(element, xMm, yMm, toleranceMm)) return i;
        }
        return -1;
    }

    /// <summary>
    /// 指针落在哪个缩放手柄上。<paramref name="radiusMm"/> 由 UI 按当前缩放换算
    /// （屏幕上约 8 像素对应的毫米数），所以放大后句柄更容易抓。
    /// </summary>
    public static ResizeHandle HandleAt(TemplateElement element, double xMm, double yMm, double radiusMm)
    {
        var r = Math.Max(0.1, radiusMm);
        if (element.Kind == ElementKind.Line)
        {
            if (Near(xMm, yMm, element.X, element.Y, r)) return ResizeHandle.LineStart;
            if (Near(xMm, yMm, element.X2, element.Y2, r)) return ResizeHandle.LineEnd;
            return ResizeHandle.None;
        }

        var box = BoxOf(element);
        var left = box.X;
        var right = box.X + box.Width;
        var top = box.Y;
        var bottom = box.Y + box.Height;

        var horizontal = Near(xMm, left, r) ? ResizeHandle.Left
            : Near(xMm, right, r) ? ResizeHandle.Right : ResizeHandle.None;
        var vertical = Near(yMm, top, r) ? ResizeHandle.Top
            : Near(yMm, bottom, r) ? ResizeHandle.Bottom : ResizeHandle.None;

        var insideY = yMm >= top - r && yMm <= bottom + r;
        var insideX = xMm >= left - r && xMm <= right + r;

        // 角上两个方向都给；只在某条边的延伸范围内才是单向缩放
        if (horizontal != ResizeHandle.None && vertical != ResizeHandle.None) return horizontal | vertical;
        if (horizontal != ResizeHandle.None && insideY) return horizontal;
        if (vertical != ResizeHandle.None && insideX) return vertical;
        return ResizeHandle.None;
    }

    private static bool Near(double value, double target, double radius) => Math.Abs(value - target) <= radius;
    private static bool Near(double x, double y, double px, double py, double radius)
        => Near(x, px, radius) && Near(y, py, radius);

    private static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= double.Epsilon) return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));

        var t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lengthSquared, 0, 1);
        var projX = x1 + t * dx;
        var projY = y1 + t * dy;
        return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }

    // ---------- 移动 ----------

    /// <summary>
    /// 位移一个元素，<strong>越界时贴边而不是拒绝</strong>：拖到标签外应当停在边上，
    /// 而不是"按下去了却没动"让人以为卡住。返回真正被接受的位移量。
    /// </summary>
    public static (double Dx, double Dy) MoveBy(LabelTemplate template, int index, double dxMm, double dyMm)
    {
        var element = ElementAt(template, index);
        var box = BoxOf(element);
        var maxX = Math.Max(0, template.WidthMm - box.Width);
        var maxY = Math.Max(0, template.HeightMm - box.Height);
        var targetX = Math.Clamp(box.X + dxMm, 0, maxX);
        var targetY = Math.Clamp(box.Y + dyMm, 0, maxY);
        var appliedX = targetX - box.X;
        var appliedY = targetY - box.Y;
        ApplyShift(element, appliedX, appliedY);
        return (appliedX, appliedY);
    }

    /// <summary>把元素左上角放到指定毫米位置（同样越界贴边），返回实际落点。</summary>
    public static (double X, double Y) MoveTo(LabelTemplate template, int index, double xMm, double yMm)
    {
        var box = BoxOf(ElementAt(template, index));
        var shift = MoveBy(template, index, xMm - box.X, yMm - box.Y);
        return (box.X + shift.Dx, box.Y + shift.Dy);
    }

    private static void ApplyShift(TemplateElement element, double dx, double dy)
    {
        element.X += dx;
        element.Y += dy;
        if (element.Kind == ElementKind.Line)
        {
            element.X2 += dx;
            element.Y2 += dy;
        }
    }

    // ---------- 缩放 ----------

    /// <summary>
    /// 按句柄拖动缩放。线段只有两个端点句柄；矩形/文本/图片按四边与四角八向，
    /// 最小边长 <see cref="MinSideMm"/>，并且不允许拖出标签范围。
    /// </summary>
    public static void ResizeBy(LabelTemplate template, int index, ResizeHandle handle, double dxMm, double dyMm)
    {
        var element = ElementAt(template, index);
        if (handle == ResizeHandle.None) return;

        if (element.Kind == ElementKind.Line)
        {
            if (handle.HasFlag(ResizeHandle.LineStart))
            {
                element.X = Clamp(element.X + dxMm, 0, template.WidthMm);
                element.Y = Clamp(element.Y + dyMm, 0, template.HeightMm);
            }
            if (handle.HasFlag(ResizeHandle.LineEnd))
            {
                element.X2 = Clamp(element.X2 + dxMm, 0, template.WidthMm);
                element.Y2 = Clamp(element.Y2 + dyMm, 0, template.HeightMm);
            }
            return;
        }

        var left = element.X;
        var top = element.Y;
        var right = element.X + element.Width;
        var bottom = element.Y + element.Height;

        if (handle.HasFlag(ResizeHandle.Left)) left = Math.Min(Clamp(left + dxMm, 0, template.WidthMm), right - MinSideMm);
        if (handle.HasFlag(ResizeHandle.Right)) right = Math.Max(Clamp(right + dxMm, left + MinSideMm, template.WidthMm), left + MinSideMm);
        if (handle.HasFlag(ResizeHandle.Top)) top = Math.Min(Clamp(top + dyMm, 0, template.HeightMm), bottom - MinSideMm);
        if (handle.HasFlag(ResizeHandle.Bottom)) bottom = Math.Max(Clamp(bottom + dyMm, top + MinSideMm, template.HeightMm), top + MinSideMm);

        element.X = Math.Max(0, left);
        element.Y = Math.Max(0, top);
        element.Width = Math.Max(MinSideMm, right - element.X);
        element.Height = Math.Max(MinSideMm, bottom - element.Y);
    }

    // ---------- 吸附 ----------

    /// <summary>
    /// 对"拖动中的位置"做吸附：先看标签边/中心/内边距/别的元素（<see cref="SnapOptions.ToleranceMm"/> 内），
    /// 没命中再退到网格取整。只修正左上角，不改尺寸。
    /// </summary>
    public static SnapResult Snap(LabelTemplate template, int index, double xMm, double yMm, SnapOptions options)
    {
        options ??= SnapOptions.None;
        var element = ElementAt(template, index);
        var box = BoxOf(element);
        var guides = new List<GuideLine>();

        if (!options.Enabled)
            return new SnapResult(xMm, yMm, guides) { OriginalX = xMm, OriginalY = yMm };

        var width = box.Width;
        var height = box.Height;
        var tolerance = Math.Max(0.05, options.ToleranceMm);

        // 三个候选边（左边 / 水平中心 / 右边）各自去贴目标线，命中后换回新的左上角
        var xHit = PickSnap(xMm, new[] { 0.0, width / 2, width }, CollectTargets(template, index, options, vertical: true), tolerance, out var xLine);
        var snappedX = xHit.HasValue ? xHit.Value : xMm;
        if (xHit.HasValue && xLine is not null)
            guides.Add(new GuideLine(xLine.Value.Source, Vertical: true, xLine.Value.Position, 0, template.HeightMm));
        if (!xHit.HasValue && options.SnapToGrid && options.GridStepMm > 0)
            snappedX = Math.Round(xMm / options.GridStepMm) * options.GridStepMm;

        var yHit = PickSnap(yMm, new[] { 0.0, height / 2, height }, CollectTargets(template, index, options, vertical: false), tolerance, out var yLine);
        var snappedY = yHit.HasValue ? yHit.Value : yMm;
        if (yHit.HasValue && yLine is not null)
            guides.Add(new GuideLine(yLine.Value.Source, Vertical: false, yLine.Value.Position, 0, template.WidthMm));
        if (!yHit.HasValue && options.SnapToGrid && options.GridStepMm > 0)
            snappedY = Math.Round(yMm / options.GridStepMm) * options.GridStepMm;

        // 吸附完仍要夹紧：贴住中心线却把元素推出边界是不能接受的
        var maxX = Math.Max(0, template.WidthMm - width);
        var maxY = Math.Max(0, template.HeightMm - height);
        return new SnapResult(Math.Clamp(snappedX, 0, maxX), Math.Clamp(snappedY, 0, maxY), guides)
        {
            OriginalX = xMm,
            OriginalY = yMm,
        };
    }

    private readonly record struct TargetLine(double Position, GuideSource Source);

    private static List<TargetLine> CollectTargets(LabelTemplate template, int index, SnapOptions options, bool vertical)
    {
        var targets = new List<TargetLine>
        {
            new(0, GuideSource.LabelEdge),
            new(vertical ? template.WidthMm : template.HeightMm, GuideSource.LabelEdge),
            new(vertical ? template.WidthMm / 2 : template.HeightMm / 2, GuideSource.LabelCenter),
        };
        if (template.PaddingMm > 0)
        {
            var pad = template.PaddingMm;
            var span = vertical ? template.WidthMm : template.HeightMm;
            if (pad < span)
            {
                targets.Add(new TargetLine(pad, GuideSource.Padding));
                targets.Add(new TargetLine(span - pad, GuideSource.Padding));
            }
        }

        if (!options.SnapNeighbors) return targets;

        for (var i = 0; i < template.Elements.Count; i++)
        {
            if (i == index) continue;
            var box = BoxOf(template.Elements[i]);
            var start = vertical ? box.X : box.Y;
            var size = vertical ? box.Width : box.Height;
            targets.Add(new TargetLine(start, GuideSource.Neighbor));
            targets.Add(new TargetLine(start + size / 2, GuideSource.Neighbor));
            targets.Add(new TargetLine(start + size, GuideSource.Neighbor));
        }
        return targets;
    }

    /// <summary>
    /// 在候选线里挑“最近且不超过容差”的一条，返回吸附后的新左上角（没命中给 null）。
    /// </summary>
    /// <param name="origin">当前左上角坐标。</param>
    /// <param name="edgeOffsets">各候选边相对左上角的偏移（0 / 中心 / 另一边）。</param>
    private static double? PickSnap(double origin, double[] edgeOffsets, List<TargetLine> targets, double tolerance, out TargetLine? hitLine)
    {
        hitLine = null;
        double? best = null;
        var bestDelta = double.MaxValue;
        foreach (var offset in edgeOffsets)
        {
            var edge = origin + offset;
            foreach (var target in targets)
            {
                var delta = Math.Abs(edge - target.Position);
                if (delta <= tolerance && delta < bestDelta)
                {
                    bestDelta = delta;
                    best = target.Position - offset;
                    hitLine = target;
                }
            }
        }
        return best;
    }

    // ---------- 对齐到标签 ----------

    public static void AlignToLabel(LabelTemplate template, int index, AlignHorizontal horizontal, AlignVertical vertical)
    {
        var element = ElementAt(template, index);
        var box = BoxOf(element);
        var dx = horizontal switch
        {
            AlignHorizontal.Center => (template.WidthMm - box.Width) / 2 - box.X,
            AlignHorizontal.Right => template.WidthMm - box.Width - box.X,
            _ => -box.X,
        };
        var dy = vertical switch
        {
            AlignVertical.Middle => (template.HeightMm - box.Height) / 2 - box.Y,
            AlignVertical.Bottom => template.HeightMm - box.Height - box.Y,
            _ => -box.Y,
        };
        ApplyShift(element, dx, dy);
    }

    /// <summary>按内边距把元素推到内容区边上（常用：整行文本贴左内边距）。</summary>
    public static void SnapToPadding(LabelTemplate template, int index, AlignHorizontal horizontal, AlignVertical vertical)
    {
        var element = ElementAt(template, index);
        var box = BoxOf(element);
        var left = Math.Clamp(template.PaddingMm, 0, template.WidthMm);
        var top = Math.Clamp(template.PaddingMm, 0, template.HeightMm);
        var right = Math.Max(left, template.WidthMm - template.PaddingMm);
        var bottom = Math.Max(top, template.HeightMm - template.PaddingMm);

        var targetX = horizontal switch
        {
            AlignHorizontal.Center => left + (right - left - box.Width) / 2,
            AlignHorizontal.Right => right - box.Width,
            _ => left,
        };
        var targetY = vertical switch
        {
            AlignVertical.Middle => top + (bottom - top - box.Height) / 2,
            AlignVertical.Bottom => bottom - box.Height,
            _ => top,
        };
        ApplyShift(element, Math.Clamp(targetX, 0, Math.Max(0, template.WidthMm - box.Width)) - box.X,
            Math.Clamp(targetY, 0, Math.Max(0, template.HeightMm - box.Height)) - box.Y);
    }

    // ---------- 层级 ----------

    /// <summary>调整绘制顺序（末尾=最上层）。返回调整后的下标。</summary>
    public static int MoveLayer(LabelTemplate template, int index, int delta)
    {
        var element = ElementAt(template, index);
        var target = Math.Clamp(index + delta, 0, template.Elements.Count - 1);
        if (target == index) return index;
        template.Elements.RemoveAt(index);
        template.Elements.Insert(target, element);
        return target;
    }

    public static int BringToFront(LabelTemplate template, int index) => MoveLayer(template, index, template.Elements.Count);

    public static int SendToBack(LabelTemplate template, int index) => MoveLayer(template, index, -template.Elements.Count);

    private static TemplateElement ElementAt(LabelTemplate template, int index)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (index < 0 || index >= template.Elements.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"元素下标 {index} 超出范围（共 {template.Elements.Count} 个）。");
        return template.Elements[index];
    }

    private static double Clamp(double value, double min, double max) => max < min ? min : Math.Clamp(value, min, max);
}
