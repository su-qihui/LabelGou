using System.Globalization;
using System.IO.Compression;
using System.Text;
using LabelGou.Core.Units;

namespace LabelGou.Core.Export;

public enum PdfImageKind
{
    /// <summary>已经编码好的 JPEG 字节流，直接以 DCTDecode 嵌入。</summary>
    Jpeg,
    /// <summary>未压缩的 24 位 RGB 逐行像素（WPF 位图拷出来的样子），由本类做 Flate 压缩。</summary>
    Rgb24,
}

/// <summary>
/// 一页 PDF = 一个页面尺寸（毫米）+ 一张铺满页面的位图。
/// 位图由 App 层用与预览完全相同的画法渲染出来，因此"看到什么打出来就是什么"；
/// 尺寸精度只取决于毫米→磅换算与像素密度，不取决于任何第三方排版引擎。
/// </summary>
public sealed record PdfPageImage(
    double WidthMm,
    double HeightMm,
    int PixelWidth,
    int PixelHeight,
    PdfImageKind Kind,
    byte[] Data)
{
    public void CollectIssues(string label, IList<string> issues)
    {
        if (!double.IsFinite(WidthMm) || WidthMm <= 0 || !double.IsFinite(HeightMm) || HeightMm <= 0)
            issues.Add($"{label}：页面尺寸无效（{WidthMm}×{HeightMm}mm）。");
        if (PixelWidth <= 0 || PixelHeight <= 0)
            issues.Add($"{label}：图像像素尺寸无效（{PixelWidth}×{PixelHeight}px）。");
        if (Data is null || Data.Length == 0)
        {
            issues.Add($"{label}：页图为空，无法写入 PDF。");
            return;
        }
        if (Kind == PdfImageKind.Rgb24 && (long)Data.Length != (long)PixelWidth * PixelHeight * 3)
            issues.Add($"{label}：RGB 像素字节数 {Data.Length} 与 {PixelWidth}×{PixelHeight}px 不符。");
    }
}

/// <summary>文档信息字典的最小内容。默认不写时间戳，保证同样输入产出同样字节（便于比对与复现）。</summary>
public sealed class PdfWriteOptions
{
    public string Producer { get; init; } = "LabelGou";
    public string? Title { get; init; }
    public string? CreatorTool { get; init; }

    /// <summary>形如 D:20260906120000+08'00'；留空则不写 /CreationDate。</summary>
    public string? CreationDate { get; init; }
}

/// <summary>
/// 自写的极简 PDF 1.4 写入器（图像型 PDF，印刷交付常用形态）。
/// <para>
/// 对象编号固定：<c>1</c>=Catalog、<c>2</c>=Pages、每页三个（Page / 内容流 / 图像 XObject）、最后是 Info。
/// Catalog 与 Pages 故意留到最后才落盘（那时才知道总页数），PDF 不要求对象按编号顺序排列，
/// 定位全靠 xref 表，所以这样写既不用回填偏移也不用 seeks。
/// </para>
/// <para>
/// 只支持"每页一张铺满的位图"：版面已经是 ImpositionEngine 算好的结果，PDF 只需忠实承载，
/// 不该再排一次版——这也是不引第三方排版库的理由（见对接文档 §八 第 3 棒定案）。
/// </para>
/// </summary>
public sealed class PdfImageWriter : IDisposable
{
    public const int MaxPages = 20000;
    private static readonly byte[] BinaryHint = { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A };    // "%âãÏÓ\n"

    private readonly Stream _stream;
    private readonly List<long> _offsets = new();      // _offsets[编号] = 该对象起始位置
    private bool _finished;

    public PdfImageWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        WriteAscii("%PDF-1.4\n");
        _stream.Write(BinaryHint, 0, BinaryHint.Length);
        _offsets.Add(0);        // 0 号是空闲表项
        _offsets.Add(0);        // 1 Catalog（Finish 时写）
        _offsets.Add(0);        // 2 Pages（Finish 时写）
    }

    public int PageCount { get; private set; }

    public static int PageObjectNumber(int pageIndex) => 3 + pageIndex * 3;
    public static int ContentObjectNumber(int pageIndex) => 4 + pageIndex * 3;
    public static int ImageObjectNumber(int pageIndex) => 5 + pageIndex * 3;

    public static void CollectIssues(IReadOnlyList<PdfPageImage> pages, IList<string> issues)
    {
        if (pages is null || pages.Count == 0) issues.Add("没有可写入的页面。");
        if (pages?.Count > MaxPages) issues.Add($"页数 {pages.Count} 超过上限 {MaxPages}，请分批导出。");
        if (pages is null) return;
        for (var i = 0; i < pages.Count; i++) pages[i].CollectIssues($"第 {i + 1} 页", issues);
    }

    /// <summary>追加一页。页图不合格会抛 <see cref="InvalidDataException"/>，此时流里已写入的内容不可恢复，应放弃该文件。</summary>
    public void AddPage(PdfPageImage page)
    {
        ThrowIfFinished();
        if (PageCount >= MaxPages)
            throw new InvalidDataException($"页数超过上限 {MaxPages}，请分批导出。");

        var issues = new List<string>();
        page.CollectIssues($"第 {PageCount + 1} 页", issues);
        if (issues.Count > 0) throw new InvalidDataException(string.Join(" ", issues));

        var index = PageCount;
        // 本页的三个对象编号先占位，下面逐个回填偏移
        _offsets.Add(0);
        _offsets.Add(0);
        _offsets.Add(0);

        var widthPt = Fmt(Mm.MmToPoint(page.WidthMm));
        var heightPt = Fmt(Mm.MmToPoint(page.HeightMm));

        _offsets[PageObjectNumber(index)] = Position;
        WriteObject(PageObjectNumber(index),
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPt} {heightPt}] " +
            $"/Resources << /XObject << /Im0 {ImageObjectNumber(index)} 0 R >> >> " +
            $"/Contents {ContentObjectNumber(index)} 0 R >>");

        var content = Encoding.ASCII.GetBytes($"q {widthPt} 0 0 {heightPt} 0 0 cm /Im0 Do Q\n");
        _offsets[ContentObjectNumber(index)] = Position;
        WriteStreamObject(ContentObjectNumber(index), content, $"<< /Length {content.Length} >>");

        _offsets[ImageObjectNumber(index)] = Position;
        WriteImageObject(ImageObjectNumber(index), page);

        PageCount++;
    }

    /// <summary>写 Catalog / Pages / Info / xref / trailer，返回已写字节数。</summary>
    public long Finish(PdfWriteOptions? options = null)
    {
        ThrowIfFinished();
        if (PageCount == 0) throw new InvalidDataException("一页都没写，不能收尾。");
        var opt = options ?? new PdfWriteOptions();

        var infoObject = 3 + PageCount * 3;
        var objectCount = infoObject + 1;

        var kids = new StringBuilder();
        for (var i = 0; i < PageCount; i++)
        {
            if (i > 0) kids.Append(' ');
            kids.Append(PageObjectNumber(i)).Append(" 0 R");
        }

        _offsets[1] = Position;
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");

        _offsets[2] = Position;
        WriteObject(2, $"<< /Type /Pages /Kids [{kids}] /Count {PageCount} >>");

        var info = new StringBuilder("<<");
        AppendString(info, "Producer", opt.Producer);
        AppendString(info, "Creator", string.IsNullOrWhiteSpace(opt.CreatorTool) ? opt.Producer : opt.CreatorTool!);
        if (!string.IsNullOrWhiteSpace(opt.Title)) AppendString(info, "Title", opt.Title);
        if (!string.IsNullOrWhiteSpace(opt.CreationDate)) AppendString(info, "CreationDate", opt.CreationDate!);
        info.Append(" >>");
        _offsets.Add(0);
        _offsets[infoObject] = Position;
        WriteObject(infoObject, info.ToString());

        var xrefPosition = Position;
        WriteAscii($"xref\n0 {objectCount}\n");
        WriteAscii("0000000000 65535 f \n");
        for (var number = 1; number < objectCount; number++)
        {
            WriteAscii(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", _offsets[number]));
        }

        WriteAscii("trailer\n");
        WriteAscii($"<< /Size {objectCount} /Root 1 0 R /Info {infoObject} 0 R >>\n");
        WriteAscii("startxref\n");
        WriteAscii(string.Format(CultureInfo.InvariantCulture, "{0}\n", xrefPosition));
        WriteAscii("%%EOF\n");
        _stream.Flush();

        _finished = true;
        return Position;
    }

    private long Position
    {
        get
        {
            if (_stream.CanSeek) return _stream.Position;
            throw new InvalidOperationException("PdfImageWriter 需要可定位的输出流。");
        }
    }

    private void ThrowIfFinished()
    {
        if (_finished) throw new InvalidOperationException("该 PDF 已收尾，不能再追加页面。");
    }

    private void WriteImageObject(int number, PdfPageImage page)
    {
        byte[] payload;
        string dictionary;
        if (page.Kind == PdfImageKind.Jpeg)
        {
            payload = page.Data;
            dictionary = ImageDictionary(page, "/Filter /DCTDecode", payload.Length, null);
        }
        else
        {
            payload = ZlibCompress(page.Data);
            dictionary = ImageDictionary(page, "/Filter /FlateDecode", payload.Length,
                $" /DecodeParms << /Colors 3 /BitsPerComponent 8 /Columns {page.PixelWidth} >>");
        }
        WriteStreamObject(number, payload, dictionary);
    }

    private static string ImageDictionary(PdfPageImage page, string filter, int length, string? decodeParms)
        => $"<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} " +
           $"/ColorSpace /DeviceRGB /BitsPerComponent 8 {filter} /Interpolate false" +
           $"{decodeParms} /Length {length} >>";

    private void WriteStreamObject(int number, byte[] payload, string dictionary)
    {
        WriteAscii($"{number} 0 obj\n{dictionary}\nstream\n");
        _stream.Write(payload, 0, payload.Length);
        WriteAscii("\nendstream\nendobj\n");
    }

    private void WriteObject(int number, string dictionary)
        => WriteAscii($"{number} 0 obj\n{dictionary}\nendobj\n");

    private void WriteAscii(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        _stream.Write(bytes, 0, bytes.Length);
    }

    private static void AppendString(StringBuilder target, string key, string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        target.Append(" /").Append(key).Append(" (").Append(escaped).Append(')');
    }

    /// <summary>坐标 4 位小数足够（1/72 英寸以下没人能量得出来），同时避免科学计数法写进 PDF。</summary>
    private static string Fmt(double value)
        => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>DeflateStream 只给裸 deflate，而 PDF 的 FlateDecode 要 zlib 外壳（2 字节头 + 4 字节 Adler-32）。</summary>
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(0x78);
        buffer.WriteByte(0x01);
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }
        var checksum = new byte[4];
        WriteBigEndian(checksum, Adler32(raw));
        buffer.Write(checksum, 0, 4);
        return buffer.ToArray();
    }

    private static void WriteBigEndian(byte[] target, uint value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        const uint modulus = 65521;
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }
        return (b << 16) | a;
    }

    public void Dispose()
    {
        if (!_finished && PageCount > 0)
        {
            // 半途而废的文件没人要：不写 xref，调用方应当丢弃这个流。
            _finished = true;
        }
    }
}

/// <summary>一次性写完（页数少、便于单测）的便捷入口；大任务请直接用 <see cref="PdfImageWriter"/> 逐页流式写。</summary>
public static class PdfImageDocument
{
    public static void CollectIssues(IReadOnlyList<PdfPageImage> pages, IList<string> issues)
        => PdfImageWriter.CollectIssues(pages, issues);

    public static bool TryWrite(IReadOnlyList<PdfPageImage> pages, PdfWriteOptions? options, out byte[] pdf, out string? error)
    {
        pdf = Array.Empty<byte>();
        var issues = new List<string>();
        PdfImageWriter.CollectIssues(pages, issues);
        error = issues.Count == 0 ? null : string.Join(" ", issues);
        if (error is not null) return false;

        using var stream = new MemoryStream();
        using (var writer = new PdfImageWriter(stream))
        {
            foreach (var page in pages) writer.AddPage(page);
            writer.Finish(options);
        }
        pdf = stream.ToArray();
        return true;
    }
}
