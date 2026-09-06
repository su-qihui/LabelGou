using System.Windows;
using System.Windows.Media;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// 整版拼版预览控件：按毫米真实尺寸画出一页不干胶（多枚标签 + 裁切线 + 套准标记）。
/// <para>
/// 它只画 <see cref="SheetPlan"/> 给的几何，不做任何排布计算——所有坐标都来自
/// <see cref="ImpositionEngine"/>，因此预览里对得齐，M3 打印/PDF 就对得齐。
/// </para>
/// <para>
/// 标签内容是按需取的：通过 <see cref="LayoutProvider"/> 只解析当前页用到的那几枚，
/// 几千张标签的任务不会因为翻页而卡住。
/// </para>
/// </summary>
public sealed class SheetPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty PlanProperty = DependencyProperty.Register(
        nameof(Plan), typeof(SheetPlan), typeof(SheetPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PageIndexProperty = DependencyProperty.Register(
        nameof(PageIndex), typeof(int), typeof(SheetPreviewControl),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(SheetPreviewControl),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGuidesProperty = DependencyProperty.Register(
        nameof(ShowGuides), typeof(bool), typeof(SheetPreviewControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>当前页码（1 起）。</summary>
    public int PageIndex
    {
        get => (int)GetValue(PageIndexProperty);
        set => SetValue(PageIndexProperty, value);
    }

    /// <summary>整版方案（来自拼版引擎）。</summary>
    public SheetPlan? Plan
    {
        get => (SheetPlan?)GetValue(PlanProperty);
        set => SetValue(PlanProperty, value);
    }

    /// <summary>显示缩放倍数（1.0 = 真实毫米尺寸对应的 96DPI 尺寸）。</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, Math.Max(0.05, Math.Min(value, 12)));
    }

    /// <summary>是否叠加元素边框辅助线。</summary>
    public bool ShowGuides
    {
        get => (bool)GetValue(ShowGuidesProperty);
        set => SetValue(ShowGuidesProperty, value);
    }

    /// <summary>标签序号（1 起）→ 版面。返回 null 表示这一枚暂时不画（空位）。</summary>
    public Func<int, LabelLayout?>? LayoutProvider { get; set; }

    static SheetPreviewControl()
    {
        FocusableProperty.OverrideMetadata(typeof(SheetPreviewControl), new FrameworkPropertyMetadata(false));
    }

    private static readonly Pen PaperEdgePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 1));

    private static readonly Pen MarginPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(200, 200, 200)), 0.6)
        { DashStyle = DashStyles.Dash });

    private static readonly Pen CropPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(90, 90, 90)), 0.8));

    private static readonly Pen RegistrationPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 200)), 1));

    private static readonly Pen OutlinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(170, 170, 170)), 0.6)
        { DashStyle = DashStyles.Dot });

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var plan = Plan;
        if (plan is null) return new Size(300, 400);
        var scale = Scale();
        return new Size(
            Math.Max(1, Mm.ToDiu(plan.PageWidthMm) * scale),
            Math.Max(1, Mm.ToDiu(plan.PageHeightMm) * scale));
    }

    private double Scale() => Zoom <= 0 || double.IsPositiveInfinity(Zoom) ? 1 : Zoom;

    protected override void OnRender(DrawingContext dc)
    {
        var plan = Plan;
        var size = RenderSize;
        if (plan is null || size.Width <= 1 || size.Height <= 1) return;

        var scale = size.Width / Math.Max(0.001, Mm.ToDiu(plan.PageWidthMm));
        var paperW = Mm.ToDiu(plan.PageWidthMm) * scale;
        var paperH = Mm.ToDiu(plan.PageHeightMm) * scale;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 纸张
        dc.DrawRectangle(Brushes.White, PaperEdgePen, new Rect(0, 0, paperW, paperH));

        // 页边示意（只影响视觉，不参与落位计算）
        var marginRect = new Rect(
            Mm.ToDiu(plan.Spec.MarginLeftMm) * scale,
            Mm.ToDiu(plan.Spec.MarginTopMm) * scale,
            Math.Max(0, Mm.ToDiu(plan.Spec.UsableWidthMm) * scale),
            Math.Max(0, Mm.ToDiu(plan.Spec.UsableHeightMm) * scale));
        dc.DrawRectangle(null, MarginPen, marginRect);

        // 辅助线先画，让标签内容压在上面（印刷上角线本来就只露在标签外）
        foreach (var mark in ImpositionEngine.BuildMarks(plan.Spec, plan, Math.Max(1, PageIndex)))
        {
            var pen = mark.Kind switch
            {
                SheetMarkKind.RegistrationMark => RegistrationPen,
                SheetMarkKind.LabelOutline => OutlinePen,
                _ => CropPen,
            };
            dc.DrawLine(pen,
                new Point(Mm.ToDiu(mark.X1) * scale, Mm.ToDiu(mark.Y1) * scale),
                new Point(Mm.ToDiu(mark.X2) * scale, Mm.ToDiu(mark.Y2) * scale));
        }

        var provider = LayoutProvider;
        if (provider is null) return;

        foreach (var placement in plan.PlacementsOnPage(Math.Max(1, PageIndex)))
        {
            var layout = provider(placement.LabelIndex);
            if (layout is null) continue;

            var x = Mm.ToDiu(placement.X) * scale;
            var y = Mm.ToDiu(placement.Y) * scale;

            if (placement.Rotated)
            {
                LabelRenderer.DrawRotated(dc, layout, scale, x, y, ShowGuides, pixelsPerDip);
            }
            else
            {
                LabelRenderer.Draw(dc, layout, scale, x, y, ShowGuides, pixelsPerDip);
            }
        }
    }

    /// <summary>纸张内容变了（换页/换纸规/重算）时由外部调用，强制重画。</summary>
    public void RefreshPage() => InvalidateVisual();

    /// <summary>整版自适应缩放。</summary>
    public static double FitZoom(SheetPlan plan, double availableWidth, double availableHeight, double maxZoom = 4)
        => LabelRenderer.FitZoom(plan.PageWidthMm, plan.PageHeightMm, availableWidth, availableHeight, maxZoom);
}
