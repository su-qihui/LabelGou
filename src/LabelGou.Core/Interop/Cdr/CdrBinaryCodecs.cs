using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace LabelGou.Core.Interop.Cdr;

/// <summary>bbox：4×s4（Corel 定点）。两个对角点，X 右 Y 上、以**页中心**为原点（见 <see cref="PageCenterBox"/>）。</summary>
public sealed class CdrBbox
{
    public int[] Raw { get; init; } = Array.Empty<int>();

    public double[] Mm { get; init; } = Array.Empty<double>();

    public bool HasValue => Raw.Length >= 4;
}

/// <summary>
/// 页中心框法（F3，探针实测坐实）换算出来的一只盒。
/// <paramref name="raw"/> 是 bbox 的四元组 Corel 定点。
/// </summary>
public readonly record struct CdrFrame(double LeftTopXmm, double TopYmm, double WidthMm, double HeightMm);

/// <summary>loda 的一个参数（类型号 + 体）。类型号认不得时 <see cref="TypeName"/> 会写明"未登记"。</summary>
public sealed class CdrLodaArg
{
    public int Index { get; init; }
    public uint TypeRaw { get; init; }
    public string TypeName { get; init; } = "";
    public int At { get; init; }
    public int Length { get; init; }
    public byte[] Body { get; init; } = Array.Empty<byte>();

    /// <summary>偏移表不合理（正序/逆序都取不出自洽区间）时的说明。</summary>
    public string? Unknown { get; init; }
}

/// <summary>loda：对象类型 + 参数表（照 ksy <c>loda_chunk_data</c>，参数表按探针实测的**逆序**对应）。</summary>
public sealed class CdrLoda
{
    public int ChunkLength { get; init; }
    public bool ChunkLengthOk { get; init; }
    public int NumOfArgs { get; init; }
    public int StartOfArgs { get; init; }
    public int StartOfArgTypes { get; init; }
    public int ChunkTypeRaw { get; init; }

    /// <summary>类型名；认不得时是 null（原号在 <see cref="ChunkTypeRaw"/>，由调用方点名）。</summary>
    public string? ChunkType { get; init; }

    public uint[] ArgTypesRaw { get; set; } = Array.Empty<uint>();
    public List<CdrLodaArg> Args { get; init; } = new();
    public string? Unknown { get; set; }

    public CdrLodaArg? Arg(string typeName)
    {
        foreach (var a in Args) if (a.TypeName == typeName && a.Unknown is null) return a;
        return null;
    }
}

/// <summary>trfd 的一条变换：type 0x08 = 6×f8 矩阵。</summary>
public sealed class CdrTrafo
{
    public int TypeRaw { get; init; }
    public double A { get; init; }
    public double B { get; init; }
    public double C { get; init; }
    public double D { get; init; }
    public double TxMm { get; init; }
    public double TyMm { get; init; }
    public double AngleDeg { get; init; }
    public double ScaleX { get; init; }
    public double ScaleY { get; init; }

    /// <summary>非 0x08 或越界时的说明。</summary>
    public string? Unknown { get; init; }
}

/// <summary>font 块：字体表条目（ksy <c>font_chunk_data</c>，名字从 +18 起是 UTF-16LE，探针实测）。</summary>
public sealed class CdrFontRecord
{
    public int At { get; init; }
    public int Length { get; init; }
    public int FontId { get; init; }
    public int EncodingRaw { get; init; }
    public string? EncodingName { get; init; }
    public uint StyleFlagsRaw { get; init; }
    public string? StyleName { get; init; }
    public string Name { get; init; } = "";
    public string Gap10Hex { get; init; } = "";
}

/// <summary>txsm 段落里的一段字符样式（字号只有 bits&amp;4 置上时才有，实测六份全没置）。</summary>
public sealed class CdrTextStyle
{
    public int NumChars { get; init; }
    public byte FlagsRaw { get; init; }
    public byte Fl3 { get; init; }
    public int[]? Font { get; init; }
    public string? StyleFlagsHex { get; init; }
    public int? FontSizeRaw { get; init; }
    public double? FontSizePt { get; init; }
    public string? Locale { get; init; }
}

/// <summary>txsm 的一个段落：文字与它的字符描述表。</summary>
public sealed class CdrTxsmParagraph
{
    public uint StyleId { get; init; }
    public int NumStyles { get; init; }
    public List<CdrTextStyle> Styles { get; } = new();
    public int NumChars { get; init; }
    public int NumBytesInText { get; init; }

    /// <summary>text_data 在这段 txsm 体里的起点（探针夹具里那条 text_hex 的位置）。</summary>
    public int TextAt { get; init; } = -1;

    public string Text { get; init; } = "";
    public string TextHex { get; init; } = "";

    /// <summary>双字节字符的判定依据（实测：char_description.flags 的 bit0；缺表时退回顾式）。</summary>
    public string Framing { get; init; } = "";

    public string? Unknown { get; init; }
}

/// <summary>txsm 的解结果（v7 分支，500≤v&lt;1600）。</summary>
public sealed class CdrTxsm
{
    public int Length { get; init; }
    public uint FrameFlagRaw { get; init; }
    public int NumFrames { get; init; }
    public int NumParagraphs { get; init; }
    public List<CdrTxsmParagraph> Paragraphs { get; } = new();
    public int Consumed { get; init; }
    public int Residual { get; init; }
    public bool ConsumedExact { get; init; }
    public string? Unknown { get; init; }

    /// <summary>各段文字按序拼接（探针 p4 的对账口径）。换行是裸 <c>\r</c>，归一交给 <c>CdrxText.Contents</c>。</summary>
    public string JoinedText()
    {
        var sb = new StringBuilder();
        foreach (var p in Paragraphs) sb.Append(p.Text);
        return sb.ToString();
    }
}

/// <summary>
/// stlt 的 <b>fonts 映射段</b>里的一条（60 字节一条，<b>由 stlt 体的固定段序定位，不是靠内容猜</b>：
/// 见 <see cref="ParseStltStyleTable"/>）。这条记录就是"字号＋字体号＋样式旗标"的家。
/// <para>
/// 字段偏移（X4=1400，实测六份零残余走通）：<c>+0</c> 本条目的 <see cref="StyleId"/>（ID，不是下标）、
/// <c>+4..+23</c> 20 字节保留（里面是 <c>0,ffff,ffff,ffff,1</c> 那串，旧口径误把它当记录头）、
/// <c>+24</c> u2 字体号、<c>+26</c> u2 编码、<c>+28..+35</c> 8 字节、<c>+36</c> s4 <b>字号 raw</b>、
/// <c>+40..+47</c>（实测是字号 raw 的另两份副本）、<c>+48</c> u4 样式旗标、<c>+52..+59</c>（旗标的副本）。
/// </para>
/// </summary>
public sealed class CdrStltStyleRecord
{
    public int Index { get; init; }
    public int AtInStlt { get; init; }

    /// <summary>本条目自己的 ID（<c>+0</c>）；<see cref="CdrStltParagraphStyleRecord.FontRecId"/> 按它<b>按值</b>查，不按位置。</summary>
    public uint StyleId { get; init; }

    /// <summary><c>+24</c> 的 u2：文档 font 块表里的字体号（同一字体号可有多个 font 块条目）。</summary>
    public int FontTableId { get; init; }

    /// <summary>旧名字，与 <see cref="FontTableId"/> 同一个数（<c>+24</c>）。保留是给对账判据用的。</summary>
    public int StyleFontIndex => FontTableId;

    public int Encoding { get; init; }
    public int SizeRaw { get; init; }
    public double SizeMm { get; init; }
    public double SizePt { get; init; }

    /// <summary>字号没按"三副本相等"排过的说明（<c>+36/+40/+44</c> 不等时非 null）。</summary>
    public string? SizeCopiesNote { get; init; }
    public uint Flags { get; init; }
    public string? FlagName { get; init; }
}

/// <summary>
/// stlt 的 <b>records 段</b>里的一条段落样式记录（变长，<c>num</c> 决定后面跟几组 ID）。
/// 对象侧 <c>txsm</c> 段落头那个 u32 <b>style_id</b> 就是按值命中这里的 <see cref="StyleId"/>，
/// 再由 <see cref="FontRecId"/> 按值命中 <see cref="CdrStltStyleRecord.StyleId"/>——这条链就是字号链。
/// </summary>
public sealed class CdrStltParagraphStyleRecord
{
    public int Index { get; init; }
    public int AtInStlt { get; init; }

    /// <summary>记录形状：1＝只带 fill/outl，2＝再带 font/align/interval/set5(/set11)，3＝再带 tab/bullet/indent/hyphen/dropcap。</summary>
    public int Num { get; init; }
    public uint StyleId { get; init; }
    public uint ParentId { get; init; }
    public string Name { get; init; } = "";
    public uint FillId { get; init; }
    public uint OutlId { get; init; }
    public uint FontRecId { get; init; }
    public uint AlignId { get; init; }

    /// <summary>num&lt;2 时根本没有 font 字段（不是"字号为 0"）。</summary>
    public bool HasFontField { get; init; }
    public bool HasParagraphFields { get; init; }
}

/// <summary>一张 stlt 样式表（六份实测每份恰好一张，且对象都在同一段里）。</summary>
public sealed class CdrStltStyleTable
{
    public int BodyLength { get; set; }
    public int NumRecordsDeclared { get; set; }
    public int Consumed { get; set; }
    public int Residual { get; set; }

    /// <summary>true＝mappings 各段与 records 段一路走到<b>体末零残余</b>；false 时 <see cref="Unknown"/> 说清卡在哪。</summary>
    public bool Ok { get; set; }
    public string? Unknown { get; set; }

    /// <summary>段序与每段条数（诊断与判据用，例如 <c>fonts×4</c>）。</summary>
    public List<string> SectionNotes { get; set; } = new();

    public List<CdrStltStyleRecord> Fonts { get; set; } = new();
    public List<CdrStltParagraphStyleRecord> Records { get; set; } = new();

    /// <summary>段落记录：style_id → 记录（<b>按 ID 按值查，不按下标</b>）。</summary>
    public IReadOnlyDictionary<uint, CdrStltParagraphStyleRecord> RecordsByStyleId => _byStyleId;

    readonly Dictionary<uint, CdrStltParagraphStyleRecord> _byStyleId = new();

    /// <summary>fonts 条目：条目自身 ID → 条目。</summary>
    public IReadOnlyDictionary<uint, CdrStltStyleRecord> FontsByStyleId => _byFontId;

    readonly Dictionary<uint, CdrStltStyleRecord> _byFontId = new();

    internal void AddFonts(IReadOnlyDictionary<uint, CdrStltStyleRecord> m) { foreach (var kv in m) _byFontId[kv.Key] = kv.Value; }
    internal void AddRecords(IReadOnlyDictionary<uint, CdrStltParagraphStyleRecord> m) { foreach (var kv in m) _byStyleId[kv.Key] = kv.Value; }

    public CdrStltParagraphStyleRecord? RecordFor(uint styleId) => _byStyleId.GetValueOrDefault(styleId);
    public CdrStltStyleRecord? FontFor(uint recId) => _byFontId.GetValueOrDefault(recId);
}

/// <summary>一次"对象 → 样式记录 → fonts 条目"的解析结果。<b>拿不到就是 null + 原因</b>，不许填个看着像的数。</summary>
public sealed class CdrStyleFontResolution
{
    public bool SizeOk { get; set; }
    public int SizeRaw { get; set; }
    public double SizePt { get; set; }
    public uint ParagraphStyleId { get; set; }
    public uint FontRecId { get; set; }
    public int StyleRecordIndex { get; set; } = -1;
    public int FontEntryIndex { get; set; } = -1;
    public string? StyleRecordName { get; set; }

    /// <summary>字号这条链没串上时的原因（<see cref="SizeOk"/> 为 false 时必有）。</summary>
    public string? SizeUnknown { get; set; }

    /// <summary>字体名（按 ID 在 font 块表里查，<b>同 ID 的多个名字不一致就退回 null</b>）。</summary>
    public string? FontName { get; set; }
    public string? FontNameUnknown { get; set; }

    /// <summary>名字取自哪一层：段落样式记录 / 首个带 has_font 的字符样式。</summary>
    public string FontNameSource { get; set; } = "";
    public int FontTableId { get; set; } = -1;
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public string FlagsHex { get; set; } = "";
    public string FlagsName { get; set; } = "";
    public List<string> Notes { get; } = new();
}

/// <summary>
/// B 通道的 L3 逐块解法。全部照 <c>cdrb.py</c> + <c>p3_l3.py</c>（<c>parse_txsm7</c>）移植，
/// 包括"版本 1400 时没有 v&gt;=1500 那 1 字节"这类分支——分支留着，但六份真样件都是 X4=1400，
/// 别的版本走到的分支**没被真样本验过**，用到时要在 degraded 里点名。
/// </summary>
public static class CdrBinaryCodecs
{
    /// <summary>loda 的对象类型号 → 名字（ksy <c>loda_chunk_data</c> 的实测表）。</summary>
    public static readonly Dictionary<int, string> LodaTypeNames = new()
    {
        [1] = "rectangle",
        [2] = "ellipse",
        [3] = "line_and_curve",
        [4] = "artistic_text",
        [5] = "bitmap",
        [6] = "paragraph_text",
        [0x14] = "polygon_coords",
        [0x25] = "path",
        [0x26] = "spline",
    };

    /// <summary>loda 参数类型号 → 名字。</summary>
    public static readonly Dictionary<uint, string> LodaArgTypeNames = new()
    {
        [10] = "line_style",
        [20] = "fill_style",
        [30] = "loda_coords",
        [100] = "waldo_trfd",
        [200] = "style",
        [1000] = "name",
        [2000] = "palt",
        [8000] = "opacity",
        [8005] = "contnr",
        [11000] = "polygon_transform",
        [12010] = "gradient",
        [12030] = "rotate",
        [19130] = "page_size",
        [40050] = "guid_layer",
    };

    /// <summary>ksy 顶层 enums 的文字编码表（本轮用到的那些）。</summary>
    public static readonly Dictionary<int, string> TextEncodings = new()
    {
        [0x00] = "latin(cp1252)",
        [0x01] = "system_default",
        [0x02] = "symbol",
        [0x4d] = "apple_roman",
        [0x80] = "japanese_shift_jis",
        [0x81] = "korean_hangul",
        [0x82] = "korean_johab",
        [0x86] = "chinese_simplified_gbk",
        [0x88] = "chinese_traditional_big5",
        [0xa1] = "greek",
        [0xa2] = "turkish",
        [0xa3] = "vietnamese",
        [0xb1] = "hebrew",
        [0xb2] = "arabic",
        [0xba] = "baltic",
        [0xcc] = "cyrillic",
        [0xde] = "thai",
        [0xee] = "latin_ii_central_european",
        [0xff] = "oem_latin_i",
    };

    static readonly Dictionary<uint, string> FontStyleNames = new()
    {
        [0x0000] = "mixed",
        [0x0010] = "light",
        [0x0020] = "light_italic",
        [0x0040] = "normal",
        [0x0080] = "italic",
        [0x0100] = "medium",
        [0x0200] = "medium_italic",
        [0x0400] = "semi_bold",
        [0x0800] = "semi_bold_italic",
        [0x1000] = "bold",
        [0x2000] = "bold_italic",
        [0x4000] = "extra_bold",
        [0x8000] = "extra_bold_italic",
    };

    // ---------------------------------------------------------------- 盒

    public static CdrBbox DecodeBbox(ReadOnlySpan<byte> body)
    {
        if (body.Length < 16) return new CdrBbox();
        var raw = new int[4];
        var mm = new double[4];
        for (var i = 0; i < 4; i++)
        {
            raw[i] = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(i * 4, 4));
            mm[i] = CdrBinaryUnits.Mm(raw[i]);
        }
        return new CdrBbox { Raw = raw, Mm = mm };
    }

    /// <summary>obbx：8×s4 = 四角（同样以页中心为原点）。</summary>
    public static int[] DecodeObbx(ReadOnlySpan<byte> body)
    {
        var n = body.Length / 4;
        var v = new int[n];
        for (var i = 0; i < n; i++) v[i] = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(i * 4, 4));
        return v;
    }

    /// <summary>
    /// F3 页中心框法（**已坐实**，与探针 <c>p4_all6.f3_box</c> 同一式子）：
    /// 文件里的 bbox 是"以页中心为原点、X 右 Y 上"，折成 cdrx 的左上原点 Y 向下要加页心偏移。
    /// 不加这两下会整整差半个页面（金沐 −70/−50 mm），而且差值是常数，只看相对位置永远发现不了。
    /// </summary>
    public static CdrFrame PageCenterBox(int[] bboxRaw, double pageWidthMm, double pageHeightMm)
    {
        var x0 = CdrBinaryUnits.Mm(bboxRaw[0]);
        var y0 = CdrBinaryUnits.Mm(bboxRaw[1]);
        var x1 = CdrBinaryUnits.Mm(bboxRaw[2]);
        var y1 = CdrBinaryUnits.Mm(bboxRaw[3]);
        return new CdrFrame(
            Math.Min(x0, x1) + pageWidthMm / 2.0,
            pageHeightMm / 2.0 - Math.Max(y0, y1),
            Math.Abs(x1 - x0),
            Math.Abs(y1 - y0));
    }

    /// <summary>
    /// 反例用的"绝对原点"框法（**错的**，探针 F1）：只为让判据能红——
    /// 拿它与 <see cref="PageCenterBox"/> 比，差值必是 (−W/2, +H/2)。生产代码不许调它。
    /// </summary>
    public static CdrFrame AbsoluteOriginBox(int[] bboxRaw, double pageWidthMm, double pageHeightMm)
    {
        var x0 = CdrBinaryUnits.Mm(bboxRaw[0]);
        var y0 = CdrBinaryUnits.Mm(bboxRaw[1]);
        var x1 = CdrBinaryUnits.Mm(bboxRaw[2]);
        var y1 = CdrBinaryUnits.Mm(bboxRaw[3]);
        return new CdrFrame(Math.Min(x0, x1), pageHeightMm - Math.Max(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
    }

    // ---------------------------------------------------------------- 页尺寸

    /// <summary>
    /// mcfg → 页宽/页高（Corel 定点）。实测六份：体首 u32=1300（表版本），+12 = 页宽、+16 = 页高，
    /// 与 ZIP 里 <c>metadata/metadata.xml</c> 的 PageWidth/PageHeight 逐位相符（1400000/1000000 = 140×100mm）。
    /// 解不出返回 null，调用方必须落进 Degraded，不许拿 0 当页尺寸。
    /// </summary>
    public static (double W, double H)? DecodeMcfgPageSize(ReadOnlySpan<byte> body, out string? note)
    {
        note = null;
        if (body.Length < 20)
        {
            note = $"mcfg 体只有 {body.Length} 字节，取不到页尺寸";
            return null;
        }
        var tableVersion = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        var w = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(12, 4));
        var h = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(16, 4));
        if (tableVersion != 1300)
            note = $"mcfg 表版本 u32[0]={tableVersion}（探针实测 1300），+12/+16 的页尺寸偏移按 1300 标定，这里可能对不上";
        if (w <= 0 || h <= 0)
        {
            note = $"mcfg 读出的页尺寸不正：{w}×{h}（Corel 定点）";
            return null;
        }
        return (CdrBinaryUnits.Mm(w), CdrBinaryUnits.Mm(h));
    }

    /// <summary>页尺寸的原始四元组（诊断用）。</summary>
    public static int[] McfgRawHead(ReadOnlySpan<byte> body)
    {
        var n = Math.Min(5, body.Length / 4);
        var v = new int[n];
        for (var i = 0; i < n; i++) v[i] = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(i * 4, 4));
        return v;
    }

    // ---------------------------------------------------------------- loda

    /// <summary>
    /// ksy <c>loda_chunk_data</c>：5×u32 头 + (n+1) 个 u32 偏移 + n 个 u32 类型，
    /// 类型与偏移**逆序**对应（<c>arg_types[n-1-i] ↔ arg_offsets[i]</c>）。
    /// 探针记过一条警告：这条逆序规律只在金沐 artistic_text 上自洽，OLU 的 polygon 两序都不自洽，
    /// 所以它**不能当公理用**——参数表取出的体一旦长度不对，本函数就标 Unknown。
    /// </summary>
    public static CdrLoda DecodeLoda(ReadOnlySpan<byte> b)
    {
        if (b.Length < 20)
            return new CdrLoda { Unknown = $"loda 体 {b.Length} 字节 < 20，头都读不出", ChunkTypeRaw = -1 };
        var cl = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(0, 4));
        var n = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(4, 4));
        var soa = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(8, 4));
        var sat = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(12, 4));
        var ct = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(16, 4));
        var loda = new CdrLoda
        {
            ChunkLength = cl,
            ChunkLengthOk = cl == b.Length,
            NumOfArgs = n,
            StartOfArgs = soa,
            StartOfArgTypes = sat,
            ChunkTypeRaw = ct,
            ChunkType = LodaTypeNames.GetValueOrDefault(ct),
        };
        if (n < 0 || n > 4096 || soa < 0 || sat < 0)
        {
            loda.Unknown = $"loda 头不合理（num_of_args={n} start_of_args={soa} start_of_arg_types={sat}），参数表没取";
            return loda;
        }
        var offs = new int[n + 1];
        for (var i = 0; i <= n; i++)
            offs[i] = soa + i * 4 + 4 <= b.Length ? BinaryPrimitives.ReadInt32LittleEndian(b.Slice(soa + i * 4, 4)) : -1;
        var types = new uint[n];
        for (var i = 0; i < n; i++)
            types[i] = sat + i * 4 + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(sat + i * 4, 4)) : 0u;
        loda.ArgTypesRaw = types;
        for (var i = 0; i < n; i++)
        {
            var t = types[n - 1 - i];
            var o0 = offs[i];
            var o1 = offs[i + 1];
            if (o0 < 0 || o1 < 0 || o1 < o0 || o1 > b.Length)
            {
                loda.Args.Add(new CdrLodaArg
                {
                    Index = i, TypeRaw = t, Unknown = $"偏移不合理 [{o0},{o1}]（体长 {b.Length}）",
                    TypeName = LodaArgTypeNames.GetValueOrDefault(t, "未登记 arg 类型"),
                });
                continue;
            }
            loda.Args.Add(new CdrLodaArg
            {
                Index = i,
                TypeRaw = t,
                TypeName = LodaArgTypeNames.GetValueOrDefault(t, "未登记 arg 类型"),
                At = o0,
                Length = o1 - o0,
                Body = b.Slice(o0, o1 - o0).ToArray(),
            });
        }
        return loda;
    }

    // ---------------------------------------------------------------- trfd

    /// <summary>从 at 起读 6×f8（顺序 a,c,tx,b,d,ty —— 与数学习惯的 a,b,c,d 不同，照 ksy 来）。</summary>
    public static double[] ReadMatrix(ReadOnlySpan<byte> b, int at)
    {
        if (at < 0 || at + 48 > b.Length) return Array.Empty<double>();
        var v = new double[6];
        for (var i = 0; i < 6; i++) v[i] = BinaryPrimitives.ReadDoubleLittleEndian(b.Slice(at + i * 8, 8));
        return v;
    }

    /// <summary>6×f8 → 变换事实（tx/ty 折成 mm，angle=atan2(b,a)）。</summary>
    public static CdrTrafo ToTrafo(double[] m)
    {
        var (a, c, tx, bb, d, ty) = (m[0], m[1], m[2], m[3], m[4], m[5]);
        return new CdrTrafo
        {
            TypeRaw = 0x08,
            A = a, B = bb, C = c, D = d,
            TxMm = CdrBinaryUnits.Mm((long)Math.Round(tx)),
            TyMm = CdrBinaryUnits.Mm((long)Math.Round(ty)),
            AngleDeg = Math.Atan2(bb, a) * 180.0 / Math.PI,
            ScaleX = Math.Sqrt(a * a + bb * bb),
            ScaleY = Math.Sqrt(c * c + d * d),
        };
    }

    /// <summary>ksy <c>trfd_chunk_data</c>：u32 len + u32 n + u32 start_of_args + n 个偏移，type 0x08 → 6×f8。</summary>
    public static List<CdrTrafo> DecodeTrfd(ReadOnlySpan<byte> b)
    {
        var list = new List<CdrTrafo>();
        if (b.Length < 12)
        {
            list.Add(new CdrTrafo { TypeRaw = -1, Unknown = $"trfd 体 {b.Length} 字节 < 12" });
            return list;
        }
        var n = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(4, 4));
        var soa = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(8, 4));
        if (n < 0 || n > 64 || soa < 0)
        {
            list.Add(new CdrTrafo { TypeRaw = -1, Unknown = $"trfd 头不合理（n={n} start_of_args={soa}）" });
            return list;
        }
        for (var i = 0; i < n; i++)
        {
            if (soa + i * 4 + 4 > b.Length)
            {
                list.Add(new CdrTrafo { TypeRaw = -1, Unknown = "偏移表越界" });
                continue;
            }
            var off = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(soa + i * 4, 4));
            if (off + 10 > b.Length)
            {
                list.Add(new CdrTrafo { TypeRaw = -1, Unknown = $"偏移 {off} 越界" });
                continue;
            }
            var t = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice((int)off + 8, 2));
            if (t != 0x08)
            {
                list.Add(new CdrTrafo { TypeRaw = t, Unknown = $"变换类型 0x{t:x2} 非 0x08，本轮没写解法" });
                continue;
            }
            var m = ReadMatrix(b, (int)off + 8 + 2 + 6);
            if (m.Length < 6)
            {
                list.Add(new CdrTrafo { TypeRaw = t, Unknown = $"矩阵越界（0x{off + 16:x}）" });
                continue;
            }
            list.Add(ToTrafo(m));
        }
        return list;
    }

    // ---------------------------------------------------------------- font

    /// <summary>ksy <c>font_chunk_data</c>：u2 id + u2 enc + u4 样式旗标 + 10 未知 + 名字（UTF-16LE，从 +18 起）。</summary>
    public static CdrFontRecord DecodeFont(ReadOnlySpan<byte> body, int at)
    {
        if (body.Length < 18)
            return new CdrFontRecord { At = at, Length = body.Length, Name = "", Gap10Hex = ToHex(body) };
        var fid = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0, 2));
        var enc = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));
        return new CdrFontRecord
        {
            At = at,
            Length = body.Length,
            FontId = fid,
            EncodingRaw = enc,
            EncodingName = TextEncodings.GetValueOrDefault(enc),
            StyleFlagsRaw = flags,
            StyleName = FontStyleNames.GetValueOrDefault(flags),
            Name = ReadUtf16Le(body.Slice(18)),
            Gap10Hex = ToHex(body.Slice(8, 10)),
        };
    }

    /// <summary>UTF-16LE 读到 0x0000 为止（不能按单字节 00 切）。</summary>
    public static string ReadUtf16Le(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder();
        for (var i = 0; i + 1 < b.Length; i += 2)
        {
            var cp = (ushort)(b[i] | (b[i + 1] << 8));
            if (cp == 0) break;
            sb.Append((char)cp);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- txsm

    /// <summary>
    /// ksy <c>txsm_7</c>（500≤v&lt;1600）的段落与文字流——**逐字等的那段**，照 <c>p3_l3.parse_txsm7</c> 逐分支搬。
    /// 双字节字符由 <c>char_description</c> 的 u2 flags 的 bit0 决定（探针实测确定的判据；
    /// "看首字节 ≥0x80"那种规则在邱总的「水」= 34 6C 上必错）。
    /// </summary>
    public static CdrTxsm ParseTxsm7(ReadOnlySpan<byte> body, int version)
    {
        var cur = 0;

        var outp = new List<CdrTxsmParagraph>();
        if (!Fits(cur, 4, body.Length)) return new CdrTxsm { Length = body.Length, Unknown = "txsm 体不足 4 字节" };
        var frameFlag = U32At(body, cur);
        cur += 4;
        var frameFlagOn = frameFlag != 0;
        cur += 32;
        if (!Fits(cur, 4, body.Length)) return Done(body, frameFlag, 0, outp, cur, "没读到 num_frames");
        var numFrames = U32At(body, cur);
        cur += 4;
        if (numFrames > 1024) return Done(body, frameFlag, (int)numFrames, outp, cur, "num_frames 不合理，停在这里");
        for (uint fi = 0; fi < numFrames; fi++)
        {
            cur += 4;                                       // frame_id
            cur += 48;                                      // blob48（矩阵候选，探针没定名）
            if (!Fits(cur, 4, body.Length)) break;
            var top = U32At(body, cur);
            cur += 4;
            if (top != 0) cur += 40;                        // text_on_path
            if (!frameFlagOn) cur += 36;                    // frame_flag 关掉时的尾巴
            if (cur > body.Length) break;
        }
        if (!Fits(cur, 4, body.Length)) return Done(body, frameFlag, (int)numFrames, outp, cur, "读到没找到 num_paragraphs");
        var numParagraphs = U32At(body, cur);
        cur += 4;
        if (numParagraphs > 4096) return Done(body, frameFlag, (int)numFrames, outp, cur, "num_paragraphs 不合理");

        for (uint pi = 0; pi < numParagraphs; pi++)
        {
            var styles = new List<CdrTextStyle>();
            string? stop = null;
            int numChars = 0, nbytes = 0, textAt = -1;
            ushort[]? charFlags = null;
            var text = "";
            var hex = "";
            var framing = "";

            if (!Fits(cur, 4, body.Length)) { stop = "style_id 越界"; outp.Add(MakePara(0, styles, numChars, nbytes, textAt, text, hex, framing, stop)); break; }
            var styleId = U32At(body, cur);
            cur += 4;
            if (!Fits(cur, 1, body.Length)) { stop = "u8_1 越界"; outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, stop)); break; }
            cur += 1;
            if (version >= 1300 && frameFlagOn)
            {
                if (!Fits(cur, 1, body.Length)) { stop = "paragraph flag 越界"; outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, stop)); break; }
                cur += 1;
            }
            if (!Fits(cur, 4, body.Length)) { stop = "num_styles 越界"; outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, stop)); break; }
            var numStyles = U32At(body, cur);
            cur += 4;
            if (numStyles > 4096) { stop = "num_styles 不合理"; outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, stop)); break; }

            for (uint si = 0; si < numStyles; si++)
            {
                if (!Fits(cur, 4, body.Length)) { stop = "style 头越界"; break; }
                var nchars = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cur, 2));
                cur += 2;
                var bits = body[cur];
                cur += 1;
                var fl3 = version >= 800 ? body[cur] : (byte)0;
                cur += 1;
                int[]? font = null;
                string? styleFlagsHex = null;
                int? sizeRaw = null;
                double? sizePt = null;
                string? locale = null;
                if ((bits & 1) != 0)
                {
                    if (!Fits(cur, 4, body.Length)) { stop = "font 越界"; break; }
                    font = new[]
                    {
                        (int)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cur, 2)),
                        (int)BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cur + 2, 2)),
                    };
                    cur += 4;
                }
                if ((bits & 2) != 0)
                {
                    if (!Fits(cur, 4, body.Length)) { stop = "style_flags 越界"; break; }
                    styleFlagsHex = ToHex(body.Slice(cur, 4));
                    cur += 4;
                }
                if ((bits & 4) != 0)
                {
                    if (!Fits(cur, 4, body.Length)) { stop = "font_size 越界"; break; }
                    sizeRaw = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(cur, 4));
                    sizePt = CdrBinaryUnits.Pt(sizeRaw.Value);
                    cur += 4;
                }
                for (var k = 0; k < 3; k++)
                {
                    var mask = 8 << k;
                    if ((bits & mask) == 0) continue;
                    if (!Fits(cur, 4, body.Length)) { stop = "偏移量域越界"; break; }
                    cur += 4;
                }
                if ((bits & 64) != 0)
                {
                    var skip = 4 + (version >= 1300 ? 48 : 0);
                    if (!Fits(cur, skip, body.Length)) { stop = "font_color 越界"; break; }
                    cur += skip;
                }
                if ((bits & 128) != 0)
                {
                    if (!Fits(cur, 4, body.Length)) { stop = "outl_id 越界"; break; }
                    cur += 4;
                }
                if ((fl3 & 0x02) != 0)
                {
                    if (!Fits(cur, 4, body.Length)) { stop = "fl3 长度域越界"; break; }
                    var l = U32At(body, cur);
                    if (!Fits(cur, 4 + (int)l * 2, body.Length)) { stop = "fl3 体越界"; break; }
                    cur += 4 + (int)l * 2;
                }
                if ((fl3 & 0x08) != 0)
                {
                    if (!Fits(cur, 4, body.Length)) { stop = "locale 长度域越界"; break; }
                    var l = U32At(body, cur);
                    if (!Fits(cur, 4 + (int)l * 2, body.Length)) { stop = "locale 体越界"; break; }
                    locale = ReadUtf16Le(body.Slice(cur + 4, (int)l * 2));
                    cur += 4 + (int)l * 2;
                }
                if ((fl3 & 0x20) != 0)
                {
                    if (!Fits(cur, 1, body.Length)) { stop = "fl3 0x20 旗标越界"; break; }
                    var flag = body[cur];
                    cur += version >= 1500
                        ? 1 + (flag != 0 ? 52 : 0)
                        : 1 + (flag != 0 ? 4 : 0);
                }
                styles.Add(new CdrTextStyle
                {
                    NumChars = nchars, FlagsRaw = bits, Fl3 = fl3, Font = font,
                    StyleFlagsHex = styleFlagsHex, FontSizeRaw = sizeRaw, FontSizePt = sizePt, Locale = locale,
                });
            }
            if (stop is not null)
            {
                outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, stop));
                break;
            }
            if (!Fits(cur, 4, body.Length))
            {
                outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, "num_chars 越界"));
                break;
            }
            numChars = (int)U32At(body, cur);
            cur += 4;
            var cdLen = numChars * 8;                       // char_description（v>=1200）= u2 flags + u1 style + u1 ? + u4
            if (cdLen < 0 || cdLen > body.Length)
            {
                outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, "char_description 表长度不合理"));
                break;
            }
            var cdArea = body.Slice(cur, cdLen);
            cur += cdLen;
            if (cdArea.Length >= cdLen && numChars > 0)
            {
                charFlags = new ushort[numChars];
                for (var k = 0; k < numChars; k++) charFlags[k] = BinaryPrimitives.ReadUInt16LittleEndian(cdArea.Slice(k * 8, 2));
            }
            else if (numChars > 0)
            {
                charFlags = null;       // 表越界 → 退回启发式分帧，并在段落里写明
            }
            if (!Fits(cur, 4, body.Length))
            {
                outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, $"num_bytes_in_text 越界（cur={cur} len={body.Length}）"));
                break;
            }
            nbytes = (int)U32At(body, cur);
            cur += 4;
            textAt = cur;
            if (nbytes < 0 || cur + nbytes > body.Length)
            {
                nbytes = Math.Max(0, Math.Min(nbytes, body.Length - cur));
                var rawShort = body.Slice(cur, nbytes);
                hex = ToHex(rawShort);
                cur += nbytes;
                (text, framing) = DecodeTextBytes(rawShort, charFlags);
                outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, "text_data 越界（已按可用长度截断）"));
                break;
            }
            var raw = body.Slice(cur, nbytes);
            hex = ToHex(raw);
            cur += nbytes;
            (text, framing) = DecodeTextBytes(raw, charFlags);
            if (cur < body.Length)
            {
                var hasPath = body[cur];
                cur += 1;
                if (hasPath != 0) cur += numChars * 24;
            }
            outp.Add(MakePara(styleId, styles, numChars, nbytes, textAt, text, hex, framing, cur > body.Length ? "path 表越界" : null));
        }
        return Done(body, frameFlag, (int)numFrames, outp, cur, null, (int)numParagraphs);
    }

    static uint U32At(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at, 4));

    static bool Fits(int cur, int n, int len) => cur + n <= len;

    static CdrTxsm Done(ReadOnlySpan<byte> body, uint frameFlag, int numFrames, List<CdrTxsmParagraph> pars,
        int consumed, string? unknown, int? numParagraphs = null)
    {
        var t = new CdrTxsm
        {
            Length = body.Length,
            FrameFlagRaw = frameFlag,
            NumFrames = numFrames,
            NumParagraphs = numParagraphs ?? pars.Count,
            Consumed = Math.Min(consumed, body.Length),
            Residual = body.Length - Math.Min(consumed, body.Length),
            ConsumedExact = consumed == body.Length,
            Unknown = unknown,
        };
        foreach (var p in pars) t.Paragraphs.Add(p);
        return t;
    }

    static CdrTxsmParagraph MakePara(uint styleId, List<CdrTextStyle> styles, int numChars, int nbytes,
        int textAt, string text, string hex, string framing, string? unknown)
    {
        var p = new CdrTxsmParagraph
        {
            StyleId = styleId,
            NumStyles = styles.Count,
            NumChars = numChars,
            NumBytesInText = nbytes,
            TextAt = textAt,
            Text = text,
            TextHex = hex,
            Framing = framing.Length == 0 ? "没走到分帧这一步" : framing,
            Unknown = unknown,
        };
        foreach (var s in styles) p.Styles.Add(s);
        return p;
    }

    /// <summary>
    /// text_data 的分帧：ASCII 1 字节 + 非 ASCII 字符 2 字节（UTF-16LE 码元）混排。
    /// 判据用 <c>char_description</c> 的 u2 flags 的 bit0（bit0==1 → 这个字符占 2 字节）。
    /// 没有 flags 时退回旧启发式（≥0x80 或 ==0x1A）并在 <paramref name="framing"/> 里写明用的是哪家——
    /// 探针里三家（cp1252 / GBK / utf-16le）都试过，最后坐实的是"窄字节按单字节码点直映 + 宽字节 UTF-16LE"。
    /// </summary>
    public static (string Text, string Framing) DecodeTextBytes(ReadOnlySpan<byte> raw, ushort[]? charFlags)
    {
        var sb = new StringBuilder();
        var i = 0;
        if (charFlags is not null)
        {
            var used = "char_description.flags.bit0";
            foreach (var fl in charFlags)
            {
                if (i >= raw.Length)
                {
                    sb.Append('\ufffd');
                    continue;
                }
                if ((fl & 1) != 0)
                {
                    var cp = i + 1 < raw.Length ? raw[i] | (raw[i + 1] << 8) : raw[i];
                    sb.Append((char)cp);
                    i += 2;
                }
                else
                {
                    sb.Append((char)raw[i]);
                    i += 1;
                }
            }
            if (i != raw.Length) used += $"（尾部剩 {raw.Length - i} 字节未消费）";
            return (sb.ToString(), used);
        }
        while (i < raw.Length)
        {
            var b = raw[i];
            if (b >= 0x80 || b == 0x1A)
            {
                if (i + 1 >= raw.Length) { sb.Append('\ufffd'); break; }
                sb.Append((char)(ushort)(raw[i] | (raw[i + 1] << 8)));
                i += 2;
            }
            else
            {
                sb.Append((char)b);
                i += 1;
            }
        }
        return (sb.ToString(), "启发式(>=0x80 或 0x1A)：char_description 表没读到，分帧不可信");
    }

    // ---------------------------------------------------------------- stlt

    /// <summary>
    /// stlt 样式表（X4=1400 标定）：<b>mappings 各段按固定段序与步长走，走完正好接 records 段，
    /// records 段走完正好落在体末（零残余）</b>——六份真样件全中，这就是本表自洽的判据。
    /// <para>
    /// 段序（每段先 u32 条数再 n×步长）：<c>fills×60 · outls×12 · fonts×60 · aligns×12 · intervals×52 ·
    /// set5s×152 · tabs×784 · bullets×变长(40+4+4+(indicator!=0?68:12)) · indents×28 · hypens×(32+4) ·
    /// dropcaps×28 · set11s×12</c>；其后是 <c>num_records</c> 条变长段落记录：
    /// <c>num · style_id · parent_id · 8B · name_len · name(UTF-16LE) · fill_id · outl_id ·</c>
    /// <c>num&gt;1 再 font_rec_id·align_id·interval_id·set5_id·set11_id · num&gt;2 再 tab·bullet·indent·hyphen·dropcap</c>。
    /// </para>
    /// <para>
    /// <b>字号不在这张表里"按值搜"</b>：对象侧 <c>txsm</c> 段落的 u32 style_id <b>按值</b>命中 records 的
    /// <see cref="CdrStltParagraphStyleRecord.StyleId"/>，记录的 <see cref="CdrStltParagraphStyleRecord.FontRecId"/>
    /// 再<b>按值</b>命中 fonts 条目的 <see cref="CdrStltStyleRecord.StyleId"/>——<b>两级都是 ID→ID，没有任何下标换算</b>。
    /// 旧的"按 stride 60 扫内容"（<c>ScanStltStyleRecords</c>）既漏条目（金沐 66.4166pt 那条因旗标三连不等被漏）
    /// 又不给 ID，串不起这条链，已整条被本函数取代。
    /// </para>
    /// </summary>
    public static CdrStltStyleTable ParseStltStyleTable(ReadOnlySpan<byte> source, int version)
    {
        // 局部函数不能捕获 span（CS9108），stlt 体只有 3 KB 量级，抄一份数组最省事
        var body = source.ToArray();
        static ReadOnlySpan<byte> At(byte[] b, int at, int len) => new(b, at, len);   // 数组版 Slice 在本 SDK 里不解析
        var fontList = new List<CdrStltStyleRecord>();
        var recList = new List<CdrStltParagraphStyleRecord>();
        var notes = new List<string>();
        var fontsBy = new Dictionary<uint, CdrStltStyleRecord>();
        var recsBy = new Dictionary<uint, CdrStltParagraphStyleRecord>();
        var t = new CdrStltStyleTable { BodyLength = body.Length };
        var cur = 0;

        CdrStltStyleTable Fail(string why)
        {
            t.Ok = false;
            t.Unknown = why;
            t.Consumed = Math.Clamp(cur, 0, body.Length);
            t.Residual = Math.Max(0, body.Length - t.Consumed);
            t.SectionNotes = notes;
            t.Fonts = fontList;
            t.Records = recList;
            t.AddFonts(fontsBy);
            t.AddRecords(recsBy);
            return t;
        }

        bool Arr(string name, int stride, bool isFonts = false)
        {
            if (cur + 4 > body.Length) return Fail($"{name}：读条数就越界（cur={cur} 体={body.Length}）").Ok;
            var n = (int)U32At(body, cur);
            cur += 4;
            if (n < 0 || n > 4096 || cur + (long)n * stride > body.Length)
                return Fail($"{name}：条数 {n}×步长 {stride} 越出体（cur={cur} 体={body.Length}）").Ok;
            notes.Add($"{name}×{n}");
            for (var i = 0; i < n; i++)
            {
                var e = cur + i * stride;
                if (!isFonts) continue;
                var sr = BinaryPrimitives.ReadInt32LittleEndian(At(body, e + 36, 4));
                var c1 = BinaryPrimitives.ReadInt32LittleEndian(At(body, e + 40, 4));
                var c2 = BinaryPrimitives.ReadInt32LittleEndian(At(body, e + 44, 4));
                var flags = U32At(body, e + 48);
                var f = new CdrStltStyleRecord
                {
                    Index = i,
                    AtInStlt = e,
                    StyleId = U32At(body, e),
                    FontTableId = BinaryPrimitives.ReadUInt16LittleEndian(At(body, e + 24, 2)),
                    Encoding = BinaryPrimitives.ReadUInt16LittleEndian(At(body, e + 26, 2)),
                    SizeRaw = sr,
                    SizeMm = Round(CdrBinaryUnits.Mm(sr), 4),
                    SizePt = Round(CdrBinaryUnits.Pt(sr), 4),
                    Flags = flags,
                    FlagName = FontStyleNames.GetValueOrDefault(flags),
                    SizeCopiesNote = sr == c1 && c1 == c2
                        ? null
                        : $"字号三副本不齐（+36/+40/+44 = {sr}/{c1}/{c2}）",
                };
                fontList.Add(f);
                fontsBy[f.StyleId] = f;
            }
            cur += n * stride;
            return true;
        }

        if (body.Length < 64) return Fail($"stlt 体 {body.Length} B 太短，连 num_records 都读不出");
        if (ReadAscii(body, 0, 4) != "stlt")
            return Fail($"stlt 体的 form_type 读到 '{ReadAscii(body, 0, 4)}'，不是 'stlt'");
        cur = 4;
        var numRecords = (int)U32At(body, cur);
        cur += 4;
        t.NumRecordsDeclared = numRecords;
        if (numRecords is <= 0 or > 4096) return Fail($"stlt num_records={numRecords} 不合理（探针实测六份 10..16）");

        if (!Arr("fills", 60) || !Arr("outls", 12) || !Arr("fonts", 60, true) ||
            !Arr("aligns", 12) || !Arr("intervals", 52) || !Arr("set5s", 152) || !Arr("tabs", 784)) return t;

        // bullets：变长，逐条按分支走（六份实测每份 5 条，indicator 都非 0）
        if (cur + 4 > body.Length) return Fail("bullets：读条数越界");
        var nb = (int)U32At(body, cur);
        cur += 4;
        notes.Add($"bullets×{nb}");
        for (var i = 0; i < nb; i++)
        {
            if (cur + 48 > body.Length) return Fail($"bullets[{i}]：走不完了（cur={cur} 体={body.Length}）");
            cur += 40;
            if (version > 1300) cur += 4;
            if (version >= 1300)
            {
                var ind = U32At(body, cur);
                cur += 4 + (ind != 0 ? 68 : 12);
            }
            else cur += 20;
        }

        if (!Arr("indents", 28) || !Arr("hypens", 32 + (version >= 1300 ? 4 : 0)) || !Arr("dropcaps", 28)) return t;
        if (version > 800 && !Arr("set11s", 12)) return t;

        for (var i = 0; i < numRecords; i++)
        {
            var start = cur;
            if (body.Length - cur < 32) return Fail($"records[{i}]：只剩 {body.Length - cur} B，连定长头（32 B）都不够");
            var num = (int)U32At(body, cur);
            cur += 4;
            if (num is < 1 or > 3) return Fail($"records[{i}].num={num}（探针实测六份只出现 1/2/3 三种形状）");
            var styleId = U32At(body, cur);
            cur += 4;
            var parentId = U32At(body, cur);
            cur += 4 + 8;
            var nameLen = (int)U32At(body, cur);
            cur += 4;
            if (version >= 1200) nameLen *= 2;
            if (nameLen < 0 || cur + nameLen > body.Length) return Fail($"records[{i}] 名字长 {nameLen} 越界");
            var name = ReadUtf16Le(At(body, cur, nameLen));
            cur += nameLen;
            var fillId = U32At(body, cur);
            var outlId = U32At(body, cur + 4);
            cur += 8;
            uint fontRecId = 0, alignId = 0;
            if (num > 1)
            {
                fontRecId = U32At(body, cur);
                alignId = U32At(body, cur + 4);
                cur += 16;
                if (version > 800) cur += 4;
            }
            if (num > 2) cur += 20;
            var r = new CdrStltParagraphStyleRecord
            {
                Index = i,
                AtInStlt = start,
                Num = num,
                StyleId = styleId,
                ParentId = parentId,
                Name = name,
                FillId = fillId,
                OutlId = outlId,
                FontRecId = fontRecId,
                AlignId = alignId,
                HasFontField = num > 1,
                HasParagraphFields = num > 2,
            };
            recList.Add(r);
            if (!recsBy.TryAdd(r.StyleId, r))
                return Fail($"records[{i}] 的 style_id 0x{r.StyleId:x8} 与前面某条重复——ID→记录的对应不再唯一");
        }
        if (cur != body.Length)
            return Fail($"records 走完停在 {cur}，体长 {body.Length}（残余 {body.Length - cur} B）——段步长与实测不再一致");
        t.Ok = true;
        t.Consumed = cur;
        t.Residual = 0;
        t.SectionNotes = notes;
        t.Fonts = fontList;
        t.Records = recList;
        t.AddFonts(fontsBy);
        t.AddRecords(recsBy);
        return t;
    }

    /// <summary>
    /// 把一只文字对象的<b>段落 style_id</b> 串到字号/字体名（<b>ID→ID 两级按值查</b>，见
    /// <see cref="ParseStltStyleTable"/>）。拿不到的项一律 null ＋ 原因，<b>绝不填一个看着像的数</b>。
    /// </summary>
    public static CdrStyleFontResolution ResolveStyleFont(CdrStltStyleTable? table, CdrTxsmParagraph? para,
        IReadOnlyList<CdrFontRecord> fontBlocks)
    {
        var res = new CdrStyleFontResolution { FontTableId = -1 };
        if (para is null)
        {
            res.SizeUnknown = "txsm 一条段落都没有，连 style_id 都拿不到";
            return res;
        }
        res.ParagraphStyleId = para.StyleId;
        if (table is null)
        {
            res.SizeUnknown = "文档里没有 LIST[stlt] 样式表";
            return res;
        }
        if (!table.Ok)
        {
            res.SizeUnknown = "stlt 样式表没走通：" + table.Unknown;
            return res;
        }
        var rec = table.RecordFor(para.StyleId);
        if (rec is null)
        {
            res.SizeUnknown = $"段落 style_id 0x{para.StyleId:x8} 在 stlt 的 {table.Records.Count} 条 records 里没命中";
            return res;
        }
        res.StyleRecordIndex = rec.Index;
        res.StyleRecordName = rec.Name;
        if (!rec.HasFontField)
        {
            res.SizeUnknown = $"段落记录 #{rec.Index}（num={rec.Num}）的形状只带 fill/outl，没有 font_rec_id 字段（这不等于字号是 0）";
            return res;
        }
        res.FontRecId = rec.FontRecId;
        var f = table.FontFor(rec.FontRecId);
        if (f is null)
        {
            res.SizeUnknown = $"段落记录 #{rec.Index} 的 font_rec_id 0x{rec.FontRecId:x8} 在 fonts 映射段（{table.Fonts.Count} 条）里没有这个 ID";
            return res;
        }
        res.FontEntryIndex = f.Index;
        res.SizeOk = true;
        res.SizeRaw = f.SizeRaw;
        res.SizePt = CdrBinaryUnits.Pt(f.SizeRaw);
        res.FontTableId = f.FontTableId;
        res.FlagsHex = "0x" + f.Flags.ToString("x8");
        res.FlagsName = f.FlagName ?? "未登记";
        res.Bold = f.Flags switch
        {
            0x0000_1000 or 0x0000_2000 => true,
            0x0000_0040 or 0x0000_0080 or 0x0000_0100 or 0x0000_0200 => false,
            _ => (bool?)null,
        };
        res.Italic = f.Flags switch
        {
            0x0000_0080 or 0x0000_2000 => true,
            0x0000_0040 or 0x0000_1000 or 0x0000_0100 => false,
            _ => (bool?)null,
        };
        if (res.Bold is null)
            res.Notes.Add($"样式旗标 {res.FlagsHex} 落在 ksy font_style 表里没登记过的那批 → Bold/Italic 不填");
        if (f.SizeCopiesNote is not null) res.Notes.Add("fonts 条目：" + f.SizeCopiesNote);

        // 名字：段落样式给的字体号是底；**首个显式带字体的字符样式**（Corel 的 run 覆盖）优先——
        // 条码那两支实测只有走这条才对得上 A（段落样式给 Arial，A 报宋体，首个显式 run 给宋体）。
        var (runFont, runEnc) = FirstExplicitRunFont(para);
        var (id, enc, source) = runFont is null
            ? (f.FontTableId, f.Encoding, "段落样式记录")
            : (runFont.Value, runEnc ?? f.Encoding, "首个带 has_font 的字符样式");
        res.FontNameSource = source;
        var (nm, why) = LookupFontName(fontBlocks, id, enc);
        res.FontName = nm;
        res.FontNameUnknown = why;
        if (runFont is not null && runFont.Value != f.FontTableId)
            res.Notes.Add($"字体名取自字符样式 run 的 font_id={runFont}，与段落样式的 {f.FontTableId} 不同" +
                          "（字号仍按段落样式：字符样式的 has_font_size 六份都没置）");
        return res;
    }

    /// <summary>本段里第一个显式带字体的字符样式：返回 (u2 font_id, u2 encoding)；没有就 (null, null)。</summary>
    public static (int? FontId, int? Encoding) FirstExplicitRunFont(CdrTxsmParagraph? para)
    {
        if (para is null) return (null, null);
        foreach (var s in para.Styles)
            if (s.Font is { Length: >= 2 } fi && (s.FlagsRaw & 1) != 0)
                return (fi[0], fi[1]);
        return (null, null);
    }

    /// <summary>
    /// font 块表按 <paramref name="fontId"/> 查名字。<b>同一 ID 可以有多条</b>（金沐实测 id=21 三条），
    /// 名字全一致就直接用；不一致就先按编码收窄；还收不拢就<b>不填</b>并给原因——不许挑一条。
    /// </summary>
    public static (string? Name, string? Unknown) LookupFontName(IReadOnlyList<CdrFontRecord> blocks, int fontId, int enc)
    {
        var same = blocks.Where(b => b.FontId == fontId).ToList();
        if (same.Count == 0) return (null, $"font 块表里没有 font_id={fontId} 的条目");
        var names = same.Select(b => b.Name).Distinct().ToList();
        if (names.Count == 1) return (names[0], null);
        var byEnc = same.Where(b => b.EncodingRaw == enc).Select(b => b.Name).Distinct().ToList();
        if (byEnc.Count == 1) return (byEnc[0], null);
        return (null, $"font_id={fontId} 在表里有 {same.Count} 条、{names.Count} 个不同名字（{string.Join("/", names)}），" +
                      $"按编码 {enc} 也收不拢 → 名字不填");
    }

    static string ReadAscii(ReadOnlySpan<byte> b, int at, int len)
    {
        var sb = new StringBuilder(len);
        for (var i = 0; i < len && at + i < b.Length; i++) sb.Append((char)b[at + i]);
        return sb.ToString();
    }

    /// <summary>
    /// 在 stlt 体里找一个字号 raw（Corel 定点，容差 ±3）——探针 <c>p5_anchors.font_size_check</c> 的口径。
    /// 返回它在 stlt 体里的字节位置；<b>这只是"文档里确有这个字号"的按值搜索</b>，
    /// 对象级字号走 <see cref="ParseStltStyleTable"/> ＋ <see cref="ResolveStyleFont"/> 那条 ID→ID 链。
    /// </summary>
    public static int FindStltSizeRaw(ReadOnlySpan<byte> body, long wantRaw, long tolerance = 3)
    {
        for (var k = 0; k + 4 <= body.Length; k += 4)
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(k, 4));
            if (Math.Abs((long)v - wantRaw) <= tolerance) return k;
        }
        return -1;
    }

    static double Round(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);

    internal static string ToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>给报告用的小写空格分隔 hex。</summary>
    public static string HexSpaced(ReadOnlySpan<byte> bytes) => ToHex(bytes);

    internal static string DescribeDouble(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
