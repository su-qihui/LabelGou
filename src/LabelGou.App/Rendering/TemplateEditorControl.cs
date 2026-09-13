using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LabelGou.App.ViewModels;
using LabelGou.Core.Editing;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// B 类模板编辑器的画布：<strong>只负责像素与鼠标事件，一切几何判断都交给 Core</strong>
/// （<see cref="EditGeometry"/> + <see cref="TemplateEditorViewModel"/>）。
/// <para>
/// 版面本体仍由 <see cref="LabelRenderer"/> 画（与预览/打印/PDF 同一出口，§五-22），
/// 本控件只在其上叠加网格、元素框、句柄与吸附辅助线这些"编辑期才存在"的东西。
/// </para>
/// </summary>
public sealed class TemplateEditorControl : FrameworkElement
{
    private const double MarginDiu = 18;
    private const double HandlePixelSize = 8;

    private static readonly Pen BoxPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 60, 130, 200)), 0.8));
    private static readonly Pen SelectedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1.4));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(70, 120, 120, 120)), 0.5));
    private static readonly Pen PaddingPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(120, 200, 140, 40)), 0.7) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) });
    private static readonly Pen GuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 40, 90)), 0.9));
    private static readonly Pen BorderPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(170, 170, 170)), 1));
    private static readonly Brush HandleBrush = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(70, 200, 60, 60)));

    private TemplateEditorViewModel? _vm;
    private double _zoom = 1;
    private bool _zoomExplicit;
    private (double X, double Y) _origin;
    private bool _dragging;

    public TemplateEditorControl()
    {
        Focusable = true;
        SnapsToDevicePixels = false;
        Cursor = Cursors.Arrow;
        // FrameworkElement 没有可重写的 OnDataContextChanged，只能挂事件
        DataContextChanged += (_, _) => HookViewModel();
        HookViewModel();
    }

    private void HookViewModel()
    {
        var vm = DataContext as TemplateEditorViewModel;
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm is not null) _vm.CanvasChanged -= OnCanvasChanged;
        _vm = vm;
        _zoomExplicit = false;
        if (_vm is not null) _vm.CanvasChanged += OnCanvasChanged;
        InvalidateMeasure();
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    private void OnCanvasChanged() => Dispatcher.Invoke(() => { InvalidateVisual(); InvalidateMeasure(); });

    // ---------- 尺寸与坐标换算 ----------

    protected override Size MeasureOverride(Size availableSize)
    {
        var vm = _vm;
        if (vm is null) return new Size(400, 300);

        var labelWidth = Mm.ToDiu(vm.Template.WidthMm);
        var labelHeight = Mm.ToDiu(vm.Template.HeightMm);
        if (!_zoomExplicit && labelWidth > 0 && labelHeight > 0)
        {
            _zoom = LabelRenderer.FitZoom(vm.Template.WidthMm, vm.Template.HeightMm,
                Math.Max(60, availableSize.Width - 2 * MarginDiu),
                Math.Max(60, availableSize.Height - 2 * MarginDiu),
                maxZoom: 6);
        }

        var width = labelWidth * _zoom;
        var height = labelHeight * _zoom;
        var needed = new Size(width + 2 * MarginDiu, height + 2 * MarginDiu);
        if (double.IsInfinity(availableSize.Width)) return needed;
        if (double.IsInfinity(availableSize.Height)) return needed;
        return new Size(Math.Max(needed.Width, availableSize.Width), Math.Max(needed.Height, availableSize.Height));
    }

    /// <summary>标签左上角在画布上的位置（设备无关单位）。</summary>
    private (double X, double Y) Origin(Size size)
    {
        var vm = _vm;
        if (vm is null) return (MarginDiu, MarginDiu);
        var width = Mm.ToDiu(vm.Template.WidthMm) * _zoom;
        var height = Mm.ToDiu(vm.Template.HeightMm) * _zoom;
        var x = Math.Max(MarginDiu, (size.Width - width) / 2);
        var y = Math.Max(MarginDiu, (size.Height - height) / 2);
        _origin = (x, y);
        return _origin;
    }

    private double ToMmX(double diuX) => Mm.FromDiu((diuX - _origin.X) / _zoom);
    private double ToMmY(double diuY) => Mm.FromDiu((diuY - _origin.Y) / _zoom);
    private double ToDiuX(double mm) => _origin.X + Mm.ToDiu(mm) * _zoom;
    private double ToDiuY(double mm) => _origin.Y + Mm.ToDiu(mm) * _zoom;

    /// <summary>句柄在屏幕上固定 8 像素，换算成毫米就是它的抓取半径。</summary>
    private double HandleRadiusMm => Mm.FromDiu(HandlePixelSize / _zoom / 2);

    // ---------- 绘制 ----------

    protected override void OnRender(DrawingContext dc)
    {
        var vm = _vm;
        var size = RenderSize;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 246, 248)), null, new Rect(0, 0, size.Width, size.Height));
        if (vm is null) return;

        var origin = Origin(size);
        var template = vm.Template;
        var widthDiu = Mm.ToDiu(template.WidthMm) * _zoom;
        var heightDiu = Mm.ToDiu(template.HeightMm) * _zoom;
        var labelRect = new Rect(origin.X, origin.Y, widthDiu, heightDiu);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 先铺白纸，再画网格——反过来画的话白底会把网格盖掉（肉眼只在产物图上发现得了）
        dc.DrawRectangle(LabelRenderer.LabelBackground, BorderPen, labelRect);
        if (vm.ShowGrid) DrawGrid(dc, labelRect, template, vm.GridStepMm);

        // 印出来长什么样：与预览/打印共用同一个画法
        if (vm.ShowPreviewText)
        {
            dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
            dc.PushTransform(new ScaleTransform(_zoom, _zoom));
            try
            {
                LabelRenderer.Draw(dc, vm.SampleLayout, 1, 0, 0, false, pixelsPerDip, drawBackground: false);
            }
            finally
            {
                dc.Pop();
                dc.Pop();
            }
        }

        if (template.PaddingMm > 0)
        {
            var padX = Mm.ToDiu(template.PaddingMm) * _zoom;
            dc.DrawRectangle(null, PaddingPen, new Rect(
                labelRect.Left + padX, labelRect.Top + padX,
                Math.Max(0, widthDiu - 2 * padX), Math.Max(0, heightDiu - 2 * padX)));
        }

        // 元素框（编辑期才有）
        foreach (var element in template.Elements)
        {
            var selected = ReferenceEquals(vm.SelectedRow?.Element, element);
            DrawElementBox(dc, element, selected, pixelsPerDip);
        }

        foreach (var guide in vm.ActiveGuides)
        {
            if (guide.Vertical)
            {
                var x = ToDiuX(guide.PositionMm);
                dc.DrawLine(GuidePen, new Point(x, ToDiuY(guide.FromMm)), new Point(x, ToDiuY(guide.ToMm)));
            }
            else
            {
                var y = ToDiuY(guide.PositionMm);
                dc.DrawLine(GuidePen, new Point(ToDiuX(guide.FromMm), y), new Point(ToDiuX(guide.ToMm), y));
            }
        }
    }

    private void DrawGrid(DrawingContext dc, Rect labelRect, LabelTemplate template, double stepMm)
    {
        var step = Math.Max(0.25, stepMm);
        for (var x = step; x < template.WidthMm - 1e-6; x += step)
        {
            var diu = ToDiuX(x);
            dc.DrawLine(GridPen, new Point(diu, labelRect.Top), new Point(diu, labelRect.Bottom));
        }
        for (var y = step; y < template.HeightMm - 1e-6; y += step)
        {
            var diu = ToDiuY(y);
            dc.DrawLine(GridPen, new Point(labelRect.Left, diu), new Point(labelRect.Right, diu));
        }
    }

    private void DrawElementBox(DrawingContext dc, TemplateElement element, bool selected, double pixelsPerDip)
    {
        // 第 44 棒：文本按 VM 量出的【墨迹盒】画（选中框贴着字走，不再框整条行带——用户圈的红框）；
        // 其余元素退回 VisualBoxOf（含拉伸的视觉盒）。量不到版面项时（隐藏/异常）也退回默认盒。
        // 转过的元素连框带句柄一起绕【排版盒中心】转——命中与句柄判据（ToLocal）锚的就是这个中心，
        // 画成轴对齐会出现"看得见句柄、点不中"的错位（43 棒遗留，这条一并收掉）。
        var box = _vm?.DisplayBoxOf(element) ?? EditGeometry.VisualBoxOf(element);
        var anchor = EditGeometry.BoxOf(element);
        var rotated = Math.Abs(element.RotationDeg) > 1e-6 && element.Kind != ElementKind.Line;
        if (rotated)
            dc.PushTransform(new RotateTransform(element.RotationDeg,
                ToDiuX(anchor.X + anchor.Width / 2), ToDiuY(anchor.Y + anchor.Height / 2)));
        try
        {
            var rect = new Rect(ToDiuX(box.X), ToDiuY(box.Y), Math.Max(1, Mm.ToDiu(box.Width) * _zoom), Math.Max(1, Mm.ToDiu(box.Height) * _zoom));
            var pen = selected ? SelectedPen : BoxPen;
            dc.DrawRectangle(null, pen, rect);

            if (!element.Visible)
            {
                dc.DrawRectangle(DimBrush, null, rect);
                dc.DrawText(new FormattedText("隐藏", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily(TemplateElement.DefaultFont), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    9, Brushes.Gray, pixelsPerDip), new Point(rect.Left + 2, rect.Top + 1));
            }

            if (!selected) return;
            if (element.Kind == ElementKind.Line)
            {
                // 画到一半的那条也要有节点方块（哪怕还没拖出柄）：CDR 就是点一下就看到点落住了。
                var drawing = _vm is { IsDrawingPath: true, PathIndex: >= 0 }
                    && ReferenceEquals(_vm.Template.Elements[_vm.PathIndex], element);
                if (CurveGeometry.IsCurved(element) || drawing)
                {
                    DrawCurveNodes(dc, element);
                    return;
                }
                DrawHandle(dc, new Point(ToDiuX(element.X), ToDiuY(element.Y)));
                DrawHandle(dc, new Point(ToDiuX(element.X2), ToDiuY(element.Y2)));
                return;
            }
            var midX = rect.X + rect.Width / 2;
            var midY = rect.Y + rect.Height / 2;
            DrawHandle(dc, rect.TopLeft);
            DrawHandle(dc, new Point(midX, rect.Top));
            DrawHandle(dc, rect.TopRight);
            DrawHandle(dc, new Point(rect.Right, midY));
            DrawHandle(dc, rect.BottomRight);
            DrawHandle(dc, new Point(midX, rect.Bottom));
            DrawHandle(dc, rect.BottomLeft);
            DrawHandle(dc, new Point(rect.Left, midY));
        }
        finally
        {
            if (rotated) dc.Pop();
        }
    }

    private void DrawHandle(DrawingContext dc, Point center)
    {
        var half = HandlePixelSize / 2;
        dc.DrawRectangle(HandleBrush, SelectedPen, new Rect(center.X - half, center.Y - half, HandlePixelSize, HandlePixelSize));
    }

    /// <summary>
    /// 选中曲线时画在画布上的节点与控制柄（照 CorelDRAW：**当前节点的方向线是带两端箭头的虚线**，
    /// 其余节点只画短柄线；当前节点实心、其他空心）。
    /// <para>为什么要箭头和虚线：用户上一轮的原话是"没有 CDR 的…预览效果就感觉很随机"——看不到切线走向，
    /// 就不知道这一拖会把上一段弯成什么样。方向线两端出头画箭头，正是 CDR 给的那点预判。</para>
    /// <para>尺寸按像素给（不随缩放变粗），位置一律经 <see cref="ToDiuX"/>/<see cref="ToDiuY"/>——
    /// 与命中判据同一套坐标，不会出现"看得见柄头、抓不到"。</para>
    /// </summary>
    private void DrawCurveNodes(DrawingContext dc, TemplateElement element)
    {
        var pts = CurveGeometry.NodesOf(element);
        var current = _vm?.CurrentNodeIndex ?? -1;
        for (var i = 0; i < pts.Count; i++)
        {
            var n = pts[i];
            var node = new Point(ToDiuX(n.X), ToDiuY(n.Y));
            if (i == current) DrawDirectionLine(dc, node, n);
            else
            {
                if (n.InX != 0 || n.InY != 0) DrawHandleLine(dc, node, new Point(ToDiuX(n.X + n.InX), ToDiuY(n.Y + n.InY)));
                if (n.OutX != 0 || n.OutY != 0) DrawHandleLine(dc, node, new Point(ToDiuX(n.X + n.OutX), ToDiuY(n.Y + n.OutY)));
            }
        }
        for (var i = 0; i < pts.Count; i++)
        {
            var at = new Point(ToDiuX(pts[i].X), ToDiuY(pts[i].Y));
            if (i == current) dc.DrawRectangle(HandleBrush, SelectedPen, new Rect(at.X - 4, at.Y - 4, 8, 8));
            else DrawHandle(dc, at);
        }
    }

    /// <summary>当前节点的方向线：虚线穿过两根柄、两头各多画一截并加箭头（CorelDRAW 画法）。</summary>
    private void DrawDirectionLine(DrawingContext dc, Point node, CurveNode n)
    {
        double ix = node.X + Mm.ToDiu(n.InX) * _zoom, iy = node.Y + Mm.ToDiu(n.InY) * _zoom;
        double ox = node.X + Mm.ToDiu(n.OutX) * _zoom, oy = node.Y + Mm.ToDiu(n.OutY) * _zoom;
        if (n.InX == 0 && n.InY == 0 && n.OutX == 0 && n.OutY == 0) return;
        if (n.InX == 0 && n.InY == 0) { (ix, iy) = (2 * node.X - ox, 2 * node.Y - oy); }
        else if (n.OutX == 0 && n.OutY == 0) { (ox, oy) = (2 * node.X - ix, 2 * node.Y - iy); }

        dc.DrawLine(DirectionPen, new Point(ix, iy), new Point(ox, oy));
        Arrow(dc, new Point(ox, oy), new Point(node.X, node.Y));
        Arrow(dc, new Point(ix, iy), new Point(node.X, node.Y));
        DrawHandleLine(dc, node, new Point(ox, oy));
        DrawHandleLine(dc, node, new Point(ix, iy));
    }

    /// <summary>箭头：沿 from→tip 的方向在尖端画两条短斜线（比攒一个 PathGeometry 轻，且不会随缩放变形）。</summary>
    private void Arrow(DrawingContext dc, Point tip, Point from)
    {
        var dx = tip.X - from.X;
        var dy = tip.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return;
        dx /= len;
        dy /= len;
        const float arm = 6f;
        const float spread = 0.42f;
        var basePoint = new Point(tip.X - dx * arm, tip.Y - dy * arm);
        var perp = new Point(-dy, dx);
        dc.DrawLine(DirectionPen, tip, new Point(basePoint.X + perp.X * arm * spread, basePoint.Y + perp.Y * arm * spread));
        dc.DrawLine(DirectionPen, tip, new Point(basePoint.X - perp.X * arm * spread, basePoint.Y - perp.Y * arm * spread));
    }

    private static readonly Pen HandleLinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 200)), 1));

    private static readonly Pen DirectionPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 200)), 1)
    {
        DashStyle = DashStyles.Dash,
    });

    private void DrawHandleLine(DrawingContext dc, Point from, Point to)
    {
        dc.DrawLine(HandleLinePen, from, to);
        const float r = 3f;
        dc.DrawEllipse(Brushes.White, HandleLinePen, to, r, r);
    }

    // ---------- 鼠标 ----------

    /// <summary>
    /// 按下这一次被谁吃掉了。<see cref="Handled"/> = 吃掉了但<strong>不进入拖动态</strong>
    /// （双击收尾、以及"元素已到上限"这类被拒）；<see cref="None"/> = 挑选工具，交给常规路径
    /// （选中/拖动/双击复位缩放）。
    /// </summary>
    internal enum ToolDown { None, Drag, Handled }

    /// <summary>
    /// 画布按下的<strong>唯一分派点</strong>。三种工具本来就互斥（同一个 <see cref="TemplateEditorViewModel.Tool"/>
    /// 字段的三个投影），所以这里用 switch 而不是嵌套 if——
    /// <strong>矩形那条从前被写成"在贝塞尔那个 if 块里再判一次 IsRectTool"，而两个开关互斥，于是永远进不去，
    /// 用户看到的就是"勾了矩形框、在画布上拖，什么都不画"</strong>（第 50 棒，VM 层 16 条测试全绿也照不出来）。
    /// </summary>
    internal static ToolDown TryToolDown(TemplateEditorViewModel vm, double xMm, double yMm, bool doubleClick, bool ctrl, bool shift = false)
    {
        switch (vm.Tool)
        {
            case TemplateEditorViewModel.EditorTool.Bezier:
                // 贝塞尔工具下双击是"这条画完了"（类 CDR），不是复位缩放——两件事抢同一个手势时，正在画的那条说了算。
                if (doubleClick)
                {
                    vm.FinishPath();
                    return ToolDown.Handled;
                }
                // 按下即落点：只点不拖＝尖角＝直线段；拖开＝这一点带柄＝刚画那一段跟着弯（与 CorelDRAW 同口径）。
                // Ctrl＝限制线条（CorelDRAW 贝塞尔工具自带文案就是这么写的），夹成水平或垂直。
                return vm.BeginPath(xMm, yMm, ctrl) ? ToolDown.Drag : ToolDown.Handled;

            case TemplateEditorViewModel.EditorTool.Rect:
            case TemplateEditorViewModel.EditorTool.Ellipse:
            case TemplateEditorViewModel.EditorTool.Polygon:
                // 形状工具（第 50 棒矩形、第 51 棒椭圆/多边形）：按下落一只，拖到哪算哪，松手定形——三种共用同一份机械。
                // 修饰键照 CorelDRAW 自带文案：Ctrl＝限制为圆/正方（「按住 Ctrl 键拖动可限制为圆形」），
                // Shift＝从中心绘制（「按住 Shift 键并拖动可从中心绘制」）。
                return vm.BeginShape(vm.Tool, xMm, yMm, shift, ctrl) ? ToolDown.Drag : ToolDown.Handled;

            default:
                return ToolDown.None;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var vm = _vm;
        if (vm is null) return;

        Focus();
        var point = e.GetPosition(this);
        // 修饰键在按下那一刻抓一次，整次拖拽用同一个判定——拖到一半才松开 Shift 不该中途换规则。
        // 口径 = 本机 CorelDRAW X4 实测（第 45 棒）：**Shift = 绕中心向四周**（角柄本来就等比，
        // 不需要键）；**Ctrl = 移动时锁水平或垂直**。44 棒把 Shift 实现成"等比"是错的，已翻案。
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (TryToolDown(vm, ToMmX(point.X), ToMmY(point.Y), e.ClickCount == 2,
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift))
        {
            case ToolDown.Drag:
                _dragging = true;
                CaptureMouse();
                e.Handled = true;
                return;

            case ToolDown.Handled:
                e.Handled = true;
                return;
        }

        // 双击 = 复位缩放（放在按下里而不是 OnMouseDown，否则会先起一次多余的拖动）
        if (e.ClickCount == 2)
        {
            _zoomExplicit = false;
            InvalidateMeasure();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var anchor = shift ? ResizeAnchor.Center : ResizeAnchor.Opposite;
        var lockAxis = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var mode = vm.BeginDrag(ToMmX(point.X), ToMmY(point.Y), HandleRadiusMm, anchor, lockAxis);
        if (mode == TemplateEditorViewModel.DragMode.None) return;

        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var vm = _vm;
        if (vm is null) return;

        var point = e.GetPosition(this);
        if (_dragging)
        {
            if (vm.IsShapeTool) vm.DragShape(ToMmX(point.X), ToMmY(point.Y));
            else if (vm.IsBezierTool) vm.DragPath(ToMmX(point.X), ToMmY(point.Y), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            else vm.DragTo(ToMmX(point.X), ToMmY(point.Y));
            return;
        }

        if (vm.IsBezierTool || vm.IsShapeTool)
        {
            Cursor = Cursors.Cross;
            return;
        }

        // 悬停与按下同一口径：命中按行带（宽容），句柄按墨迹盒（与画出来的框对齐）。
        var index = EditGeometry.TopmostAt(vm.Template, ToMmX(point.X), ToMmY(point.Y));
        Cursor = index < 0 ? Cursors.Arrow : CursorFor(vm, vm.Template.Elements[index], ToMmX(point.X), ToMmY(point.Y));
    }

    private Cursor CursorFor(TemplateEditorViewModel vm, TemplateElement element, double xMm, double yMm)
    {
        // 曲线的节点/柄头优先：抓到它们就给手型（与 BeginDrag 的命中顺序一致，不然"点得中却提示能拖"）。
        if (element.Kind == ElementKind.Line && CurveGeometry.IsCurved(element)
            && CurveGeometry.HandleAt(element, xMm, yMm, HandleRadiusMm) is not null) return Cursors.Hand;

        var handle = EditGeometry.HandleAt(element, xMm, yMm, HandleRadiusMm, vm.DisplayBoxOf(element));
        return handle switch
        {
            ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
            ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
            ResizeHandle.LineStart or ResizeHandle.LineEnd => Cursors.Cross,
            ResizeHandle.None => Cursors.SizeAll,
            _ => Cursors.SizeAll,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_vm is { IsShapeTool: true }) _vm.EndShape();
        else if (_vm is { IsBezierTool: true }) _vm.EndPathSegment();
        else _vm?.EndDrag();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var vm = _vm;
        if (vm is null) return;

        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        _zoom = Math.Clamp(_zoom * factor, 0.2, 12);
        _zoomExplicit = true;
        InvalidateMeasure();
        InvalidateVisual();
        e.Handled = true;
    }

    // ---------- 键盘 ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var vm = _vm;
        if (vm is null) return;

        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.Z)
        {
            vm.Undo();
            e.Handled = true;
            return;
        }
        if (control && (e.Key == Key.Y || e.Key == Key.G))
        {
            vm.Redo();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && vm.IsDrawingPath)
        {
            vm.FinishPath();
            e.Handled = true;
            return;
        }
        // 正在画一条曲线时，Esc / Delete 都是"这条不要了"：删掉元素而节点表还指着那个下标，下一个点会写歪。
        if (vm.IsDrawingPath && (e.Key == Key.Escape || e.Key == Key.Delete))
        {
            _dragging = false;
            ReleaseMouseCapture();
            vm.CancelPath();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _vm?.IsDrawingShape == true)
        {
            _dragging = false;
            ReleaseMouseCapture();
            _vm.CancelShape();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            vm.CancelDrag();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete)
        {
            if (vm.RemoveCommand.CanExecute(null)) vm.RemoveCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // 画到一半不认方向键微调：微调改的是元素本体，下一个点仍会按内存里那份节点表写回去，等于白挪。
        if (vm.IsDrawingPath && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            e.Handled = true;
            return;
        }

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1
            : control ? 2d
            : 0.5;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0d),
            Key.Right => (step, 0d),
            Key.Up => (0d, -step),
            Key.Down => (0d, step),
            _ => (0d, 0d),
        };
        if (dx == 0 && dy == 0) return;
        vm.Nudge(dx, dy);
        e.Handled = true;
    }

    /// <summary>标签左上角在画布上的位置与当前缩放（状态栏显示光标坐标、单测验算用）。</summary>
    public (double X, double Y) CurrentOrigin => _origin;

    public double CurrentZoom => _zoom;
}
