using System.Text;

namespace LabelGou.Core.Export;

/// <summary>
/// 一帧 CMYK 位图：<strong>逐像素交错四通道（C M Y K C M Y K…），每通道 8 位，
/// ink-direct（0 = 无墨，255 = 满墨）</strong>。由 <c>PageRasterizer.RenderCmykPage</c> 交过来。
/// </summary>
public sealed record CmykTiffFrame(int Width, int Height, byte[] Pixels);

/// <summary>
/// 自写的 CMYK TIFF 写出器（多页、Deflate 压缩）。第 48 棒的黑白稿出口用它。
/// <para>
/// <strong>为什么不用 <c>TiffBitmapEncoder</c></strong>：探针实测（《LabelGou-AI对接文档》§三-阶段 48）
/// WPF 会把 Cmyk32 的字节原样落盘、<c>PhotometricInterpretation=5</c>、<c>SamplesPerPixel=4</c> 都对，
/// 但它<strong>不写 InkSet(333) 也不写 DotRange(336)</strong>——而 CMYK TIFF 的"0 到底是无墨还是满墨"
/// 恰恰是各家实现最容易想当然的地方。少这两个标签，文件就把最要紧的那句话留给读的人猜。
/// 自己写多花 100 行，换来的是约定写在文件里。
/// </para>
/// <para>
/// 写进去的约定：<c>DotRange=[0,255]</c>（TIFF 里 DotRange 的含义就是"这个区间从『没有点』数到『满点』"），
/// 外加一条 ASCII 的 <c>ImageDescription</c> 把同一句话用英文写给肉眼看的人。
/// <strong>刻意不写 InkSet/NumberOfInks/InkNames</strong>：实测（<c>labelgou-other\_probe\cmyk-outlet\verify_outlets.py</c>）
/// 写了它们，libtiff 每次打开这个文件都要打一条 Warning 加一条 Error（它自己数不出配套的 InkNames），
/// 而 Photometric=5 + SamplesPerPixel=4 本来就说明了"这是 CMYK 分层"——多三个标签换不来信息，只换来噪音。
/// </para>
/// </summary>
public static class CmykTiffWriter
{
    /// <summary>一帧的 IFD 条目数（标签见 <see cref="WriteIfd"/>，必须按号升序）。</summary>
    private const int IfdEntries = 16;

    private const int IfdSize = 2 + 12 * IfdEntries + 4;

    /// <summary>ASCII 描述里写死的约定声明（TIFF 的 ASCII 字段只放 7 位字符，中文会碎）。</summary>
    public const string InkConvention =
        "LabelGou CMYK 8-bit, samples are ink amount: 0 = no ink, 255 = 100% ink (DotRange 0..255).";

    public static void CollectIssues(IReadOnlyList<CmykTiffFrame> frames, IList<string> issues)
    {
        if (frames is null || frames.Count == 0) issues.Add("没有可写入的帧。");
        if (frames is null) return;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Width <= 0 || frame.Height <= 0)
                issues.Add($"第 {i + 1} 帧尺寸无效（{frame.Width}×{frame.Height}px）。");
            else if ((long)frame.Pixels.Length != (long)frame.Width * frame.Height * 4)
                issues.Add($"第 {i + 1} 帧 CMYK 字节数 {frame.Pixels.Length} 与 {frame.Width}×{frame.Height}px×4 不符。");
        }
    }

    /// <summary>把若干帧写成一个多页 CMYK TIFF。不合格直接抛，调用方按"这个文件作废"处理。</summary>
    public static byte[] Write(IReadOnlyList<CmykTiffFrame> frames, string software, int dpi)
    {
        var issues = new List<string>();
        CollectIssues(frames, issues);
        if (issues.Count > 0) throw new InvalidDataException(string.Join(" ", issues));
        if (dpi < 72 || dpi > 2400) throw new InvalidDataException($"DPI {dpi} 超出可用范围（72~2400）。");

        var sw = AsciiNul(software);
        var desc = AsciiNul(InkConvention);
        var payloads = new byte[frames.Count][];
        for (var i = 0; i < frames.Count; i++) payloads[i] = Deflate.Compress(frames[i].Pixels);

        // 先排布，再写：TIFF 的 IFD 里全是绝对偏移，算错一个整页就读不出来。
        var bitsOffset = 8 + IfdSize * frames.Count;
        var xResOffset = bitsOffset + 8;
        var yResOffset = xResOffset + 8;
        var swOffset = yResOffset + 8;
        var descOffset = swOffset + sw.Length;
        var cursor = descOffset + desc.Length;
        var stripOffsets = new int[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            stripOffsets[i] = cursor;
            cursor += payloads[i].Length + (payloads[i].Length & 1);      // TIFF 传统偶数对齐
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)0x49); w.Write((byte)0x49);       // "II" = 小端
        w.Write((ushort)42);
        w.Write((uint)8);

        for (var i = 0; i < frames.Count; i++)
        {
            WriteIfd(w, frames[i], bitsOffset, xResOffset, yResOffset, swOffset, sw.Length,
                descOffset, desc.Length, dpi, stripOffsets[i], payloads[i].Length,
                next: i + 1 < frames.Count ? 8 + IfdSize * (i + 1) : 0);
        }

        w.Seek(bitsOffset, SeekOrigin.Begin);
        foreach (var b in new ushort[] { 8, 8, 8, 8 }) w.Write(b);          // BitsPerSample：四通道各 8 位
        w.Write((uint)dpi); w.Write(1u);                                    // XResolution
        w.Write((uint)dpi); w.Write(1u);                                    // YResolution
        w.Write(sw);
        w.Write(desc);
        for (var i = 0; i < frames.Count; i++)
        {
            w.Seek(stripOffsets[i], SeekOrigin.Begin);
            w.Write(payloads[i]);
            if ((payloads[i].Length & 1) != 0) w.Write((byte)0);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteIfd(BinaryWriter w, CmykTiffFrame frame, int bitsOffset,
        int xResOffset, int yResOffset, int swOffset, int swLength, int descOffset, int descLength,
        int dpi, int stripOffset, int stripLength, int next)
    {
        w.Write((ushort)IfdEntries);
        Long(w, 256, (uint)frame.Width);                                   // ImageWidth
        Long(w, 257, (uint)frame.Height);                                 // ImageLength
        Offset(w, 258, (ushort)3, 4, bitsOffset);                         // BitsPerSample = 8,8,8,8
        Short(w, 259, 8);                                                  // Compression = Adobe Deflate
        Short(w, 262, 5);                                                  // PhotometricInterpretation = Separated(CMYK)
        Offset(w, 270, 2, descLength, descOffset);                        // ImageDescription（那句约定）
        Long(w, 273, (uint)stripOffset);                                  // StripOffsets
        Short(w, 277, 4);                                                  // SamplesPerPixel
        Long(w, 278, (uint)frame.Height);                                 // RowsPerStrip（整页一条 strip）
        Long(w, 279, (uint)stripLength);                                  // StripByteCounts
        Offset(w, 282, 5, 1, xResOffset);                                 // XResolution RATIONAL
        Offset(w, 283, 5, 1, yResOffset);                                 // YResolution RATIONAL
        Short(w, 284, 1);                                                  // PlanarConfiguration = Chunky（CMYK 逐像素交错）
        Short(w, 296, 2);                                                  // ResolutionUnit = 英寸
        Offset(w, 305, 2, swLength, swOffset);                            // Software
        // InkSet(333)/NumberOfInks(334)/InkNames(335) 刻意不写：实测（verify_outlets.py）libtiff 一看到
        // 前两条就要求配套的 InkNames 与它自己数出来的名字数一致，对不上就在**每次打开**时打
        // Warning/Error——而 Photometric=5 + SamplesPerPixel=4 已经说明这是 CMYK 分层文件。
        // 真正没人猜得到的只有"0 是无墨还是满墨"，那由下面的 DotRange 与 ImageDescription 说。
        Shorts2(w, 336, 0, 255);                                          // DotRange：0 = 没有点，255 = 满点
        w.Write((uint)next);
    }

    private static void Long(BinaryWriter w, ushort tag, uint value)
    {
        w.Write(tag); w.Write((ushort)4); w.Write(1u); w.Write(value);
    }

    private static void Short(BinaryWriter w, ushort tag, ushort value)
    {
        w.Write(tag); w.Write((ushort)3); w.Write(1u); w.Write(value); w.Write((ushort)0);
    }

    /// <summary>两个 SHORT 塞进 4 字节值域：按小端，前一个在低半部。</summary>
    private static void Shorts2(BinaryWriter w, ushort tag, ushort first, ushort second)
    {
        w.Write(tag); w.Write((ushort)3); w.Write(2u); w.Write(first); w.Write(second);
    }

    private static void Offset(BinaryWriter w, ushort tag, ushort type, int count, int valueOffset)
    {
        w.Write(tag); w.Write(type); w.Write((uint)count); w.Write((uint)valueOffset);
    }

    /// <summary>TIFF 的 ASCII 字段按规范带结尾 NUL，且只放 7 位字符——非 ASCII 一律换成 '?' 而不是抛。</summary>
    private static byte[] AsciiNul(string text)
    {
        var bytes = new byte[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
            bytes[i] = text[i] is >= (char)0x20 and <= (char)0x7E ? (byte)text[i] : (byte)'?';
        return bytes;
    }
}
