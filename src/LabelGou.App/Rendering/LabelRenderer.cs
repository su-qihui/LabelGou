using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.Core.Layout;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// <see cref="LabelLayout"/> 的唯一画法（毫米 → 设备单位，纯绘制不改数据）。
/// <para>
/// 单标签预览、整版拼版预览、M3 的 PDF/图片导出都必须走这里，
/// 否则「所见即所得」会变成「预览一个样、印出来另一个样」。
/// </para>
/// </summary>
public static class LabelRenderer
{
    public static readonly Brush LabelBackground = Brushes.White;

    private static readonly Pen EdgePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(160, 160, 160)), 1));

    private static readonly Pen InkPen = Frozen(new Pen(Brushes.Black, 1));

    private static readonly Pen GuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(120, 170, 220)), 0.6));

    private static readonly Brush FlagBrush = Frozen(new SolidColorBrush(Color.FromRgb(198, 40, 40)));

    private static readonly Brush FlagBackground = Frozen(new SolidColorBrush(Color.FromArgb(28, 198, 40, 40)));

    private static readonly Brush TextBrush = Brushes.Black;

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    /// <summary>
    /// 把一张标签画在 <c>(offsetX, offsetY)</c>（设备单位）处，尺寸按 <paramref name="scale"/> 缩放。
    /// </summary>
    /// <param name="dc">目标绘制上下文。</param>
    /// <param name="layout">已解析的标签版面。</param>
    /// <param name="scale">显示缩放倍数（1 = 真实毫米尺寸对应的 96DPI 尺寸）。</param>
    /// <param name="offsetX">落点 X（设备单位）。</param>
    /// <param name="offsetY">落点 Y（设备单位）。</param>
    /// <param name="showGuides">是否叠加元素边框辅助线。</param>
    /// <param name="pixelsPerDip">当前 DPI 系数，FormattedText 需要。</param>
    /// <param name="drawBackground">是否先铺白底并描边（整版里每张都铺会盖掉拼版辅助线，故可选）。</param>
    public static void Draw(
        DrawingContext dc,
        LabelLayout layout,
        double scale,
        double offsetX,
        double offsetY,
        bool showGuides,
        double pixelsPerDip,
        bool drawBackground = true)
    {
        var widthDiu = Mm.ToDiu(layout.WidthMm) * scale;
        var heightDiu = Mm.ToDiu(layout.HeightMm) * scale;
        if (widthDiu <= 0 || heightDiu <= 0) return;

        var pushed = false;
        if (drawBackground)
        {
            dc.DrawRectangle(LabelBackground, EdgePen, new Rect(offsetX, offsetY, widthDiu, heightDiu));
        }

        if (Math.Abs(offsetX) > 0.01 || Math.Abs(offsetY) > 0.01)
        {
            dc.PushTransform(new TranslateTransform(offsetX, offsetY));
            pushed = true;
        }

        try
        {
            foreach (var item in layout.Items)
            {
                switch (item)
                {
                    case RectItem rect:
                        DrawRect(dc, rect, scale, showGuides);
                        break;
                    case LineItem line:
                        DrawLine(dc, line, scale);
                        break;
                    case TextItem text:
                        DrawText(dc, text, scale, showGuides, pixelsPerDip);
                        break;
                    case ImageItem image:
                        DrawImage(dc, image, scale);
                        break;
                }
            }
        }
        finally
        {
            if (pushed) dc.Pop();
        }
    }

    /// <summary>
    /// 旋转 90° 落位（拼版引擎判定省料时会用）。
    /// 标签自身的毫米坐标不变，只是整体顺时针转 90°，所以占位宽高互换。
    /// </summary>
    public static void DrawRotated(
        DrawingContext dc,
        LabelLayout layout,
        double scale,
        double offsetX,
        double offsetY,
        bool showGuides,
        double pixelsPerDip,
        bool drawBackground = true)
    {
        var heightDiu = Mm.ToDiu(layout.HeightMm) * scale;

        var group = new TransformGroup
        {
            Children =
            {
                new RotateTransform(90),
                new TranslateTransform(offsetX + heightDiu, offsetY),
            },
        };
        dc.PushTransform(group);
        try
        {
            Draw(dc, layout, scale, 0, 0, showGuides, pixelsPerDip, drawBackground);
        }
        finally
        {
            dc.Pop();
        }
    }

    private static void DrawRect(DrawingContext dc, RectItem rect, double scale, bool showGuides)
    {
        var r = new Rect(Mm.ToDiu(rect.X) * scale, Mm.ToDiu(rect.Y) * scale, Mm.ToDiu(rect.Width) * scale, Mm.ToDiu(rect.Height) * scale);
        var pen = new Pen(InkPen.Brush, Math.Max(0.6, Mm.ToDiu(rect.ThicknessMm) * scale));
        pen.Freeze();
        dc.DrawRectangle(null, pen, r);
        if (showGuides) dc.DrawRectangle(null, GuidePen, r);
    }

    private static void DrawLine(DrawingContext dc, LineItem line, double scale)
    {
        var p1 = new Point(Mm.ToDiu(line.X1) * scale, Mm.ToDiu(line.Y1) * scale);
        var p2 = new Point(Mm.ToDiu(line.X2) * scale, Mm.ToDiu(line.Y2) * scale);
        var pen = new Pen(InkPen.Brush, Math.Max(0.6, Mm.ToDiu(line.ThicknessMm) * scale));
        pen.Freeze();
        dc.DrawLine(pen, p1, p2);
    }

    private static void DrawText(DrawingContext dc, TextItem text, double scale, bool showGuides, double pixelsPerDip)
    {
        var box = new Rect(Mm.ToDiu(text.X) * scale, Mm.ToDiu(text.Y) * scale, Mm.ToDiu(text.Width) * scale, Mm.ToDiu(text.Height) * scale);

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

        if (showGuides) dc.DrawRectangle(null, GuidePen, box);
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

    private static void DrawImage(DrawingContext dc, ImageItem image, double scale)
    {
        try
        {
            var bitmap = new BitmapImage(new Uri(image.AbsolutePath, UriKind.Absolute));
            dc.DrawImage(bitmap, new Rect(
                Mm.ToDiu(image.X) * scale, Mm.ToDiu(image.Y) * scale,
                Mm.ToDiu(image.Width) * scale, Mm.ToDiu(image.Height) * scale));
        }
        catch (Exception)
        {
            // 图片读不出不影响其余版面；缺图在 M7 的印前检查里单独告警
        }
    }

    /// <summary>系统里找不到指定字体时退回默认字体（打印店机器字体不可控）。</summary>
    public static FontFamily SafeFontFamily(string family)
    {
        var fallback = new FontFamily(TemplateElement.DefaultFont + ", SimSun, sans-serif");
        if (string.IsNullOrWhiteSpace(family)) return fallback;

        try
        {
            foreach (var existing in Fonts.SystemFontFamilies)
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

    /// <summary>按给定可用区域算出自适应缩放倍数（毫米尺寸通用，单标签与整版都用它）。</summary>
    public static double FitZoom(double widthMm, double heightMm, double availableWidth, double availableHeight, double maxZoom = 6)
    {
        var wDiu = Mm.ToDiu(widthMm);
        var hDiu = Mm.ToDiu(heightMm);
        if (wDiu <= 0 || hDiu <= 0) return 1;
        var zoom = Math.Min(availableWidth / wDiu, availableHeight / hDiu);
        if (double.IsNaN(zoom) || double.IsInfinity(zoom)) zoom = 1;
        return Math.Max(0.05, Math.Min(zoom, maxZoom));
    }
}
