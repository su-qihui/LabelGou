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

/// <summary>
/// 缩放时"钉住哪个点"。<strong>口径来自本机 CorelDRAW X4 实测</strong>（2026-09-12，第 45 棒）：
/// 拖角 = 等比 + 对角固定（不需要按键）；按住 Shift = 绕元素中心向四周。
/// </summary>
public enum ResizeAnchor
{
    /// <summary>没被拖的那组边（角柄则是对角）保持不动——CDR 默认。</summary>
    Opposite = 0,
    /// <summary>元素中心不动，四周对称伸缩——CDR 按住 Shift。</summary>
    Center = 1,
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
            // 曲线的外接框要量弧本身（解导数根），拿两端点算会把鼓出去的那截漏掉——选择框与越界校验都跟着错。
            if (Templates.CurveGeometry.IsCurved(element)) return Templates.CurveGeometry.BoundsMm(element);
            var x = Math.Min(element.X, element.X2);
            var y = Math.Min(element.Y, element.Y2);
            return (x, y, Math.Abs(element.X2 - element.X), Math.Abs(element.Y2 - element.Y));
        }
        return (element.X, element.Y, Math.Max(0, element.Width), Math.Max(0, element.Height));
    }

    /// <summary>
    /// 元素<strong>旋转之前</strong>的视觉包围盒（毫米）：对文本，把字面拉伸倍率乘进排版盒、
    /// 且以盒中心为锚点向外扩（拉伸是"绕中心抻"，不是"贴左上角长"）；其余元素与 <see cref="BoxOf"/> 相同。
    /// <para>第 43 棒：编辑器的选择框/句柄、以及越界校验都改用它，才能保证"屏幕上框多大、印出来占多大"，
    /// 拉伸到探出纸时越界校验抓得到（不抓就是"会印错且看不见"那一类）。Line 不参与拉伸，原样返回。</para>
    /// </summary>
    public static (double X, double Y, double Width, double Height) VisualBoxOf(TemplateElement element)
    {
        var box = BoxOf(element);
        if (element.Kind != ElementKind.Text) return box;
        if (element.TextScaleX == 1 && element.TextScaleY == 1) return box;

        var cx = box.X + box.Width / 2;
        var cy = box.Y + box.Height / 2;
        var w = box.Width * element.TextScaleX;
        var h = box.Height * element.TextScaleY;
        return (cx - w / 2, cy - h / 2, w, h);
    }

    /// <summary>
    /// 元素"实际会占掉的纸面范围"（毫米，轴对齐外接矩形）：先算视觉盒（含文字拉伸），
    /// 旋转非零时再绕中心转一圈取四角 min/max。<strong>越界校验与编辑器都只认这一个出口</strong>，
    /// 别处不许再各算一遍拉伸/旋转（§五-62：两套算术各自长歪）。
    /// </summary>
    public static (double X, double Y, double Right, double Bottom) OccupiedBoundsOf(TemplateElement element)
    {
        var box = VisualBoxOf(element);
        return RotatedBoundsOf(box, element.RotationDeg,
            BoxOf(element).X + Math.Max(0, element.Width) / 2,
            BoxOf(element).Y + Math.Max(0, element.Height) / 2);
    }

    /// <summary>
    /// 把一个毫米盒绕 (<paramref name="cx"/>, <paramref name="cy"/>) 转 <paramref name="angleDeg"/> 度后的
    /// <strong>轴对外接矩形</strong>（纸面坐标）。角度为 0 时原样返回。
    /// <para>抽成公开纯函数是给 App 层用的：墨迹盒（Core 量不出来）转过的角度要算同一份外接算术，
    /// 复制一遍迟早长歪（§五-62）。渲染端的变换锚点 = 排版盒中心，这里就按同一个中心转。</para>
    /// </summary>
    public static (double X, double Y, double Right, double Bottom) RotatedBoundsOf(
        (double X, double Y, double Width, double Height) box, double angleDeg, double cx, double cy)
    {
        if (Math.Abs(angleDeg) <= 1e-9)
            return (box.X, box.Y, box.X + box.Width, box.Y + box.Height);

        var rad = angleDeg * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (px, py) in new[]
        {
            (box.X, box.Y), (box.X + box.Width, box.Y),
            (box.X + box.Width, box.Y + box.Height), (box.X, box.Y + box.Height),
        })
        {
            var dx = px - cx;
            var dy = py - cy;
            var rx = cx + dx * cos - dy * sin;
            var ry = cy + dx * sin + dy * cos;
            minX = Math.Min(minX, rx);
            minY = Math.Min(minY, ry);
            maxX = Math.Max(maxX, rx);
            maxY = Math.Max(maxY, ry);
        }
        return (minX, minY, maxX, maxY);
    }

    public static bool HitTest(TemplateElement element, double xMm, double yMm, double toleranceMm = HitToleranceMm,
        (double X, double Y, double Width, double Height)? inkBox = null)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (element.Kind == ElementKind.Line)
        {
            var tol = Math.Max(toleranceMm, element.ThicknessMm);
            if (Templates.CurveGeometry.IsCurved(element))
            {
                if (Templates.CurveGeometry.DistanceMm(element, xMm, yMm) <= tol) return true;
                // 第 85 棒：闭合的那只（「转为曲线」后的多边形/矩形）还要认它外接盒的那一圈边——
                // 这一棒刚给它的八向句柄正长在盒角与盒边中点上，只认轮廓线的话句柄画出来却够不着，等于没修。
                // 刻意不认盒的内部（第 83 棒：宽容是便利，不是抢别人的理由）：
                // 认了整只盒，一只大形状会把它空白处下面那一只的点选抢走。
                // 点填充形状的内部仍选不中是既成事实，已记 §六 活账。
                if (Templates.CurveGeometry.IsClosed(element))
                {
                    var body = VisualBoxOf(element);
                    var inside = xMm >= body.X - tol && xMm <= body.X + body.Width + tol
                        && yMm >= body.Y - tol && yMm <= body.Y + body.Height + tol;
                    var hollow = xMm > body.X + tol && xMm < body.X + body.Width - tol
                        && yMm > body.Y + tol && yMm < body.Y + body.Height - tol;
                    return inside && !hollow;
                }
                return false;
            }
            return DistanceToSegment(xMm, yMm, element.X, element.Y, element.X2, element.Y2) <= tol;
        }

        // 第 63 棒①：命中范围 = 排版盒 ∪ 看得见的墨迹盒。永不折行的字能排出排版盒右端（用户："右半段无法移动"），
        // 只认盒就抓不到那一段；墨迹盒由 App 量（Core 量不了字），没递就退回只认盒＝逐字旧行为。
        if (inkBox is { } ink && ink.Width > 0 && ink.Height > 0)
        {
            var box0 = VisualBoxOf(element);
            var l = Math.Min(box0.X, ink.X) - toleranceMm;
            var t = Math.Min(box0.Y, ink.Y) - toleranceMm;
            var r = Math.Max(box0.X + box0.Width, ink.X + ink.Width) + toleranceMm;
            var b = Math.Max(box0.Y + box0.Height, ink.Y + ink.Height) + toleranceMm;
            if (xMm >= l && xMm <= r && yMm >= t && yMm <= b) return true;
            // 转过的字：墨迹盒是转完的外接矩形，落在里面就够了；盒外的部分交给上面这条
            if (element.RotationDeg == 0) return false;
        }

        // 转过的元素按"转回去"判：把纸面坐标绕元素中心反向旋转回本地域，再对轴对齐盒测。
        var box = VisualBoxOf(element);
        var (px, py) = ToLocal(element, xMm, yMm);
        return px >= box.X - toleranceMm && px <= box.X + box.Width + toleranceMm
            && py >= box.Y - toleranceMm && py <= box.Y + box.Height + toleranceMm;
    }

    /// <summary>
    /// 元素渲染旋转绕的中心（毫米，<strong>全项目唯一锚点口径</strong>，第 52 棒）。
    /// <para>非文本：排版盒＝视觉盒＝形状本体，中心没有歧义。文本：绕<strong>看得见的墨迹中心</strong>转
    /// （App 量出墨迹盒喂进来；量不到才退回视觉盒中心）。CDR 的行为就是绕对象自己的中心转——
    /// 从前绕排版盒（隐形行带）中心，左对齐的短字在宽行带里一转就"飞出去"，用户 2026-09-14 报的正是这个。</para>
    /// </summary>
    public static (double Cx, double Cy) RotationCenterOf(TemplateElement element,
        (double X, double Y, double Width, double Height)? visualOverride = null)
    {
        var box = visualOverride ?? VisualBoxOf(element);
        return (box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    /// <summary>
    /// 纸面坐标 → 元素本地坐标（绕 <see cref="RotationCenterOf"/> 反向旋转 <see cref="TemplateElement.RotationDeg"/>）。
    /// 未旋转时原样返回；Line 不参与旋转（两端点已是纸面坐标）。
    /// <paramref name="visualOverride"/> = App 量出的墨迹盒（文本必喂，否则锚点与画出来的框不是一套）。
    /// </summary>
    public static (double X, double Y) ToLocal(TemplateElement element, double xMm, double yMm,
        (double X, double Y, double Width, double Height)? visualOverride = null)
    {
        if (element.RotationDeg == 0 || element.Kind == ElementKind.Line) return (xMm, yMm);
        var (cx, cy) = RotationCenterOf(element, visualOverride);
        var rad = -element.RotationDeg * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = xMm - cx;
        var dy = yMm - cy;
        return (cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    /// <summary>纸面位移向量 → 本地位移向量（只转方向不转位置；未旋转时原样）。</summary>
    public static (double Dx, double Dy) ToLocalDelta(TemplateElement element, double dxMm, double dyMm)
    {
        if (element.RotationDeg == 0) return (dxMm, dyMm);
        var rad = -element.RotationDeg * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return (dxMm * cos - dyMm * sin, dxMm * sin + dyMm * cos);
    }

    /// <summary>命中最上层元素的下标（列表末尾=画在最上面）；没命中返回 -1。
    /// <para><strong>两遍，顺序就是优先级</strong>（第 83 棒①）：第一遍只认<strong>看得见的那一块</strong>
    /// （文本 = 墨迹盒，转过的按转完的外接；非文本 = 它自己的框），第二遍才放开到宽容盒。</para>
    /// <para>为什么要有第二遍：第 44 棒定"点选按宽容盒（文本=整条行带）"是为了"点文字旁边的空白也选得中这一行"；
    /// 第 63 棒又把墨迹盒<em>并</em>进同一遍命中范围，为的是"永不折行的右半段抓不住"。两条叠在一起就漏出
    /// 用户今天圈的洞：一条 130×123mm 的通栏行带压在别人家的字上面，<strong>点谁的字都选中它</strong>
    /// （"点击也是只能被第二行控制"）。宽容是便利，不是抢别人的理由。</para>
    /// <para>墨迹盒由 App 量（Core 不许碰 WPF）；没递进来或量不到（隐藏、变量全空）的文本第一遍直接跳过，
    /// 交给第二遍——它照样点得中，只是没有"看得见那块"可优先。</para></summary>
    public static int TopmostAt(LabelTemplate template, double xMm, double yMm, double toleranceMm = HitToleranceMm,
        Func<TemplateElement, (double X, double Y, double Width, double Height)?>? inkBoxes = null)
    {
        for (var i = template.Elements.Count - 1; i >= 0; i--)
        {
            var element = template.Elements[i];
            if (element.Visible && VisiblePartHit(element, xMm, yMm, toleranceMm, inkBoxes?.Invoke(element))) return i;
        }
        for (var i = template.Elements.Count - 1; i >= 0; i--)
        {
            var element = template.Elements[i];
            if (element.Visible && HitTest(element, xMm, yMm, toleranceMm, inkBoxes?.Invoke(element))) return i;
        }
        return -1;
    }

    /// <summary>这一点落在元素"看得见的那一块"里吗（<see cref="TopmostAt"/> 的第一遍）。</summary>
    private static bool VisiblePartHit(TemplateElement element, double xMm, double yMm, double toleranceMm,
        (double X, double Y, double Width, double Height)? ink)
    {
        if (element.Kind != ElementKind.Text) return HitTest(element, xMm, yMm, toleranceMm);   // 非文本：框就是它自己
        if (ink is not { } box) return false;                                                    // 量不到 → 交给第二遍

        // 与画出来的那个框同一个外接：绕墨迹自己的中心转（渲染端同一个锚点，第 52 棒）
        var occ = RotatedBoundsOf(box, element.RotationDeg, box.X + box.Width / 2, box.Y + box.Height / 2);
        return xMm >= occ.X - toleranceMm && xMm <= occ.Right + toleranceMm
            && yMm >= occ.Y - toleranceMm && yMm <= occ.Bottom + toleranceMm;
    }

    /// <summary>
    /// 指针落在哪个缩放手柄上。<paramref name="radiusMm"/> 由 UI 按当前缩放换算
    /// （屏幕上约 8 像素对应的毫米数），所以放大后句柄更容易抓。
    /// </summary>
    public static ResizeHandle HandleAt(TemplateElement element, double xMm, double yMm, double radiusMm,
        (double X, double Y, double Width, double Height)? displayBox = null)
    {
        var r = Math.Max(0.1, radiusMm);
        if (element.Kind == ElementKind.Line)
        {
            if (Near(xMm, yMm, element.X, element.Y, r)) return ResizeHandle.LineStart;
            if (Near(xMm, yMm, element.X2, element.Y2, r)) return ResizeHandle.LineEnd;
            // 第 85 棒：从前到这儿就 return None 了——一条线只有两个端点，于是曲线（含「转为曲线」后的
            // 多边形/矩形）永远拿不到整只框的八向句柄，属性面板宽高那格也跟着消失（HasBox => Kind != Line），
            // 用户报的"删角后无法拉伸扭曲"就是这么来的。端点没抓中时落到下面那段盒句柄；
            // 一条没有节点也没有柄的直线照旧只吃两个端点（第 49 棒口径，不给它造第二种缩放手势）。
            if (!Templates.CurveGeometry.IsCurved(element) && !Templates.CurveGeometry.IsClosed(element))
                return ResizeHandle.None;
        }

        var box = displayBox ?? VisualBoxOf(element);
        var left = box.X;
        var right = box.X + box.Width;
        var top = box.Y;
        var bottom = box.Y + box.Height;

        // 转过的元素：句柄也长在"转过的边"上——先把指针反变换回本地域再对轴对齐盒判。
        // 反变换的锚点必须与画出来的框同一个（墨迹中心），否则句柄看得见抓不着（§五-146 同族）。
        var (px, py) = ToLocal(element, xMm, yMm, displayBox);

        var horizontal = Near(px, left, r) ? ResizeHandle.Left
            : Near(px, right, r) ? ResizeHandle.Right : ResizeHandle.None;
        var vertical = Near(py, top, r) ? ResizeHandle.Top
            : Near(py, bottom, r) ? ResizeHandle.Bottom : ResizeHandle.None;

        var insideY = py >= top - r && py <= bottom + r;
        var insideX = px >= left - r && px <= right + r;

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
    /// <para>
    /// <strong>第 46 棒：边界按 <paramref name="occupancy"/> 算，不按排版盒。</strong>文本的排版盒是一条
    /// 130mm 的隐形行带，纸只有 140mm 时它整行只剩 5mm 活动量——用户看到的"短字怎么拖都到不了右边"
    /// 就是这条带子顶的。占位盒 = 屏幕上看得见的那一块（文本 = 编辑器量出的墨迹盒）；不传则退回排版盒，
    /// 非文本元素两者相同，行为逐字不变。位移对两轴都是纯平移，所以"墨迹贴边"与"排版盒跟着走同样距离"不矛盾。
    /// </para>
    /// </summary>
    public static (double Dx, double Dy) MoveBy(LabelTemplate template, int index, double dxMm, double dyMm,
        (double X, double Y, double Width, double Height)? occupancy = null)
    {
        var element = ElementAt(template, index);
        var occ = occupancy ?? BoxOf(element);
        // 第 81 棒（用户：「墨迹边框被固定在纸张范围内无法超出」）：**文本不夹**。文本会印出去的是 App 量出来的
        // 那块墨迹，排版盒只是"字在哪对齐"的虚拟基准（第 46 棒已经为它撤掉校验器那条），拿它当占物夹住摆位
        // 就是"短字怎么拖都到不了右边"那堵墙。越界改由两处接手：编辑器清单里按样例墨迹量的提醒，
        // 与 ④⑤ 步按真数据量的出纸闸——都看得见、都说得出毫米数，还有「缩回纸内」一键可退。
        if (element.Kind == ElementKind.Text)
        {
            ApplyShift(element, dxMm, dyMm);
            return (dxMm, dyMm);
        }
        // 占物比标签还宽的那根轴没有"贴边"可言：夹住等于把元素钉死在 0，用户一往右拖就弹回来，
        // 看着既是"被限制"又是"卡顿"。那一轴放开，越界由墨迹那道闸与「缩回纸内」负责说。
        var targetX = occ.Width > template.WidthMm ? occ.X + dxMm
            : Math.Clamp(occ.X + dxMm, 0, template.WidthMm - occ.Width);
        var targetY = occ.Height > template.HeightMm ? occ.Y + dyMm
            : Math.Clamp(occ.Y + dyMm, 0, template.HeightMm - occ.Height);
        var appliedX = targetX - occ.X;
        var appliedY = targetY - occ.Y;
        ApplyShift(element, appliedX, appliedY);
        return (appliedX, appliedY);
    }

    /// <summary>把元素左上角放到指定毫米位置（同样越界贴边），返回实际落点。<paramref name="occupancy"/> 见 <see cref="MoveBy"/>。</summary>
    public static (double X, double Y) MoveTo(LabelTemplate template, int index, double xMm, double yMm,
        (double X, double Y, double Width, double Height)? occupancy = null)
    {
        var box = BoxOf(ElementAt(template, index));
        var shift = MoveBy(template, index, xMm - box.X, yMm - box.Y, occupancy);
        return (box.X + shift.Dx, box.Y + shift.Dy);
    }

    /// <summary>
    /// 把一个元素原地收进标签内：<strong>越界贴边，装不下就缩到装得下</strong>（不拒绝、不丢弃）。
    /// <para>导入底稿、改纸尺寸这类“批量落位”用它；手动拖动时仍走
    /// <see cref="MoveBy"/> 与 <see cref="ResizeBy"/>，两者口径与本方法同源。</para>
    /// </summary>
    public static void ClampIntoLabel(LabelTemplate template, TemplateElement element)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (element is null) throw new ArgumentNullException(nameof(element));

        if (element.Kind == ElementKind.Line)
        {
            if (Templates.CurveGeometry.IsCurved(element))
            {
                // 曲线不能各端点分别夹：那样会把一条弧撕成另一个形状。量整条的外接框，算"最少挪多少能进纸"，整条平移。
                var box = BoxOf(element);
                var dx = box.Width >= template.WidthMm ? 0 : Math.Min(0, template.WidthMm - (box.X + box.Width)) + Math.Max(0, -box.X);
                var dy = box.Height >= template.HeightMm ? 0 : Math.Min(0, template.HeightMm - (box.Y + box.Height)) + Math.Max(0, -box.Y);
                ApplyShift(element, dx, dy);
                return;
            }
            element.X = Clamp(element.X, 0, template.WidthMm);
            element.Y = Clamp(element.Y, 0, template.HeightMm);
            element.X2 = Clamp(element.X2, 0, template.WidthMm);
            element.Y2 = Clamp(element.Y2, 0, template.HeightMm);
            return;
        }

        var maxWidth = Math.Max(MinSideMm, template.WidthMm);
        var maxHeight = Math.Max(MinSideMm, template.HeightMm);
        var width = Math.Clamp(element.Width <= 0 ? MinSideMm : element.Width, MinSideMm, maxWidth);
        var height = Math.Clamp(element.Height <= 0 ? MinSideMm : element.Height, MinSideMm, maxHeight);
        element.X = Clamp(element.X, 0, Math.Max(0, template.WidthMm - width));
        element.Y = Clamp(element.Y, 0, Math.Max(0, template.HeightMm - height));
        element.Width = width;
        element.Height = height;
    }

    private static void ApplyShift(TemplateElement element, double dx, double dy)
    {
        element.X += dx;
        element.Y += dy;
        if (element.Kind == ElementKind.Line)
        {
            element.X2 += dx;
            element.Y2 += dy;
            // 端点动了中间节点也必须动：只挪两端等于把弧身留在原地。柄是相对量，跟着走不用改。
            Templates.CurveGeometry.Translate(element, dx, dy);
        }
    }

    // ---------- 缩放 ----------

    /// <summary>
    /// 角柄拖动时字号跟着等比放大的最小有效比率：宽高几何平均偏离不足 0.2% 就不动字号。
    /// <para>防止亚像素级抖动（0.02mm）把字号算成 12.0000001pt，界面上显示出一串小数还污染撤销栈。
    /// 阈值是比率口径，同一拖幅在小元素上占比更大——这是刻意的：小框对拖动本来就更敏感。</para>
    /// </summary>
    public const double FontScaleEpsilon = 1.002;

    /// <summary>
    /// 按句柄拖动缩放。<strong>语义照 CorelDRAW X4 实测口径</strong>（第 45 棒，别再改回"Shift=等比"）：
    /// <list type="bullet">
    /// <item><description>拖<strong>角</strong>柄 = 等比（两轴同倍率），倍率由拖拽量在「锚点 → 被拖的那个角」方向上的投影算出；</description></item>
    /// <item><description>拖<strong>边</strong>柄 = 单轴，另一轴一个字不动；</description></item>
    /// <item><description><paramref name="anchor"/> = <see cref="ResizeAnchor.Center"/>（按住 Shift）时锚点从"对角/对边"换成"元素中心"。</description></item>
    /// </list>
    /// <para>
    /// <strong>文本走方案 B：排版盒的宽绝不动</strong>。折行、缩字号、截断全部按盒宽判（<c>TextFit.DoesNotFit</c>），
    /// 那是出纸侧的行为，不该被编辑器里的一个拖动手势顺手动掉——所以文本角柄只把<strong>字号</strong>乘上倍率，
    /// 并把<strong>盒高</strong>等比放大（不放大盒高，<c>ShrinkToFit</c> 会因为"装不下"把字号又原地压回去，
    /// 拖了等于没拖）。要改盒宽去属性面板改"宽(mm)"，那里改的是折行判定，改完看得见。
    /// </para>
    /// <para>
    /// <paramref name="displayBox"/> = 用户实际看见并抓住的那块（文本 = 44 棒量出的墨迹盒）。
    /// 倍率必须按它算，否则"手柄在墨迹角上、算的却是排版盒对角"，手感与数字两头不对。
    /// 位置校正不在这里做：Core 量不了字，由 App 量完两侧墨迹盒后用 <see cref="AnchorShift"/> 纯平移补回。
    /// </para>
    /// <para>字号夹在 <see cref="TemplateValidator.MinFontPt"/>~<see cref="TemplateValidator.MaxFontPt"/>：
    /// 放太大或缩太小都会让模板存不进库（校验器报 Error），那不是"限制"而是"存不了"，与其让用户存盘时
    /// 才发现，不如拖的时候就停在上限。</para>
    /// </summary>
    public static void ResizeBy(LabelTemplate template, int index, ResizeHandle handle, double dxMm, double dyMm,
        ResizeAnchor anchor = ResizeAnchor.Opposite,
        (double X, double Y, double Width, double Height)? displayBox = null)
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
            // 只抓了端点（或什么都没抓）就到此为止；抓的是整只框的边/角 → 曲线整体缩放。
            if (handle is ResizeHandle.None or ResizeHandle.LineStart or ResizeHandle.LineEnd) return;
            ScaleCurveByBox(template, element, handle, dxMm, dyMm, anchor);
            return;
        }

        var box = displayBox ?? VisualBoxOf(element);
        var horizontal = handle.HasFlag(ResizeHandle.Left) || handle.HasFlag(ResizeHandle.Right);
        var vertical = handle.HasFlag(ResizeHandle.Top) || handle.HasFlag(ResizeHandle.Bottom);

        // 转过的元素：拖拽量先反变换回本地方向，否则"往纸面右拖"不等于"沿框的右边拖"。
        var (ldx, ldy) = ToLocalDelta(element, dxMm, dyMm);

        if (horizontal && vertical)
        {
            var ratio = UniformRatio(box, handle, anchor, ldx, ldy);
            if (Math.Abs(ratio - 1) < FontScaleEpsilon - 1) return;

            if (element.Kind == ElementKind.Text)
            {
                // 方案 B：字号 ×倍率、盒高等比跟上，盒宽一个字不动。位置交给 AnchorShift。
                element.FontSizePt = Math.Clamp(Math.Round(element.FontSizePt * ratio, 2),
                    TemplateValidator.MinFontPt, TemplateValidator.MaxFontPt);
                element.Height = Math.Max(MinSideMm, Math.Round(element.Height * ratio, 3));
                return;
            }

            var newWidth = Math.Clamp(Math.Round(box.Width * ratio, 3), MinSideMm, template.WidthMm);
            var newHeight = Math.Clamp(Math.Round(box.Height * ratio, 3), MinSideMm, template.HeightMm);
            var widthBefore = Math.Max(1e-9, box.Width);
            var heightBefore = Math.Max(1e-9, box.Height);
            var (heldX, heldY) = HeldPoint(box, handle, anchor);
            // 非文本元素：盒 == 视觉盒，锚点在 Core 里就能算准，一次到位。
            // 锚点是盒上哪个点，就反推新左上角：中心 → 减一半；拖的是左边/上边（钉住右/下）→ 减一整条边长。
            var newX = anchor == ResizeAnchor.Center ? heldX - newWidth / 2
                : handle.HasFlag(ResizeHandle.Left) ? heldX - newWidth
                : heldX;
            var newY = anchor == ResizeAnchor.Center ? heldY - newHeight / 2
                : handle.HasFlag(ResizeHandle.Top) ? heldY - newHeight
                : heldY;
            element.X = Math.Max(0, Math.Round(newX, 3));
            element.Y = Math.Max(0, Math.Round(newY, 3));
            element.Width = newWidth;
            element.Height = newHeight;
            ScaleFontWithBox(element, widthBefore, newWidth, heightBefore, newHeight);
            return;
        }

        if (element.Kind == ElementKind.Text)
        {
            // 文本单边柄（第 43 棒）：抻字身，不改排版盒——折行/缩字仍按盒决定（口径见
            // TemplateElement.TextScaleX 注释）。渲染是"绕盒中心等比抻"，所以 grabbed 边要 1:1 跟手，
            // 对面边会对称让开：拖右边 m 毫米 → 视觉宽 +2m；对边该不该让开由 AnchorShift 校正说了算。
            // 越界由校验器的 OccupiedBoundsOf 兜。
            var visual = VisualBoxOf(element);
            double? scaleX = null, scaleY = null;
            if (horizontal)
            {
                var grow = handle.HasFlag(ResizeHandle.Right) ? ldx : -ldx;
                var wanted = Math.Max(MinSideMm, visual.Width + 2 * grow);
                scaleX = Math.Clamp(wanted / Math.Max(1e-9, element.Width), TemplateValidator.MinStretch, TemplateValidator.MaxStretch);
            }
            if (vertical)
            {
                var grow = handle.HasFlag(ResizeHandle.Bottom) ? ldy : -ldy;
                var wanted = Math.Max(MinSideMm, visual.Height + 2 * grow);
                scaleY = Math.Clamp(wanted / Math.Max(1e-9, element.Height), TemplateValidator.MinStretch, TemplateValidator.MaxStretch);
            }
            if (scaleX is not null) element.TextScaleX = scaleX.Value;
            if (scaleY is not null) element.TextScaleY = scaleY.Value;
            return;
        }

        // 其余元素（矩形/图片/矢量底图）单边柄：照旧只改框——它们的"拉伸"本来就是宽高。
        var left = element.X;
        var top = element.Y;
        var right = element.X + element.Width;
        var bottom = element.Y + element.Height;
        if (handle.HasFlag(ResizeHandle.Left)) left = Math.Min(Clamp(left + ldx, 0, template.WidthMm), right - MinSideMm);
        if (handle.HasFlag(ResizeHandle.Right)) right = Math.Max(Clamp(right + ldx, left + MinSideMm, template.WidthMm), left + MinSideMm);
        if (handle.HasFlag(ResizeHandle.Top)) top = Math.Min(Clamp(top + ldy, 0, template.HeightMm), bottom - MinSideMm);
        if (handle.HasFlag(ResizeHandle.Bottom)) bottom = Math.Max(Clamp(bottom + ldy, top + MinSideMm, template.HeightMm), top + MinSideMm);

        element.X = Math.Max(0, left);
        element.Y = Math.Max(0, top);
        element.Width = Math.Max(MinSideMm, right - element.X);
        element.Height = Math.Max(MinSideMm, bottom - element.Y);
    }

    /// <summary>
    /// 整只框缩放一条曲线（第 85 棒）：先按盒算出新的宽与高（拖角＝等比、拖边＝单轴，
    /// 与矩形那两套用的是<strong>同一个</strong> <see cref="UniformRatio"/> / 单边夹取算法），
    /// 再把倍率交给 <c>CurveGeometry.ScaleBy</c> 一次映射到每个点和每根柄上。
    /// <para>这里<strong>故意不写 element.Width / Height</strong>：曲线的外接框是量出来的
    /// （<see cref="BoxOf"/> 对曲线走 <c>CurveGeometry.BoundsMm</c>），存进去那两个数没人读，
    /// 写了只会留下两份互相矛盾的账（§五-62 那一族）。</para>
    /// </summary>
    private static void ScaleCurveByBox(LabelTemplate template, TemplateElement element, ResizeHandle handle,
        double dxMm, double dyMm, ResizeAnchor anchor)
    {
        var box = VisualBoxOf(element);
        if (box.Width <= 1e-9 || box.Height <= 1e-9) return;      // 退化成一条横/竖线的形状：没有可放大的那一轴
        var (ldx, ldy) = ToLocalDelta(element, dxMm, dyMm);
        var (heldX, heldY) = HeldPoint(box, handle, anchor);
        var horizontal = handle.HasFlag(ResizeHandle.Left) || handle.HasFlag(ResizeHandle.Right);
        var vertical = handle.HasFlag(ResizeHandle.Top) || handle.HasFlag(ResizeHandle.Bottom);

        double sx, sy;
        if (horizontal && vertical)
        {
            var ratio = UniformRatio(box, handle, anchor, ldx, ldy);
            sx = sy = ratio;
        }
        else if (horizontal)
        {
            var left = box.X;
            var right = box.X + box.Width;
            if (handle.HasFlag(ResizeHandle.Left)) left = Math.Min(left + ldx, right - MinSideMm);
            if (handle.HasFlag(ResizeHandle.Right)) right = Math.Max(right + ldx, left + MinSideMm);
            sx = (right - left) / box.Width;
            sy = 1;
        }
        else if (vertical)
        {
            var top = box.Y;
            var bottom = box.Y + box.Height;
            if (handle.HasFlag(ResizeHandle.Top)) top = Math.Min(top + ldy, bottom - MinSideMm);
            if (handle.HasFlag(ResizeHandle.Bottom)) bottom = Math.Max(bottom + ldy, top + MinSideMm);
            sy = (bottom - top) / box.Height;
            sx = 1;
        }
        else return;

        // 与矩形那条路同一档夹紧：新盒不许超过这张纸。
        var newWidth = Math.Clamp(box.Width * sx, MinSideMm, template.WidthMm);
        var newHeight = Math.Clamp(box.Height * sy, MinSideMm, template.HeightMm);
        sx = newWidth / box.Width;
        sy = newHeight / box.Height;
        if (Math.Abs(sx - 1) < 1e-9 && Math.Abs(sy - 1) < 1e-9) return;
        Templates.CurveGeometry.ScaleBy(element, sx, sy, heldX, heldY);
    }

    /// <summary>
    /// 把元素的盒"设定"到指定宽高（属性面板那两格走这里，第 85 棒）。
    /// <para>普通元素就是写 <c>Width</c>/<c>Height</c>；<strong>曲线不行</strong>——它的外接框是量出来的
    /// （<see cref="BoxOf"/> 对曲线走 <c>CurveGeometry.BoundsMm</c>），写进去那两个数没有任何人读，
    /// 于是"面板填了宽、形状一动不动"。这里改成按倍率整体缩放，锚点取盒左上角（与"X/Y 不动"那条老直觉一致）。
    /// 面板与拖框两条路都从这一处过，不留第二份算式（§五-62 那一族）。</para>
    /// </summary>
    public static void SetBoxSize(TemplateElement element, double? widthMm, double? heightMm)
    {
        if (!HasMeasuredBox(element))
        {
            if (widthMm > 0) element.Width = Math.Max(MinSideMm, widthMm.Value);
            if (heightMm > 0) element.Height = Math.Max(MinSideMm, heightMm.Value);
            return;
        }
        var box = VisualBoxOf(element);
        if (box.Width <= 1e-9 || box.Height <= 1e-9) return;      // 退化成一条线：那一轴没有"宽"可设
        var sx = widthMm > 0 ? Math.Max(MinSideMm, widthMm.Value) / box.Width : 1;
        var sy = heightMm > 0 ? Math.Max(MinSideMm, heightMm.Value) / box.Height : 1;
        Templates.CurveGeometry.ScaleBy(element, sx, sy, box.X, box.Y);
    }

    /// <summary>
    /// 这只元素的盒是不是<strong>量出来的</strong>（曲线与「转为曲线」后的形状）：
    /// 是 → <c>Width</c>/<c>Height</c> 那两个字段没人读，改尺寸只能整体缩放。
    /// 一条没有节点也没有柄的直线不算（它吃两个端点，第 49 棒口径）。
    /// </summary>
    public static bool HasMeasuredBox(TemplateElement element) =>
        element.Kind == ElementKind.Line
        && (Templates.CurveGeometry.IsCurved(element) || Templates.CurveGeometry.IsClosed(element));

    /// <summary>
    /// 角柄拖拽的等比倍率：把"新的角位置"投影到「锚点 → 原来的角」这条对角线上，投影比就是倍率。
    /// <para>与 CDR 同一算法：沿对角线拖多少就放大多少，垂直于对角线的抖动被投影吃掉（不会误放大）。</para>
    /// </summary>
    public static double UniformRatio((double X, double Y, double Width, double Height) box,
        ResizeHandle handle, ResizeAnchor anchor, double dxMm, double dyMm)
    {
        var cornerX = handle.HasFlag(ResizeHandle.Left) ? box.X : box.X + box.Width;
        var cornerY = handle.HasFlag(ResizeHandle.Top) ? box.Y : box.Y + box.Height;
        var (anchorX, anchorY) = HeldPoint(box, handle, anchor);
        var vx = cornerX - anchorX;
        var vy = cornerY - anchorY;
        var squared = vx * vx + vy * vy;
        if (squared <= 1e-9) return 1;
        var projected = ((cornerX + dxMm - anchorX) * vx + (cornerY + dyMm - anchorY) * vy) / squared;
        return Math.Max(0.02, projected);
    }

    /// <summary>
    /// 这次拖拽"应当钉住不动的那个点"（毫米）：<see cref="ResizeAnchor.Center"/> = 盒中心；
    /// 否则 = 没被拖的那组边（拖左边钉右边，拖右边钉左边；该轴没拖就钉这条轴的起始边，位移本应为 0）。
    /// </summary>
    public static (double X, double Y) HeldPoint((double X, double Y, double Width, double Height) box,
        ResizeHandle handle, ResizeAnchor anchor)
        => anchor == ResizeAnchor.Center
            ? (box.X + box.Width / 2, box.Y + box.Height / 2)
            : (handle.HasFlag(ResizeHandle.Left) ? box.X + box.Width : box.X,
               handle.HasFlag(ResizeHandle.Top) ? box.Y + box.Height : box.Y);

    /// <summary>
    /// 锚点残差 → 需要补的<strong>纯平移量</strong>：把"拖完之后量出来的那块"搬回"拖之前该钉住的那个点"。
    /// <para>Core 量不了字（不许碰 WPF），所以由 App 层用生产量具 <c>TextFit</c> 量出拖前/拖后两块墨迹盒
    /// 再喂进来。只平移、不改尺寸——尺寸是上面 <see cref="ResizeBy"/> 的决定，两处各算必长歪（§五-62 那族）。</para>
    /// </summary>
    public static (double Dx, double Dy) AnchorShift(
        (double X, double Y, double Width, double Height) before,
        (double X, double Y, double Width, double Height) after,
        ResizeHandle handle, ResizeAnchor anchor)
    {
        var (bx, by) = HeldPoint(before, handle, anchor);
        var (ax, ay) = HeldPoint(after, handle, anchor);
        return (bx - ax, by - ay);
    }

    /// <summary>
    /// 角柄等比缩放时把字号带上：取宽高两个比率的几何平均（正方形拖动时两者本就相等，
    /// 拖成长条形时取平均比取单边更贴近"整体放大了一圈"的观感）。
    /// <para>只对带字号的元素生效（文本与条码下方的可读数字）；图片、矩形、矢量底图没有字号这一说。
    /// 文本的角柄不走这条路——它在 <see cref="ResizeBy"/> 里直接把字号乘上倍率（方案 B）。</para>
    /// </summary>
    private static void ScaleFontWithBox(TemplateElement element, double widthBefore, double widthAfter, double heightBefore, double heightAfter)
    {
        if (element.Kind is not (ElementKind.Text or ElementKind.Barcode)) return;
        if (widthBefore <= 0 || heightBefore <= 0) return;

        var widthRatio = widthAfter / widthBefore;
        var heightRatio = heightAfter / heightBefore;
        if (widthRatio <= 0 || heightRatio <= 0) return;

        var ratio = Math.Sqrt(widthRatio * heightRatio);
        if (Math.Abs(ratio - 1) < FontScaleEpsilon - 1) return;

        element.FontSizePt = Math.Clamp(
            Math.Round(element.FontSizePt * ratio, 2),
            TemplateValidator.MinFontPt,
            TemplateValidator.MaxFontPt);
    }

    // ---------- 吸附 ----------

    /// <summary>
    /// 对"拖动中的位置"做吸附：先看标签边/中心/内边距/别的元素（<see cref="SnapOptions.ToleranceMm"/> 内），
    /// 没命中再退到网格取整。只修正左上角，不改尺寸。
    /// <para>第 46 棒：给了 <paramref name="occupancy"/>（文本=墨迹盒）时，候选边、网格取整、夹紧
    /// 全在占位盒坐标系里做，算完再换回排版盒原点——吸的是人看见的那条边。邻居辅助线仍按各元素排版盒取
    /// （本棒不扩到那里，扩之前要先想清楚"吸到邻居的墨迹边"在行带模板里是不是用户想要的）。</para>
    /// </summary>
    public static SnapResult Snap(LabelTemplate template, int index, double xMm, double yMm, SnapOptions options,
        (double X, double Y, double Width, double Height)? occupancy = null)
    {
        options ??= SnapOptions.None;
        var element = ElementAt(template, index);
        var box = BoxOf(element);
        var occ = occupancy ?? box;
        var offX = occ.X - box.X;
        var offY = occ.Y - box.Y;
        var originX = xMm + offX;
        var originY = yMm + offY;
        var guides = new List<GuideLine>();

        if (!options.Enabled)
            return new SnapResult(xMm, yMm, guides) { OriginalX = xMm, OriginalY = yMm };

        var width = occ.Width;
        var height = occ.Height;
        var tolerance = Math.Max(0.05, options.ToleranceMm);

        // 三个候选边（左边 / 水平中心 / 右边）各自去贴目标线，命中后换回新的左上角
        var xHit = PickSnap(originX, new[] { 0.0, width / 2, width }, CollectTargets(template, index, options, vertical: true), tolerance, out var xLine);
        var snappedX = xHit.HasValue ? xHit.Value : originX;
        if (xHit.HasValue && xLine is not null)
            guides.Add(new GuideLine(xLine.Value.Source, Vertical: true, xLine.Value.Position, 0, template.HeightMm));
        if (!xHit.HasValue && options.SnapToGrid && options.GridStepMm > 0)
            snappedX = Math.Round(originX / options.GridStepMm) * options.GridStepMm;

        var yHit = PickSnap(originY, new[] { 0.0, height / 2, height }, CollectTargets(template, index, options, vertical: false), tolerance, out var yLine);
        var snappedY = yHit.HasValue ? yHit.Value : originY;
        if (yHit.HasValue && yLine is not null)
            guides.Add(new GuideLine(yLine.Value.Source, Vertical: false, yLine.Value.Position, 0, template.WidthMm));
        if (!yHit.HasValue && options.SnapToGrid && options.GridStepMm > 0)
            snappedY = Math.Round(originY / options.GridStepMm) * options.GridStepMm;

        // 吸附完仍要夹紧：贴住中心线却把元素推出边界是不能接受的。
        // 但文本不夹（第 81 棒，与 MoveBy 同口径）：它的占物是量出来的墨迹，摆位允许探出纸，
        // 越界由编辑器那条按样例墨迹量的提醒与出纸前那道真数据闸接手。
        var free = element.Kind == ElementKind.Text;
        var maxX = template.WidthMm - width;
        var maxY = template.HeightMm - height;
        var clampedX = free || maxX < 0 ? snappedX : Math.Clamp(snappedX, 0, maxX);
        var clampedY = free || maxY < 0 ? snappedY : Math.Clamp(snappedY, 0, maxY);
        return new SnapResult(clampedX - offX, clampedY - offY, guides)
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

    /// <summary>
    /// 对齐到标签边。某个轴传 null 表示这个轴不动 —— 界面上那六个按钮各管一个轴，
    /// 上一版只能双轴一起给，点「顶对齐」会把水平位置也甩到最左。
    /// <para>第 46 棒：给了 <paramref name="occupancy"/>（文本=墨迹盒）就按占位盒的边对齐——
    /// 点「右对齐」要的是<strong>看得见的字</strong>贴右缘，而不是一条比字宽得多的隐形行带顶到右缘。</para>
    /// </summary>
    public static void AlignToLabel(LabelTemplate template, int index, AlignHorizontal? horizontal, AlignVertical? vertical,
        (double X, double Y, double Width, double Height)? occupancy = null)
    {
        var element = ElementAt(template, index);
        var occ = occupancy ?? BoxOf(element);
        var dx = horizontal switch
        {
            AlignHorizontal.Center => (template.WidthMm - occ.Width) / 2 - occ.X,
            AlignHorizontal.Right => template.WidthMm - occ.Width - occ.X,
            AlignHorizontal.Left => -occ.X,
            _ => 0d,
        };
        var dy = vertical switch
        {
            AlignVertical.Middle => (template.HeightMm - occ.Height) / 2 - occ.Y,
            AlignVertical.Bottom => template.HeightMm - occ.Height - occ.Y,
            AlignVertical.Top => -occ.Y,
            _ => 0d,
        };
        if (dx == 0 && dy == 0) return;
        ApplyShift(element, dx, dy);
    }

    /// <summary>按内边距把元素推到内容区边上（常用：整行文本贴左内边距）。占位盒口径同 <see cref="AlignToLabel"/>。</summary>
    public static void SnapToPadding(LabelTemplate template, int index, AlignHorizontal horizontal, AlignVertical vertical,
        (double X, double Y, double Width, double Height)? occupancy = null)
    {
        var element = ElementAt(template, index);
        var occ = occupancy ?? BoxOf(element);
        var left = Math.Clamp(template.PaddingMm, 0, template.WidthMm);
        var top = Math.Clamp(template.PaddingMm, 0, template.HeightMm);
        var right = Math.Max(left, template.WidthMm - template.PaddingMm);
        var bottom = Math.Max(top, template.HeightMm - template.PaddingMm);

        var targetX = horizontal switch
        {
            AlignHorizontal.Center => left + (right - left - occ.Width) / 2,
            AlignHorizontal.Right => right - occ.Width,
            _ => left,
        };
        var targetY = vertical switch
        {
            AlignVertical.Middle => top + (bottom - top - occ.Height) / 2,
            AlignVertical.Bottom => bottom - occ.Height,
            _ => top,
        };
        ApplyShift(element, Math.Clamp(targetX, 0, Math.Max(0, template.WidthMm - occ.Width)) - occ.X,
            Math.Clamp(targetY, 0, Math.Max(0, template.HeightMm - occ.Height)) - occ.Y);
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
