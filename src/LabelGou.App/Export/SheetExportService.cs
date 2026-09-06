using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using LabelGou.App.Rendering;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;

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

    public int JpegQuality { get; init; } = 92;

    public void CollectIssues(IList<string> issues)
    {
        if (PageIndexes is null || PageIndexes.Count == 0) issues.Add("没有选中任何一页可导出。");
        if (Dpi < 72 || Dpi > 2400) issues.Add($"DPI {Dpi} 超出可用范围（72~2400）。");
        if (string.IsNullOrWhiteSpace(BaseName)) issues.Add("文件名不能为空。");
        if (Plan.PerPage <= 0) issues.Add("纸规放不下任何一枚标签，请先调整拼版设置。");
        foreach (var index in PageIndexes ?? Array.Empty<int>())
        {
            if (index < 0 || index >= Math.Max(1, Plan.PageCount))
            {
                issues.Add($"页序号 {index + 1} 超出整版页数（共 {Plan.PageCount} 页）。");
                break;
            }
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
/// 三种出片出口：PDF（打印交付）、逐页 PNG（发图方便）、多帧 TIFF（印厂收图）。
/// 三者共用 <see cref="PageRasterizer"/> 与 <see cref="SheetRenderer"/>，与预览同一套画法。
/// </summary>
public static class SheetExportService
{
    /// <summary>TIFF 是一次性在内存里攒帧的，页数必须设限，否则大任务直接 OOM。</summary>
    public const int MaxTiffFrames = 40;

    public static string ProducerName => "LabelGou " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev");

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
            var done = 0;
            foreach (var index in request.PageIndexes)
            {
                token.ThrowIfCancellationRequested();
                done++;
                progress?.Report($"正在渲染 PDF 第 {index + 1} 页（{done}/{request.PageIndexes.Count}）…");
                var bitmap = PageRasterizer.RenderPage(request.Plan, index + 1, request.Dpi,
                    request.Source.AsProvider(), false, PageRenderPurpose.Image, request.IncludeTrimMarks);
                var data = request.RasterKind == PdfImageKind.Jpeg
                    ? PageRasterizer.EncodeJpeg(bitmap, request.JpegQuality)
                    : PageRasterizer.CopyRgb24(bitmap);
                writer.AddPage(new PdfPageImage(request.Plan.PageWidthMm, request.Plan.PageHeightMm,
                    bitmap.PixelWidth, bitmap.PixelHeight, request.RasterKind, data));
            }
            var bytes = writer.Finish(options);
            var summary = string.Format(CultureInfo.InvariantCulture,
                "{0} 导出 {1} 页 · {2}DPI · {3} · {4}（页面 {5}×{6}mm）",
                Path.GetFileName(filePath), request.PageIndexes.Count, request.Dpi,
                request.RasterKind == PdfImageKind.Jpeg ? "JPEG" : "无损", FormatSize(bytes),
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
                "写出 {0} 个 PNG · {1}DPI · 合计 {2}", files.Count, request.Dpi, FormatSize(total));
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
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            foreach (var index in request.PageIndexes)
            {
                token.ThrowIfCancellationRequested();
                var bitmap = PageRasterizer.RenderPage(request.Plan, index + 1, request.Dpi,
                    request.Source.AsProvider(), false, PageRenderPurpose.Image, request.IncludeTrimMarks);
                frames.Add(bitmap);
                progress?.Report($"已渲染 {frames.Count}/{request.PageIndexes.Count} 页…");
            }
            var bytes = PageRasterizer.EncodeTiff(frames);
            File.WriteAllBytes(filePath, bytes);
            var summary = string.Format(CultureInfo.InvariantCulture,
                "{0} 导出 {1} 帧 TIFF · {2}DPI · {3}", Path.GetFileName(filePath), frames.Count, request.Dpi, FormatSize(bytes.Length));
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

    /// <summary>粗估 PDF 体积，让用户在按下去之前就知道要等多久、U 盘装不装得下（每像素按 0.09 字节估，线稿 JPEG 的经验值）。</summary>
    public static string EstimatePdfSize(SheetExportRequest request)
    {
        var perPage = (long)(request.SheetWidthPx * (double)request.SheetHeightPx * 0.09);
        if (request.RasterKind == PdfImageKind.Rgb24) perPage = (long)(perPage * 2.5);
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
