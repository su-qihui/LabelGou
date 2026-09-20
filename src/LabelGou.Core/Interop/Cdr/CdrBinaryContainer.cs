using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LabelGou.Core.Interop.Cdr;

/// <summary>
/// B 通道的单位口径：Corel 定点 = 1/10000 mm（= 254000/inch）。
/// 全仓只在这里出现一次除号，其余地方一律调本类，别在解析里再写 /10000。
/// </summary>
public static class CdrBinaryUnits
{
    public const double CorelUnitsPerMm = 10000.0;
    public const double UnitsPerInch = 254000.0;

    /// <summary>Corel 定点 → 毫米。</summary>
    public static double Mm(long corelRaw) => corelRaw / CorelUnitsPerMm;

    /// <summary>Corel 定点字号（1/10000 mm）→ 磅。</summary>
    public static double Pt(long corelRaw) => Mm(corelRaw) / 25.4 * 72.0;
}

/// <summary>RIFF 顶层子块（L0）：只清点，不解读。</summary>
public sealed class CdrTopChunk
{
    public int At { get; init; }
    public string Fourcc { get; init; } = "";
    public int Length { get; init; }

    /// <summary>LIST 的 form_type，非 LIST 为 null。</summary>
    public string? Form { get; init; }
}

/// <summary>
/// 一条块记录（L2）。<see cref="Length"/> 不是流里就地读出来的，
/// 而是 <see cref="PoolIndex"/> 指到"第二块＝u32 长度池"里取的那个 u32
/// ——分区实测：按 1 字节下标六份全部越界，按 u32 则"块记录数＝池项数"精确相等且零残余。
/// </summary>
public sealed class CdrBlock
{
    public required CdrSegment Segment { get; init; }
    public int Depth { get; init; }
    public string Path { get; init; } = "";

    /// <summary>块记录（fourcc 那 4 个字节）在块流里的起点。</summary>
    public int At { get; init; }

    public string Fourcc { get; init; } = "";
    public int PoolIndex { get; init; }
    public int Length { get; init; }

    /// <summary>LIST 的 form_type；非 LIST 为 null。递归时回填。</summary>
    public string? Form { get; internal set; }

    /// <summary>合成记录（"残余/越界/池下标越界"），不是真块。</summary>
    public bool Synthetic { get; init; }

    /// <summary>解不出的原因。非 null＝这一条只能登记，不许当成"它不存在"。</summary>
    public string? Unknown { get; init; }

    /// <summary>非块序列的容器（stlt/cmpr 的体是表，不是子块序列）。</summary>
    public string? BodyKind { get; internal set; }

    /// <summary>fourcc 里有非可打印字节：原样记 hex，不猜名字。</summary>
    public string? SuspiciousFourccHex { get; init; }

    public bool IsList => Fourcc == CdrBinaryFourcc.List;

    /// <summary>体起点：LIST 的体含 4 字节 form_type（与探针同一口径）。</summary>
    public int BodyAt => At + 8;

    /// <summary>体的末点（不含）。</summary>
    public int BodyEnd => At + 8 + Length;

    /// <summary>体（越界就截断，与 Python 切片同行为）。</summary>
    public Span<byte> Body => Segment.Slice(BodyAt, Length);

    /// <summary>孩子区的起点：LIST 要跳过 4 字节 form_type。</summary>
    public int ChildrenAt => BodyAt + (IsList ? 4 : 0);

    public string Name => Form is null ? Fourcc : $"{Fourcc}[{Form}]";
}

/// <summary>一段 <c>LIST/cmpr</c>：解压后是「块流 + u32 长度池」两条流（L1）。</summary>
public sealed class CdrSegment
{
    public required CdrBinaryContainer Container { get; init; }
    public int Index { get; internal set; }

    /// <summary>LIST 体首（form_type "cmpr" 那 4 字节的起点）。</summary>
    public int BodyAt { get; internal set; }

    public uint C1 { get; internal set; }
    public uint U1 { get; internal set; }
    public uint C2 { get; internal set; }
    public uint U2 { get; internal set; }

    /// <summary>头里那个四字符标记，实测恒为 <c>CPng</c>。</summary>
    public string CpngTag { get; internal set; } = "";

    /// <summary>头尾旗标，实测恒为 <c>01 00 04 00</c>。</summary>
    public string MagicHex { get; internal set; } = "";

    /// <summary>第一块＝块树流；解不出为空数组。</summary>
    public byte[] BlockStream { get; private set; } = Array.Empty<byte>();

    /// <summary>第二块＝u32 长度池；解不出为空数组。</summary>
    public byte[] Pool { get; private set; } = Array.Empty<byte>();

    public string Part1Note { get; private set; } = "";
    public string Part2Note { get; private set; } = "";
    public string? Part2HeaderTag { get; private set; }
    public int Part2HeaderAt { get; private set; } = -1;

    /// <summary>块树；未走（空段/解不出）时为空表。</summary>
    public List<CdrBlock> Blocks { get; } = new();

    /// <summary>这一段的降级/未解说明。</summary>
    public List<string> Notes { get; } = new();

    public int PoolItems => Pool.Length / 4;
    public int PoolTailBytes => Pool.Length % 4;
    public int RecordCount => Blocks.Count(b => !b.Synthetic);
    public int ResidualCount => Blocks.Count(b => b.Synthetic);
    public bool Empty => C1 == 16 && U1 == 0 && C2 == 16 && U2 == 0;
    public bool Walked => Blocks.Count > 0;

    /// <summary>块流 sha256 前 16 位（对账探针夹具用；位图那种大流也只用它当锚，不搬字节）。</summary>
    public string? BlockStreamSha16 { get; private set; }

    public bool RecordsEqualPoolItems => RecordCount == PoolItems;

    public CdrBlock? Find(string fourcc, string? form = null, int afterAt = 0)
    {
        foreach (var b in Blocks)
        {
            if (b.At < afterAt) continue;
            if (b.Synthetic || b.Fourcc != fourcc) continue;
            if (form is null || b.Form == form) return b;
        }
        return null;
    }

    public List<CdrBlock> FindAll(string fourcc, string? form = null)
    {
        var list = new List<CdrBlock>();
        foreach (var b in Blocks)
            if (!b.Synthetic && b.Fourcc == fourcc && (form is null || b.Form == form)) list.Add(b);
        return list;
    }

    /// <summary>某容器的直接子块：at 落在体内且 depth 恰好 +1（探针实测：不能按 path 认，会串台）。</summary>
    public List<CdrBlock> ChildrenOf(CdrBlock container)
    {
        var lo = container.ChildrenAt;
        var hi = container.BodyEnd;
        var list = new List<CdrBlock>();
        foreach (var b in Blocks)
            if (!b.Synthetic && b.Depth == container.Depth + 1 && b.At >= lo && b.At < hi) list.Add(b);
        return list;
    }

    public CdrBlock? ChildOf(CdrBlock? container, string fourcc, string? form = null)
    {
        if (container is null) return null;
        foreach (var b in ChildrenOf(container))
            if (b.Fourcc == fourcc && (form is null || b.Form == form)) return b;
        return null;
    }

    public Span<byte> Slice(int at, int length)
    {
        if (at < 0 || at >= BlockStream.Length) return Array.Empty<byte>();
        var len = Math.Min(length, BlockStream.Length - at);
        return len <= 0 ? Array.Empty<byte>() : BlockStream.AsSpan(at, len);
    }

    internal void SetParts(byte[] part1, byte[] part2, string note1, string note2, string? cpng2, int hdr2At)
    {
        BlockStream = part1;
        Pool = part2;
        Part1Note = note1;
        Part2Note = note2;
        Part2HeaderTag = cpng2;
        Part2HeaderAt = hdr2At;
        BlockStreamSha16 = part1.Length == 0 ? null : Sha16(part1);
    }

    static string Sha16(byte[] data)
    {
        var hash = SHA256.HashData(data);
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}

/// <summary>
/// B 通道的 L0/L1/L2：<c>.cdr</c>（ZIP 容器）→ <c>content/riffData.cdr</c>（RIFF/CDRE）
/// → <c>LIST/cmpr</c> 两段 → inflate 出「块流 + u32 长度池」→ 递归走块树。
/// <para>
/// 逐层都是照 <c>labegou-CDR\other\_probe\cdr3-offline\cdrb.py</c> 那份**已跑通**的探针移植的，
/// 不自己重新推导格式；三处口径已按实测坐实（详见 <see cref="CdrBinaryParser"/> 的类注释）。
/// </para>
/// </summary>
public sealed class CdrBinaryContainer
{
    /// <summary>LIST/cmpr 体首到 zlib 起点的距离：'cmpr'4 + 4×u32 16 + 'CPng'4 + 旗标 4 = 28。</summary>
    public const int CmprBodyZlibOffset = 28;

    /// <summary>长度池下标是 u32，所以一条块记录头是 fourcc(4)+u32(4)=8 字节。</summary>
    public const int BlockRecordHeaderBytes = 8;

    /// <summary>单段 inflate 的天花板（字节）。超了就不解，明写"这段太大没解"。</summary>
    public const long InflateCapBytes = 256L * 1024 * 1024;

    /// <summary>递归深度上限（探针 64，真文件最深 5）。</summary>
    public const int MaxWalkDepth = 64;

    static readonly byte[] RiffDataEntryName = "content/riffData.cdr"u8.ToArray();

    /// <summary>这些 form_type 的体不是块序列，不许递归（ksy 给它们各自的表结构）。</summary>
    static readonly HashSet<string> NonContainerForms = new() { "stlt", "cmpr" };

    readonly byte[] _buf;

    CdrBinaryContainer(byte[] buf, string origin)
    {
        Buffer = buf;
        Origin = origin;
        _buf = buf;
    }

    /// <summary>RIFF 原始字节（ZIP 路线时＝<c>content/riffData.cdr</c> 条目内容）。</summary>
    public byte[] Buffer { get; }

    /// <summary>这份 RIFF 来自哪里（<c>zip:content/riffData.cdr</c> / <c>riff:</c> 前缀）。</summary>
    public string Origin { get; }

    /// <summary>RIFF 头声明的文件长。</summary>
    public int RiffSizeField { get; private set; }

    /// <summary>实测 <c>CDRE</c>。</summary>
    public string FormType { get; private set; } = "";

    /// <summary>
    /// <c>vrsn</c> 报的**值**（实测 1400 = X4）。
    /// 注意 <c>vrsn</c> 的**长度字段是 2**——把长度当值会读成 2（分区活文档原话就这么错过）。
    /// </summary>
    public int Version { get; private set; }

    /// <summary><c>vrsn</c> 的长度字段（实测 2）。</summary>
    public int VersionFieldLength { get; private set; }

    /// <summary>顶层子块清单（含 vrsn / LIST/cmpr / 其它）。</summary>
    public List<CdrTopChunk> TopChunks { get; } = new();

    /// <summary>解出来的 LIST/cmpr 段（实测恒 2 段：五份的第 0 段是空段，OLU 的第 0 段装位图）。</summary>
    public List<CdrSegment> Segments { get; } = new();

    /// <summary>容器层的降级说明（还没走到对象层就已经丢东西的地方）。</summary>
    public List<string> Degraded { get; } = new();

    /// <summary>整棵树里 ksy 与 libcdr 两张参照表都没登记过的 fourcc：只登记，不套语义。</summary>
    public HashSet<string> UnrecognizedFourcc { get; } = new(StringComparer.Ordinal);

    /// <summary>块流的原始字节别名（取证/测试直接按 at 区间取）。</summary>
    public byte[] RiffBytes => _buf;

    /// <summary>打开一个 <c>.cdr</c>：ZIP 路线与裸 RIFF 路线都试，都失败就抛，不静默返回空文档。</summary>
    public static CdrBinaryContainer OpenFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到这份 .cdr：" + path, path);
        var bytes = File.ReadAllBytes(path);
        return OpenBytes(bytes, Path.GetFileName(path));
    }

    public static CdrBinaryContainer OpenBytes(byte[] bytes, string origin)
    {
        byte[]? riff = null;
        var originTag = origin;
        if (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B)   // PK → ZIP
        {
            using var ms = new MemoryStream(bytes, false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry("content/riffData.cdr");
            if (entry is null)
            {
                var names = new List<string>();
                foreach (var e in zip.Entries) names.Add(e.FullName);
                throw new InvalidDataException(
                    $"这份 .cdr 的 ZIP 里没有 content/riffData.cdr 条目（只有 {names.Count} 个条目：{string.Join(", ", names.Take(8))}），" +
                    $"B 通道只能解 X4 那种把 RIFF 装进 ZIP 的写法：{origin}");
            }
            using var es = entry.Open();
            using var outMs = new MemoryStream();
            es.CopyTo(outMs);
            riff = outMs.ToArray();
            originTag = "zip:content/riffData.cdr ← " + origin;
        }
        else if (bytes.Length >= 4 && HasAscii(bytes, 0, "RIFF"))
        {
            riff = bytes;
            originTag = "riff:" + origin;
        }
        else
        {
            throw new InvalidDataException(
                $"这份 .cdr 既不是 ZIP 容器也不是裸 RIFF（头 4 字节 {HexAt(bytes, 0, 4)}），B 通道解不了：{origin}");
        }

        var c = new CdrBinaryContainer(riff, originTag);
        c.ReadRiff();
        return c;
    }

    void ReadRiff()
    {
        var buf = _buf;
        if (buf.Length < 12)
        {
            Degraded.Add($"riffData.cdr 只有 {buf.Length} 字节，不足 12 字节的 RIFF 头，容器层就没解开");
            return;
        }
        FormType = Ascii(buf, 8, 4);
        RiffSizeField = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
        if (Ascii(buf, 0, 4) != "RIFF")
            Degraded.Add($"RIFF 标记读到的不是 'RIFF' 而是 '{Ascii(buf, 0, 4)}'，仍按 RIFF 往下走，对账时留意");
        if (FormType != "CDRE")
            Degraded.Add($"RIFF form_type 是 '{FormType}'，不是探针实测的 'CDRE'，块树口径可能不同");

        // 顶层子块：从 12 起，fourcc(4) + u32 len + body + 偶数对齐
        var cur = 12;
        var firstSubchunk = (At: 0, Fourcc: "", Len: 0);
        while (cur + 8 <= buf.Length)
        {
            var cc = Ascii(buf, cur, 4);
            var ln = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(cur + 4));
            string? form = null;
            if (cc == CdrBinaryFourcc.List && ln >= 4) form = Ascii(buf, cur + 8, 4);
            TopChunks.Add(new CdrTopChunk { At = cur, Fourcc = cc, Length = ln, Form = form });
            if (firstSubchunk.Fourcc.Length == 0) firstSubchunk = (cur, cc, ln);
            if (cc == CdrBinaryFourcc.List && form == "cmpr") ReadOneSegment(cur + 8, ln);
            cur = Add(cur, 8, ln, ln % 2);
            if (cur > buf.Length)
            {
                Degraded.Add($"顶层子块扫描在 {cur} 处冲出文件尾（文件 {buf.Length} 字节），后面的段没读到");
                break;
            }
        }
        if (firstSubchunk.Fourcc == CdrBinaryFourcc.Version)
        {
            VersionFieldLength = firstSubchunk.Len;
            var bodyAt = firstSubchunk.At + 8;
            if (firstSubchunk.Len >= 2 && bodyAt + 2 <= buf.Length)
                Version = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(bodyAt));
            else
                Degraded.Add($"vrsn 体不足 2 字节，Corel 版本没读到（长度字段 {firstSubchunk.Len}）");
            if (firstSubchunk.Len != 2)
                Degraded.Add($"vrsn 长度字段是 {firstSubchunk.Len}（探针实测 2），值的取法可能要跟着改");
        }
        else if (firstSubchunk.Fourcc.Length > 0)
        {
            Degraded.Add($"第一个顶层子块是 '{firstSubchunk.Fourcc}' 而不是 vrsn，Corel 版本没读到");
        }
        if (Segments.Count == 0)
            Degraded.Add("这段 RIFF 里一段 LIST/cmpr 都没有——对象层就没有（不等于这份设计是空的，先怀疑解法）");
    }

    /// <summary>解一段 <c>LIST/cmpr</c> 并走它的块树。</summary>
    void ReadOneSegment(int bodyAt, int listLen)
    {
        var seg = new CdrSegment { Container = this, Index = Segments.Count, BodyAt = bodyAt };
        Segments.Add(seg);
        if (_buf.Length - bodyAt < CmprBodyZlibOffset)
        {
            seg.Notes.Add($"LIST 体只有 {_buf.Length - bodyAt} 字节，不足 {CmprBodyZlibOffset} 字节的 cmpr 头，这段没解压");
            Degraded.Add($"cmpr#{seg.Index}：头不足 {CmprBodyZlibOffset} 字节，整段没解");
            return;
        }
        var h = _buf.AsSpan(bodyAt, CmprBodyZlibOffset);
        seg.CpngTag = Ascii(_buf, bodyAt + 20, 4);
        seg.MagicHex = Hex(_buf.AsSpan(bodyAt + 24, 4).ToArray());
        seg.C1 = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(4, 4));
        seg.U1 = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(8, 4));
        seg.C2 = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(12, 4));
        seg.U2 = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(16, 4));
        if (seg.CpngTag != "CPng" || seg.MagicHex != "01000400")
        {
            seg.Notes.Add($"cmpr 头与探针实测不符（CPng='{seg.CpngTag}' 旗标={seg.MagicHex}），inflate 起点 +{CmprBodyZlibOffset} 可能不再成立");
            Degraded.Add($"cmpr#{seg.Index}：头形制与实测不同，解压结果不可信");
        }

        var zAt = bodyAt + CmprBodyZlibOffset;
        var (part1, note1) = Inflate(zAt, seg.U1, $"cmpr#{seg.Index} 第一块", seg.Notes, Degraded);

        // 第二块的头落在「第一块压缩体」的末尾：z_at + (c1 - 8)，声明的 c1 含它那 8 字节头
        var part2 = Array.Empty<byte>();
        var note2 = "";
        var hdrAt = -1;
        string? cpng2 = null;
        if (part1.Length > 0 || seg.C1 >= 8)
        {
            hdrAt = zAt + (int)(seg.C1 - 8);
            if (hdrAt + 8 <= _buf.Length)
            {
                cpng2 = Ascii(_buf, hdrAt, 4);
                var (p2, n2) = Inflate(hdrAt + 8, seg.U2, $"cmpr#{seg.Index} 第二块（长度池）", seg.Notes, Degraded);
                part2 = p2;
                note2 = n2;
            }
            else
            {
                note2 = seg.C1 == 0 && seg.U2 == 0 ? "空段（无长度池）" : $"第二块头位置 {hdrAt} 越界（文件 {_buf.Length}）";
                seg.Notes.Add(note2);
            }
        }
        seg.SetParts(part1, part2, note1, note2, cpng2, hdrAt);
        if (part1.Length == 0 && part2.Length == 0 && seg.U1 == 0 && seg.U2 == 0)
        {
            seg.Notes.Add("这段 LIST/cmpr 声明 0 字节（探针实测：六份里有五份的第 0 段是空段），无块可走");
            return;
        }
        if (cpng2 is not null && cpng2 != "CPng")
            seg.Notes.Add($"第二块头不是 CPng 而是 '{cpng2}'，长度池可能取错位置");
        if (part1.Length == 0 || part2.Length == 0)
        {
            Degraded.Add($"cmpr#{seg.Index}：块流 {part1.Length} B / 长度池 {part2.Length} B，有一条流没解出来，这段的对象一个都没读到");
            return;
        }
        WalkBlocks(seg);
        foreach (var b in seg.Blocks)
            if (!b.Synthetic && !CdrBinaryFourcc.IsKnown(b.Fourcc)) UnrecognizedFourcc.Add(b.Name);
    }

    (byte[] Data, string Note) Inflate(int at, uint declaredRaw, string what, List<string> segNotes, List<string> degraded)
    {
        if (at >= _buf.Length) return (Array.Empty<byte>(), $"{what}：起点 {at} 越界");
        if (declaredRaw > InflateCapBytes)
        {
            var n = $"{what}：声明解压 {declaredRaw} B 超过上限 {InflateCapBytes} B，这段没解";
            segNotes.Add(n);
            degraded.Add(n);
            return (Array.Empty<byte>(), n);
        }
        try
        {
            using var src = new MemoryStream(_buf, at, _buf.Length - at, false, false);
            using var zs = new ZLibStream(src, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = zs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ms.Write(buffer, 0, read);
                total += read;
                if (total > InflateCapBytes)
                {
                    var n = $"{what}：实解字节超过上限 {InflateCapBytes} B，已中止";
                    segNotes.Add(n);
                    degraded.Add(n);
                    return (Array.Empty<byte>(), n);
                }
            }
            var data = ms.ToArray();
            // src.Position = zlib 流实际吃掉的字节（含它自己读过头的那点），拿它跟声明的压缩长度对个账
            var consumed = src.Position;
            var tail = consumed > 0 && at + consumed < _buf.Length
                ? $"（后面还有 {_buf.Length - at - consumed} 字节未消费）"
                : "（流正好到头）";
            var note = $"实解 {data.Length} B / 声明 {declaredRaw} B " +
                       (data.Length == declaredRaw ? "相符" : "不符") + tail;
            if (data.Length != declaredRaw)
            {
                segNotes.Add($"{what}：{note}");
                degraded.Add($"{what}：{note}");
            }
            return (data, note);
        }
        catch (Exception e)   // zlib 头不对/数据坏了：明写解不出，别当成"这段没有内容"
        {
            var n = $"{what}：inflate 失败（{e.GetType().Name}: {e.Message}）——起点 0x{at:x} 处前两字节 " +
                    Hex(_buf.AsSpan(at, Math.Min(2, _buf.Length - at)).ToArray()) + "，探针实测 zlib 头应是 78 9c";
            segNotes.Add(n);
            degraded.Add(n);
            return (Array.Empty<byte>(), n);
        }
    }

    /// <summary>
    /// 走块树：fourcc(4) + u32 池下标，长度取池里那个 u32；LIST 递归时跳过 4 字节 form_type。
    /// 探针口径：一条流走到尾应当**零残余**，且记录数恰等于池项数——不满足就把残余/越界写进块表。
    /// </summary>
    static void WalkBlocks(CdrSegment seg)
    {
        var data = seg.BlockStream;
        var pool = seg.Pool;
        int PoolLength(int idx) => idx >= 0 && idx <= (pool.Length - 4) / 4
            ? (int)BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan(idx * 4))
            : -1;

        void Walk(int start, int end, int depth, string path)
        {
            var cur = start;
            while (cur + BlockRecordHeaderBytes <= end)
            {
                var cc = Ascii(data, cur, 4);
                var idx = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cur + 4));
                var suspicious = SuspiciousHex(data, cur, 4);
                var ln = PoolLength(idx);
                if (ln < 0)
                {
                    seg.Blocks.Add(new CdrBlock
                    {
                        Segment = seg, Depth = depth, Path = path, At = cur, Fourcc = cc,
                        PoolIndex = idx, Unknown = $"池下标 {idx} 越界（池 {pool.Length / 4} 项），这一层到此为止",
                    });
                    return;
                }
                var block = new CdrBlock
                {
                    Segment = seg, Depth = depth, Path = path, At = cur, Fourcc = cc,
                    PoolIndex = idx, Length = ln, SuspiciousFourccHex = suspicious,
                };
                seg.Blocks.Add(block);
                if (cc == CdrBinaryFourcc.List)
                {
                    var form = ln >= 4 ? Ascii(data, cur + 8, 4) : "?";
                    block.Form = form;
                    var childrenAt = cur + 12;
                    var childrenEnd = cur + 8 + ln;
                    if (NonContainerForms.Contains(form))
                        block.BodyKind = $"非块序列（{form} 的体是一张表，探针没逐字段解）";
                    else if (depth < MaxWalkDepth && ln >= 4)
                        Walk(childrenAt, childrenEnd, depth + 1, path + "/" + cc + ":" + form);
                    cur = Add(cur, BlockRecordHeaderBytes, ln, ln % 2);
                    continue;
                }
                cur = Add(cur, BlockRecordHeaderBytes, ln, ln % 2);
            }
            if (cur != end)
            {
                var over = cur > end;
                seg.Blocks.Add(new CdrBlock
                {
                    Segment = seg, Depth = depth, Path = path, At = Math.Min(cur, end),
                    Fourcc = over ? "<越界>" : "<残余>",
                    Synthetic = true,
                    Unknown = $"{(over ? "越界" : "残余")} {Math.Abs(end - cur)} 字节（消费点 {cur} / 容器体长 {end - start}）",
                });
            }
        }

        Walk(0, data.Length, 0, "");
    }

    static int Add(int a, int b, int c, int d)
    {
        var v = (long)a + b + c + d;
        return v > int.MaxValue ? int.MaxValue : (int)v;
    }

    static string Ascii(byte[] buf, int at, int len)
    {
        if (at < 0 || at + len > buf.Length) len = Math.Max(0, buf.Length - at);
        if (len <= 0) return "";
        // latin1 一字节一字符：fourcc 里出现控制字节也要留得住原样
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = (char)buf[at + i];
        return new string(chars);
    }

    static string? SuspiciousHex(byte[] buf, int at, int len)
    {
        if (at + len > buf.Length) return null;
        for (var i = 0; i < len; i++)
            if (buf[at + i] < 0x20 || buf[at + i] > 0x7E) return Hex(buf.AsSpan(at, len).ToArray());
        return null;
    }

    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    static string HexAt(byte[] bytes, int at, int len)
    {
        if (at < 0 || at >= bytes.Length || len <= 0) return "";
        return Convert.ToHexString(bytes.AsSpan(at, Math.Min(len, bytes.Length - at))).ToLowerInvariant();
    }

    static bool HasAscii(byte[] buf, int at, string s)
    {
        if (at + s.Length > buf.Length) return false;
        for (var i = 0; i < s.Length; i++) if (buf[at + i] != (byte)s[i]) return false;
        return true;
    }
}

/// <summary>
/// 块名登记表。三档：
/// <see cref="Decoded"/>＝B 通道真给了语义；<see cref="NamedBySpec"/>＝ksy 或 libcdr 表里登记过但本轮没解；
/// 两表都没有的（实测：ole2/olel）→ 原样进 <c>UnrecognizedFourcc</c>，只登记不套语义。
/// </summary>
public static class CdrBinaryFourcc
{
    public const string List = "LIST";
    public const string Version = "vrsn";
    public const string Bbox = "bbox";
    public const string Obbx = "obbx";
    public const string Loda = "loda";
    public const string Trfd = "trfd";
    public const string Txsm = "txsm";
    public const string Font = "font";
    public const string Mcfg = "mcfg";
    public const string Olel = "olel";
    public const string Ole2 = "ole2";
    public const string Bmp = "bmp ";

    /// <summary>本轮真解开了语义的块名（其余只能登记）。</summary>
    public static readonly HashSet<string> Decoded = new(StringComparer.Ordinal)
    {
        List, Version, Bbox, Obbx, Loda, Trfd, Txsm, Font, Mcfg,
    };

    /// <summary>
    /// ksy <c>chunk_data_common</c> 有名 + libcdr <c>CDR_FOURCC_*</c> 有名（两份实测清单合并），
    /// 但本轮没解语义的块：登记它们的存在，不许被读成"没有这个块"。
    /// </summary>
    public static readonly HashSet<string> NamedBySpec = new(StringComparer.Ordinal)
    {
        "DISP", "fild", "fill", "flgs", "fver", "lobj", "outl", "spnd", "stlt", "uidr", "urls", "usdn",
        "bmp ", "ptrt", "pfrd", "pref", "osfp", "txtj", "ftil",
    };

    /// <summary>LIST 的 form_type（libcdr 有名字的容器形态）。</summary>
    public static readonly HashSet<string> NamedForms = new(StringComparer.Ordinal)
    {
        "doc ", "page", "gobj", "layr", "lgob", "obj ", "grp ", "trfl", "fntt", "filt", "filc",
        "otlt", "stlt", "cmpr", "bmpt", "data", "cctn",
    };

    public static bool IsKnown(string fourcc)
        => fourcc == List || Decoded.Contains(fourcc) || NamedBySpec.Contains(fourcc);
}
