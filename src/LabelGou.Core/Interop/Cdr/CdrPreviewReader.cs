using System.Buffers.Binary;
using System.IO.Compression;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Interop.Cdr;

/// <summary>缩略图的两种形态：一种是现成的图片文件，一种是要自己按调色板还原的索引位图。</summary>
public enum CdrPreviewKind
{
    /// <summary>ZIP 里的 <c>thumbnail.*</c> 条目：<c>EncodedBytes</c> 是完整图片文件，交给解码器。</summary>
    EncodedImage = 0,

    /// <summary>RIFF 的 <c>DISP</c> chunk：256 色索引位图，已还原成自上而下的 BGRA32。</summary>
    Argb32 = 1,
}

/// <summary>从 <c>.cdr</c> 里抠出来的一张预览图。</summary>
/// <param name="Kind">形态。</param>
/// <param name="Width">像素宽。</param>
/// <param name="Height">像素高。</param>
/// <param name="EncodedBytes"><see cref="CdrPreviewKind.EncodedImage"/> 时的完整图片字节。</param>
/// <param name="Pixels"><see cref="CdrPreviewKind.Argb32"/> 时的像素（每像素 4 字节 B,G,R,A，行优先，自上而下）。</param>
/// <param name="Origin">来源说明（<c>zip:thumbnail.jpeg</c> / <c>riff:DISP</c>），报告里要说清。</param>
/// <param name="VersionHint"><c>vrsn</c> 报出的格式版本，认不全时为 null。</param>
public sealed record CdrPreview(
    CdrPreviewKind Kind,
    int Width,
    int Height,
    byte[]? EncodedBytes,
    byte[]? Pixels,
    string Origin,
    int? VersionHint = null)
{
    /// <summary>按常见标签尺寸估的等效分辨率（DPI），用来告诉用户"这张只能看不能印"。</summary>
    public double EquivalentDpiAt(double printWidthMm)
    {
        if (printWidthMm <= 0 || Width <= 0) return 0;
        return Width / (printWidthMm / 25.4);
    }
}

/// <summary>一次读取的结果：拿到图、或拿到"为什么拿不到"。</summary>
public sealed record CdrPreviewResult(CdrPreview? Preview, IReadOnlyList<TemplateIssue> Issues)
{
    public bool HasPreview => Preview is not null;

    public bool HasError => Issues.Any(i => i.Severity == IssueLevel.Error);

    public IReadOnlyList<string> ErrorMessages => Issues.ErrorMessages();
}

/// <summary>
/// 从 CorelDRAW 专有 <c>.cdr</c> 里<strong>只取内嵌预览图</strong>（M5 定案丙）。
/// <para>
/// 为什么只做这一件：矢量对象层零依赖读不了——<c>CDR-specification</c> 的 <c>loda</c>
/// （几何+填充+轮廓）小节是空的，<c>coreldraw_cdr.ksy</c> 自己写着
/// "the positions are completely off in newer CDR versions"，而 X6+ 的载荷干脆外置在
/// <c>content/data/*.dat</c>。唯一被证据撑住的可得物就是这张预览图。
/// </para>
/// <para>两条取证路线：</para>
/// <list type="number">
///   <item>X4 起的 <c>.cdr</c> 本质是 ZIP，里面有现成的 <c>thumbnail.*</c> 条目
///   （参照 <c>QuickLook.Plugin.CorelDrawViewer-Thumbnail</c>，Apache-2.0，只用 BCL <see cref="ZipArchive"/>）。</item>
///   <item>更早的裸 RIFF：顶层 <c>DISP</c> chunk 就是一张 256 色位图，布局
///   <c>4B 未知 + 4B 头长 + 4B 宽 + 4B 高 + 28B DIB 剩余 + 1024B 调色板 + 索引</c>
///   ——《CDR-specification》与 ksy 的 <c>disp_chunk_data</c> 两边数字完全对得上（那 28 字节正好是 BITMAPINFOHEADER 的剩余部分）。</item>
/// </list>
/// <para>
/// ⚠ <strong>没有真机样本可证</strong>的部分：行序（BMP 惯例是自下而上，这里按 BMP 处理后再翻正）、
/// 以及 <c>vrsn</c> 版本号与 CDR 商品名的对应。单测只能拿按规范合成的文件自证，真样本待用户验收。
/// </para>
/// </summary>
public static class CdrPreviewReader
{
    /// <summary>单张预览图的像素上限（4 字节/像素）：超过就拒，防一个 20000×20000 的怪文件把内存吃光。</summary>
    public const int MaxPixels = 40_000_000;

    /// <summary>ZIP 里缩略图条目的体积上限。</summary>
    public const long MaxEncodedBytes = 64L * 1024 * 1024;

    public static CdrPreviewResult ReadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"底稿文件打不开：{ex.Message}");
        }
    }

    public static CdrPreviewResult Read(Stream stream)
    {
        var issues = new List<TemplateIssue>();
        byte[] bytes;
        try
        {
            if (stream is FileStream fs)
            {
                if (fs.Length > 1024L * 1024 * 1024)
                    return Fail($"这个 .cdr 有 {fs.Length / 1024.0 / 1024 / 1024:0.#} GB，不像是一枚唛头的底稿，先不读。");
                bytes = new byte[(int)fs.Length];
                var read = fs.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                if (read < bytes.Length) Array.Resize(ref bytes, read);
            }
            else
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return Fail($"底稿读了一半就断了：{ex.Message}");
        }

        if (bytes.Length < 12) return Fail("文件太小（不足 12 字节），不是 .cdr 或者已经损坏。");

        // 路线 ①：ZIP 套现成缩略图
        var zip = TryReadZip(bytes, issues);
        if (zip is not null) return new CdrPreviewResult(zip, issues);

        // 路线 ②：裸 RIFF 的 DISP
        var riff = TryReadRiff(bytes, issues);
        if (riff is not null) return new CdrPreviewResult(riff, issues);

        if (issues.Any(i => i.Severity == IssueLevel.Error)) return new CdrPreviewResult(null, issues);

        var magic = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(4, bytes.Length));
        var why = bytes[0] == 'P' && bytes[1] == 'K'
            ? "这是个 ZIP（CDR 的容器），但里面没有 thumbnail 条目，说明存盘时没带预览图。"
            : magic switch
            {
                _ when magic.StartsWith("WL", StringComparison.OrdinalIgnoreCase) => "这是 1992 年以前的老 CDR（WL 格式），内嵌预览的位置和现代版本不同，当前没有解它。",
                _ when magic.StartsWith("RIFF", StringComparison.Ordinal) => "这是 RIFF 结构的 CDR，但里面没有找到 DISP 预览块——多半是极简文件或版本不同。",
                _ => $"文件头是「{magic}」，不像 CorelDRAW 底稿。",
            };
        issues.Add(new TemplateIssue(IssueLevel.Error,
            why + " 请在装有 CorelDRAW 的机器上把它「文件 → 导出 → SVG」，再导进来就能拿到保真矢量底图。"));
        return new CdrPreviewResult(null, issues);
    }

    // ---------- ZIP ----------

    private static CdrPreview? TryReadZip(byte[] bytes, List<TemplateIssue> issues)
    {
        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K') return null;

        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or NotSupportedException)
        {
            issues.Add(new TemplateIssue(IssueLevel.Warning, $"看着像 ZIP 但打不开（{ex.Message}），改按 RIFF 结构再试一次。"));
            return null;
        }

        using (archive)
        {
            ZipArchiveEntry? best = null;
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Length <= 0 || entry.Length > MaxEncodedBytes) continue;
                if (best is null || Better(entry, best)) best = entry;
            }
            if (best is null) return null;

            try
            {
                using var source = best.Open();
                using var copy = new MemoryStream();
                source.CopyTo(copy);
                var data = copy.ToArray();
                if (data.Length == 0) return null;
                var (width, height) = ProbeSize(data, best.FullName);
                return new CdrPreview(CdrPreviewKind.EncodedImage, width, height, data, null,
                    "zip:" + best.FullName.Replace('\\', '/'));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                issues.Add(new TemplateIssue(IssueLevel.Warning, $"缩略图条目「{best.FullName}」取不出来（{ex.Message}）。"));
                return null;
            }
        }
    }

    /// <summary>同名多个条目时优先真能显示的位图（CDR 有时同时塞 thumbnail.wmf 与 thumbnail.jpeg）。</summary>
    private static bool Better(ZipArchiveEntry candidate, ZipArchiveEntry current)
    {
        static int Rank(string name) => Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" => 3,
            ".bmp" or ".gif" or ".tif" or ".tiff" => 2,
            _ => 1, // wmf/emf/未知：交给解码器赌一把
        };
        return Rank(candidate.FullName) > Rank(current.FullName);
    }

    /// <summary>从图片文件头里读尺寸；读不出来给 0×0，由界面按“尺寸未知”处理。</summary>
    private static (int Width, int Height) ProbeSize(byte[] data, string entryName)
    {
        _ = entryName;
        if (data.Length >= 24 && data[0] == 'B' && data[1] == 'M')
            return (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18)), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22))));
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G')
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20)));
        if (data.Length >= 20 && data[0] == 0xFF && data[1] == 0xD8)
        {
            // JPEG：扫到第一个 SOF 段（段里高在前、宽在后）
            var i = 2;
            while (i + 9 < data.Length)
            {
                if (data[i] != 0xFF) { i++; continue; }
                var marker = data[i + 1];
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    return ((int)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 7)), (int)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 5)));
                var segment = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 2));
                i += 2 + Math.Max(2, (int)segment);
            }
        }
        return (0, 0);
    }

    // ---------- RIFF ----------

    private static CdrPreview? TryReadRiff(byte[] bytes, List<TemplateIssue> issues)
    {
        if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return null;

        var version = 0;
        byte[]? display = null;
        var offset = 12; // RIFF + size + form type
        var guard = 0;
        while (offset + 8 <= bytes.Length && guard++ < 4000)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (size < 0 || offset + 8 + size > bytes.Length) break; // 结构对不上就别硬解了

            var dataStart = offset + 8;
            switch (id)
            {
                case "vrsn" when size >= 2:
                    version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(dataStart, 2));
                    break;
                case "DISP":
                    display = bytes.AsSpan(dataStart, size).ToArray();
                    break;
            }
            if (display is not null) break;
            offset = dataStart + size + (size & 1); // chunk 按偶数字节对齐
        }

        if (display is null) return null;
        var preview = DecodeDisp(display, version);
        if (preview is null)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"找到 DISP 预览块但结构不是已知的 256 色布局（块长 {display.Length} 字节），没硬猜。请改用 SVG 导出。"));
            return null;
        }
        return preview;
    }

    /// <summary>
    /// 解 <c>DISP</c>：<c>4B 未知 + 4B 头长 + u32 宽 + u32 高 + 28B DIB 剩余 + 256×4 调色板 + 索引</c>，
    /// 索引按 BMP 惯例自下而上存储，翻成自上而下输出。
    /// </summary>
    private static CdrPreview? DecodeDisp(byte[] disp, int version)
    {
        const int paletteBytes = 1024;
        if (disp.Length < 44 + paletteBytes) return null;

        var width = BinaryPrimitives.ReadInt32LittleEndian(disp.AsSpan(8, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(disp.AsSpan(12, 4));
        var topDown = height < 0; // BMP 的负高度就是自上而下
        var rows = Math.Abs(height);
        if (width <= 0 || rows <= 0 || (long)width * rows > MaxPixels) return null;

        // DIB 头里：planes 在 16..18、bitcount 在 18..20（与 DISP 的 28 字节"未知"区完全对得上）
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(disp.AsSpan(18, 2));
        var bpp = bitCount == 0 ? 8 : (int)bitCount;    // 每像素多少位，不是字节
        if (bpp is not (1 or 4 or 8)) return null; // 24/32 位没有调色板，本路径只处理索引色

        var paletteStart = 44;
        if (disp.Length - paletteStart < paletteBytes) return null;
        var stride = (width * bpp + 7) / 8;
        var padded = (stride + 3) / 4 * 4;
        var needed = (long)padded * rows;
        var indexStart = paletteStart + paletteBytes;
        if (disp.Length - indexStart < needed)
        {
            // 有些版本不补 4 字节对齐，退回紧排一行试一次
            needed = (long)stride * rows;
            if (disp.Length - indexStart < needed) return null;
            padded = stride;
        }

        var pixels = new byte[width * rows * 4];
        for (var y = 0; y < rows; y++)
        {
            // BMP 自下而上：源里的第 y 行对应输出的第 rows-1-y 行
            var sourceRow = topDown ? y : rows - 1 - y;
            var rowStart = indexStart + sourceRow * padded;
            for (var x = 0; x < width; x++)
            {
                var index = ReadIndex(disp, rowStart, x, bpp);
                var colorStart = paletteStart + index * 4;
                var target = (y * width + x) * 4;
                pixels[target] = disp[colorStart];       // B
                pixels[target + 1] = disp[colorStart + 1]; // G
                pixels[target + 2] = disp[colorStart + 2]; // R
                pixels[target + 3] = 255;
            }
        }
        return new CdrPreview(CdrPreviewKind.Argb32, width, rows, null, pixels, "riff:DISP", version == 0 ? null : version);
    }

    private static int ReadIndex(byte[] data, int rowStart, int column, int bpp)
    {
        // 1/4/8 位索引色的取位方式；bpp 决定一字节里塞了几个像素
        if (bpp == 8)
        {
            var single = rowStart + column;
            return single < data.Length && single >= 0 ? data[single] : 0;
        }
        var perByte = 8 / bpp;
        var byteIndex = rowStart + column / perByte;
        if (byteIndex < 0 || byteIndex >= data.Length) return 0;
        var shift = 8 - bpp * (column % perByte + 1);
        return (data[byteIndex] >> shift) & ((1 << bpp) - 1);
    }

    private static CdrPreviewResult Fail(string message)
        => new(null, new[] { new TemplateIssue(IssueLevel.Error, message) });

    /// <summary>vrsn 版本号 → 人能看懂的 CDR 名字（对应不上的直接报数字，不编）。</summary>
    public static string DescribeVersion(int? version) => version switch
    {
        null => "版本未知",
        200 => "CDR 1.x（1992 年前的 WL 格式）",
        300 => "CorelDRAW 3",
        >= 400 and < 500 => "CorelDRAW 4",
        >= 500 and < 600 => "CorelDRAW 5",
        >= 600 and < 700 => "CorelDRAW 6",
        >= 700 and < 800 => "CorelDRAW 7",
        >= 800 and < 900 => "CorelDRAW 8",
        >= 900 and < 1000 => "CorelDRAW 9",
        >= 1000 and < 1100 => "CorelDRAW 10",
        >= 1100 and < 1200 => "CorelDRAW 11",
        >= 1200 and < 1300 => "CorelDRAW 12",
        >= 1300 and < 1400 => "CorelDRAW X3 (13)",
        >= 1400 and < 1500 => "CorelDRAW X4 (14)",
        >= 1500 and < 1600 => "CorelDRAW X5 (15)",
        >= 1600 and < 1700 => "CorelDRAW X6 (16)",
        >= 1700 and < 1800 => "CorelDRAW X7 (17)",
        >= 1800 and < 1900 => "CorelDRAW 2017 (18)",
        >= 1900 and < 2000 => "CorelDRAW 2018 (19)",
        >= 2000 and < 2100 => "CorelDRAW 2019 (20)",
        >= 2100 and < 2200 => "CorelDRAW 2020 (21)",
        >= 2200 and < 2300 => "CorelDRAW 2021 (22)",
        >= 2300 and < 2400 => "CorelDRAW 2022 (23)",
        >= 2400 and < 2500 => "CorelDRAW 2024 (24)",
        _ => $"CorelDRAW 版本 {version}（比本程序已知的还新，可能有没认出来的布局）",
    };
}
