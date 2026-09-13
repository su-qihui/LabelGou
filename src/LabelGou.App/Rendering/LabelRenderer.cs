using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.Core.Barcodes;
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
        dc.DrawRectangle(null, RenderRules.PenFor(RenderRules.InkOf(rect.Ink), rect.ThicknessMm, scale, target), r);
        if (showGuides) dc.DrawRectangle(null, GuidePen, r);
    }

    private static void DrawLine(DrawingContext dc, LineItem line, double scale, RenderTarget target)
    {
        var pen = RenderRules.PenFor(RenderRules.InkOf(line.Ink), line.ThicknessMm, scale, target);
        if (line.Arc is { Count: > 0 } arc)
        {
            dc.DrawGeometry(null, pen, ArcGeometry(arc, scale));
            return;
        }
        var p1 = new Point(Mm.ToDiu(line.X1) * scale, Mm.ToDiu(line.Y1) * scale);
        var p2 = new Point(Mm.ToDiu(line.X2) * scale, Mm.ToDiu(line.Y2) * scale);
        dc.DrawLine(pen, p1, p2);
    }

    /// <summary>
    /// 曲线段序列 → WPF 几何（设备单位）。<strong>全项目只有这一份"弧怎么连"</strong>：画布、位图、打印
    /// 三条吃几何的出口与编辑器选择框都走这里，直线段退化成 <c>LineSegment</c>。
    /// </summary>
    public static Geometry ArcGeometry(IReadOnlyList<Core.Templates.CurveSegment> arc, double scale)
    {
        var figure = new PathFigure
        {
            StartPoint = Diu(arc[0].X1, arc[0].Y1, scale),
            IsClosed = false,
            // 不填充由下面 DrawGeometry 传 null 画刷表达，几何本身不管这件事。
        };
        foreach (var s in arc)
        {
            // 第四个参数是 isStroked（不是 isSmoothJoin）：曲线只有描边没有填充，传 false 就等于整条不画。
            // （实测症状：分色版上一个像素都没有，直线却正常——因为直线走的是另一条 DrawLine 分支。）
            figure.Segments.Add(s.IsStraight
                ? new LineSegment(Diu(s.X2, s.Y2, scale), true)
                : new BezierSegment(
                    Diu(s.CX1, s.CY1, scale), Diu(s.CX2, s.CY2, scale), Diu(s.X2, s.Y2, scale), true));
        }
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point Diu(double mmX, double mmY, double scale)
        => new(Mm.ToDiu(mmX) * scale, Mm.ToDiu(mmY) * scale);

    private static void DrawText(DrawingContext dc, TextItem text, double scale, bool showGuides, double pixelsPerDip, RenderTarget target)
    {
        // 缩字号、垂直居中、换行截断全在 TextFit 里定（定案 D10）：这里只负责把它画上去。
        // 第 43 棒：字面拉伸（TextScaleX/Y）与旋转叠在 TextFit 决定【之后】——先按未拉伸盒排好版，
        // 再整块绕中心做 scale→rotate。这样五出口共用同一决定，不会「预览拉伸了导出没拉伸」，
        // 也让拉伸不会偷偷改变折行（折行是排版盒的事，见 TemplateElement.TextScaleX 注释）。
        var fit = TextFit.Solve(text, scale, pixelsPerDip);
        if (fit is null) return;

        var box = fit.BoxDiu;
        var pushed = PushGeometry(dc, box, text.RotationDeg, text.TextScaleX, text.TextScaleY);
        try
        {
            // 需人工核对的字段与”缩到下限仍装不下、被省略号截断”共用同一套警示样式（文字颜色已在 TextFit 里换成警示色）
            if (text.Flagged || fit.Truncated) dc.DrawRectangle(RenderRules.FlagBackground, null, box);

            dc.DrawText(fit.Formatted, new Point(fit.InkLeftDiu, fit.TextTopDiu));

            if (showGuides) dc.DrawRectangle(null, GuidePen, box);
        }
        finally
        {
            if (pushed) dc.Pop();
        }
    }

    /// <summary>
    /// 叠一层「先按中心拉伸、再按中心旋转」的变换（毫米盒已由调用方乘成 DIU）。<paramref name=”rotationDeg”/>
    /// 为 0 且倍率为 1 时什么都不做（返回 false，调用方连 Pop 都省）。
    /// <para>顺序是 scale 后 rotate（CDR 变换手感：抻完再转，转的是抻好的结果）。</para>
    /// </summary>
    private static bool PushGeometry(DrawingContext dc, Rect box, double rotationDeg, double scaleX, double scaleY)
    {
        var stretch = Math.Abs(scaleX - 1) > 1e-6 || Math.Abs(scaleY - 1) > 1e-6;
        var rotate = Math.Abs(rotationDeg) > 1e-6;
        if (!stretch && !rotate) return false;

        var cx = box.X + box.Width / 2;
        var cy = box.Y + box.Height / 2;
        var group = new TransformGroup();
        // 变换按【从先到后】作用到点上（WPF TransformGroup 子项就是这个顺序）。目标合成 =
        // 先绕盒中心拉伸、再绕盒中心旋转（CDR 手感：抻完再转，转的是抻好的结果）。
        // 中心缩放的三段式是 translate(-c) → scale → translate(+c)，顺序写反会把拉伸镜像到中心另一侧。
        if (stretch) group.Children.Add(new TranslateTransform(-cx, -cy));
        if (stretch) group.Children.Add(new ScaleTransform(scaleX, scaleY));
        if (rotate)
        {
            // 拉伸时中心已被平移到原点，这里用"绕原点"旋转；没拉伸才用带圆心的 RotateTransform。
            group.Children.Add(stretch ? new RotateTransform(rotationDeg) : new RotateTransform(rotationDeg, cx, cy));
        }
        if (stretch) group.Children.Add(new TranslateTransform(cx, cy));
        dc.PushTransform(group);
        return true;
    }

    private static void DrawImage(DrawingContext dc, ImageItem image, double scale)
    {
        var rect = new Rect(
            Mm.ToDiu(image.X) * scale, Mm.ToDiu(image.Y) * scale,
            Mm.ToDiu(image.Width) * scale, Mm.ToDiu(image.Height) * scale);
        // 图片的"拉伸"就是宽高本身（画上去填满框），所以这里只有旋转一个自由度（第 43 棒）。
        var pushed = PushGeometry(dc, rect, image.RotationDeg, 1, 1);
        try
        {
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
        finally
        {
            if (pushed) dc.Pop();
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

        // 第 43 棒：底图也可旋转（绕中心）；先套旋转再平移到框左上角画整份底稿。
        var rotPushed = Math.Abs(vector.RotationDeg) > 1e-6;
        if (rotPushed) dc.PushTransform(new RotateTransform(vector.RotationDeg, box.X + box.Width / 2, box.Y + box.Height / 2));
        dc.PushTransform(new TranslateTransform(box.Left, box.Top));
        try
        {
            dc.DrawDrawing(drawing);
        }
        finally
        {
            dc.Pop();
            if (rotPushed) dc.Pop();
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

        var ink = barcode.Flagged ? RenderRules.FlagInk : RenderRules.InkOf(barcode.Ink);
        var barsTop = Mm.ToDiu(barcode.BarsY) * scale;
        foreach (var bar in barcode.Bars)
        {
            // 每根条按自己的高画：UPC/EAN 的保护条比数据条高一截（第 41 棒，照 CDR 样本实测的 6 个模块）。
            dc.DrawRectangle(ink, null, new Rect(
                Mm.ToDiu(bar.X) * scale, barsTop, Mm.ToDiu(bar.Width) * scale,
                Mm.ToDiu(barcode.HeightOf(bar)) * scale));
        }

        if (barcode.ShowText)
        {
            // UPC/EAN 的数字 Core 已经逐字分好格（首位骑静区、每位压自己那 7 个模块），照格子画；
            // 其余制式（Code 128/39/ITF）仍整串居中。两条路都走同一个 DrawText，不另写画法。
            if (barcode.Hri is { Count: > 0 })
            {
                foreach (var glyph in ReadableGlyphs(barcode))
                    DrawText(dc, glyph, scale, showGuides, pixelsPerDip, target);
            }
            else
            {
                DrawText(dc, ReadableLine(barcode), scale, showGuides, pixelsPerDip, target);
            }
        }

        if (showGuides) dc.DrawRectangle(null, GuidePen, box);
    }

    /// <summary>
    /// 条码下方那串可读数字：把它包成一条 <see cref="TextItem"/>，好复用 <see cref="DrawText"/> 与 SVG 那边的同一套规则。
    /// <para>给没有分段规矩的制式（Code 128 / Code 39 / ITF）用——整串居中。</para>
    /// </summary>
    public static TextItem ReadableLine(BarcodeItem barcode)
    {
        var textTop = barcode.BarsY + barcode.BarsHeight;
        return new TextItem(
            barcode.Data,
            barcode.X, textTop, barcode.Width, Math.Max(1, barcode.Height - (textTop - barcode.Y)),
            barcode.FontFamily, barcode.FontSizePt, false, HorizontalAlign.Center,
            ShrinkToFit: true, MaxLines: 1,
            Flagged: barcode.Flagged, FlagReason: barcode.FlagReason,
            Ink: barcode.Ink);
    }

    /// <summary>
    /// UPC/EAN 的可读数字：Core 已经把每个字符的格子算好了（<c>BarcodeBars.BuildHri</c>），
    /// 这里把「一格一个字」包成一组 <see cref="TextItem"/>。
    /// <para><strong>为什么一个字一条</strong>：EAN 的数字不是等间距地摊在整条码下面——
    /// 首位骑在左静区、中间两段各自铺满自己的 7 模块格，中间保护条那五格是空的。
    /// 整串居中排出来的间距和 BARCODE WIZARD 对不上（差到 2.5 mm），所以必须逐格定位。
    /// 包成 TextItem 是为了让预览、打印、PDF、图片、SVG 五个出口共用同一个画法（§七-11）。</para>
    /// </summary>
    public static IReadOnlyList<TextItem> ReadableGlyphs(BarcodeItem barcode)
    {
        if (barcode.Hri is not { Count: > 0 }) return Array.Empty<TextItem>();
        // 数字带从数据条底再往下让一点才起（照 CDR 实测 3.175 个模块）。
        // 但让多少得封顶——扁框里模块宽被撑大时 3 个模块能到 4 mm，字会整个掉出元素底边。
        var textTop = barcode.BarsY + barcode.BarsHeight
                      + Math.Min(BarcodeBars.HriTopOffsetModules * barcode.ModuleMm,
                                 BarcodeBars.MinTextBandMm * 0.3);
        var bandHeight = Math.Max(1, barcode.Height - (textTop - barcode.Y));
        var list = new List<TextItem>(barcode.Hri.Count);
        foreach (var glyph in barcode.Hri)
        {
            list.Add(new TextItem(
                glyph.Ch.ToString(),
                glyph.X, textTop, glyph.Width, bandHeight,
                barcode.FontFamily, barcode.FontSizePt, false, HorizontalAlign.Center,
                ShrinkToFit: true, MaxLines: 1,
                Flagged: barcode.Flagged, FlagReason: barcode.FlagReason,
                Ink: barcode.Ink));
        }
        return list;
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
