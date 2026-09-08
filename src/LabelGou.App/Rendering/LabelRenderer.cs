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

    private static readonly Pen GuidePen = Frozen(new Pen(RenderRules.GuideInk, 0.6));

    private static readonly Pen ReferencePen = Frozen(new Pen(RenderRules.ReferenceTint, 0.8)
    {
        DashStyle = DashStyles.Dash,
    });

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
    /// <param name="target">落到哪儿去，只决定线宽保底（见 <see cref="RenderRules"/>）。</param>
    public static void Draw(
        DrawingContext dc,
        LabelLayout layout,
        double scale,
        double offsetX,
        double offsetY,
        bool showGuides,
        double pixelsPerDip,
        bool drawBackground = true,
        RenderTarget target = RenderTarget.Screen)
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
                        DrawRect(dc, rect, scale, showGuides, target);
                        break;
                    case LineItem line:
                        DrawLine(dc, line, scale, target);
                        break;
                    case TextItem text:
                        DrawText(dc, text, scale, showGuides, pixelsPerDip, target);
                        break;
                    case ImageItem image:
                        DrawImage(dc, image, scale);
                        break;
                    case VectorItem vector:
                        DrawVector(dc, vector, scale, pixelsPerDip);
                        break;
                    case BarcodeItem barcode:
                        DrawBarcode(dc, barcode, scale, showGuides, pixelsPerDip, target);
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
        bool drawBackground = true,
        RenderTarget target = RenderTarget.Screen)
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
            Draw(dc, layout, scale, 0, 0, showGuides, pixelsPerDip, drawBackground, target);
        }
        finally
        {
            dc.Pop();
        }
    }

    private static void DrawRect(DrawingContext dc, RectItem rect, double scale, bool showGuides, RenderTarget target)
    {
        var r = new Rect(Mm.ToDiu(rect.X) * scale, Mm.ToDiu(rect.Y) * scale, Mm.ToDiu(rect.Width) * scale, Mm.ToDiu(rect.Height) * scale);
        dc.DrawRectangle(null, RenderRules.InkPen(rect.ThicknessMm, scale, target), r);
        if (showGuides) dc.DrawRectangle(null, GuidePen, r);
    }

    private static void DrawLine(DrawingContext dc, LineItem line, double scale, RenderTarget target)
    {
        var p1 = new Point(Mm.ToDiu(line.X1) * scale, Mm.ToDiu(line.Y1) * scale);
        var p2 = new Point(Mm.ToDiu(line.X2) * scale, Mm.ToDiu(line.Y2) * scale);
        dc.DrawLine(RenderRules.InkPen(line.ThicknessMm, scale, target), p1, p2);
    }

    private static void DrawText(DrawingContext dc, TextItem text, double scale, bool showGuides, double pixelsPerDip, RenderTarget target)
    {
        // 缩字号、垂直居中、换行截断全在 TextFit 里定（定案 D10）：这里只负责把它画上去
        var fit = TextFit.Solve(text, scale, pixelsPerDip);
        if (fit is null) return;

        var box = fit.BoxDiu;
        // 需人工核对的字段与“缩到下限仍装不下、被省略号截断”共用同一套警示样式（文字颜色已在 TextFit 里换成警示色）
        if (text.Flagged || fit.Truncated) dc.DrawRectangle(RenderRules.FlagBackground, null, box);

        dc.DrawText(fit.Formatted, new Point(box.Left, fit.TextTopDiu));

        if (showGuides) dc.DrawRectangle(null, GuidePen, box);
    }

    private static void DrawImage(DrawingContext dc, ImageItem image, double scale)
    {
        var rect = new Rect(
            Mm.ToDiu(image.X) * scale, Mm.ToDiu(image.Y) * scale,
            Mm.ToDiu(image.Width) * scale, Mm.ToDiu(image.Height) * scale);
        try
        {
            var bitmap = new BitmapImage(new Uri(image.AbsolutePath, UriKind.Absolute));
            if (image.ReferenceOnly)
            {
                var group = new DrawingGroup { Opacity = RenderRules.ReferenceOpacity };
                using (var inner = group.Open()) inner.DrawImage(bitmap, rect);
                dc.DrawDrawing(group);
                DrawReferenceBadge(dc, rect);
            }
            else
            {
                dc.DrawImage(bitmap, rect);
            }
        }
        catch (Exception)
        {
            // 图片读不出不影响其余版面；缺图在 M7 的印前检查里单独告警
            dc.DrawRectangle(null, ReferencePen, rect);
        }
    }

    /// <summary>
    /// 矢量底图（M5 的 C 类模板）。底稿里几百个对象在模板里只占 1 个元素位（定案 D5），
    /// 所以这里一次画一整份，不拆开、不重排。
    /// </summary>
    private static void DrawVector(DrawingContext dc, VectorItem vector, double scale, double pixelsPerDip)
    {
        var box = new Rect(
            Mm.ToDiu(vector.X) * scale, Mm.ToDiu(vector.Y) * scale,
            Mm.ToDiu(vector.Width) * scale, Mm.ToDiu(vector.Height) * scale);

        var plan = SvgDrawableBuilder.Load(vector.AbsolutePath);
        var drawing = SvgDrawableBuilder.BuildDrawing(
            plan, vector.Width, vector.Height, pixelsPerDip,
            vector.ReferenceOnly ? RenderRules.ReferenceOpacity : 1);

        if (drawing is null)
        {
            // 底稿丢了/读不动：画个虚线框占位，绝不留一片看着像"本来就没东西"的空白
            dc.DrawRectangle(null, ReferencePen, box);
            return;
        }

        dc.PushTransform(new TranslateTransform(box.Left, box.Top));
        try
        {
            dc.DrawDrawing(drawing);
        }
        finally
        {
            dc.Pop();
        }

        if (vector.ReferenceOnly) DrawReferenceBadge(dc, box, pixelsPerDip);
    }

    /// <summary>
    /// 条码。条的位置与宽全由 Core 算好了（<c>BarcodeBars</c>），这里只画矩形：
    /// 预览/打印/PDF/图片四个出口共用这一段，所以上纸的那张与屏幕上那张条宽一模一样。
    /// </summary>
    private static void DrawBarcode(DrawingContext dc, BarcodeItem barcode, double scale, bool showGuides, double pixelsPerDip, RenderTarget target)
    {
        var box = new Rect(
            Mm.ToDiu(barcode.X) * scale, Mm.ToDiu(barcode.Y) * scale,
            Mm.ToDiu(barcode.Width) * scale, Mm.ToDiu(barcode.Height) * scale);

        if (barcode.Error is { Length: > 0 } error)
        {
            // 编不出来：画一个红底框 + 一句原因，绝不留一片看着像「本来就没东西」的空白。
            // 这一项同时带着 Flagged，打印前的复核闸门会拦着它（宁可不出纸也不出一张错码）。
            dc.DrawRectangle(RenderRules.FlagBackground, null, box);
            var why = new FormattedText(
                error,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(RenderRules.SafeFontFamily(barcode.FontFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                Math.Max(7, Mm.ToDiu(barcode.Height) * scale * 0.32),
                RenderRules.FlagInk,
                pixelsPerDip)
            {
                MaxTextWidth = Math.Max(20, box.Width),
                Trimming = TextTrimming.CharacterEllipsis,
                MaxTextHeight = Math.Max(10, box.Height),
            };
            dc.DrawText(why, new Point(box.Left + 2, box.Top + 2));
            return;
        }

        var ink = barcode.Flagged ? RenderRules.FlagInk : RenderRules.Ink;
        var barsTop = Mm.ToDiu(barcode.BarsY) * scale;
        var barsHeight = Mm.ToDiu(barcode.BarsHeight) * scale;
        foreach (var bar in barcode.Bars)
        {
            dc.DrawRectangle(ink, null, new Rect(
                Mm.ToDiu(bar.X) * scale, barsTop, Mm.ToDiu(bar.Width) * scale, barsHeight));
        }

        if (barcode.ShowText)
        {
            // 可读数字那一行走与文本完全同一条路（同一个 TextFit）：缩字号、居中、装不下就标红，
            // 不在条码里再写一套“看着差不多”的画法。
            DrawText(dc, ReadableLine(barcode), scale, showGuides, pixelsPerDip, target);
        }

        if (showGuides) dc.DrawRectangle(null, GuidePen, box);
    }

    /// <summary>条码下方那串可读数字：把它包成一条 <see cref="TextItem"/>，好复用 <see cref="DrawText"/> 与 SVG 那边的同一套规则。</summary>
    public static TextItem ReadableLine(BarcodeItem barcode)
    {
        var textTop = barcode.BarsY + barcode.BarsHeight;
        return new TextItem(
            barcode.Data,
            barcode.X, textTop, barcode.Width, Math.Max(1, barcode.Height - (textTop - barcode.Y)),
            barcode.FontFamily, barcode.FontSizePt, false, HorizontalAlign.Center,
            ShrinkToFit: true, MaxLines: 1,
            Flagged: barcode.Flagged, FlagReason: barcode.FlagReason);
    }

    /// <summary>参考底图的角标：淡蓝虚线框 + "参考" 二字，明确告诉人这东西不上纸（定案 D7）。</summary>
    private static void DrawReferenceBadge(DrawingContext dc, Rect box, double pixelsPerDip = 1)
    {
        dc.DrawRectangle(null, ReferencePen, box);
        var label = new FormattedText(
            "参考底图·不打印",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(RenderRules.SafeFontFamily(TemplateElement.DefaultFont), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            9,
            RenderRules.ReferenceTint,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(20, box.Width),
            Trimming = TextTrimming.None,
        };
        dc.DrawText(label, new Point(box.Left + 2, box.Bottom - label.Height - 2));
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
