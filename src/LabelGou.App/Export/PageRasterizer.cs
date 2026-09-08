using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.App.Rendering;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.Export;

/// <summary>
/// 一页位图从哪来、按什么密度出。
/// <para>
/// 关键口径：Core 与 <see cref="SheetRenderer"/> 都在"96 DPI 的设备无关单位"里按毫米画真实尺寸，
/// 分辨率完全交给 <see cref="RenderTargetBitmap"/> 的 dpi 参数。因此 300DPI 与 600DPI 之间
/// 只有像素多少的区别，没有第二套坐标换算——这是 §五-1（WPF 打印 DPI 与坐标换算）风险的根源性防法。
/// </para>
/// </summary>
public static class PageRasterizer
{
    public const double ReferenceDpi = 96.0;

    public static int PixelsForMillimetres(double millimetres, double dpi)
        => Math.Max(1, (int)Math.Round(Mm.ToPixels(millimetres, dpi), MidpointRounding.AwayFromZero));

    /// <summary>把一页整版按指定 DPI 栅格化。scale 恒为 1（真实尺寸），分辨率由 dpi 决定。</summary>
    public static RenderTargetBitmap RenderPage(
        SheetPlan plan,
        int pageIndex,
        double dpi,
        Func<int, LabelLayout?>? layoutProvider,
        bool showElementGuides,
        PageRenderPurpose purpose,
        bool includeTrimMarks)
    {
        if (dpi < 72 || dpi > 2400)
            throw new ArgumentOutOfRangeException(nameof(dpi), $"DPI {dpi} 超出可用范围（72~2400）。");

        var width = PixelsForMillimetres(plan.PageWidthMm, dpi);
        var height = PixelsForMillimetres(plan.PageHeightMm, dpi);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            SheetRenderer.DrawPage(dc, plan, pageIndex, 1.0, layoutProvider, showElementGuides,
                purpose, dpi / ReferenceDpi, includeTrimMarks);
        }

        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>用于打印：真实尺寸的 DrawingVisual（96DPI 口径），由驱动决定最终点密度。</summary>
    public static DrawingVisual BuildPrintVisual(
        SheetPlan plan,
        int pageIndex,
        Func<int, LabelLayout?>? layoutProvider,
        bool includeTrimMarks,
        double fitScale)
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        if (Math.Abs(fitScale - 1.0) > 0.0005)
        {
            dc.PushTransform(new ScaleTransform(fitScale, fitScale));
        }
        SheetRenderer.DrawPage(dc, plan, pageIndex, 1.0, layoutProvider, false,
            PageRenderPurpose.Printer, 1.0, includeTrimMarks);
        if (Math.Abs(fitScale - 1.0) > 0.0005) dc.Pop();
        return visual;
    }

    public static byte[] EncodePng(BitmapSource bitmap) => Encode(bitmap, new PngBitmapEncoder());

    public static byte[] EncodeJpeg(BitmapSource bitmap, int quality = 92)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        return Encode(bitmap, encoder);
    }

    /// <summary>PDF 的 FlateDecode 需要的原始 24 位 RGB 逐行像素（自上而下，与 PDF 图像采样顺序一致）。</summary>
    public static byte[] CopyRgb24(BitmapSource bitmap)
    {
        var converted = bitmap.Format == PixelFormats.Rgb24
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Rgb24, null, 0);
        converted.Freeze();
        var stride = converted.PixelWidth * 3;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>把若干页拼成一个多帧 TIFF（一张文件、每页一帧，印厂常用交付形态）。LZW 无损且彩/灰都支持。</summary>
    public static byte[] EncodeTiff(IReadOnlyList<BitmapSource> frames)
    {
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
        foreach (var frame in frames) encoder.Frames.Add(BitmapFrame.Create(frame));
        return EncodeToBytes(encoder);
    }

    private static byte[] Encode(BitmapSource bitmap, BitmapEncoder encoder)
    {
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return EncodeToBytes(encoder);
    }

    private static byte[] EncodeToBytes(BitmapEncoder encoder)
    {
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

/// <summary>
/// 渲染快照：把"这一批标签长什么样"固定在启动导出的那一刻。
/// 后台线程只读它，不回头碰 ViewModel（<c>BuildLayoutFor</c> 读的是 UI 线程持有的字段，跨线程直接用会打架）。
/// </summary>
public sealed class PageContentSource
{
    private readonly LabelTemplate _template;
    private readonly IReadOnlyList<MarkRecord> _records;
    private readonly MarkTextCase _textCase;

    /// <param name="textCase">
    /// 这批标签的大小写口径。它跟模板与记录一起在这个时刻<strong>固定下来</strong>：
    /// 后台线程跑到一半用户改了下拉，也不能让同一批 PDF 里前几页大写、后几页小写。
    /// </param>
    public PageContentSource(LabelTemplate template, IReadOnlyList<MarkRecord> records, string sourcePath,
        MarkTextCase textCase = MarkTextCase.AsSource)
    {
        _template = template;
        _records = records;
        _textCase = textCase;
        SourceName = Path.GetFileName(sourcePath ?? string.Empty);
    }

    public string SourceName { get; }

    /// <summary>快照里的模板（SVG 出口要说清“这些字从哪几个字段来”，只能问它）。</summary>
    public LabelTemplate Template => _template;

    public int LabelCount => _records.Count;

    private int? _unconfirmedCount;

    /// <summary>
    /// 含「需人工核对」字段的标签张数（§七-11 的打印闸门输入）。
    /// 快照不可变，所以算一次就缓存；没数据时返回 0 不拦人。
    /// </summary>
    public int UnconfirmedLabelCount => _unconfirmedCount ??= CountUnconfirmed();

    private int CountUnconfirmed()
    {
        var count = 0;
        for (var i = 1; i <= _records.Count; i++)
        {
            if (BuildAt(i)?.HasUnconfirmed == true) count++;
        }
        return count;
    }

    /// <summary>标签序号（1 起）→ 版面。这里没有缓存：每页只算它用到的那几枚，内存保持平稳。</summary>
    public LabelLayout? BuildAt(int labelIndex)
    {
        if (labelIndex < 1 || labelIndex > _records.Count) return null;
        var context = new LayoutContext(labelIndex, Math.Max(1, _records.Count), SourceName, TextCase: _textCase);
        return LayoutEngine.Build(_template, _records[labelIndex - 1], context);
    }

    public Func<int, LabelLayout?> AsProvider() => BuildAt;
}
