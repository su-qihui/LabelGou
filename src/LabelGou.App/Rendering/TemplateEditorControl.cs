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
        var box = EditGeometry.BoxOf(element);
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

    private void DrawHandle(DrawingContext dc, Point center)
    {
        var half = HandlePixelSize / 2;
        dc.DrawRectangle(HandleBrush, SelectedPen, new Rect(center.X - half, center.Y - half, HandlePixelSize, HandlePixelSize));
    }

    // ---------- 鼠标 ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var vm = _vm;
        if (vm is null) return;

        // 双击 = 复位缩放（放在按下里而不是 OnMouseDown，否则会先起一次多余的拖动）
        if (e.ClickCount == 2)
        {
            _zoomExplicit = false;
            InvalidateMeasure();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        Focus();
        var point = e.GetPosition(this);
        var mode = vm.BeginDrag(ToMmX(point.X), ToMmY(point.Y), HandleRadiusMm);
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
            vm.DragTo(ToMmX(point.X), ToMmY(point.Y));
            return;
        }

        var index = EditGeometry.TopmostAt(vm.Template, ToMmX(point.X), ToMmY(point.Y));
        Cursor = index < 0 ? Cursors.Arrow : CursorFor(vm.Template.Elements[index], ToMmX(point.X), ToMmY(point.Y));
    }

    private Cursor CursorFor(TemplateElement element, double xMm, double yMm)
    {
        var handle = EditGeometry.HandleAt(element, xMm, yMm, HandleRadiusMm);
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
        _vm?.EndDrag();
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
