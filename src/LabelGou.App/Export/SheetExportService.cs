using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using LabelGou.App.Rendering;
using LabelGou.App.Services;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Templates;

namespace LabelGou.App.Export;

/// <summary>导出请求。页号统一 0 起始（与 <see cref="SheetPlan.PlacementsOnPage"/> 的调用口径不同，那里是 1 起始）。</summary>
public sealed class SheetExportRequest
{
    public required SheetPlan Plan { get; init; }
    public required PageContentSource Source { get; init; }
    public required IReadOnlyList<int> PageIndexes { get; init; }
    public required string BaseName { get; init; }
    public int Dpi { get; init; } = 300;
    public bool IncludeTrimMarks { get; init; } = true;

    /// <summary>PDF 里页图的存放方式：JPEG 文件小，无损（Flate）边缘更硬。</summary>
    public PdfImageKind RasterKind { get; init; } = PdfImageKind.Jpeg;

    /// <summary>
    /// 第 48 棒：<strong>按 CMYK 四版出</strong>（PDF 与 TIFF 两条位图出口认这个开关）。
    /// <para>开着它时 PDF 里 <see cref="RasterKind"/> 不再适用（CMYK 走无损 Flate，JPEG 那套是三色
    /// 有损的），面板上那颗格式下拉会一起灰掉——不留"我明明选了 JPEG 怎么不是"的怪事。
    /// <strong>PNG 出口不认它</strong>：47 棒探针实测 WPF 写 PNG 会把 Cmyk32 悄悄转成 Bgr24，
    /// 那正是"看着是 CMYK、拿到手是 RGB"的静默降级，所以我们宁可不提供。</para>
    /// </summary>
    public bool CmykPlates { get; init; }

    public int JpegQuality { get; init; } = 92;

    public void CollectIssues(IList<string> issues)
    {
        if (PageIndexes is null || PageIndexes.Count == 0) issues.Add("没有选中任何一页可导出。");
        if (Dpi < 72 || Dpi > 2400) issues.Add($"DPI {Dpi} 超出可用范围（72~2400）。");
        if (string.IsNullOrWhiteSpace(BaseName)) issues.Add("文件名不能为空。");
        if (Plan.PerPage <= 0) issues.Add("纸规放不下任何一枚标签，请先调整拼版设置。");
        if (CmykPlates) CollectPlateCoverageIssues(issues);
        Plan.CollectPageRangeIssues(PageIndexes, issues);
    }

    /// <summary>
    /// CMYK 四版目前只分得开<strong>文字、线条、矩形框、条码</strong>——它们的墨量直接来自用户在「墨色」里
    /// 填的那四个百分数。<strong>图片与矢量底图分不了</strong>：它们带着自己的颜色，画到灰版上只有红通道
    /// 会被当成这一版的墨，四版会印成同一张红版灰度图（不是"颜色差一点"，是整版错）。
    /// 与其出一份看着像 CMYK、上机全废的件，不如在这里停下并说清下一步（48.5 棒补分色画法）。
    /// </summary>
    private void CollectPlateCoverageIssues(IList<string> issues)
    {
        var blockers = Source.Template.Elements.Count(e =>
            e.Visible && !e.ReferenceOnly && e.Kind is ElementKind.Image or ElementKind.Vector);
        if (blockers > 0)
        {
            issues.Add($"CMYK 四版暂时分不开图片与矢量底图（这一版里有 {blockers} 处）。" +
                       "它们要的分色画法在下一棒，现在硬出会四版印成同一张红通道灰度图。" +
                       "先按 RGB 出这份，或把底图/图片暂时隐藏。");
        }
    }

    public int SheetWidthPx => PageRasterizer.PixelsForMillimetres(Plan.PageWidthMm, Dpi);
    public int SheetHeightPx => PageRasterizer.PixelsForMillimetres(Plan.PageHeightMm, Dpi);
}

public sealed record ExportOutcome(
    bool Success,
    string? Error,
    IReadOnlyList<string> Files,
    long BytesWritten,
    string? Summary)
{
    public static ExportOutcome Fail(string error) => new(false, error, Array.Empty<string>(), 0, null);
}

/// <summary>
/// 四种出片出口：PDF（打印交付）、逐页 PNG（发图方便）、多帧 TIFF（印厂收图）、SVG（衔接 CorelDRAW 既有出片流程）。
/// 前三条共用 <see cref="PageRasterizer"/> 与 <see cref="SheetRenderer"/>，与预览同一套栅格画法；
/// SVG 不吃栅格，但吃同一批数据（落位、角线、版面要素）与同一个 <see cref="Rendering.TextFit"/>。
/// </summary>
public static class SheetExportService
{
    /// <summary>TIFF 是一次性在内存里攒帧的，页数必须设限，否则大任务直接 OOM。</summary>
    public const int MaxTiffFrames = 40;

    /// <summary>SVG 一次最多写多少个文件（一枚一图时 50 枚版打 20 页就上千个，防手滑）。</summary>
    public const int MaxSvgFiles = 600;

    // 版本号单一出口在 AppInfo（与标题栏、「关于」同一个），PDF/SVG 的 producer 不再自己反射拼一份
    public static string ProducerName => "LabelGou " + AppInfo.Version;

    public static ExportOutcome ExportPdf(SheetExportRequest request, string filePath, IProgress<string>? progress, CancellationToken token)
    {
        var invalid = Validate(request, requireDiskSpaceHint: true);
        if (invalid is not null) return invalid;

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        try
        {
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            return ExportOutcome.Fail($"输出目录打不开：{ex.Message}");
        }

        try
        {
            using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new PdfImageWriter(file);
            var options = new PdfWriteOptions { Producer = ProducerName, Title = request.BaseName, CreatorTool = ProducerName };
            var kind = request.CmykPlates ? PdfImageKind.Cmyk32 : request.RasterKind;
            var done = 0;
            foreach (var index in request.PageIndexes)
            {
                token.ThrowIfCancellationRequested();
                done++;
                progress?.Report($"正在渲染 PDF 第 {index + 1} 页（{done}/{request.PageIndexes.Count}）…");
                byte[] data;
                int pixelWidth, pixelHeight;
                if (request.CmykPlates)
                {
                    var plate = PageRasterizer.RenderCmykPage(request.Plan, index + 1, request.Dpi,
                        request.Source.AsPlateProvider(), request.IncludeTrimMarks);
                    (pixelWidth, pixelHeight, data) = (plate.Width, plate.Height, plate.Pixels);
                }
                else
                {
                    var bitmap = PageRasterizer.RenderPage(request.Plan, index + 1, request.Dpi,
                        request.Source.AsProvider(), false, PageRenderPurpose.Image, request.IncludeTrimMarks);
                    data = kind == PdfImageKind.Jpeg
                        ? PageRasterizer.EncodeJpeg(bitmap, request.JpegQuality)
                        : PageRasterizer.CopyRgb24(bitmap);
                    (pixelWidth, pixelHeight) = (bitmap.PixelWidth, bitmap.PixelHeight);
                }
                writer.AddPage(new PdfPageImage(request.Plan.PageWidthMm, request.Plan.PageHeightMm,
                    pixelWidth, pixelHeight, kind, data));
            }
            var bytes = writer.Finish(options);
            var summary = string.Format(CultureInfo.InvariantCulture,
                "{0} 导出 {1} 页 · {2}DPI · {3} · {4}（页面 {5}×{6}mm）",
                Path.GetFileName(filePath), request.PageIndexes.Count, request.Dpi,
                DescribePdfRaster(request), FormatSize(bytes),
                request.Plan.PageWidthMm.ToString("0.#", CultureInfo.InvariantCulture),
                request.Plan.PageHeightMm.ToString("0.#", CultureInfo.InvariantCulture));
            return new ExportOutcome(true, null, new[] { filePath }, bytes, summary);
        }
        catch (OperationCanceledException)
        {
            TryDeletePartial(filePath);
            return ExportOutcome.Fail("导出已取消，半成品文件已删除。");
        }
        catch (InvalidDataException ex)
        {
            TryDeletePartial(filePath);
            return ExportOutcome.Fail($"页图不合格，PDF 未写完：{ex.Message}");
        }
        catch (Exception ex)
        {
            TryDeletePartial(filePath);
            return ExportOutcome.Fail($"导出 PDF 失败：{ex.Message}");
        }
    }

    public static ExportOutcome ExportPngPages(SheetExportRequest request, string directory, IProgress<string>? progress, CancellationToken token)
    {
        var invalid = Validate(request);
        if (invalid is not null) return invalid;

        var files = new List<string>();
        var total = 0L;
        try
        {
            Directory.CreateDirectory(directory);
            var width = request.PageIndexes.Count.ToString(CultureInfo.InvariantCulture).Length;
            var step = 0;
            foreach (var index in request.PageIndexes)
            {
                token.ThrowIfCancellationRequested();
                step++;
                progress?.Report($"正在写出 PNG 第 {index + 1} 页（{step}/{request.PageIndexes.Count}）…");
                var bitmap = PageRasterizer.RenderPage(request.Plan, index + 1, request.Dpi,
                    request.Source.AsProvider(), false, PageRenderPurpose.Image, request.IncludeTrimMarks);
                var name = string.Format(CultureInfo.InvariantCulture, "{0}_P{1}.png",
                    SafeFileName(request.BaseName), step.ToString("D" + Math.Max(2, width), CultureInfo.InvariantCulture));
                var path = Path.Combine(directory, name);
                var bytes = PageRasterizer.EncodePng(bitmap);
                File.WriteAllBytes(path, bytes);
                files.Add(path);
                total += bytes.Length;
                progress?.Report($"已写出 {step}/{request.PageIndexes.Count} 页…");
            }
            var summary = string.Format(CultureInfo.InvariantCulture,
                "写出 {0} 个 PNG · {1}DPI · 合计 {2}{3}", files.Count, request.Dpi, FormatSize(total),
                request.CmykPlates ? "｜PNG 装不下 CMYK 四版，这一份仍是 RGB（要四版请出 PDF 或 TIFF）" : string.Empty);
            return new ExportOutcome(true, null, files, total, summary);
        }
        catch (OperationCanceledException)
        {
            foreach (var file in files) TryDeletePartial(file);
            return ExportOutcome.Fail("导出已取消，已写出的图片已删除。");
        }
        catch (Exception ex)
        {
            return ExportOutcome.Fail($"导出 PNG 失败：{ex.Message}");
        }
    }

    public static ExportOutcome ExportTiff(SheetExportRequest request, string filePath, IProgress<string>? progress, CancellationToken token)
    {
        var invalid = Validate(request);
        if (invalid is not null) return invalid;
        if (request.PageIndexes.Count > MaxTiffFrames)
        {
            return ExportOutcome.Fail($"TIFF 一次最多 {MaxTiffFrames} 页（当前 {request.PageIndexes.Count} 页），大任务请改用 PDF 或逐页 PNG。");
        }

        var frames = new List<BitmapSource>();
        var cmykFrames = new List<CmykTiffFrame>();
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            foreach (var index in request.PageIndexes)
            {
                token.ThrowIfCancellationRequested();
                if (request.CmykPlates)
                {
                    var plate = PageRasterizer.RenderCmykPage(request.Plan, index + 1, request.Dpi,
                        request.Source.AsPlateProvider(), request.IncludeTrimMarks);
                    cmykFrames.Add(new CmykTiffFrame(plate.Width, plate.Height, plate.Pixels));
                }
                else
                {
                    var bitmap = PageRasterizer.RenderPage(request.Plan, index + 1, request.Dpi,
                        request.Source.AsProvider(), false, PageRenderPurpose.Image, request.IncludeTrimMarks);
                    frames.Add(bitmap);
                }
                progress?.Report($"已渲染 {(request.CmykPlates ? cmykFrames.Count : frames.Count)}/{request.PageIndexes.Count} 页…");
            }
            var bytes = request.CmykPlates
                ? CmykTiffWriter.Write(cmykFrames, ProducerName, request.Dpi)
                : PageRasterizer.EncodeTiff(frames);
            File.WriteAllBytes(filePath, bytes);
            var frameCount = request.CmykPlates ? cmykFrames.Count : frames.Count;
            var summary = string.Format(CultureInfo.InvariantCulture,
                "{0} 导出 {1} 帧 TIFF · {2}DPI · {3} · {4}", Path.GetFileName(filePath), frameCount, request.Dpi,
                request.CmykPlates ? "CMYK 四版（0=无墨）" : "RGB", FormatSize(bytes.Length));
            return new ExportOutcome(true, null, new[] { filePath }, bytes.Length, summary);
        }
        catch (OperationCanceledException)
        {
            return ExportOutcome.Fail("导出已取消。");
        }
        catch (Exception ex)
        {
            foreach (var frame in frames) (frame as IDisposable)?.Dispose();
            return ExportOutcome.Fail($"导出 TIFF 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 矢量出口：整版写成可被 CorelDRAW/Illustrator 直开的 SVG（M5）。
    /// <para>与位图出口的区别：不吃 DPI，所以纸规再大也不会把内存吃光；代价是件里没有栅格，
    /// 内嵌图靠 base64。</para>
    /// </summary>
    public static ExportOutcome ExportSvg(SheetExportRequest request, string directory, SvgExportOptions options,
        IProgress<string>? progress, CancellationToken token)
    {
        var invalid = Validate(request);
        if (invalid is not null) return invalid;

        // 口径提醒（关转曲、图片改引用）不是错，但必须进摘要，不能默默降质
        var reminders = new List<string>();
        options.CollectIssues(reminders);

        // 取证实测：CorelDRAW X4 的 SVG 通道装不下 CMYK（IESVG.flt 里 CMYK 字样 0 次），
        // 所以这份文件里的颜色到对方手里只剩屏幕近似值。这句话必须在摘要里说，不能等印坏了再问。
        if (request.Source.Template.Elements.Any(e => e.InkColor?.Entry == Core.Colors.ColorEntrySpace.Cmyk))
        {
            reminders.Add("SVG 这条通道带不动 CMYK，元素墨量到这里只剩屏幕近似色；要给印刷店准确的墨量，请另出 PDF 或 TIFF 的「CMYK 四版」。");
        }

        // 一枚一图时数的是“真会写出几个文件”：只导两页却按整批 LabelCount 算上限，会把合法请求误拦下
        var totalFiles = options.Mode == SvgExportMode.PerLabel
            ? request.PageIndexes.Sum(i => request.Plan.PlacementsOnPage(i + 1).Count)
            : request.PageIndexes.Count;
        if (totalFiles > MaxSvgFiles)
        {
            return ExportOutcome.Fail($"这一次会写出约 {totalFiles} 个 SVG，超过 {MaxSvgFiles} 个上限；请改成整页一图或分批导出。");
        }

        var notes = new List<string>(reminders);
        var files = new List<string>();
        var total = 0L;
        var elements = 0;
        try
        {
            Directory.CreateDirectory(directory);
            var done = 0;
            foreach (var index in request.PageIndexes)
            {
                token.ThrowIfCancellationRequested();
                done++;
                progress?.Report($"正在写出 SVG 第 {index + 1} 页（{done}/{request.PageIndexes.Count}）…");
                foreach (var file in SheetSvgWriter.WritePage(request, index, options))
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Combine(directory, file.FileName);
                    var bytes = new UTF8Encoding(false).GetBytes(file.Xml);
                    File.WriteAllBytes(path, bytes);
                    files.Add(path);
                    total += bytes.Length;
                    elements += file.ElementCount;
                    foreach (var note in file.Notes)
                    {
                        if (!notes.Contains(note, StringComparer.Ordinal)) notes.Add(note);
                    }
                }
            }

            var summary = string.Format(CultureInfo.InvariantCulture,
                "写出 {0} 个 SVG · {1} · 共 {2} 个矢量元素 · 合计 {3}",
                files.Count, options.Describe(), elements, FormatSize(total));
            if (notes.Count > 0)
            {
                summary += "｜" + string.Join(" ｜ ", notes.Take(4)) + (notes.Count > 4 ? $" …等 {notes.Count} 条提示" : string.Empty);
            }
            return new ExportOutcome(true, null, files, total, summary);
        }
        catch (OperationCanceledException)
        {
            foreach (var file in files) TryDeletePartial(file);
            return ExportOutcome.Fail("导出已取消，已写出的 SVG 已删除。");
        }
        catch (Exception ex)
        {
            return ExportOutcome.Fail($"导出 SVG 失败：{ex.Message}");
        }
    }

    /// <summary>PDF 摘要里那一格怎么说。CMYK 开着时用户选的 JPEG/无损那档不适用，必须说清楚。</summary>
    private static string DescribePdfRaster(SheetExportRequest request) => request.CmykPlates
        ? "CMYK 四版 · 无损（Flate）· 0=无墨"
        : request.RasterKind == PdfImageKind.Jpeg ? "JPEG" : "无损";

    /// <summary>粗估 PDF 体积，让用户在按下去之前就知道要等多久、U 盘装不装得下（每像素按 0.09 字节估，线稿 JPEG 的经验值）。</summary>
    public static string EstimatePdfSize(SheetExportRequest request)
    {
        var perPage = (long)(request.SheetWidthPx * (double)request.SheetHeightPx * 0.09);
        if (request.CmykPlates) perPage = (long)(perPage * 3.2);           // 四通道无损，比 RGB 那份再大一档
        else if (request.RasterKind == PdfImageKind.Rgb24) perPage = (long)(perPage * 2.5);
        var total = perPage * Math.Max(1, request.PageIndexes.Count);
        return FormatSize(total) + "（估算）";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        return bytes + " B";
    }

    /// <summary>Windows 文件名非法字符 + 两端空格点号，一次处理干净。</summary>
    public static string SafeFileName(string raw)
    {
        var trimmed = (raw ?? string.Empty).Trim().TrimEnd('.');
        foreach (var illegal in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(illegal, '_');
        }
        trimmed = trimmed.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
        if (trimmed.Length == 0) trimmed = "LabelGou";
        return trimmed.Length > 80 ? trimmed[..80] : trimmed;
    }

    private static ExportOutcome? Validate(SheetExportRequest request, bool requireDiskSpaceHint = false)
    {
        var issues = new List<string>();
        request.CollectIssues(issues);
        if (requireDiskSpaceHint && (long)request.PageIndexes.Count * request.SheetWidthPx * request.SheetHeightPx > 6L * 1024 * 1024 * 1024)
        {
            issues.Add("这次导出的像素总量过大，请降低 DPI 或分批导出。");
        }
        return issues.Count == 0 ? null : ExportOutcome.Fail(string.Join(" ", issues));
    }

    private static void TryDeletePartial(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就在摘要里说明，不掩盖主错误 */ }
    }
}
