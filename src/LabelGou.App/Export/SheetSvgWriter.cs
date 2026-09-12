using System.IO;
using System.Windows;
using System.Windows.Media;
using LabelGou.App.Rendering;
using LabelGou.Core.Colors;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Interop.Svg;
using LabelGou.Core.Layout;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.Export;

/// <summary>一次写出的一个 SVG 文件。</summary>
/// <param name="FileName">建议文件名（不含目录）。</param>
/// <param name="Xml">完整文档文本。</param>
/// <param name="ElementCount">写进去的元素数（摘要要说"这一页画了多少东西"）。</param>
/// <param name="Notes">降级说明（图太大改引用、底稿读不到、多行文字仍转曲等）。</param>
public sealed record SvgFile(string FileName, string Xml, int ElementCount, IReadOnlyList<string> Notes);

/// <summary>
/// 第五个出口：整版 → 可被 CorelDRAW/Illustrator 打开的矢量 SVG（M5）。
/// <para>
/// 与位图/PDF/打印三条出口的<strong>分工</strong>：那三条吃 <see cref="SheetRenderer.DrawPage"/> 的栅格结果，
/// 这一条吃同一批<em>数据</em>——<see cref="SheetPlan.PlacementsOnPage"/> 的落位、
/// <see cref="ImpositionEngine.BuildMarks"/> 的角线、<see cref="LabelLayout.Items"/> 的内容。
/// 也就是说：**几何只有一份真值，只是换了支笔**，不存在"预览一个样、SVG 另一个样"。
/// </para>
/// <para>三条纪律（定案 D9/D10）：</para>
/// <list type="number">
///   <item>1 用户单位 = 1 毫米，<c>width/height</c> 写 <c>…mm</c>，CDR 开就是 1:1，不需要缩放手续。</item>
///   <item>文字默认<strong>转曲</strong>：落位与字号一律问 <see cref="TextFit"/>（与预览同一个决定），
///   再把 <c>FormattedText</c> 的字形轮廓写成 <c>&lt;path&gt;</c>。</item>
///   <item>角线/套准/内容/注记各成一层，对方在 CDR 里整层选、整层删。</item>
/// </list>
/// </summary>
public static class SheetSvgWriter
{
    /// <summary>设备单位 → 毫米（导出恒在 scale=1 域里算，这里只是把结果换回毫米）。</summary>
    private const double UnitToMm = 25.4 / 96.0;

    private static readonly SvgPaint BlackFill = new() { Color = "#000000" };

    private static readonly SvgPaint FlagFill = new() { Color = RenderRules.HexOf(RenderRules.FlagColor) };

    private static readonly SvgPaint FlagBackdrop = new() { Color = RenderRules.HexOf(RenderRules.FlagColor), Opacity = 0.11 };

    private static readonly SvgPaint NoteStroke = new() { Color = "#999999", WidthMm = 0.1 };

    private static readonly SvgPaint NoteFill = new() { Color = "#999999" };

    /// <summary>条码编不出来时那个红框的线（与预览/打印同一个红）。</summary>
    private static readonly SvgPaint FlagStroke = new() { Color = RenderRules.HexOf(RenderRules.FlagColor), WidthMm = 0.2 };

    /// <summary>三种标记的颜色从 <see cref="RenderRules"/> 取，与预览/打印/位图那三条出口同一份。</summary>
    private static readonly SvgPaint CropPaint = new() { Color = RenderRules.HexOf(RenderRules.CropMarkColor) };

    private static readonly SvgPaint RegistrationPaint = new() { Color = RenderRules.HexOf(RenderRules.RegistrationColor) };

    private static readonly SvgPaint OutlinePaint = new() { Color = RenderRules.HexOf(RenderRules.LabelOutlineColor) };

    /// <summary>把底稿里带颜色的笔刷写成 SVG 颜色；拿不到实体色就退回黑（唛头本来就是单色活）。</summary>
    private static SvgPaint PaintOf(Brush? brush, SvgPaint fallback)
        => brush is SolidColorBrush solid
            ? new SvgPaint { Color = RenderRules.HexOf(solid.Color), Opacity = solid.Opacity }
            : fallback;

    /// <summary>
    /// 元素自己那支墨写给 SVG 的填充（null = 黑）。
    /// <para>颜色字符串只从 <see cref="RenderRules.InkHex"/> 出，与预览那支笔同源——
    /// 第 47 棒之前这里直接写 <c>BlackFill</c>，元素一旦带色就会出现"屏上红、SVG 黑"。</para>
    /// </summary>
    private static SvgPaint InkPaint(LabelColor? ink)
        => ink is null ? BlackFill : new SvgPaint { Color = RenderRules.InkHex(ink) };

    /// <summary>
    /// 写一页（0 起始，与 <see cref="SheetExportRequest.PageIndexes"/> 同一口径）。
    /// <para><see cref="SvgExportMode.PerSheet"/> 返 1 个文件；<see cref="SvgExportMode.PerLabel"/> 返该页每枚标签各一个。</para>
    /// </summary>
    public static IReadOnlyList<SvgFile> WritePage(SheetExportRequest request, int pageIndex, SvgExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        return options.Mode == SvgExportMode.PerLabel
            ? WriteLabels(request, pageIndex + 1, options)
            : new[] { WriteSheet(request, pageIndex + 1, options) };
    }

    private static string ProducerOf(SvgExportOptions options) =>
        string.IsNullOrWhiteSpace(options.Producer) ? SheetExportService.ProducerName : options.Producer!;

    // ---------- 整页一图 ----------

    private static SvgFile WriteSheet(SheetExportRequest request, int page, SvgExportOptions options)
    {
        var plan = request.Plan;
        var provider = request.Source.AsProvider();
        var notes = new List<string>();
        var builder = new SvgBuilder(plan.PageWidthMm, plan.PageHeightMm,
            $"{ProducerOf(options)} · {options.Describe()}");
        string TransformOf(LabelPlacement placement) => placement.Rotated
            // 与 LabelRenderer.DrawRotated 完全同源的落位：先转 90°，再平移到 (X+高, Y)
            ? $"translate({Num(placement.X + placement.Height)},{Num(placement.Y)}) rotate(90)"
            : $"translate({Num(placement.X)},{Num(placement.Y)})";

        WriteMetadata(builder, request, page, options);

        if (options.IncludePaperFrame)
        {
            builder.StartLayer("sheet", "纸张框");
            builder.Rect(0, 0, plan.PageWidthMm, plan.PageHeightMm, null, Stroke(0.1, "#bbbbbb"));
            builder.EndLayer();
        }

        var marks = ImpositionEngine.BuildMarks(plan.Spec, plan, page);
        if (options.IncludeCropMarks)
        {
            builder.StartLayer("crop-marks", "裁切角线");
            foreach (var mark in marks.Where(m => m.Kind == SheetMarkKind.CropMark))
            {
                builder.Line(mark.X1, mark.Y1, mark.X2, mark.Y2, Stroke(mark.ThicknessMm, CropPaint.Color));
            }
            builder.EndLayer();
        }

        if (options.IncludeRegistrationMarks)
        {
            builder.StartLayer("registration", "套准十字");
            foreach (var mark in marks.Where(m => m.Kind == SheetMarkKind.RegistrationMark))
            {
                builder.Line(mark.X1, mark.Y1, mark.X2, mark.Y2, Stroke(mark.ThicknessMm, RegistrationPaint.Color));
            }
            builder.EndLayer();
        }

        if (options.IncludeLabelOutlines)
        {
            builder.StartLayer("label-outlines", "刀框示意");
            foreach (var mark in marks.Where(m => m.Kind == SheetMarkKind.LabelOutline))
            {
                builder.Line(mark.X1, mark.Y1, mark.X2, mark.Y2, Stroke(mark.ThicknessMm, OutlinePaint.Color));
            }
            builder.EndLayer();
        }

        builder.StartLayer("labels", "标签内容");
        var placements = plan.PlacementsOnPage(page);
        foreach (var placement in placements)
        {
            var layout = provider(placement.LabelIndex);
            if (layout is null)
            {
                notes.Add($"第 {placement.LabelIndex} 枚标签没有版面数据，这一处留空。");
                continue;
            }

            // 与 LabelRenderer.DrawRotated 完全同源的落位：先转 90°，再平移到 (X+高, Y)
            builder.StartLayer($"label-{placement.LabelIndex:D4}", null, TransformOf(placement));
            WriteItems(builder, layout, options, notes);
            builder.EndLayer();
        }
        builder.EndLayer();

        if (options.IncludeNotes)
        {
            builder.StartLayer("notes", "注记（可整层删）");
            foreach (var placement in placements)
            {
                var layout = provider(placement.LabelIndex);
                if (layout is null) continue;
                builder.StartLayer($"note-{placement.LabelIndex:D4}", null, TransformOf(placement));
                WriteNotes(builder, layout);
                builder.EndLayer();
            }
            builder.EndLayer();
        }

        var fileName = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}_P{1}.svg",
            SheetExportService.SafeFileName(request.BaseName), page.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
        return new SvgFile(fileName, builder.Build(), builder.ElementCount, notes);
    }

    // ---------- 一枚一图 ----------

    private static IReadOnlyList<SvgFile> WriteLabels(SheetExportRequest request, int page, SvgExportOptions options)
    {
        var files = new List<SvgFile>();
        var provider = request.Source.AsProvider();
        foreach (var placement in request.Plan.PlacementsOnPage(page))
        {
            var layout = provider(placement.LabelIndex);
            if (layout is null) continue;

            var notes = new List<string>();
            var builder = new SvgBuilder(layout.WidthMm, layout.HeightMm,
                $"{ProducerOf(options)} · {options.Describe()}");
            WriteMetadata(builder, request, page, options);

            // 一枚一图：画布就是标签，不需要平移；旋转由对方拼版时自己定
            builder.StartLayer("label", $"第 {placement.LabelIndex} 枚");
            WriteItems(builder, layout, options, notes);
            builder.EndLayer();

            if (options.IncludeNotes)
            {
                builder.StartLayer("notes", "注记（可整层删）");
                WriteNotes(builder, layout);
                builder.EndLayer();
            }

            var fileName = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}_L{1}.svg",
                SheetExportService.SafeFileName(request.BaseName), placement.LabelIndex.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            files.Add(new SvgFile(fileName, builder.Build(), builder.ElementCount, notes));
        }
        return files;
    }

    // ---------- 内容 ----------

    /// <summary>
    /// 把一枚标签的要素写进<strong>当前层</strong>（坐标已是标签局部毫米）。
    /// 两种成件模式都走它——这是"只有一套画法"在矢量出口上的落点。
    /// </summary>
    private static void WriteItems(SvgBuilder builder, LabelLayout layout, SvgExportOptions options, List<string> notes)
    {
        var assetIndex = 0;
        foreach (var item in layout.Items)
        {
            switch (item)
            {
                case RectItem rect:
                    builder.Rect(rect.X, rect.Y, rect.Width, rect.Height, null, Stroke(rect.ThicknessMm, RenderRules.InkHex(rect.Ink)));
                    break;

                case LineItem line:
                    builder.Line(line.X1, line.Y1, line.X2, line.Y2, Stroke(line.ThicknessMm, RenderRules.InkHex(line.Ink)));
                    break;

                case TextItem text:
                    WriteText(builder, text, options, notes);
                    break;

                case ImageItem image:
                    // 第 43 棒：图片的"拉伸"就是宽高本身，这里只有旋转一个自由度；
                    // 与 LabelRenderer.DrawImage 的 PushGeometry 同一锚点（元素中心）、同一判据。
                    {
                        var rotated = Math.Abs(image.RotationDeg) > 1e-6;
                        if (rotated)
                            builder.StartGroup(SvgBuilder.GeometryTransform(
                                image.X + image.Width / 2, image.Y + image.Height / 2, image.RotationDeg, 1, 1));
                        try
                        {
                            WriteImage(builder, image, options, notes);
                        }
                        finally
                        {
                            if (rotated) builder.EndLayer();
                        }
                        break;
                    }

                case VectorItem vector:
                    WriteVectorAsset(builder, vector, ++assetIndex, notes);
                    break;

                case BarcodeItem barcode:
                    WriteBarcode(builder, barcode, options, notes);
                    break;
            }
        }
    }

    private static void WriteText(SvgBuilder builder, TextItem text, SvgExportOptions options, List<string> notes)
    {
        // 第 43 棒：带旋转/拉伸的文字整段包进一个带 transform 的 <g>——变换在 TextFit 决定之后
        // （与 LabelRenderer.DrawText 的 PushGeometry 严格同一顺序同一锚点），五出口才是同一张纸。
        var wrapped = text.HasGeometry;
        if (wrapped)
        {
            var cx = text.X + text.Width / 2;
            var cy = text.Y + text.Height / 2;
            builder.StartGroup(SvgBuilder.GeometryTransform(cx, cy, text.RotationDeg, text.TextScaleX, text.TextScaleY));
        }
        try
        {
            WriteTextInner(builder, text, options, notes);
        }
        finally
        {
            if (wrapped) builder.EndLayer();
        }
    }

    private static void WriteTextInner(SvgBuilder builder, TextItem text, SvgExportOptions options, List<string> notes)
    {
        // 字号、居中偏移、换行与省略号一律问 TextFit：与预览/打印同一个决定（定案 D10）
        var fit = TextFit.Solve(text, scale: 1.0, TextFit.CanonicalPixelsPerDip);
        if (fit is null) return;

        // 截断与需人工核对同一个颜色：矢量出口不许静默把货号截成半截
        if (fit.Truncated)
            notes.Add($"「{Shrink(text.Content)}」缩到下限仍装不下，已被省略号截断——这一格必须人工改模板或改数据。");

        var fill = text.Flagged || fit.Truncated ? FlagFill : InkPaint(text.Ink);

        // 淡红底：与预览/打印同一判据（Flagged 或被截断都算），而且必须画在字之前、
        // 也要在「未转曲早退」之前，否则单行不转曲那条出口连个提示都不剩。
        if (text.Flagged || fit.Truncated)
        {
            builder.Rect(fit.BoxDiu.Left * UnitToMm, fit.BoxDiu.Top * UnitToMm,
                fit.BoxDiu.Width * UnitToMm, fit.BoxDiu.Height * UnitToMm,
                FlagBackdrop, null);
        }

        if (!options.TextAsOutlines)
        {
            var lines = fit.LineCount;
            if (lines == 1)
            {
                // 永不折行的行：墨迹左缘已由 TextFit 按对齐算好（第 46 棒），这里只照抄，不再二次推导——
                // 两处各推一遍迟早长歪（§五-62 那族）。折行的行仍按盒宽与对齐推锚点。
                if (text.NoWrap)
                {
                    builder.Text(
                        text.Content,
                        fit.InkLeftDiu * UnitToMm,
                        (fit.TextTopDiu + fit.Formatted.Baseline) * UnitToMm,
                        fit.EmSizeDiu * 72.0 / 96.0,
                        text.FontFamily,
                        text.Bold,
                        SvgTextAnchor.Start,
                        fill);
                    return;
                }
                var anchorX = text.Align switch
                {
                    HorizontalAlign.Center => fit.BoxDiu.Left + fit.BoxDiu.Width / 2,
                    HorizontalAlign.Right => fit.BoxDiu.Right,
                    _ => fit.BoxDiu.Left,
                };
                builder.Text(
                    text.Content,
                    anchorX * UnitToMm,
                    (fit.TextTopDiu + fit.Formatted.Baseline) * UnitToMm,
                    fit.EmSizeDiu * 72.0 / 96.0,
                    text.FontFamily,
                    text.Bold,
                    text.Align switch
                    {
                        HorizontalAlign.Center => SvgTextAnchor.Middle,
                        HorizontalAlign.Right => SvgTextAnchor.End,
                        _ => SvgTextAnchor.Start,
                    },
                    fill);
                return;
            }
            notes.Add($"「{Shrink(text.Content)}」排成了 {fit.LineCount} 行，未转曲的 <text> 表达不了多行，这一段仍按轮廓写出。");
        }

        var geometry = fit.Formatted.BuildGeometry(new Point(fit.InkLeftDiu, fit.TextTopDiu));
        var commands = SvgGeometryConverter.ToCommands(geometry, UnitToMm, out var evenOdd, notes);
        builder.Path(commands, fill, null, evenOdd);
    }

    /// <summary>
    /// 条码的矢量出口：一根条一个 <c>&lt;rect&gt;</c>，坐标就是 Core 算好的毫米（1 用户单位 = 1 mm）。
    /// <para>为什么不用 <c>&lt;path&gt;</c> 合一条：CorelDRAW 里一堆独立矩形比一条超长路径好编，
    /// 而且拼版/裁切软件对矩形最不容易出错。</para>
    /// </summary>
    private static void WriteBarcode(SvgBuilder builder, BarcodeItem barcode, SvgExportOptions options, List<string> notes)
    {
        if (barcode.Error is { Length: > 0 } error)
        {
            // 编不出来：写一个红框 + 一句原因，不静默留空（这一项同时被复核闸门拦着）。
            builder.Rect(barcode.X, barcode.Y, barcode.Width, barcode.Height, null, FlagStroke);
            builder.Text("条码编不出来：" + error, barcode.X + 1, barcode.Y + 3.2, 6,
                TemplateElement.DefaultFont, true, SvgTextAnchor.Start, FlagFill);
            notes.Add("有一处条码编不出来，已按红框占位写出：" + error);
            return;
        }

        // 与预览同一判据：待核的条码整条标红，其余用元素自己那支墨（第 47 棒起可以带色）。
        var bars = barcode.Flagged ? FlagFill : InkPaint(barcode.Ink);
        foreach (var bar in barcode.Bars)
            // 每根条按自己的高写：UPC/EAN 的保护条比数据条高一截（第 41 棒）。
            builder.Rect(bar.X, barcode.BarsY, bar.Width, barcode.HeightOf(bar), bars, null);

        if (barcode.ShowText)
        {
            if (barcode.Hri is { Count: > 0 })
            {
                // UPC/EAN：一字一格，坐标由 Core 算好（首位骑静区、每位压自己那 7 个模块）。
                foreach (var glyph in LabelRenderer.ReadableGlyphs(barcode))
                    WriteText(builder, glyph, options, notes);
            }
            else
            {
                WriteText(builder, LabelRenderer.ReadableLine(barcode), options, notes);
            }
        }

        if (barcode.Warning is { Length: > 0 } warn) notes.Add("条码能画但可能扫不出：" + warn);
    }

    private static void WriteImage(SvgBuilder builder, ImageItem image, SvgExportOptions options, List<string> notes)
    {
        if (image.ReferenceOnly)
        {
            notes.Add("有一张仅供对齐的参考底图被跳过了（它本来就不该上纸）。");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(image.AbsolutePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"图片「{Path.GetFileName(image.AbsolutePath)}」读不出来，这一处留空：{ex.Message}");
            return;
        }

        var mime = MimeTypeOf(image.AbsolutePath);
        if (options.EmbedRasterImages && bytes.Length <= options.MaxEmbeddedImageBytes)
        {
            builder.Image(bytes, mime, image.X, image.Y, image.Width, image.Height);
            return;
        }

        if (options.EmbedRasterImages)
        {
            notes.Add($"图片「{Path.GetFileName(image.AbsolutePath)}」有 {SheetExportService.FormatSize(bytes.Length)}，超出内嵌上限，改为文件引用（图要跟着 SVG 一起拷）。");
        }
        builder.ImageRef(new Uri(image.AbsolutePath, UriKind.Absolute).AbsoluteUri, image.X, image.Y, image.Width, image.Height);
    }

    /// <summary>
    /// 内联一份矢量底图（C 类模板的底稿）。
    /// <para>底稿在模板里只占 1 个元素位（定案 D5），写出来就是一个带变换的 <c>&lt;g&gt;</c>：
    /// 坐标本就是毫米，套一层 <c>translate+scale</c> 铺进元素框，不做任何重排。</para>
    /// </summary>
    private static void WriteVectorAsset(SvgBuilder builder, VectorItem vector, int assetIndex, List<string> notes)
    {
        if (vector.ReferenceOnly)
        {
            notes.Add("底稿里仅供对齐的部分已跳过（参考图不上纸）。");
            return;
        }

        var document = SvgDrawableBuilder.LoadDocument(vector.AbsolutePath);
        var plan = SvgDrawableBuilder.Load(vector.AbsolutePath);
        var name = Path.GetFileName(vector.AbsolutePath);
        if (document is null || plan is null)
        {
            notes.Add($"矢量底图「{name}」读不到，这一处只留了一个虚线框占位。");
            builder.Rect(vector.X, vector.Y, vector.Width, vector.Height, null, NoteStroke);
            return;
        }

        var docWidth = plan.WidthMm > 0 ? plan.WidthMm : Math.Max(1e-6, document.WidthMm);
        var docHeight = plan.HeightMm > 0 ? plan.HeightMm : Math.Max(1e-6, document.HeightMm);

        // 底稿里被降级的部分（颜色写法、特效、渐变…）以前只在**导入那一次**说过：
        // plan.Issues 全工程没人读，之后每次出片都悄悄用着降级结果。出片这一刻才是人真正在看的时候，
        // 把它并进导出的降级清单（同一份清单会进摘要与日志），不再让第 5 个出口替前面兜底。
        foreach (var issue in plan.Issues.Distinct())
            notes.Add($"底稿「{name}」：{issue}");

        var sx = vector.Width / docWidth;
        var sy = vector.Height / docHeight;

        // 第 43 棒：可绕元素中心旋转。SVG 的 transform 从右往左作用，rotate 写在最左 = 最后施加，
        // 与 LabelRenderer.DrawVector（先平移到框、外层套旋转）同一结果。旋转 0 度时前缀为空、逐字节不变。
        var rotatePrefix = Math.Abs(vector.RotationDeg) > 1e-6
            ? $"rotate({Num(vector.RotationDeg)},{Num(vector.X + vector.Width / 2)},{Num(vector.Y + vector.Height / 2)}) "
            : string.Empty;
        builder.StartLayer($"background-{assetIndex}", "矢量底图",
            rotatePrefix + $"translate({Num(vector.X)},{Num(vector.Y)}) scale({Num(sx)},{Num(sy)})");

        foreach (var path in document.Paths)
        {
            builder.Path(path.Commands, path.Fill, path.Stroke, path.EvenOdd);
        }

        foreach (var image in plan.Images)
        {
            builder.Image(image.Bytes, image.MimeType, image.XMm, image.YMm, image.WidthMm, image.HeightMm);
        }

        // 底稿里的文字同样转曲：否则对方机器缺那款中文字体就会掉字（§五-6 的老坑）
        // 颜色跟底稿一致，不再一律抹成黑：预览里看见的红字，导出到件上也得是红字
        foreach (var text in plan.Texts)
        {
            var drawable = SvgDrawableBuilder.BuildText(text, TextFit.CanonicalPixelsPerDip);
            if (drawable is null) continue;
            var geometry = drawable.Formatted.BuildGeometry(drawable.OriginDiu);
            var commands = SvgGeometryConverter.ToCommands(geometry, UnitToMm, out var evenOdd, notes);
            builder.Path(commands, PaintOf(text.Fill, BlackFill), null, evenOdd);
        }

        builder.EndLayer();
    }

    private static void WriteNotes(SvgBuilder builder, LabelLayout layout)
    {
        foreach (var element in layout.Template.Elements.Where(e => e.Visible && !e.ReferenceOnly))
        {
            switch (element.Kind)
            {
                case ElementKind.Line:
                    builder.Line(element.X, element.Y, element.X2, element.Y2, NoteStroke);
                    continue;
                case ElementKind.Rect:
                case ElementKind.Text:
                case ElementKind.Image:
                case ElementKind.Vector:
                default:
                    builder.Rect(element.X, element.Y, element.Width, element.Height, null, NoteStroke);
                    break;
            }

            var label = element.Kind is ElementKind.Text or ElementKind.Barcode && !string.IsNullOrWhiteSpace(element.Text)
                ? element.Text
                : element.Kind.ToString();
            builder.Text(label, element.X, element.Y + 2.6, 6, TemplateElement.DefaultFont, false, SvgTextAnchor.Start, NoteFill);
        }
    }

    private static void WriteMetadata(SvgBuilder builder, SheetExportRequest request, int page, SvgExportOptions options)
    {
        builder.Metadata($"生成：{ProducerOf(options)}；口径：{options.Describe()}");
        if (!string.IsNullOrWhiteSpace(options.Title)) builder.Metadata($"任务：{options.Title}");
        builder.Metadata($"纸规：{request.Plan.Spec.Description}");
        builder.Metadata($"页次：{page} / {Math.Max(1, request.Plan.PageCount)}；本版共 {request.Plan.LabelCount} 枚标签；数据源：{request.Source.SourceName}");
        builder.Metadata("尺寸：1 用户单位 = 1 毫米，画布 " + Num(request.Plan.PageWidthMm) + "×" + Num(request.Plan.PageHeightMm) + " mm");

        var fields = new List<string>();
        // 条码也算用了字段：它的表达式与文本同一套占位符，漏掉它会让拿这份 SVG 去接数据的同事少对一列。
        foreach (var element in request.Source.Template.Elements.Where(e =>
                     e.Kind is ElementKind.Text or ElementKind.Barcode && !string.IsNullOrWhiteSpace(e.Text)))
        {
            foreach (var token in TemplateTokenizer.EnumerateTokens(element.Text!))
            {
                if (!fields.Contains(token, StringComparer.OrdinalIgnoreCase)) fields.Add(token);
            }
        }
        if (fields.Count > 0)
        {
            builder.Metadata("字段清单（JSON 数组）：[" + string.Join(",", fields.Select(f => "\"" + f + "\"")) + "]");
        }
    }

    /// <summary>模板毫米线宽 → SVG 线宽（走 <see cref="RenderRules"/> 的矢量口径，与位图/打印同源）。</summary>
    private static SvgPaint Stroke(double thicknessMm, string color) => new()
    {
        Color = color,
        WidthMm = RenderRules.LineWidthMm(thicknessMm, RenderTarget.Vector),
    };

    private static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    private static string Num(double value) => SvgLength.Format(value);

    private static string Shrink(string text) => text.Length <= 20 ? text : text[..20] + "…";
}
