using LabelGou.Core.Editing;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// B 类模板编辑器的几何与历史。
/// <para>
/// 这些规则决定"拖一下之后还印不印得出来"，全在 Core 里就是为了能自动化验证：
/// 越界必须贴边而不是消失、撤销必须真的回到旧值（快照要和在编对象断开）、
/// 新建元素不能压在别人身上。这些靠手点 GUI 是发现不全的。
/// </para>
/// </summary>
public class EditingTests
{
    private static LabelTemplate Page(double width = 100, double height = 80, double padding = 4) => new()
    {
        Id = "user.test",
        Name = "测试模板",
        WidthMm = width,
        HeightMm = height,
        PaddingMm = padding,
        BuiltIn = false,
    };

    private static TemplateElement Box(double x, double y, double w, double h, ElementKind kind = ElementKind.Rect)
        => new() { Kind = kind, X = x, Y = y, Width = w, Height = h };

    private static TemplateElement Line(double x1, double y1, double x2, double y2)
        => new() { Kind = ElementKind.Line, X = x1, Y = y1, X2 = x2, Y2 = y2, Width = Math.Abs(x2 - x1), Height = Math.Abs(y2 - y1) };

    // ---------- 包围盒与命中 ----------

    [Fact]
    public void LineBoundingBoxComesFromEndpoints()
    {
        var box = EditGeometry.BoxOf(Line(30, 50, 10, 20));
        Assert.Equal(10, box.X);
        Assert.Equal(20, box.Y);
        Assert.Equal(20, box.Width);
        Assert.Equal(30, box.Height);
    }

    [Fact]
    public void HitTestRespectsToleranceOutsideTheBox()
    {
        var element = Box(10, 10, 20, 8);
        Assert.True(EditGeometry.HitTest(element, 25, 14));
        Assert.True(EditGeometry.HitTest(element, 9.6, 14), "差 0.4mm 应当算点中，手指没那么准");
        Assert.False(EditGeometry.HitTest(element, 9.0, 14));
    }

    [Fact]
    public void ThinLineIsStillClickable()
    {
        var line = Line(0, 20, 100, 20);
        line.ThicknessMm = 0.3;
        Assert.True(EditGeometry.HitTest(line, 50, 20.4), "0.3mm 的线按线宽放宽容差");
        Assert.False(EditGeometry.HitTest(line, 50, 25));
    }

    [Fact]
    public void TopmostElementWinsTheClick()
    {
        var template = Page();
        var bottom = Box(5, 5, 40, 40);
        var top = Box(20, 20, 10, 10);
        template.Elements.Add(bottom);
        template.Elements.Add(top);

        Assert.Equal(1, EditGeometry.TopmostAt(template, 22, 22));
        Assert.Equal(0, EditGeometry.TopmostAt(template, 8, 8));
        Assert.Equal(-1, EditGeometry.TopmostAt(template, 80, 70));
    }

    [Fact]
    public void InvisibleElementsDoNotCatchClicks()
    {
        var template = Page();
        template.Elements.Add(Box(5, 5, 40, 40));
        var hidden = Box(20, 20, 10, 10);
        hidden.Visible = false;
        template.Elements.Add(hidden);

        Assert.Equal(0, EditGeometry.TopmostAt(template, 22, 22));
    }

    [Fact]
    public void HandlesDistinguishCornerFromEdge()
    {
        var element = Box(10, 10, 20, 8);
        Assert.Equal(ResizeHandle.TopLeft, EditGeometry.HandleAt(element, 10, 10, 1));
        Assert.Equal(ResizeHandle.BottomRight, EditGeometry.HandleAt(element, 30, 18, 1));
        Assert.Equal(ResizeHandle.Left, EditGeometry.HandleAt(element, 10, 14, 1));
        Assert.Equal(ResizeHandle.Top, EditGeometry.HandleAt(element, 20, 10, 1));
        Assert.Equal(ResizeHandle.None, EditGeometry.HandleAt(element, 20, 14, 1));
    }

    [Fact]
    public void LineEndpointsAreTheHandles()
    {
        var line = Line(10, 10, 60, 30);
        Assert.Equal(ResizeHandle.LineStart, EditGeometry.HandleAt(line, 10, 10, 1));
        Assert.Equal(ResizeHandle.LineEnd, EditGeometry.HandleAt(line, 60, 30, 1));
        Assert.Equal(ResizeHandle.None, EditGeometry.HandleAt(line, 35, 20, 1));
    }

    // ---------- 移动 ----------

    [Fact]
    public void DraggingPastTheEdgeStopsAtTheBorder()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));

        var shift = EditGeometry.MoveBy(template, 0, 200, 200);

        Assert.Equal(80, template.Elements[0].X, 6);
        Assert.Equal(72, template.Elements[0].Y, 6);
        Assert.Equal(70, shift.Dx, 6);
        Assert.True(shift.Dx < 200, "报回来的位移要小于请求量，UI 才知道被挡住了");
    }

    [Fact]
    public void MovingLineShiftsBothEndpoints()
    {
        var template = Page();
        template.Elements.Add(Line(10, 10, 60, 30));

        EditGeometry.MoveBy(template, 0, 5, -4);

        Assert.Equal(15, template.Elements[0].X, 6);
        Assert.Equal(6, template.Elements[0].Y, 6);
        Assert.Equal(65, template.Elements[0].X2, 6);
        Assert.Equal(26, template.Elements[0].Y2, 6);
    }

    [Fact]
    public void OversizedElementIsNotPinnedToZero()
    {
        var template = Page(width: 30, height: 30);
        template.Elements.Add(Box(0, 0, 60, 60));

        var moved = EditGeometry.MoveTo(template, 0, 500, 500);

        // 占物比标签还大 → 那一轴没有"贴边"可言。旧口径把它夹死在 (0,0)：用户一拖就弹回来，
        // 既像"被限制"又像"卡顿"（第 46 棒翻案，越界交给墨迹那道闸与「缩回纸内」说）。
        Assert.Equal(500, moved.Item1, 6);
        Assert.Equal(500, moved.Item2, 6);
        Assert.False(double.IsNaN(moved.Item1));
    }

    // ---------- 缩放 ----------

    [Fact]
    public void ResizeKeepsMinimumSide()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));

        EditGeometry.ResizeBy(template, 0, ResizeHandle.Left, 50, 0);

        Assert.Equal(29.2, template.Elements[0].X, 6);
        Assert.Equal(EditGeometry.MinSideMm, template.Elements[0].Width, 6);
    }

    [Fact]
    public void ResizeCannotDragOffThePaper()
    {
        var template = Page();
        template.Elements.Add(Box(60, 10, 20, 8));

        EditGeometry.ResizeBy(template, 0, ResizeHandle.Right, 50, 0);

        Assert.Equal(100, template.Elements[0].X + template.Elements[0].Width, 6);
    }

    [Fact]
    public void LineResizeOnlyMovesTheGrabbedEnd()
    {
        var template = Page();
        template.Elements.Add(Line(10, 10, 60, 30));

        EditGeometry.ResizeBy(template, 0, ResizeHandle.LineEnd, 5, 5);

        Assert.Equal(10, template.Elements[0].X, 6);
        Assert.Equal(65, template.Elements[0].X2, 6);
        Assert.Equal(35, template.Elements[0].Y2, 6);
    }

    // ---------- 第 45 棒：缩放语义照 CorelDRAW X4 实测（角柄等比+对角固定，Shift=绕中心） ----------

    /// <summary>
    /// 带折行宽度的文本夹具：本文件里越界/旋转/拉伸那几条测的都是「排版盒 × 拉伸」这条占物判据，
    /// 而第 46 棒起<strong>永不折行</strong>的文本不再按排版盒判越界（那条带子只是对齐基准，不出纸）。
    /// 不折行那条路另用专门夹具测，别混进来。
    /// </summary>
    private static TemplateElement TextBox(double x, double y, double w, double h, double fontPt)
        => new() { Kind = ElementKind.Text, Text = "{{ItemNo}}", X = x, Y = y, Width = w, Height = h,
            FontSizePt = fontPt, WrapWidthMm = w };

    [Fact]
    public void CornerDragScalesFontAndKeepsBandWidth()
    {
        var template = Page();
        template.Elements.Add(TextBox(10, 10, 20, 10, 12));

        // 右下角拖 +10/+10：锚点=左上角(10,10)，被拖的角(30,20)，对角向量(20,10)。
        // 投影比 = ((30+10-10)*20 + (20+10-10)*10) / (20²+10²) = (800+200)/500 = 1.6。
        // 方案 B：字号 ×1.6、盒高等比跟上，**盒宽绝不动**（折行/缩字判定按盒宽，那是出纸侧的行为）。
        EditGeometry.ResizeBy(template, 0, ResizeHandle.BottomRight, 10, 10);

        var e = template.Elements[0];
        Assert.Equal(19.2, e.FontSizePt, 6);
        Assert.Equal(16, e.Height, 6);
        Assert.Equal(20, e.Width, 6);           // ← 这条就是方案 B 的钉子
        Assert.Equal(10, e.X, 6);               // Core 不动位置，锚点由 App 量完墨迹再纯平移补
        Assert.Equal(10, e.Y, 6);
    }

    [Fact]
    public void SquareCornerDragDoublesFontAndHeight()
    {
        var template = Page();
        template.Elements.Add(TextBox(10, 10, 20, 20, 10));

        // 正方形拖大一倍：投影比 = 2 → 字号翻倍、盒高翻倍、盒宽仍不动
        EditGeometry.ResizeBy(template, 0, ResizeHandle.BottomRight, 20, 20);

        var e = template.Elements[0];
        Assert.Equal(20, e.FontSizePt, 6);
        Assert.Equal(40, e.Height, 6);
        Assert.Equal(20, e.Width, 6);
    }

    [Fact]
    public void CornerShrinkStopsAtPrintableFloor()
    {
        var template = Page();
        template.Elements.Add(TextBox(0, 0, 40, 40, 20));

        // 缩到投影比 0.05 → 字号 1pt，必须夹在可印下限上（跌破就存不进库，那是死路不是限制）
        EditGeometry.ResizeBy(template, 0, ResizeHandle.BottomRight, -38, -38);

        var e = template.Elements[0];
        Assert.Equal(TemplateValidator.MinFontPt, e.FontSizePt, 6);
        Assert.False(TemplateValidator.Validate(template).HasError());
    }

    [Fact]
    public void TinyCornerJitterChangesNothing()
    {
        var template = Page();
        template.Elements.Add(TextBox(10, 10, 20, 10, 12));

        // 亚像素级抖动（0.02mm≈0.08px）：投影比 1.0012，低于 FontScaleEpsilon(0.2%) → 整条早退，
        // 字号与盒高都不动，不污染界面小数与撤销栈。
        EditGeometry.ResizeBy(template, 0, ResizeHandle.BottomRight, 0.02, 0.02);

        var e = template.Elements[0];
        Assert.Equal(12, e.FontSizePt, 6);
        Assert.Equal(10, e.Height, 6);
    }

    [Fact]
    public void RectCornerDragIsProportionalByDefault()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 10));   // 2:1 的框

        // **不按任何键**：角柄就等比（本机 CDR 实测两轴倍率同为 129.4%）。
        // 投影比 1.6 → 32×16，比例仍是 2:1；旧口径（自由拉扁成 30×20）随 44 棒的"Shift=等比"一起翻掉。
        EditGeometry.ResizeBy(template, 0, ResizeHandle.BottomRight, 10, 10);

        var e = template.Elements[0];
        Assert.Equal(32, e.Width, 6);
        Assert.Equal(16, e.Height, 6);
        Assert.Equal(2.0, e.Width / e.Height, 3);
        Assert.Equal(10, e.X, 6);                     // 锚点 = 没被拖的左上角
        Assert.Equal(10, e.Y, 6);
    }

    [Fact]
    public void RectCornerDragFromTopLeftHoldsBottomRight()
    {
        var template = Page();
        template.Elements.Add(Box(40, 40, 20, 10));

        // 拖左上角 (-10,-10)：等比放大后右下角必须还在 (60,50)——锚点是没被拖的那组边
        EditGeometry.ResizeBy(template, 0, ResizeHandle.TopLeft, -10, -10);

        var e = template.Elements[0];
        Assert.Equal(60, e.X + e.Width, 6);
        Assert.Equal(50, e.Y + e.Height, 6);
        Assert.Equal(2.0, e.Width / e.Height, 3);     // 2:1 没变形
    }

    [Fact]
    public void ShiftAnchorGrowsFromTheCenter()
    {
        var template = Page();
        template.Elements.Add(Box(40, 40, 20, 10));

        // 按住 Shift（anchor=Center）：中心 (50,45) 不许动，四周对称长。
        // 投影比 = ((20)*10 + (15)*5)/125 = 2.2 → 44×22 → 新左上 (28,34)，中心仍是 (50,45)。
        EditGeometry.ResizeBy(template, 0, ResizeHandle.BottomRight, 10, 10, ResizeAnchor.Center);

        var e = template.Elements[0];
        Assert.Equal(50, e.X + e.Width / 2, 6);
        Assert.Equal(45, e.Y + e.Height / 2, 6);
        Assert.Equal(2.0, e.Width / e.Height, 3);
    }

    [Fact]
    public void TextEdgeDragStretchesGlyphNotLayoutBox()
    {
        var template = Page();
        template.Elements.Add(TextBox(10, 10, 20, 10, 12));

        // 第 43 棒语义保留：文本拖边 = 把字身抻长（像图片那种拉伸），字号与排版盒都不动。
        // 渲染是绕盒中心等比抻，所以 grabbed 边 1:1 跟手、对面边对称让开：拖右边 10mm → 视觉宽 20→40、倍率 2。
        // （对面边该不该让开，由 AnchorShift 在 App 层校正说了算。）
        EditGeometry.ResizeBy(template, 0, ResizeHandle.Right, 10, 0);

        var e = template.Elements[0];
        Assert.Equal(2, e.TextScaleX, 6);                           // 视觉宽 20→40 = 倍率 2
        Assert.Equal(20, e.Width, 6);                                // 排版盒没动（折行/缩字口径不变）
        Assert.Equal(12, e.FontSizePt, 6);                           // 字号一个点都不动
        Assert.Equal(1, e.TextScaleY, 6);                            // 只横抻，纵向不跟着变
    }

    [Fact]
    public void NonTextEdgeDragStillChangesBox()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 10));   // 矩形的"拉伸"本来就是宽高

        EditGeometry.ResizeBy(template, 0, ResizeHandle.Right, 10, 0);

        var e = template.Elements[0];
        Assert.Equal(30, e.Width, 6);
        Assert.Equal(1, e.TextScaleX, 6);   // 非文本元素不碰字面倍率
    }

    [Fact]
    public void CornerDragRatioComesFromTheInkBoxNotTheLayoutBox()
    {
        var template = Page();
        // AI 行式模板的真形状：排版盒 114.57 宽（比纸还宽），墨迹只有 66 宽。
        // 用户抓的是墨迹的右下角，倍率必须按墨迹算——按排版盒算会差出一个量级（1.167 vs 1.094）。
        var e = TextBox(0, 24, 114.57, 10.42, 14.3);
        e.TextScaleX = 0.763;
        e.TextScaleY = 0.763;
        template.Elements.Add(e);
        var ink = (13.6, 25.0, 66.0, 8.0);

        EditGeometry.ResizeBy(template, 0, ResizeHandle.BottomRight, 10, 10, ResizeAnchor.Opposite, ink);

        Assert.Equal(16.69, template.Elements[0].FontSizePt, 6);   // 14.3 × 1.1674（墨迹投影）
        Assert.Equal(1.167, template.Elements[0].Height / 10.42, 3);
        Assert.Equal(114.57, template.Elements[0].Width, 6);       // 盒宽照旧一个字不动
    }

    // ---------- AnchorShift：App 层量完墨迹盒后，把锚点用纯平移补回去 ----------

    [Fact]
    public void AnchorShiftMovesTheHeldCornerBack()
    {
        var before = (10.0, 10.0, 20.0, 10.0);
        var after = (12.0, 11.0, 32.0, 16.0);     // 墨迹左上角被拉伸锚点搬走了 2/1mm

        var (dx, dy) = EditGeometry.AnchorShift(before, after, ResizeHandle.BottomRight, ResizeAnchor.Opposite);

        Assert.Equal(-2, dx, 6);
        Assert.Equal(-1, dy, 6);
    }

    [Fact]
    public void AnchorShiftWithCenterAnchorHoldsTheMiddle()
    {
        var before = (10.0, 10.0, 20.0, 10.0);    // 中心 (20,15)
        var after = (14.0, 13.0, 40.0, 20.0);     // 中心 (34,23)

        var (dx, dy) = EditGeometry.AnchorShift(before, after, ResizeHandle.BottomRight, ResizeAnchor.Center);

        Assert.Equal(-14, dx, 6);
        Assert.Equal(-8, dy, 6);
    }

    [Fact]
    public void AnchorShiftForEdgeHandleHoldsTheOppositeEdge()
    {
        var before = (10.0, 10.0, 20.0, 10.0);
        var after = (15.0, 10.0, 35.0, 10.0);     // 拖右边，左边却自己滑右了 5mm

        var (dx, dy) = EditGeometry.AnchorShift(before, after, ResizeHandle.Right, ResizeAnchor.Opposite);

        Assert.Equal(-5, dx, 6);
        Assert.Equal(0, dy, 6);                    // 这一轴本来就没动
    }

    [Fact]
    public void HeldPointPicksTheUnDraggedEdges()
    {
        var box = (10.0, 10.0, 20.0, 10.0);

        Assert.Equal((10.0, 10.0), EditGeometry.HeldPoint(box, ResizeHandle.BottomRight, ResizeAnchor.Opposite));
        Assert.Equal((30.0, 20.0), EditGeometry.HeldPoint(box, ResizeHandle.TopLeft, ResizeAnchor.Opposite));
        // 拖横边时纵向根本没动，那一轴钉起始边就够（位移本应为 0，取中心与取上边同结果）——别误读成"绕中心"。
        Assert.Equal((30.0, 10.0), EditGeometry.HeldPoint(box, ResizeHandle.Left, ResizeAnchor.Opposite));
        Assert.Equal((20.0, 15.0), EditGeometry.HeldPoint(box, ResizeHandle.BottomRight, ResizeAnchor.Center));
    }

    // ---------- 第 46 棒：摆位/对齐按看得见的墨迹算，不再被 130mm 隐形行带顶住 ----------

    /// <summary>用户实测形状：纸 140，AI 给的行带 130（X=5），可这一行的字只有 20 宽。</summary>
    private static LabelTemplate BandBound(out TemplateElement text)
    {
        var template = Page(width: 140, height: 100);
        text = TextBox(5, 20, 130, 10, 14.3);
        template.Elements.Add(text);
        return template;
    }

    private static readonly (double X, double Y, double Width, double Height) InkOfBandBound = (10, 22, 20, 7);

    /// <summary>
    /// 第 81 棒②改口（用户：「墨迹边框被固定在纸张范围内无法超出」）：文本的摆位<strong>不再被行带顶住</strong>，
    /// 拖多远走多远，越过纸边也不夹——排版盒只是"字在哪对齐"的虚拟基准，它不出纸。
    /// 越界那份保护交给两处能量墨迹的地方（编辑器 Warning + 出纸前真数据闸，判据在 App.Tests）。
    /// 非文本那一半仍夹在纸内：它们的框就是会印出去的东西（<see cref="DraggingPastTheEdgeStopsAtTheBorder"/>）。
    /// </summary>
    [Fact]
    public void TextDragsWithTheHandEvenPastThePaperEdge()
    {
        var template = BandBound(out var text);

        EditGeometry.MoveBy(template, 0, 500, 0);

        Assert.Equal(505, text.X, 6);     // 5 + 500：从前停在 140−130=10 那堵墙上，字看着离右边还远
    }

    [Fact]
    public void InkOccupancyIsNoLongerWalledAtThePaperEdgeEither()
    {
        var template = BandBound(out var text);

        EditGeometry.MoveBy(template, 0, 120, 0, InkOfBandBound);

        // 喂了墨迹占位盒也一样放开：墨迹右缘 30 → 150，越过 140 的纸边不夹回来
        Assert.Equal(125, text.X, 6);
        Assert.Equal(150, InkOfBandBound.X + (text.X - 5) + InkOfBandBound.Width, 6);
    }

    [Fact]
    public void AlignRightUsesTheInkEdgeNotTheBandEdge()
    {
        var bandOnly = BandBound(out var a);
        EditGeometry.AlignToLabel(bandOnly, 0, AlignHorizontal.Right, null);
        Assert.Equal(10, a.X, 6);                       // 旧口径：把 130 的行带顶到右缘，字看着还在左半边

        var withInk = BandBound(out var b);
        EditGeometry.AlignToLabel(withInk, 0, AlignHorizontal.Right, null, InkOfBandBound);
        Assert.Equal(115, b.X, 6);                      // 新口径：看得见的字贴右缘
    }

    // ---------- 第 43 棒：视觉盒、旋转外接、越界拦截、schema v4、SVG 变换串 ----------

    [Fact]
    public void VisualBoxStretchesAroundCenterForTextOnly()
    {
        var template = Page();
        var e = TextBox(10, 10, 20, 10, 12);
        e.TextScaleX = 2;                       // 横向抻一倍，绕盒中心向外扩
        template.Elements.Add(e);

        var vb = EditGeometry.VisualBoxOf(e);
        Assert.Equal(0, vb.X, 6);               // 中心 20，视觉宽 40 → 左边 0
        Assert.Equal(40, vb.Width, 6);
        Assert.Equal(10, vb.Y, 6);              // 纵向没抻，不动
        Assert.Equal(10, vb.Height, 6);

        // 非文本元素：VisualBoxOf 恒等于 BoxOf（拉伸是文本专属）
        var rect = Box(10, 10, 20, 10);
        Assert.Equal(EditGeometry.BoxOf(rect), EditGeometry.VisualBoxOf(rect));
    }

    [Fact]
    public void OccupiedBoundsOfRotatesAroundCenter()
    {
        var e = TextBox(10, 10, 20, 10, 12);   // 中心 (20,15)
        e.RotationDeg = 90;

        var occ = EditGeometry.OccupiedBoundsOf(e);
        Assert.Equal(15, occ.X, 6);             // 宽 20 高 10 转 90° → 占 10 宽 20 高，仍居中 (20,15)
        Assert.Equal(5, occ.Y, 6);
        Assert.Equal(25, occ.Right, 6);
        Assert.Equal(25, occ.Bottom, 6);
    }

    /// <summary>
    /// 第 81 棒改口：文本被抻出纸，<strong>Core 不再报 Error</strong>——字面拉伸绕着走的是那条虚拟排版盒，
    /// 而 Core 量不了字，判它越界只会把"能拖能摆"变成"存不了盘"（用户那份 AI 建议版式当场撞在这）。
    /// 接手这份保护的两处都能真量墨迹：编辑器清单里按样例墨迹量的 Warning（App.Tests 的
    /// <c>NoWrapLongValueRunsOffThePaperAndWarnsButSaves</c>），与 ④⑤ 步按真数据量的出纸闸
    /// （<c>RealDataOverflowIsCountedForThePrintGate</c>）。
    /// </summary>
    [Fact]
    public void TextStretchedOffThePaperIsLeftToTheInkChecks()
    {
        var template = Page(width: 100);
        var e = TextBox(70, 10, 20, 6, 12);    // 原视觉 right=90，不越界
        Assert.False(TemplateValidator.Validate(template).HasError());
        e.TextScaleX = 3;                       // 视觉宽 60，中心 80 → right=110 探出
        template.Elements.Add(e);

        Assert.False(TemplateValidator.Validate(template).HasError(),
            "文本的排版盒不许再当越界判据：那是虚拟对齐基准，出纸的是墨迹");
    }

    [Fact]
    public void RotatedElementPokingOffTopIsAnError()
    {
        var template = Page(width: 100, height: 80);
        // 第 81 棒：夹具换成矩形——旋转对任何元素都成立，而文本自第 81 棒起不由 Core 判越界（量不了字）。
        // 这半边安全网一寸不许松：转出纸的元素会被刀模裁掉，属于"会印错"那一类。
        var e = Box(60, 5, 30, 6);              // 中心 (75,8)，不旋转时 right=90、top=5，都在纸内
        Assert.False(TemplateValidator.Validate(template).HasError());
        e.RotationDeg = 90;                      // 转后占 6 宽 30 高 → top = 8-15 = -7 探出上边
        template.Elements.Add(e);

        Assert.True(TemplateValidator.Validate(template).HasError(), "转出的角探出纸顶要拦");
    }

    [Fact]
    public void BarcodeRefusesRotationAndStretch()
    {
        var template = Page();
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode, Text = "1234567", X = 5, Y = 5, Width = 40, Height = 12,
            RotationDeg = 15,
        });

        var issues = TemplateValidator.Validate(template);
        Assert.Contains(issues, i => i.Severity == IssueLevel.Error && i.Message.Contains("条码"));
    }

    [Fact]
    public void OldSchemaV3JsonReadsWithoutRotationOrStretch()
    {
        // v3 文件里根本没有这三个字段：从磁盘读回来必须落到"不转、不拉"的默认，不能报 0 之外的怪值。
        // 走 TemplateStore.ReadFile 这条真实读档路（ProfileStore.JsonOptions 是 internal，测试不直接反序列化）。
        var v3Json = """
        {"id":"user.old","name":"旧模板","widthMm":100,"heightMm":80,"paddingMm":4,"borderMm":0,"schemaVersion":3,"builtIn":false,
         "elements":[{"kind":0,"x":5,"y":5,"width":30,"height":6,"text":"{{ItemNo}}","fontSizePt":12}]}
        """;
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-b43-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "old.json");
            File.WriteAllText(file, v3Json);

            var (template, issues) = new TemplateStore(dir).ReadFile(file);

            Assert.NotNull(template);
            Assert.False(issues.HasError());
            var e = Assert.Single(template!.Elements);
            Assert.Equal(0, e.RotationDeg, 6);
            Assert.Equal(1, e.TextScaleX, 6);
            Assert.Equal(1, e.TextScaleY, 6);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GeometryTransformEmptyWhenNothingApplied()
    {
        Assert.Equal(string.Empty, LabelGou.Core.Interop.Svg.SvgBuilder.GeometryTransform(20, 15, 0, 1, 1));
    }

    [Fact]
    public void GeometryTransformWrapsScaleInCenterTranslates()
    {
        var t = LabelGou.Core.Interop.Svg.SvgBuilder.GeometryTransform(20, 15, 0, 2, 1);
        Assert.StartsWith("translate(20,15)", t);
        Assert.Contains("scale(2,1)", t);
        Assert.EndsWith("translate(-20,-15)", t);
        Assert.DoesNotContain("rotate", t);
    }

    // ---------- 吸附 ----------

    [Fact]
    public void SnapToLabelCenterLine()
    {
        var template = Page();
        template.Elements.Add(Box(39.5, 10, 20, 6));

        var result = EditGeometry.Snap(template, 0, 39.5, 10, new SnapOptions { SnapToGrid = false });

        Assert.Equal(40, result.X, 6);
        var guide = Assert.Single(result.Guides);
        Assert.True(guide.Vertical);
        Assert.Equal(GuideSource.LabelCenter, guide.Source);
    }

    [Fact]
    public void SnapToPaddingLine()
    {
        var template = Page();
        template.Elements.Add(Box(4.4, 30, 20, 6));

        var result = EditGeometry.Snap(template, 0, 4.4, 30, new SnapOptions { SnapToGrid = false });

        Assert.Equal(4, result.X, 6);
        Assert.Equal(GuideSource.Padding, Assert.Single(result.Guides).Source);
    }

    [Fact]
    public void SnapToNeighbourEdge()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        template.Elements.Add(Box(30.2, 55, 20, 8));

        var result = EditGeometry.Snap(template, 1, 30.2, 55, new SnapOptions { SnapToGrid = false });

        Assert.Equal(30, result.X, 6);
        Assert.Equal(55, result.Y, 6);
        Assert.Equal(GuideSource.Neighbor, Assert.Single(result.Guides).Source);
    }

    [Fact]
    public void GridRoundingWhenNothingIsNear()
    {
        var template = Page();
        template.Elements.Add(Box(10.2, 60.3, 20, 6));

        var result = EditGeometry.Snap(template, 0, 10.2, 60.3, new SnapOptions { GridStepMm = 1 });

        Assert.Equal(10, result.X, 6);
        Assert.Equal(60, result.Y, 6);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void SnappedPositionIsStillClampedInsideThePaper()
    {
        var template = Page();
        template.Elements.Add(Box(90, 10, 10, 10));
        template.Elements.Add(Box(89.6, 40, 15, 6));

        var result = EditGeometry.Snap(template, 1, 89.6, 40, new SnapOptions { SnapToGrid = false });

        Assert.Equal(85, result.X, 6);
        Assert.True(result.X + 15 <= template.WidthMm + EditGeometry.HitToleranceMm);
    }

    [Fact]
    public void SnapCanBeTurnedOff()
    {
        var template = Page();
        template.Elements.Add(Box(39.5, 10, 20, 6));

        var result = EditGeometry.Snap(template, 0, 39.5, 10, SnapOptions.None);

        Assert.Equal(39.5, result.X, 6);
        Assert.Empty(result.Guides);
    }

    // ---------- 对齐与层级 ----------

    [Theory]
    [InlineData(AlignHorizontal.Left, 0)]
    [InlineData(AlignHorizontal.Center, 40)]
    [InlineData(AlignHorizontal.Right, 80)]
    public void AlignHorizontallyAgainstTheLabel(AlignHorizontal horizontal, double expectedX)
    {
        var template = Page();
        template.Elements.Add(Box(15, 10, 20, 8));

        EditGeometry.AlignToLabel(template, 0, horizontal, AlignVertical.Top);

        Assert.Equal(expectedX, template.Elements[0].X, 6);
        Assert.Equal(0, template.Elements[0].Y, 6);
    }

    [Fact]
    public void AlignUsesBoundingBoxForLines()
    {
        var template = Page();
        template.Elements.Add(Line(10, 70, 50, 74));

        EditGeometry.AlignToLabel(template, 0, AlignHorizontal.Right, AlignVertical.Top);

        Assert.Equal(60, template.Elements[0].X, 6);
        Assert.Equal(100, template.Elements[0].X2, 6);
        Assert.Equal(0, template.Elements[0].Y, 6);
        Assert.Equal(4, template.Elements[0].Y2, 6);
    }

    [Fact]
    public void SnapToPaddingMovesIntoTheContentArea()
    {
        var template = Page(padding: 6);
        template.Elements.Add(Box(0, 0, 20, 8));

        EditGeometry.SnapToPadding(template, 0, AlignHorizontal.Right, AlignVertical.Bottom);

        Assert.Equal(74, template.Elements[0].X, 6);
        Assert.Equal(66, template.Elements[0].Y, 6);
    }

    [Fact]
    public void LayerOrderMovesTheElement()
    {
        var template = Page();
        var first = Box(5, 5, 10, 10);
        var second = Box(20, 20, 10, 10);
        var third = Box(35, 35, 10, 10);
        template.Elements.AddRange(new[] { first, second, third });

        Assert.Equal(1, EditGeometry.MoveLayer(template, 0, 1));
        Assert.Same(second, template.Elements[0]);
        Assert.Same(first, template.Elements[1]);

        // 现在列表是 [second, first, third]：把最上面的 second 再置底
        Assert.Equal(2, EditGeometry.BringToFront(template, 0));
        Assert.Same(second, template.Elements[^1]);

        Assert.Equal(0, EditGeometry.SendToBack(template, 2));
        Assert.Same(second, template.Elements[0]);
    }

    [Fact]
    public void BadIndexReportsReadableError()
    {
        var template = Page();
        template.Elements.Add(Box(5, 5, 10, 10));

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => EditGeometry.MoveBy(template, 7, 1, 1));
        Assert.Contains("超出范围", error.Message);
    }

    // ---------- 撤销 / 重做 ----------

    [Fact]
    public void UndoRestoresTheValueBeforeTheChange()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        history.Capture(template);
        template.Elements[0].X = 55;
        var undone = history.Undo(template);

        Assert.True(undone);
        Assert.Equal(10, template.Elements[0].X, 6);
    }

    [Fact]
    public void SnapshotsAreDetachedFromTheWorkingTemplate()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        history.Capture(template);
        template.Elements[0].X = 55;
        template.Elements[0].Text = "改了也不该影响快照";
        history.Undo(template);

        Assert.Equal(10, template.Elements[0].X, 6);
        Assert.Null(template.Elements[0].Text);
    }

    [Fact]
    public void RedoBringsTheChangeBack()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        history.Capture(template);
        template.Elements[0].X = 55;
        history.Undo(template);

        Assert.True(history.Redo(template));
        Assert.Equal(55, template.Elements[0].X, 6);
    }

    [Fact]
    public void NewChangeClearsTheRedoStack()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        history.Capture(template);
        template.Elements[0].X = 20;
        history.Undo(template);
        history.Capture(template);

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void HistoryStopsAtTheLimitAndDropsTheOldest()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory(limit: 3);

        for (var i = 1; i <= 5; i++)
        {
            history.Capture(template);
            template.Elements[0].X = i * 5;
        }

        Assert.Equal(3, history.UndoCount);
        history.Undo(template);
        Assert.Equal(20, template.Elements[0].X, 6);
    }

    [Fact]
    public void UndoOnEmptyStackChangesNothing()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        Assert.False(history.Undo(template));
        Assert.False(history.Redo(template));
        Assert.Equal(10, template.Elements[0].X, 6);
    }

    [Fact]
    public void CopyIntoKeepsTheSameInstanceForTheUi()
    {
        var source = Page();
        source.Elements.Add(Box(12, 13, 20, 8));
        var target = Page();
        target.Elements.Add(Box(99, 99, 5, 5));

        source.CopyInto(target);

        Assert.Equal("user.test", target.Id);
        Assert.Single(target.Elements);
        Assert.Equal(12, target.Elements[0].X, 6);
        Assert.NotSame(source.Elements[0], target.Elements[0]);
    }

    // ---------- 工厂 ----------

    [Fact]
    public void BlankTemplatePassesValidation()
    {
        var template = TemplateFactory.Blank("我的唛头");

        Assert.False(TemplateValidator.Validate(template).HasError());
        Assert.Equal(4, template.Elements.Count);
        Assert.Equal("我的唛头", template.Name);
        Assert.False(template.BuiltIn);
    }

    [Fact]
    public void BlankTemplateHonoursRequestedSize()
    {
        var template = TemplateFactory.Blank("小标签", 60, 40);

        Assert.Equal(60, template.WidthMm);
        Assert.Equal(40, template.HeightMm);
        Assert.False(TemplateValidator.Validate(template).HasError());
        foreach (var element in template.Elements)
        {
            var box = EditGeometry.BoxOf(element);
            Assert.True(box.X + box.Width <= 60 + TemplateValidator.ToleranceMm, $"{element.Kind} 右侧越界");
            Assert.True(box.Y + box.Height <= 40 + TemplateValidator.ToleranceMm, $"{element.Kind} 下方越界");
        }
    }

    [Fact]
    public void CopyOfBuiltInIsAnIndependentUserTemplate()
    {
        var source = BuiltInTemplates.All().First();
        var copy = TemplateFactory.CopyOf(source, "客户 A 专用");

        Assert.False(copy.BuiltIn);
        Assert.StartsWith("user.", copy.Id);
        Assert.NotSame(source.Elements[0], copy.Elements[0]);

        copy.Elements[0].X += 5;
        Assert.NotEqual(copy.Elements[0].X, source.Elements[0].X);
    }

    [Fact]
    public void NewElementsCarryPrintableDefaults()
    {
        Assert.False(TemplateValidator.Validate(new LabelTemplate
        {
            Elements =
            {
                TemplateFactory.NewText("{{GrossWeight}}", 4, 4, 30, 6),
                TemplateFactory.NewLine(4, 12, 60, 12),
                TemplateFactory.NewRect(4, 16, 30, 10),
            },
        }).HasError());

        Assert.Equal(0.35, TemplateFactory.NewLine(0, 0, 10, 0).ThicknessMm, 6);
        Assert.True(TemplateFactory.NewRect(0, 0, 0.1, 0.1).Width >= EditGeometry.MinSideMm);
    }

    [Fact]
    public void AddedElementAvoidsLandingOnTopOfOthers()
    {
        var template = Page();
        template.Elements.Add(Box(4, 4, 40, 10));

        var index = TemplateFactory.AddElement(template, TemplateFactory.NewText("{{Origin}}", 4, 4, 30, 6), 4, 4);

        Assert.NotNull(index);
        Assert.False(index!.Overlapped, "纸面还有空位时不该叠上去");
        var added = EditGeometry.BoxOf(template.Elements[index.Index]);
        var occupied = EditGeometry.BoxOf(template.Elements[0]);
        Assert.False(TemplateFactory.Overlaps(occupied, added), "新建元素压住了已有的，用户会以为没加上");
    }

    [Fact]
    public void AddedElementStaysOnThePaper()
    {
        var template = Page(width: 40, height: 30);

        var index = TemplateFactory.AddElement(template, TemplateFactory.NewRect(0, 0, 30, 20), 0, 0);

        var box = EditGeometry.BoxOf(template.Elements[index!.Index]);
        Assert.True(box.X + box.Width <= 40 + TemplateValidator.ToleranceMm);
        Assert.True(box.Y + box.Height <= 30 + TemplateValidator.ToleranceMm);
    }

    /// <summary>
    /// 第 42 棒翻掉的老规矩：原来叫 <c>FullPaperRefusesMoreElements</c>，纸面没空位就返回 null（不给加），
    /// 界面还把这个失败报成「一张标签最多 80 个元素」。用户对着 AI 排的四行版式点「加文字」永远失败，
    /// 而屏幕上明明是一大片空白 —— 因为 <see cref="RowLayoutSpec.Build"/> 出的每行都是全宽行带，
    /// 四行铺满后行间只剩 2mm 缝，小于新元素 6mm 高加呼吸间距，全纸面找不到落点。
    /// <para>现在的口径与 CDR/PPT 一致：照加，叠上去，并如实告知「叠放了」，让用户自己拖开。</para>
    /// </summary>
    [Fact]
    public void FullPaperStillAddsButReportsOverlap()
    {
        var template = Page();
        template.Elements.Add(Box(0, 0, 100, 80));

        var added = TemplateFactory.AddElement(template, TemplateFactory.NewRect(0, 0, 30, 20), 4, 4);

        Assert.NotNull(added);
        Assert.True(added!.Overlapped, "叠上去了要说清是叠放，界面才不会静默");
        Assert.Equal(1, added.Index);
        // 叠放也必须落在纸内：越界会让校验器报 Error 存不了盘，那不是提醒是死路。
        var box = EditGeometry.BoxOf(template.Elements[added.Index]);
        Assert.True(box.X + box.Width <= 100 + TemplateValidator.ToleranceMm);
        Assert.True(box.Y + box.Height <= 80 + TemplateValidator.ToleranceMm);
        Assert.False(TemplateValidator.Validate(template).HasError());
    }

    /// <summary>
    /// AI 行式模板的真实形状：四行全宽行带铺满 140×100，行间距 2mm。
    /// 用户报的「加不进东西」就是这个形状下必然发生的，所以拿它当钉子最有说服力。
    /// </summary>
    [Fact]
    public void AiRowLayoutTemplateStillAcceptsNewText()
    {
        var spec = new RowLayoutSpec { WidthMm = 140, HeightMm = 100, PaddingMm = 5, GapMm = 2 };
        spec.Rows.Add(new RowSpec { Content = "JP" });
        spec.Rows.Add(new RowSpec { Content = "{{col:ITEM NO}}" });
        spec.Rows.Add(new RowSpec { Content = "{{col:QTY}}" });
        spec.Rows.Add(new RowSpec { Content = "MADE IN CHINA" });
        var template = spec.Build();
        Assert.NotNull(template);
        Assert.Equal(4, template!.Elements.Count);

        var added = TemplateFactory.AddFieldText(template, "ItemNo");

        Assert.NotNull(added);
        Assert.Equal(4, added!.Index);
        Assert.Equal("{{ItemNo}}", template.Elements[added.Index].Text);
        Assert.False(TemplateValidator.Validate(template).HasError(), "叠放不许把模板改成存不进库的样子");
    }

    [Fact]
    public void ElementCountCapIsEnforced()
    {
        var template = Page();
        for (var i = 0; i < TemplateValidator.MaxElements; i++)
        {
            template.Elements.Add(Box(0, 0, 2, 2));
        }

        // 上限是 AddElement 唯一一种失败：null 只表示"真到 80 个了"，不再兼表"没空位"。
        Assert.Null(TemplateFactory.AddElement(template, TemplateFactory.NewRect(0, 0, 2, 2), 0, 0));
    }

    [Fact]
    public void DuplicateIsOffsetAndDetached()
    {
        var template = Page();
        var source = Box(10, 10, 20, 8);
        source.Text = "{{PoNumber}}";
        template.Elements.Add(source);

        var copy = TemplateFactory.Duplicate(template, 0, 2);

        Assert.Equal(12, copy.X, 6);
        Assert.NotSame(template.Elements[0], copy);
        copy.X = 99;
        Assert.Equal(10, template.Elements[0].X, 6);
    }

    [Fact]
    public void FieldTextCarriesThePlaceholderSyntax()
    {
        var template = Page();

        var index = TemplateFactory.AddFieldText(template, "NetWeight");

        Assert.NotNull(index);
        Assert.Equal("{{NetWeight}}", template.Elements[index!.Index].Text);
        Assert.False(TemplateValidator.Validate(template).HasError());
    }

    // ---------- 模板库配合编辑器的两条新入口 ----------

    [Fact]
    public void FindFileLocatesSavedUserTemplateOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "labelgou-m4-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new TemplateStore(directory);
            Assert.Null(store.FindFileFor(BuiltInTemplates.IdStandard));

            var template = TemplateFactory.Blank("找得到的模板");
            var (saved, fileName, _) = store.Save(template);

            Assert.True(saved);
            var found = store.FindFileFor(template.Id);
            Assert.NotNull(found);
            Assert.Equal(fileName, Path.GetFileName(found!));
        }
        finally
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExportedJsonReadsBackIdentically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "labelgou-m4-" + Guid.NewGuid().ToString("N")[..8]);
        var source = TemplateFactory.Blank("导出再导入");
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "export.json");
            new TemplateStore(directory).ExportFile(source, path);

            var (readBack, issues) = new TemplateStore(directory).ReadFile(path);

            Assert.False(issues.HasError());
            Assert.NotNull(readBack);
            Assert.Equal(source.Elements.Count, readBack!.Elements.Count);
            Assert.Equal(source.Elements[0].X, readBack.Elements[0].X, 6);
            Assert.False(readBack.BuiltIn);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
