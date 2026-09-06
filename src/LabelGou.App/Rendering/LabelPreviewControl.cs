using System.Windows;
using System.Windows.Media;
using LabelGou.Core.Layout;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// 单标签预览控件：把 <see cref="LabelLayout"/>（毫米）按真实尺寸画出来。
/// <para>
/// 缩放只作用于"显示"，不改变任何毫米值——所以屏幕上量出来的比例和实际打印一致，
/// 这也是 M3 打印/PDF/图片导出能复用同一份 Layout 的原因。
/// </para>
/// <para>
/// 具体画法已抽到 <see cref="LabelRenderer"/>，整版拼版预览（M2）复用的是同一套，
/// 保证「单标签看到的」与「整版上的那一枚」永远一致。
/// </para>
/// </summary>
public sealed class LabelPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(LabelLayout), typeof(LabelPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(LabelPreviewControl),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGuidesProperty = DependencyProperty.Register(
        nameof(ShowGuides), typeof(bool), typeof(LabelPreviewControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public LabelLayout? Layout
    {
        get => (LabelLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>显示缩放倍数（1.0 = 按实际毫米尺寸显示）。</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, Math.Max(0.1, Math.Min(value, 12)));
    }

    /// <summary>是否画出各元素的边界辅助框（排版检查用）。</summary>
    public bool ShowGuides
    {
        get => (bool)GetValue(ShowGuidesProperty);
        set => SetValue(ShowGuidesProperty, value);
    }

    static LabelPreviewControl()
    {
        FocusableProperty.OverrideMetadata(typeof(LabelPreviewControl), new FrameworkPropertyMetadata(false));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = Layout;
        if (layout is null) return new Size(240, 180);

        var scale = Zoom <= 0 || double.IsPositiveInfinity(Zoom) ? 1 : Zoom;
        return new Size(
            Math.Max(1, Mm.ToDiu(layout.WidthMm) * scale),
            Math.Max(1, Mm.ToDiu(layout.HeightMm) * scale));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var layout = Layout;
        var size = RenderSize;
        if (layout is null || size.Width <= 1 || size.Height <= 1) return;

        var scale = size.Width / Math.Max(0.001, Mm.ToDiu(layout.WidthMm));
        LabelRenderer.Draw(dc, layout, scale, 0, 0, ShowGuides, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    /// <summary>按给定可用区域算出自适应缩放倍数。</summary>
    public static double FitZoom(LabelLayout layout, double availableWidth, double availableHeight, double maxZoom = 6)
        => LabelRenderer.FitZoom(layout.WidthMm, layout.HeightMm, availableWidth, availableHeight, maxZoom);
}
