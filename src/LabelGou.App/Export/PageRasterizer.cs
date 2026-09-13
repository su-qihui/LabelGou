using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.App.Rendering;
using LabelGou.Core.Colors;
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
        bool includeTrimMarks,
        InkPlate plate = InkPlate.None)
    {
        if (dpi < 72 || dpi > 2400)
            throw new ArgumentOutOfRangeException(nameof(dpi), $"DPI {dpi} 超出可用范围（72~2400）。");

        var width = PixelsForMillimetres(plan.PageWidthMm, dpi);
        var height = PixelsForMillimetres(plan.PageHeightMm, dpi);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            SheetRenderer.DrawPage(dc, plan, pageIndex, 1.0, layoutProvider, showElementGuides,
                purpose, dpi / ReferenceDpi, includeTrimMarks, plate);
        }

        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 一页 CMYK 像素：<strong>四通道、每通道 8 位、逐像素交错（C M Y K C M Y K…）、ink-direct
    /// （0 = 无墨，255 = 满墨）</strong>。第 48 棒两条位图出口（TIFF / PDF）共用这一份，
    /// 免得两条出口各自分色再各自错一遍。
    /// <para>
    /// 为什么画四遍而不把渲染好的 RGB 逐像素反算成 CMYK：<see cref="CmykMath"/> 那对公式折回来
    /// <strong>不是同一套配墨</strong>（CMYK 37/63/11/5 反算是 29/58/0/15），反算等于把用户亲手填的
    /// 四个数换掉——那是 47 棒立"两端并存"要防的事。所以每一版只画"这一版该上的那些墨"
    /// （<see cref="LayoutEngine"/> 按 <see cref="InkPlate"/> 把每支墨折成一块灰），取反就是这一版的墨量。
    /// </para>
    /// <para>
    /// <strong>已知限制：元素互相盖住时，上面那块把下面的挖掉（knockout）</strong>——四版分别是四张灰图，
    /// 后画的盖前画的，与印刷里非叠印的默认行为一致；叠印（overprint）本软件还没有这个概念。
    /// </para>
    /// <para>
    /// <strong>实测到的第二条限制：字身边缘会有 ±2/255（约 0.8% 墨）的串色</strong>。
    /// 一支纯黑的字（只该落在 K 版）在青/品/黄三版上留下峰值 2 的边——不是我们的换算漏了，
    /// 是 WPF 文本渲染在 sRGB↔线性往返时的舍入（满格处也有 ±1~2）。0.8% 低于多数 RIP 的最小网点，
    /// 但"分色版上有 0 以外的值"这件事必须先写在明面上：真要抠到逐位干净，得改成自己算灰版，
    /// 那是独立一棒的量级（见《LabelGou-AI对接文档》§六）。
    /// </para>
    /// </summary>
    public static CmykPage RenderCmykPage(
        SheetPlan plan, int pageIndex, double dpi,
        Func<InkPlate, Func<int, LabelLayout?>> plateProviders, bool includeTrimMarks)
    {
        var plates = new[] { InkPlate.Cyan, InkPlate.Magenta, InkPlate.Yellow, InkPlate.Black };
        byte[]? pixels = null;
        var width = 0;
        var height = 0;
        for (var p = 0; p < plates.Length; p++)
        {
            var bitmap = RenderPage(plan, pageIndex, dpi, plateProviders(plates[p]), false,
                PageRenderPurpose.Image, includeTrimMarks, plates[p]);
            if (pixels is null)
            {
                width = bitmap.PixelWidth;
                height = bitmap.PixelHeight;
                pixels = new byte[width * height * 4];
            }
            else if (bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            {
                throw new InvalidDataException(
                    $"第 {pageIndex + 1} 页四版尺寸对不上（{bitmap.PixelWidth}×{bitmap.PixelHeight} ≠ {width}×{height}）。");
            }
            FillPlane(bitmap, pixels, p);
        }
        return new CmykPage(width, height, pixels!);
    }

    /// <summary>把一张灰版（Pbgra32，白底）搬进四通道缓冲的第 channel 个平面：墨量 = 255 - 红通道。</summary>
    private static void FillPlane(BitmapSource plate, byte[] target, int channel)
    {
        var stride = plate.PixelWidth * 4;
        var src = new byte[stride * plate.PixelHeight];
        plate.CopyPixels(src, stride, 0);
        for (var i = 0; i < plate.PixelWidth * plate.PixelHeight; i++)
            target[i * 4 + channel] = (byte)(255 - src[i * 4 + 2]);      // Pbgra32 里红在第三个字节
    }

    /// <summary>一页的 CMYK 像素（ink-direct）。Width×Height×4 字节。</summary>
    public sealed record CmykPage(int Width, int Height, byte[] Pixels)
    {
        /// <summary>按版拆成四条单通道平面（PDF 与 TIFF 各自要的形状）。</summary>
        public byte[][] ToPlanes()
        {
            var planes = new byte[4][];
            for (var c = 0; c < 4; c++) planes[c] = new byte[Width * Height];
            for (var i = 0; i < Width * Height; i++)
                for (var c = 0; c < 4; c++) planes[c][i] = Pixels[i * 4 + c];
            return planes;
        }
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

    /// <summary>
    /// 墨迹越界最多量多少张：每量一张要为它的文本建 <c>FormattedText</c>，
    /// 几万套标签会把出纸前那一刻冻住，所以设上限，超出的部分由闸门如实说"只抽查了前 N 张"。
    /// </summary>
    public const int InkScanCap = 2000;

    private int? _inkOverflowCount;

    /// <summary>有几张标签的<strong>文字墨迹探出纸边</strong>（第 46 棒：永不折行后接手行带那份保护）。算一次就缓存。</summary>
    public int InkOverflowLabelCount => _inkOverflowCount ??= CountInkOverflow();

    /// <summary>这批是否被抽查上限截过（截过就不能说"全部量过"）。</summary>
    public bool InkScanTruncated => _records.Count > InkScanCap;

    private int CountInkOverflow()
    {
        var count = 0;
        for (var i = 1; i <= Math.Min(InkScanCap, _records.Count); i++)
        {
            var layout = BuildAt(i);
            if (layout is not null && Rendering.TextInkBox.OverflowMm(layout) > Core.Templates.TemplateValidator.ToleranceMm) count++;
        }
        return count;
    }

    private int CountUnconfirmed()
    {
        var count = 0;
        for (var i = 1; i <= _records.Count; i++)
        {
            if (BuildAt(i)?.HasUnconfirmed == true) count++;
        }
        return count;
    }

    /// <summary>
    /// 标签序号（1 起）→ 版面。这里没有缓存：每页只算它用到的那几枚，内存保持平稳。
    /// <para><paramref name="plate"/> 非 None 只发生在第 48 棒的分色光栅里（那一版该上多少墨，
    /// 由 <see cref="LayoutEngine"/> 折算，渲染端不判颜色归属）。</para>
    /// </summary>
    public LabelLayout? BuildAt(int labelIndex, InkPlate plate = InkPlate.None)
    {
        if (labelIndex < 1 || labelIndex > _records.Count) return null;
        var context = new LayoutContext(labelIndex, Math.Max(1, _records.Count), SourceName,
            TextCase: _textCase, Plate: plate);
        return LayoutEngine.Build(_template, _records[labelIndex - 1], context);
    }

    public Func<int, LabelLayout?> AsProvider() => index => BuildAt(index);

    /// <summary>分色用的提供者：先挑版，再挑标签。四遍渲染各自拿一条，别共用（会串版）。</summary>
    public Func<InkPlate, Func<int, LabelLayout?>> AsPlateProvider() => plate => index => BuildAt(index, plate);
}
