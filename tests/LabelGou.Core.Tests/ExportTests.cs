using System.Globalization;
using System.IO.Compression;
using System.Text;
using LabelGou.Core.Export;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 页范围解析 + 打印落位体检 + 自写 PDF 写入器。
/// PDF 部分不信"看起来能打开"，而是手工解析 xref 表验证每个对象偏移真的对得上——
/// 偏移错一位，印厂的 RIP 就会报"文件损坏"，而这种错误在预览里完全看不出来。
/// </summary>
public class ExportTests
{
    // ---------- PageRange ----------

    [Fact]
    public void EmptyTextMeansAllPages()
    {
        Assert.True(PageRange.TryParse("  ", 5, out var range, out var error));
        Assert.Null(error);
        Assert.True(range!.IsAll);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, range.SelectedPages());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, range.SelectedIndexes());
    }

    [Theory]
    [InlineData("1-3,5", new[] { 1, 2, 3, 5 })]
    [InlineData("5,1,3", new[] { 1, 3, 5 })]
    [InlineData("3-1", new[] { 1, 2, 3 })]              // 手滑写反
    [InlineData("1,1,1", new[] { 1 })]                  // 重复只留一次
    [InlineData("2 4", new[] { 2, 4 })]                 // 空格分隔
    [InlineData("2；4", new[] { 2, 4 })]                // 全角分号
    [InlineData("1-2;3", new[] { 1, 2, 3 })]
    [InlineData("4~6", new[] { 4, 5, 6 })]              // 波浪号
    public void ParsesCommonNotations(string text, int[] expected)
    {
        Assert.True(PageRange.TryParse(text, 10, out var range, out var error), error ?? "ok");
        Assert.Equal(expected, range!.SelectedPages());
    }

    [Theory]
    [InlineData("0")]        // 没有第 0 页
    [InlineData("11")]       // 超出总页数
    [InlineData("abc")]
    [InlineData("1-")]
    [InlineData("-3")]
    public void RejectsBadInputWithHumanMessage(string text)
    {
        Assert.False(PageRange.TryParse(text, 10, out var range, out var error));
        Assert.Null(range);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains("页", error!);
    }

    [Fact]
    public void ZeroPageCountIsExplainedNotCrashed()
    {
        Assert.False(PageRange.TryParse("", 0, out _, out var error));
        Assert.Contains("没有整版", error!);
    }

    [Fact]
    public void DescribeCompressesRuns()
    {
        Assert.True(PageRange.TryParse("1,2,3,7,9,10", 10, out var range, out _));
        Assert.Equal("1-3,7,9-10", range!.Describe());
    }

    // ---------- PrintFit ----------

    [Fact]
    public void ExactWhenPrintableAreaFitsPage()
    {
        var advice = PrintFit.Evaluate(210, 297, 210, 297);
        Assert.Equal(PrintFitLevel.Tight, advice.Level);      // 零余量 = 勉强放得下
        Assert.True(advice.IsSafeToPrintAtOneToOne);
        Assert.Equal(1.0, advice.SuggestedScale);
    }

    [Fact]
    public void ExactWhenPlentyOfRoom()
    {
        var advice = PrintFit.Evaluate(100, 80, 200, 290);
        Assert.Equal(PrintFitLevel.Exact, advice.Level);
        Assert.Empty(advice.Warnings);
    }

    [Fact]
    public void NeedsShrinkForLaserPrinterMargins()
    {
        // 常见激光机：A4 纸但四边各打不掉 5mm
        var advice = PrintFit.Evaluate(210, 297, 200, 287);
        Assert.Equal(PrintFitLevel.NeedsShrink, advice.Level);
        Assert.True(advice.SuggestedScale < 1.0);
        Assert.True(advice.SuggestedScale >= PrintFit.MinimumAcceptableScale);
        Assert.Equal(200.0 / 210.0, Math.Round(advice.SuggestedScale, 3), 3);
    }

    [Fact]
    public void RejectsWhenShrinkWouldLieAboutSize()
    {
        var advice = PrintFit.Evaluate(297, 420, 200, 287);    // A3 版打到只能打 A4 的机器
        Assert.Equal(PrintFitLevel.Rejected, advice.Level);
        Assert.False(advice.IsSafeToPrintAtOneToOne);
        Assert.True(advice.SuggestedScale < PrintFit.MinimumAcceptableScale);
    }

    [Fact]
    public void UnknownPrintableAreaBecomesWarningNotError()
    {
        var advice = PrintFit.Evaluate(210, 297, 0, 0);
        Assert.Equal(PrintFitLevel.Exact, advice.Level);
        Assert.Contains(advice.Warnings, w => w.Contains("没报出"));
    }

    // ---------- PDF 写入器 ----------

    private const int PageW = 210;
    private const int PageH = 297;

    private static PdfPageImage JpegPage(int seed = 1)
    {
        // 假 JPEG（以 SOI 开头即可，写入器不解析图像内容，只负责搬运）
        var bytes = new byte[64];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        for (var i = 2; i < bytes.Length; i++) bytes[i] = (byte)((i * seed) % 251);
        return new PdfPageImage(PageW, PageH, 8, 8, PdfImageKind.Jpeg, bytes);
    }

    private static PdfPageImage RgbPage(int pixelWidth, int pixelHeight, byte fill)
    {
        var bytes = new byte[pixelWidth * pixelHeight * 3];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = fill;
        return new PdfPageImage(PageW, PageH, pixelWidth, pixelHeight, PdfImageKind.Rgb24, bytes);
    }

    [Fact]
    public void WritesHeaderCatalogAndPageCount()
    {
        var ok = PdfImageDocument.TryWrite(new[] { JpegPage(), JpegPage(2) }, null, out var pdf, out var error);
        Assert.True(ok, error);
        var text = Encoding.ASCII.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF", text.TrimEnd());
        Assert.Contains("/Type /Catalog /Pages 2 0 R", text);
        Assert.Contains("/Kids [3 0 R 6 0 R] /Count 2", text);
        Assert.Contains("/Producer (LabelGou)", text);
        Assert.Equal(2, CountOccurrences(text, "/Type /Page /Parent"));
    }

    [Fact]
    public void MediaBoxUsesMillimetreToPointsConversion()
    {
        Assert.True(PdfImageDocument.TryWrite(new[] { JpegPage() }, null, out var pdf, out _));
        var text = Encoding.ASCII.GetString(pdf);
        // 210mm = 595.2756pt，297mm = 841.8898pt（保留 4 位）
        Assert.Contains("/MediaBox [0 0 595.2756 841.8898]", text);
        Assert.Contains("q 595.2756 0 0 841.8898 0 0 cm /Im0 Do Q", text);
    }

    [Fact]
    public void CustomPaperSizeShowsUpInPoints()
    {
        var page = new PdfPageImage(100, 80, 8, 8, PdfImageKind.Jpeg, new byte[] { 0xFF, 0xD8, 1, 2 });
        Assert.True(PdfImageDocument.TryWrite(new[] { page }, null, out var pdf, out _));
        var text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/MediaBox [0 0 283.4646 226.7717]", text);   // 100mm/80mm
    }

    [Fact]
    public void XrefOffsetsReallyPointAtTheirObjects()
    {
        Assert.True(PdfImageDocument.TryWrite(new[] { JpegPage(), RgbPage(2, 2, 0x40) }, null, out var pdf, out _));
        var text = Encoding.ASCII.GetString(pdf);

        var start = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + "startxref".Length)..].Split('\n')[1].Trim(), CultureInfo.InvariantCulture);
        Assert.True(start > 0 && start < pdf.Length, "startxref 指向的位置必须落在文件里");

        var table = text[start..];
        Assert.StartsWith("xref", table);
        var header = table.Split('\n')[1];                       // "0 10"
        var parts = header.Split(' ');
        Assert.Equal("0", parts[0]);
        var size = int.Parse(parts[1], CultureInfo.InvariantCulture);

        var entries = table.Split('\n').Skip(2).Take(size - 1).ToList();
        Assert.Equal(size - 1, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            Assert.Equal(19, entry.Length);     // 19 字符 + 换行 = 规范要求的 20 字节表项
            Assert.EndsWith(i == 0 ? "f " : "n ", entry);
            if (i == 0) continue;                                // 0 号是空闲项
            var offset = int.Parse(entry[..10], CultureInfo.InvariantCulture);
            var expected = $"{i} 0 obj";
            Assert.True(text.Length > offset + expected.Length, $"对象 {i} 的偏移 {offset} 超出文件长度");
            Assert.Equal(expected, text.Substring(offset, expected.Length));
        }
    }

    [Fact]
    public void FlateStreamDecompressesBackToOriginalPixels()
    {
        var source = RgbPage(3, 2, 0xAB);
        Assert.True(PdfImageDocument.TryWrite(new[] { source }, null, out var pdf, out _));

        var text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Filter /FlateDecode", text);
        Assert.Contains("/DecodeParms << /Colors 3 /BitsPerComponent 8 /Columns 3 >>", text);

        var payload = ExtractStream(pdf, text, "/Filter /FlateDecode");
        Assert.Equal(0x78, payload[0]);                                   // zlib 头必须齐
        Assert.Equal(0x01, payload[1]);
        using var input = new MemoryStream(payload, 2, payload.Length - 6);   // 去掉头 2 与尾 Adler 4
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        Assert.Equal(source.Data, output.ToArray());
    }

    [Fact]
    public void JpegPageIsEmbeddedVerbatim()
    {
        var source = JpegPage(7);
        Assert.True(PdfImageDocument.TryWrite(new[] { source }, null, out var pdf, out _));
        var text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Filter /DCTDecode", text);
        var payload = ExtractStream(pdf, text, "/Filter /DCTDecode");
        Assert.Equal(source.Data, payload);
    }

    [Fact]
    public void SameInputProducesSameBytes()
    {
        var pages = new[] { JpegPage(), RgbPage(2, 2, 0x10) };
        Assert.True(PdfImageDocument.TryWrite(pages, null, out var first, out _));
        Assert.True(PdfImageDocument.TryWrite(pages, null, out var second, out _));
        Assert.Equal(first, second);
    }

    [Fact]
    public void StreamedWriterMatchesOneShotWriter()
    {
        var pages = new[] { JpegPage(), JpegPage(3), RgbPage(2, 3, 0x7F) };
        Assert.True(PdfImageDocument.TryWrite(pages, null, out var oneShot, out _));

        using var stream = new MemoryStream();
        using (var writer = new PdfImageWriter(stream))
        {
            foreach (var page in pages) writer.AddPage(page);
            Assert.Equal(3, writer.PageCount);
            writer.Finish();
        }
        Assert.Equal(oneShot, stream.ToArray());
    }

    [Fact]
    public void RejectsBrokenPagesWithReadableErrors()
    {
        var issues = new List<string>();
        PdfImageDocument.CollectIssues(new[]
        {
            new PdfPageImage(0, 0, 0, 0, PdfImageKind.Jpeg, Array.Empty<byte>()),
            new PdfPageImage(210, 297, 2, 2, PdfImageKind.Rgb24, new byte[5]),      // 像素数不够
        }, issues);
        // 页 1 同一条上同报三项（尺寸/像素/空图），页 2 报像素不符
        Assert.Equal(4, issues.Count);
        Assert.Contains(issues, i => i.Contains("页面尺寸无效"));
        Assert.Contains(issues, i => i.Contains("像素尺寸无效"));
        Assert.Contains(issues, i => i.Contains("页图为空"));
        Assert.Contains(issues, i => i.Contains("像素字节数"));
        Assert.Equal(3, issues.Count(i => i.StartsWith("第 1 页", StringComparison.Ordinal)));
        Assert.Single(issues, i => i.StartsWith("第 2 页", StringComparison.Ordinal));

        Assert.False(PdfImageDocument.TryWrite(Array.Empty<PdfPageImage>(), null, out var empty, out var error));
        Assert.Empty(empty);
        Assert.Contains("没有可写入的页面", error!);
    }

    [Fact]
    public void WriterRefusesToWriteAfterFinish()
    {
        using var stream = new MemoryStream();
        var writer = new PdfImageWriter(stream);
        writer.AddPage(JpegPage());
        writer.Finish();
        Assert.Throws<InvalidOperationException>(() => writer.AddPage(JpegPage()));
    }

    [Fact]
    public void WriterThrowsOnBadPageInsteadOfSilentlySkipping()
    {
        using var stream = new MemoryStream();
        using var writer = new PdfImageWriter(stream);
        Assert.Throws<InvalidDataException>(() => writer.AddPage(RgbPage(2, 2, 0x00) with { Data = new byte[3] }));
    }

    /// <summary>从指定字典后面取 stream ... endstream 的原始字节。</summary>
    private static byte[] ExtractStream(byte[] pdf, string text, string dictionaryMarker)
    {
        var markerIndex = text.IndexOf(dictionaryMarker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"找不到 {dictionaryMarker}");
        var head = text.IndexOf("stream\n", markerIndex, StringComparison.Ordinal) + "stream\n".Length;
        var tail = text.IndexOf("\nendstream", head, StringComparison.Ordinal);
        Assert.True(head > 0 && tail > head, "stream 边界不完整");
        var result = new byte[tail - head];
        Array.Copy(pdf, head, result, 0, result.Length);
        return result;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
