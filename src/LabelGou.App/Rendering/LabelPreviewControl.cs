using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.Core.Layout;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// 标签预览控件：把 <see cref="LabelLayout"/>（毫米）按真实尺寸画出来。
/// <para>
/// 缩放只作用于"显示"，不改变任何毫米值——所以屏幕上量出来的比例和实际打印一致，
/// 这也是 M3 打印/PDF/图片导出能复用同一份 Layout 的原因。
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

    private static readonly Brush LabelBackground = Brushes.White;
    private static readonly Pen LabelEdgePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(160, 160, 160)), 1));
    private static readonly Pen InkPen = Frozen(new Pen(Brushes.Black, 1));
    private static readonly Pen GuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(120, 170, 220)), 0.6));
    private static readonly Brush FlagBrush = Frozen(new SolidColorBrush(Color.FromRgb(198, 40, 40)));
    private static readonly Brush FlagBackground = Frozen(new SolidColorBrush(Color.FromArgb(28, 198, 40, 40)));
    private static readonly Brush TextBrush = Brushes.Black;

    static LabelPreviewControl()
    {
        FocusableProperty.OverrideMetadata(typeof(LabelPreviewControl), new FrameworkPropertyMetadata(false));
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = Layout;
        if (layout is null) return new Size(240, 180);

        var scale = ScaleFor(layout);
        return new Size(
            Math.Max(1, Mm.ToDiu(layout.WidthMm) * scale),
            Math.Max(1, Mm.ToDiu(layout.HeightMm) * scale));
    }

    private double ScaleFor(LabelLayout layout)
    {
        if (double.IsPositiveInfinity(Zoom) || Zoom <= 0) return 1;
        // Zoom 以"1 = 实际尺寸"为基准；AutoFit 由 ViewModel 折算成一个倍数传进来
        return Zoom;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var layout = Layout;
        var size = RenderSize;
        if (layout is null || size.Width <= 1 || size.Height <= 1) return;

        dc.DrawRectangle(LabelBackground, LabelEdgePen, new Rect(0, 0, size.Width, size.Height));

        var scale = size.Width / Math.Max(0.001, Mm.ToDiu(layout.WidthMm));
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var item in layout.Items)
        {
            switch (item)
            {
                case RectItem rect:
                    DrawRect(dc, rect, scale);
                    break;
                case LineItem line:
                    DrawLine(dc, line, scale);
                    break;
                case TextItem text:
                    DrawText(dc, text, scale, pixelsPerDip);
                    break;
                case ImageItem image:
                    DrawImage(dc, image, scale);
                    break;
            }
        }
    }

    private void DrawRect(DrawingContext dc, RectItem rect, double scale)
    {
        var r = new Rect(ToX(rect.X, scale), ToY(rect.Y, scale), ToLen(rect.Width, scale), ToLen(rect.Height, scale));
        var pen = new Pen(InkPen.Brush, Math.Max(0.6, ToLen(rect.ThicknessMm, scale)));
        pen.Freeze();
        dc.DrawRectangle(null, pen, r);
        if (ShowGuides) dc.DrawRectangle(null, GuidePen, r);
    }

    private void DrawLine(DrawingContext dc, LineItem line, double scale)
    {
        var p1 = new Point(ToX(line.X1, scale), ToY(line.Y1, scale));
        var p2 = new Point(ToX(line.X2, scale), ToY(line.Y2, scale));
        var pen = new Pen(InkPen.Brush, Math.Max(0.6, ToLen(line.ThicknessMm, scale)));
        pen.Freeze();
        dc.DrawLine(pen, p1, p2);
    }

    private void DrawText(DrawingContext dc, TextItem text, double scale, double pixelsPerDip)
    {
        var box = new Rect(ToX(text.X, scale), ToY(text.Y, scale), ToLen(text.Width, scale), ToLen(text.Height, scale));

        var family = SafeFontFamily(text.FontFamily);
        var typeface = new Typeface(family, FontStyles.Normal, text.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var foreground = text.Flagged ? FlagBrush : TextBrush;

        if (text.Flagged) dc.DrawRectangle(FlagBackground, null, box);

        // 字号：磅 → DIU（1pt = 96/72 DIU），再乘显示缩放
        var emSize = Mm.ToDiu(Mm.PointToMm(text.FontSizePt)) * scale;
        var minEmSize = emSize * 0.62;

        FormattedText? formatted = null;
        var guard = 0;
        while (guard++ < 24)
        {
            formatted = BuildFormatted(text, typeface, foreground, emSize, box.Width, pixelsPerDip);
            if (!text.ShrinkToFit || formatted.Height <= box.Height + 0.5 || emSize <= minEmSize) break;
            emSize *= 0.92;
        }
        if (formatted is null) return;

        // 垂直居中；水平由对齐决定（FormattedText 已按对齐排布，这里只需处理宽度未占满的情况）
        var offsetY = box.Top + Math.Max(0, (box.Height - formatted.Height) / 2);
        dc.DrawText(formatted, new Point(box.Left, offsetY));

        if (ShowGuides) dc.DrawRectangle(null, GuidePen, box);
    }

    private static FormattedText BuildFormatted(
        TextItem text, Typeface typeface, Brush foreground, double emSize, double maxWidth, double pixelsPerDip)
    {
        // WPF 的 FormattedText 没有 TextWrapping 成员（属性/构造参都没有），
        // 换行完全由 MaxTextWidth 驱动；行数上限用 MaxLineCount + Trimming。
        var formatted = new FormattedText(
            text.Content,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            emSize,
            foreground,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            TextAlignment = text.Align switch
            {
                HorizontalAlign.Center => TextAlignment.Center,
                HorizontalAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
            Trimming = TextTrimming.CharacterEllipsis,
        };

        if (text.MaxLines > 0) formatted.MaxLineCount = text.MaxLines;
        return formatted;
    }

    private void DrawImage(DrawingContext dc, ImageItem image, double scale)
    {
        try
        {
            var bitmap = new BitmapImage(new Uri(image.AbsolutePath, UriKind.Absolute));
            dc.DrawImage(bitmap, new Rect(ToX(image.X, scale), ToY(image.Y, scale), ToLen(image.Width, scale), ToLen(image.Height, scale)));
        }
        catch (Exception)
        {
            // 图片读不出不影响其余版面；缺图在 M7 的印前检查里单独告警
        }
    }

    private static FontFamily SafeFontFamily(string family)
    {
        var fallback = new FontFamily(TemplateElement.DefaultFont + ", SimSun, sans-serif");
        if (string.IsNullOrWhiteSpace(family)) return fallback;

        try
        {
            var names = Fonts.SystemFontFamilies;
            foreach (var existing in names)
            {
                if (string.Equals(existing.Source, family, StringComparison.OrdinalIgnoreCase))
                    return new FontFamily(family);
            }
        }
        catch (Exception)
        {
            // 枚举系统字体失败（字体服务异常）时用默认字体，不阻断预览
        }
        return fallback;
    }

    private static double ToX(double mm, double scale) => Mm.ToDiu(mm) * scale;

    private static double ToY(double mm, double scale) => Mm.ToDiu(mm) * scale;

    private static double ToLen(double mm, double scale) => Mm.ToDiu(mm) * scale;

    /// <summary>按给定可用区域算出自适应缩放倍数。</summary>
    public static double FitZoom(LabelLayout layout, double availableWidth, double availableHeight, double maxZoom = 6)
    {
        var wDiu = Mm.ToDiu(layout.WidthMm);
        var hDiu = Mm.ToDiu(layout.HeightMm);
        if (wDiu <= 0 || hDiu <= 0) return 1;
        var zoom = Math.Min(availableWidth / wDiu, availableHeight / hDiu);
        if (double.IsNaN(zoom) || double.IsInfinity(zoom)) zoom = 1;
        return Math.Max(0.15, Math.Min(zoom, maxZoom));
    }
}
