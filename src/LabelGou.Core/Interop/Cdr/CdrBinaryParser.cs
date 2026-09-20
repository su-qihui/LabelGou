using System.Buffers.Binary;
using System.Globalization;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Interop.Cdr;

/// <summary>
/// 一只对象在文件里的原始事实（判据与对账要用：盒的原始四元组、loda 类型号、txsm 结果、olel/ole2 有没有）。
/// </summary>
public sealed class CdrBinaryObjectFacts
{
    public int SegmentIndex { get; init; }
    public int ContainerAt { get; init; }
    public int ContainerLength { get; init; }
    public int Depth { get; init; }
    public string Path { get; init; } = "";
    public bool IsGroup { get; init; }
    public int[]? BboxRaw { get; init; }
    public int[]? ObbxRaw { get; init; }
    public CdrFrame? Box { get; init; }
    public int? LodaTypeRaw { get; init; }
    public string? LodaType { get; init; }
    public CdrLoda? Loda { get; init; }
    public List<CdrTrafo> Trafos { get; init; } = new();
    public CdrTxsm? Txsm { get; init; }

    /// <summary>字号链的解析结果（段落 style_id → records → fonts）；非文字对象为 null。</summary>
    public CdrStyleFontResolution? StyleFont { get; init; }
    public bool HasOlel { get; init; }
    public bool HasOle2 { get; init; }
    public int Ole2Length { get; init; }
    public List<string> ChildNames { get; init; } = new();

    /// <summary>这只对象最终产出的 cdrx 记录。</summary>
    public CdrxObject Object { get; init; } = new();
}

/// <summary>一段 <c>LIST/cmpr</c> 的清点事实（"块记录数＝池项数、零残余"这类判据要用的数）。</summary>
public sealed class CdrSegmentFacts
{
    public int Index { get; init; }
    public int BodyAt { get; init; }
    public uint C1 { get; init; }
    public uint U1 { get; init; }
    public uint C2 { get; init; }
    public uint U2 { get; init; }
    public int Part1Length { get; init; }
    public int Part2Length { get; init; }
    public int PoolItems { get; init; }
    public int PoolTailBytes { get; init; }
    public int Records { get; init; }
    public int Residuals { get; init; }
    public bool RecordsEqualPoolItems { get; init; }
    public string? Part1Sha16 { get; init; }
    public string Part1Note { get; init; } = "";
    public string Part2Note { get; init; } = "";
    public string CpngTag { get; init; } = "";
    public string MagicHex { get; init; } = "";
    public int ObjectContainers { get; init; }
    public int GroupContainers { get; init; }
    public int BitmapBlocks { get; init; }
    public long BitmapBodyBytes { get; init; }
    public int BmptForms { get; init; }
    public List<string> Notes { get; init; } = new();
}

/// <summary>B 通道的产物：一份 <see cref="CdrxDoc"/> ＋ 对账要用的原始事实。</summary>
public sealed class CdrBinaryDocument
{
    /// <summary>与 A 通道（<c>.cdrx.json</c>）同一个消费形状，消费方不用管这份是从哪条路来的。</summary>
    public CdrxDoc Doc { get; init; } = new();

    public required CdrBinaryContainer Container { get; init; }

    /// <summary>页宽/页高（mm）。null＝mcfg 没解出来，此时一只对象的盒都不算。</summary>
    public double? PageWidthMm { get; init; }
    public double? PageHeightMm { get; init; }

    /// <summary>页尺寸是从哪来的，报告里要说清。</summary>
    public string PageSource { get; init; } = "";

    public int Version { get; init; }
    public int VersionFieldLength { get; init; }

    public List<CdrFontRecord> Fonts { get; init; } = new();

    /// <summary>文档级 stlt 样式表（字号链的那张表）；六份实测每份恰好一张。</summary>
    public CdrStltStyleTable? StyleTable { get; init; }

    /// <summary>= <see cref="StyleTable"/> 的 fonts 映射段（旧名字的延续，字号候选全集）。</summary>
    public List<CdrStltStyleRecord> StltStyleRecords { get; init; } = new();
    public int StltBodyLength { get; init; }
    public int StltAt { get; init; } = -1;
    public int StltTables { get; init; }
    public List<CdrSegmentFacts> Segments { get; init; } = new();

    /// <summary>obj/grp 容器，按文件树前序（与 A 的 z 序同向）。</summary>
    public List<CdrBinaryObjectFacts> Facts { get; init; } = new();

    /// <summary><c>LIST[obj ]</c> 容器总数（跨段合计）。群组不算在内。</summary>
    public int ObjectContainerCount { get; init; }

    public int GroupContainerCount { get; init; }
    public int PageContainers { get; init; }
    public int PagesWithObjects { get; init; }
    public List<string> Degraded { get; init; } = new();
    public List<string> UnrecognizedFourcc { get; init; } = new();
    public int FildBlocks { get; init; }
    public long FildBytes { get; init; }
    public int OutlBlocks { get; init; }
    public long OutlBytes { get; init; }

    /// <summary>
    /// 在各段的 stlt 体里找字号 raw（Corel 定点，容差 ±3），返回它在 stlt 体里的字节位置，-1＝没找到。
    /// 这是"文档里确有这个字号"，<b>不是</b>"某个对象用的是这个字号"（索引链未标定）。
    /// </summary>
    public int FindStltSizeRaw(long wantRaw)
    {
        foreach (var seg in Container.Segments)
        {
            var st = seg.Find("LIST", "stlt");
            if (st is null) continue;
            var at = CdrBinaryCodecs.FindStltSizeRaw(st.Body, wantRaw);
            if (at >= 0) return at;
        }
        return -1;
    }
}

/// <summary>
/// B 通道：<strong>不启动 CorelDRAW，离线直解 <c>.cdr</c> 二进制</strong>，产出 <see cref="CdrxDoc"/>。
/// <para>
/// 整条链路是 <c>labegou-CDR\other\_probe\cdr3-offline\</c> 那套已跑通、已核对的 Python 探针
/// （<c>cdrb.py</c> 的 L0~L3 + <c>p3_l3.parse_txsm7</c> + <c>p4_all6</c> 的对象提取与对账口径）的移植。
/// 三处口径按实测坐实：① 长度池下标是 <c>u32</c> 不是 1 字节；② <c>vrsn</c> 的<b>长度</b>是 2、<b>值</b>是 1400；
/// ③ 对象盒以<b>页中心</b>为原点（<see cref="CdrBinaryCodecs.PageCenterBox"/>）。
/// </para>
/// <para>
/// <strong>降级不静默</strong>：解不出的块/字段进 <c>Source.Degraded[]</c> 或对象 <c>Notes[]</c> 并点名是哪一类，
/// "读不出来"绝不写成"它不存在"。本轮解不出的清单见 <see cref="NotSolvedThisRound"/>
/// 与每份文档实际生成的 <c>Degraded</c> 行。
/// </para>
/// </summary>
public static class CdrBinaryParser
{
    /// <summary>cdrx 的 source.kind：B 通道固定是它，回归比对按它分组。</summary>
    public const string SourceKind = "cdr-binary";

    public const string ProducerName = "LabelGou cdr-binary B 通道（离线直解 .cdr，不依赖 CorelDRAW）";

    /// <summary>
    /// 本轮解不出的东西（逐条说明缺什么知识）。这些主题每一行都会以带实测数字的形式进
    /// <c>Source.Degraded</c>，本表是给人对着看的清单。
    /// </summary>
    public static readonly string[] NotSolvedThisRound =
    {
        "颜色 fild/outl：两张文档级表只登记未解，loda 的 fill_style/line_style 参数到表项的索引也没串 → Ink/Stroke 全 null",
        "粗体只有文件侧一面账：Bold 的旗标取自串出字号的那条 stlt fonts 条目 +48（ksy font_style：0x1000=bold / 0x40=normal / 0x100=medium，旁证是 0x100 那条的名字就叫 Noto Sans SC Medium），但 A 通道六份都没报 bold，无从对账 → Bold 的语义未由 A 验证，且对象里若有字符样式 run 自带旗标时两者可以不一致（金沐 obj0 实测如此，已写进对象 Notes）",
        "字号链/字体名的残余：见 Source.Degraded 里「字号链」那行的逐份实测数（没串上的对象会逐只点名，一条都不许吞）",
        "节点级几何：loda 的 polygon_coords / line_and_curve 点列取不出长度自洽的 (s4,s4) 序列（逆序对应不是普遍规律）→ Curve 不填；六份里零支真曲线，spline/path 的载荷规范是空的",
        "内嵌位图：LIST[bmpt]/'bmp ' 像素流只定位不导出（OLU 第 0 段 9,546,456 B）→ Image 不填",
        "图层与群组：块树里没有图层名（只在 ZIP 的 metadata/textinfo.xml）→ Layers 空、对象 Layer=null；群组层级按 LIST[grp ] 的块树嵌套重建，但成员 trfd 的局部/全页口径未定案",
        "OLE 内容：olel/ole2 两张参照表都没登记，内嵌复合文档里的条码数字解不出 → 只有盒，kind=unknown，保留可见",
        "txtj / pref / pfrd / osfp / ptrt / ftil / usdn / spnd / flgs：只登记字节，未定语义（flgs 未解，所以本通道一律不判隐形）",
        "圆角：loda rectangle 的 6×coord 原样进 Notes，r0..r3 的圆角语义未标定 → Geom 不填",
    };

    /// <summary>解一份真 <c>.cdr</c>，直接给消费方用的 <see cref="CdrxDoc"/>。</summary>
    public static CdrxDoc ParseFile(string cdrPath) => ParseFileDetailed(cdrPath).Doc;

    /// <summary>解一份真 <c>.cdr</c>，连对账事实一起给（单测与诊断走这条）。</summary>
    public static CdrBinaryDocument ParseFileDetailed(string cdrPath)
        => Build(CdrBinaryContainer.OpenFile(cdrPath), Path.GetFileName(cdrPath));

    public static CdrBinaryDocument ParseBytesDetailed(byte[] cdrBytes, string originName)
        => Build(CdrBinaryContainer.OpenBytes(cdrBytes, originName), originName);

    static CdrBinaryDocument Build(CdrBinaryContainer container, string fileName)
    {
        var doc = new CdrxDoc
        {
            FormatVersion = CdrxFormat.Version,
            Source =
            {
                File = fileName,
                Kind = SourceKind,
                Producer = ProducerName,
                CorelVersion = container.Version > 0 ? container.Version.ToString(CultureInfo.InvariantCulture) : null,
            },
        };

        // 页尺寸：盒的算法要用页宽高；拿不到就一只盒都不产（不许拿 0 当页尺寸硬算）
        double? pageW = null, pageH = null;
        var pageSource = "没解出（LIST[doc ]/mcfg 缺失或体太短）";
        var fild = 0;
        long fildBytes = 0;
        var outl = 0;
        long outlBytes = 0;
        var pageContainers = 0;
        var fonts = new List<CdrFontRecord>();
        var stltRecords = new List<CdrStltStyleRecord>();
        CdrStltStyleTable? styleTable = null;
        var stltAt = -1;
        var stltLen = 0;
        var stltTables = 0;
        var unrecognized = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var seg in container.Segments)
        {
            foreach (var b in seg.Blocks)
            {
                if (b.Synthetic) continue;
                switch (b.Fourcc)
                {
                    case "fild":
                        fild++; fildBytes += b.Length; break;
                    case "outl":
                        outl++; outlBytes += b.Length; break;
                    case "font":
                        fonts.Add(CdrBinaryCodecs.DecodeFont(b.Body, b.At));
                        break;
                    case "mcfg" when pageW is null:
                    {
                        var s = CdrBinaryCodecs.DecodeMcfgPageSize(b.Body, out var note);
                        if (s is not null)
                        {
                            pageW = s.Value.W;
                            pageH = s.Value.H;
                            pageSource = "LIST[doc ]/mcfg 体 +12/+16（Corel 定点，原样 " +
                                         string.Join("×", CdrBinaryCodecs.McfgRawHead(b.Body).Skip(3).Take(2)) + "）";
                        }
                        if (note is not null) container.Degraded.Add($"cmpr#{seg.Index} mcfg：{note}");
                        break;
                    }
                }
                if (b.Fourcc == "LIST" && b.Form == "stlt")
                {
                    stltTables++;
                    if (stltAt < 0)
                    {
                        stltAt = b.At;
                        stltLen = b.Length;
                        styleTable = CdrBinaryCodecs.ParseStltStyleTable(b.Body, container.Version);
                        stltRecords = styleTable.Fonts;
                    }
                }
                if (b.Fourcc == "LIST" && b.Form == "page") pageContainers++;
                if (!CdrBinaryFourcc.IsKnown(b.Fourcc)) unrecognized.Add(b.Name);
            }
        }

        var segFacts = new List<CdrSegmentFacts>();
        var facts = new List<CdrBinaryObjectFacts>();
        var objCount = 0;
        var grpCount = 0;
        var segmentsWithObjects = 0;

        foreach (var seg in container.Segments)
        {
            var objs = seg.FindAll("LIST", "obj ");
            var grps = seg.FindAll("LIST", "grp ");
            var bmps = seg.FindAll("bmp ");
            long bmpBytes = 0;
            foreach (var m in bmps) bmpBytes += m.Length;

            var notes = new List<string>(seg.Notes);
            if (seg.Walked && !seg.RecordsEqualPoolItems)
                notes.Add($"块记录数 {seg.RecordCount} ≠ 长度池项数 {seg.PoolItems}（探针实测六份恒相等）——池下标口径可能又退回 1 字节了");
            if (seg.ResidualCount > 0)
                notes.Add($"这块流有 {seg.ResidualCount} 处残余/越界，块树没走到尾，后面的对象读不到");
            segFacts.Add(new CdrSegmentFacts
            {
                Index = seg.Index,
                BodyAt = seg.BodyAt,
                C1 = seg.C1, U1 = seg.U1, C2 = seg.C2, U2 = seg.U2,
                Part1Length = seg.BlockStream.Length,
                Part2Length = seg.Pool.Length,
                PoolItems = seg.PoolItems,
                PoolTailBytes = seg.PoolTailBytes,
                Records = seg.RecordCount,
                Residuals = seg.ResidualCount,
                RecordsEqualPoolItems = seg.RecordsEqualPoolItems,
                Part1Sha16 = seg.BlockStreamSha16,
                Part1Note = seg.Part1Note,
                Part2Note = seg.Part2Note,
                CpngTag = seg.CpngTag,
                MagicHex = seg.MagicHex,
                ObjectContainers = objs.Count,
                GroupContainers = grps.Count,
                BitmapBlocks = bmps.Count,
                BitmapBodyBytes = bmpBytes,
                BmptForms = seg.FindAll("LIST", "bmpt").Count,
                Notes = notes,
            });
            objCount += objs.Count;
            grpCount += grps.Count;
            if (objs.Count > 0) segmentsWithObjects++;
            if (pageW is not null && pageH is not null)
                BuildObjects(seg, objs, grps, pageW.Value, pageH.Value, facts, doc, styleTable, fonts);
        }

        var pagesWithObjects = PagesWithObjects(container);

        doc.Page.W = pageW;
        doc.Page.H = pageH;

        var degraded = new List<string>(container.Degraded);
        if (pageW is null)
            degraded.Add("页尺寸没从 mcfg 解出来 → 全部对象的盒都没算（不是「这些对象没有盒」）");
        degraded.Add($"颜色：文档级 fild {fild} 条 / {fildBytes:N0} B、outl {outl} 条 / {outlBytes:N0} B 只登记未解，" +
                     "对象 Ink/Stroke 一律 null；缺的是 fild/outl 的载荷布局，以及 loda 的 fill_style/line_style 参数到表项的索引");
        degraded.Add(StltDegradedLine(stltAt, stltLen, styleTable, facts));
        degraded.Add(FontDegradedLine(fonts.Count));
        degraded.Add(FontBindingLine(facts));
        degraded.Add(GeometryDegradedLine(facts));
        degraded.Add(BitmapDegradedLine(segFacts));
        degraded.Add(LayerDegradedLine(pageContainers));
        degraded.Add(OleDegradedLine(facts));
        degraded.Add(FlagsDegradedLine(facts));
        if (unrecognized.Count > 0)
            degraded.Add("两张参照表（ksy chunk_data_common / libcdr CDRDocumentStructure.h）都没登记过的块名，原样列出、只登记不套语义：" +
                         string.Join(" ", unrecognized));
        if (pagesWithObjects > 1)
            degraded.Add($"这份有 {pagesWithObjects} 页带对象，cdrx 只有一张页（Page 取 mcfg 的母页尺寸），" +
                         $"跨页对象的盒都按这页尺寸折（{segmentsWithObjects} 段块流里各产了一批对象）");
        degraded.AddRange(NotSolvedThisRound);   // 常驻清单 + 上面带实测数字的逐份说明，两者都在
        doc.Source.Degraded = degraded;          // 忘了这一句就是"降级静默"：文档里一条都看不到

        return new CdrBinaryDocument
        {
            Doc = doc,
            Container = container,
            PageWidthMm = pageW,
            PageHeightMm = pageH,
            PageSource = pageSource,
            Version = container.Version,
            VersionFieldLength = container.VersionFieldLength,
            Fonts = fonts,
            StyleTable = styleTable,
            StltStyleRecords = stltRecords,
            StltBodyLength = stltLen,
            StltAt = stltAt,
            StltTables = stltTables,
            Segments = segFacts,
            Facts = facts,
            ObjectContainerCount = objCount,
            GroupContainerCount = grpCount,
            PageContainers = pageContainers,
            PagesWithObjects = pagesWithObjects,
            Degraded = degraded,
            UnrecognizedFourcc = unrecognized.ToList(),
            FildBlocks = fild,
            FildBytes = fildBytes,
            OutlBlocks = outl,
            OutlBytes = outlBytes,
        };
    }

    /// <summary>带对象的页数：按 <c>LIST[page]</c> 容器套住多少只 <c>obj </c> 容器来数。</summary>
    static int PagesWithObjects(CdrBinaryContainer container)
    {
        var hit = 0;
        foreach (var seg in container.Segments)
        {
            var allObjs = seg.FindAll("LIST", "obj ");
            foreach (var page in seg.FindAll("LIST", "page"))
            {
                var n = 0;
                foreach (var f in allObjs)
                    if (f.At >= page.ChildrenAt && f.At < page.BodyEnd) n++;
                if (n > 0) hit++;
            }
        }
        return hit;
    }

    /// <summary>
    /// 字号链这一行必须<b>自报实测数</b>：表走没走通、几只文字对象、串上几只、没串上的逐只点名。
    /// 串上了也不许把这行删掉——它是"这条链是靠 stlt 走通而非靠盒推算"的凭据行。
    /// </summary>
    static string StltDegradedLine(int stltAt, int stltLen, CdrStltStyleTable? table, List<CdrBinaryObjectFacts> facts)
    {
        var texts = facts.Where(f => !f.IsGroup && f.Txsm is not null).ToList();
        var ok = texts.Count(f => f.StyleFont?.SizeOk == true);
        var miss = texts.Where(f => f.StyleFont?.SizeOk != true).ToList();
        var head = stltAt < 0
            ? $"stlt 容器没找到 → 字号一个都串不上（不是「这份设计没有字号」）；文字对象 {texts.Count} 只"
            : $"字号链：stlt（at={stltAt}, {stltLen:N0} B）" +
              (table is null ? "没解"
               : table.Ok
                   ? $"按段序走通（{string.Join(" ", table.SectionNotes)} + records×{table.Records.Count}，零残余）"
                   : $"**没走通**：{table.Unknown}") +
              $"；文字对象 {texts.Count} 只，链上串出字号 {ok} 只";
        if (miss.Count == 0)
            return head + "；Fonts 段字号候选 " +
                   string.Join("/", (table?.Fonts ?? new List<CdrStltStyleRecord>()).Select(r => Fmt(r.SizePt, 4))) +
                   " pt（这条链是 ID→ID 按值查，不是由盒高反推）";
        return head + "；没串上的：" + string.Join("；", miss.Select(f =>
                   $"obj#{f.SegmentIndex}:{f.ContainerAt} {(f.StyleFont?.SizeUnknown ?? "没跑解析")}")) +
               " → 这些对象的 Text.Font.SizePt 不填";
    }

    static string FontDegradedLine(int n)
        => $"字体表：font 块解出 {n} 条（id / 编码 / 样式旗标 / UTF-16LE 名字，中文名如「宋体」在这儿），" +
           "stlt 的 fonts 条目按 +24 的 u2 字体号接进这张表";

    /// <summary>字体名这行同样自报实测数：几只填了、几只没填、为什么没填。</summary>
    static string FontBindingLine(List<CdrBinaryObjectFacts> facts)
    {
        var texts = facts.Where(f => !f.IsGroup && f.Txsm is not null).ToList();
        var named = texts.Count(f => f.StyleFont?.FontName is not null);
        var miss = texts.Where(f => f.StyleFont?.FontName is null).ToList();
        var runSourced = texts.Count(f => f.StyleFont?.FontNameSource == "首个带 has_font 的字符样式");
        var s = $"字体名：{texts.Count} 只文字对象里 {named} 只按「stlt fonts +24 → font 块表」查到名字" +
                $"（其中 {runSourced} 只的名字取自首个显式带字体的字符样式 run，A 侧 Shape.Font 报的就是这一层）；" +
                "同一 font_id 在表里可多条（金沐 id=21 三条），名字全一致才敢用，不一致且按编码也收不拢就不填";
        if (miss.Count == 0) return s;
        return s + "；没填的：" + string.Join("；", miss.Select(f =>
                   $"obj#{f.SegmentIndex}:{f.ContainerAt} {f.StyleFont?.FontNameUnknown ?? f.StyleFont?.SizeUnknown ?? "没跑解析"}"));
    }

    static string GeometryDegradedLine(List<CdrBinaryObjectFacts> facts)
    {
        var needPointList = facts.Count(f => !f.IsGroup && f.LodaType is "polygon_coords" or "line_and_curve");
        var solved = facts.Count(f => f.Object.Curve is not null);
        return $"节点级几何：需要点列的对象 {needPointList} 个，真解出 {solved} 个。" +
               "loda 参数表按 ksy 的逆序对应取到的体不成「u32 点数 + n×(s4,s4) + n×u1 类型」的自洽长度，换正序也不成" +
               "（探针 §5 已记：逆序对应不是普遍规律）→ Curve 不填";
    }

    static string BitmapDegradedLine(List<CdrSegmentFacts> segs)
    {
        var bytes = segs.Sum(s => s.BitmapBodyBytes);
        var blocks = segs.Sum(s => s.BitmapBlocks);
        return blocks == 0
            ? "内嵌位图：块树里没有 'bmp ' 块（不等于这份设计没有位图，先确认段找齐了）"
            : $"内嵌位图：'bmp ' 块 {blocks} 条 / {bytes:N0} B 已定位（含 OLU 那种 LIST[bmpt] 9,546,456 B 的像素流），" +
              "本轮不解码、不落文件 → Image 不填；缺的是 Corel 位图载荷的压缩与通道布局";
    }

    static string LayerDegradedLine(int pages)
        => $"图层：块树里 {pages} 个 LIST[page] 下的 layr 容器只有 flgs+lgob(loda)，没有图层名，" +
           "图层名只在 ZIP 的 metadata/textinfo.xml 的 LayerNames 里，本轮没接那条路 → Layers 空、对象 Layer=null";

    static string OleDegradedLine(List<CdrBinaryObjectFacts> facts)
    {
        var olel = facts.Count(f => f.HasOlel);
        var ole2 = facts.Count(f => f.HasOle2);
        return $"OLE：带 olel 的对象 {olel} 只、ole2 容器 {ole2} 处。olel(18 B)/ole2 在两张参照表里都没有名字，" +
               "内嵌复合文档里的内容（条码数字）解不出 → 这些对象只到「有盒、kind=unknown」，" +
               "但保留可见、不判隐形（判隐形就等于把「读不出来」写成「它不存在」）";
    }

    static string FlagsDegradedLine(List<CdrBinaryObjectFacts> facts)
        => $"透明度/特效/可见性：loda 的 opacity(8000)、gradient(12010) 之类参数只登记类型号未解体；" +
           $"flgs({facts.Count(f => f.ChildNames.Contains("flgs"))} 处)/usdn({facts.Count(f => f.ChildNames.Contains("usdn"))} 处)" +
           $"/spnd({facts.Count(f => f.ChildNames.Contains("spnd"))} 处) 未定语义 → 本通道既不判隐形也不报透明度，Visible 只在 OLE 那几只上明写成 true";

    /// <summary>把一段里的 obj/grp 容器折成 cdrx 对象：grp 装成 children，顺序＝文件树前序。</summary>
    static void BuildObjects(CdrSegment seg, List<CdrBlock> objs, List<CdrBlock> grps,
        double pageW, double pageH, List<CdrBinaryObjectFacts> facts, CdrxDoc doc,
        CdrStltStyleTable? styleTable, List<CdrFontRecord> fontBlocks)
    {
        var containers = new List<CdrBlock>(objs.Count + grps.Count);
        containers.AddRange(objs);
        containers.AddRange(grps);
        containers.Sort((a, b) => a.At.CompareTo(b.At));      // at 顺序＝树前序

        var made = new List<(CdrBlock Block, CdrxObject Obj, CdrBinaryObjectFacts Fact)>(containers.Count);
        foreach (var c in containers)
        {
            var (o, f) = OneObject(seg, c, pageW, pageH, styleTable, fontBlocks);
            made.Add((c, o, f));
            facts.Add(f);
        }

        var byIndex = new Dictionary<int, CdrxObject>();
        var roots = new List<CdrxObject>();
        for (var i = 0; i < made.Count; i++)
        {
            var (block, o, _) = made[i];
            byIndex[i] = o;
            var parentIndex = ParentIndexOf(made, block);
            if (parentIndex < 0)
            {
                doc.Objects.Add(o);
                roots.Add(o);
            }
            else
            {
                var parent = byIndex[parentIndex];
                parent.Children ??= new List<CdrxObject>();
                parent.Children.Add(o);
            }
        }
        AssignZ(roots);
        foreach (var p in made)
            if (p.Obj.Children is { Count: > 0 }) AssignZ(p.Obj.Children);
    }

    /// <summary>层内序：与 A 通道同一口径（同一父下从 1 数起，母页对象与群组成员各自独立数）。</summary>
    static void AssignZ(List<CdrxObject> list)
    {
        for (var i = 0; i < list.Count; i++) list[i].Z = i + 1;
    }

    /// <summary>最近一层容器祖先的下标；没有＝根。</summary>
    static int ParentIndexOf(List<(CdrBlock Block, CdrxObject Obj, CdrBinaryObjectFacts Fact)> made, CdrBlock block)
    {
        var best = -1;
        for (var i = 0; i < made.Count; i++)
        {
            var p = made[i].Block;
            if (ReferenceEquals(p, block) || p.Depth >= block.Depth) continue;
            if (block.At < p.ChildrenAt || block.At >= p.BodyEnd) continue;
            if (best < 0 || p.Depth > made[best].Block.Depth) best = i;
        }
        return best;
    }

    static (CdrxObject, CdrBinaryObjectFacts) OneObject(CdrSegment seg, CdrBlock container, double pageW, double pageH,
        CdrStltStyleTable? styleTable, IReadOnlyList<CdrFontRecord> fontBlocks)
    {
        var isGroup = container.Form == "grp ";
        var kids = seg.ChildrenOf(container);
        var childNames = kids.ConvertAll(k => k.Name);
        var notes = new List<string>();
        var o = new CdrxObject { Kind = CdrxKinds.Unknown };

        var bboxBlock = kids.Find(k => k.Fourcc == "bbox");
        var obbxBlock = kids.Find(k => k.Fourcc == "obbx");
        int[]? bboxRaw = null;
        CdrFrame? frame = null;
        if (bboxBlock is not null)
        {
            var bb = CdrBinaryCodecs.DecodeBbox(bboxBlock.Body);
            if (bb.HasValue)
            {
                bboxRaw = bb.Raw;
                frame = CdrBinaryCodecs.PageCenterBox(bb.Raw, pageW, pageH);
                o.Box = new CdrxBox
                {
                    X = frame.Value.LeftTopXmm,
                    Y = frame.Value.TopYmm,
                    W = frame.Value.WidthMm,
                    H = frame.Value.HeightMm,
                };
            }
            else
            {
                notes.Add($"bbox 体 {bboxBlock.Length} B < 16，盒读不出来（不等于这只对象没有盒）");
            }
        }
        else
        {
            notes.Add(isGroup
                ? "群组容器里没有 bbox：群组的盒读不出来"
                : "容器里没有 bbox 块：这只对象的盒读不出来（不等于它没有盒）");
        }

        var obbx = obbxBlock is not null ? CdrBinaryCodecs.DecodeObbx(obbxBlock.Body) : null;

        CdrLoda? loda = null;
        int? lodaType = null;
        string? lodaTypeName = null;
        var trafos = new List<CdrTrafo>();
        var hasOlel = false;
        var hasOle2 = false;
        var ole2Len = 0;
        var lgob = kids.Find(k => k.Fourcc == "LIST" && k.Form == "lgob");
        if (lgob is not null)
        {
            var lodaBlock = seg.ChildOf(lgob, "loda");
            if (lodaBlock is not null)
            {
                loda = CdrBinaryCodecs.DecodeLoda(lodaBlock.Body);
                lodaType = loda.ChunkTypeRaw;
                lodaTypeName = loda.ChunkType;
            }
            else if (!isGroup)
            {
                notes.Add("LIST[lgob] 里没有 loda：对象类型读不出来");
            }
            var trfl = seg.ChildOf(lgob, "LIST", "trfl");
            var trfd = seg.ChildOf(trfl, "trfd");
            if (trfd is not null) trafos = CdrBinaryCodecs.DecodeTrfd(trfd.Body);
            else if (!isGroup) notes.Add("LIST[lgob]/LIST[trfl]/trfd 没找到：旋转与缩放读不出来（不等于它没被旋过）");
            hasOlel = seg.ChildOf(lgob, CdrBinaryFourcc.Olel) is not null;
            var ole2 = seg.ChildOf(lgob, CdrBinaryFourcc.Ole2);
            if (ole2 is not null)
            {
                hasOle2 = true;
                ole2Len = ole2.Length;
            }
        }
        else if (!isGroup)
        {
            notes.Add("容器里没有 LIST[lgob]：对象类型、变换与填充/轮廓引用都读不出来");
        }

        if (isGroup)
        {
            var members = kids.Count(k => k.Form is "obj " or "grp ");
            notes.Add($"群组层级按块树嵌套重建（LIST[grp ]，成员 {members} 个）；" +
                      "群组成员的 trfd 是局部量还是全页量探针没定案（feasibility §10 未验证⑥），故本通道不给群组再乘变换");
        }
        o.Kind = isGroup ? CdrxKinds.Group : KindOf(lodaType, lodaTypeName);

        var firstTrafo = trafos.Find(t => t.Unknown is null);
        if (firstTrafo is not null)
        {
            if (Math.Abs(firstTrafo.AngleDeg) > 1e-6) o.Rot = Math.Round(firstTrafo.AngleDeg, 4);
            // 拉伸只在"这是个合理的仿射"时带上。OLU 那份 fixture 的矩形 trfd 是零矩阵
            // （scale_x/scale_y 都是 0）——照抄进模板就是把 TextScaleX 设成 0，
            // 校验直接判 Error，等于把一次读不懂降级成一次导入失败。
            if (Carriable(firstTrafo.ScaleX) && Math.Abs(firstTrafo.ScaleX - 1) > 1e-6)
                o.ScaleX = Math.Round(firstTrafo.ScaleX, 6);
            if (Carriable(firstTrafo.ScaleY) && Math.Abs(firstTrafo.ScaleY - 1) > 1e-6)
                o.ScaleY = Math.Round(firstTrafo.ScaleY, 6);
            if (!(Carriable(firstTrafo.ScaleX) && Carriable(firstTrafo.ScaleY)))
                notes.Add($"trfd 的矩阵解不出合理拉伸（横 {Fmt(firstTrafo.ScaleX, 6)} / 纵 {Fmt(firstTrafo.ScaleY, 6)}，" +
                          $"能带走的区间是 {TemplateValidator.MinStretch}~{TemplateValidator.MaxStretch}）：这条按不拉伸画");
        }

        var txsmBlock = kids.Find(k => k.Fourcc == "txsm");
        CdrTxsm? txsm = null;
        CdrStyleFontResolution? styleFont = null;
        if (txsmBlock is not null)
        {
            txsm = CdrBinaryCodecs.ParseTxsm7(txsmBlock.Body, seg.Container.Version);
            o.Text = new CdrxText
            {
                Contents = txsm.JoinedText(),     // setter 把裸 \r 归一成 \n，别绕开它
                Artistic = lodaTypeName switch
                {
                    "artistic_text" => true,
                    "paragraph_text" => false,
                    _ => null,
                },
            };
            if (!txsm.ConsumedExact)
                notes.Add($"txsm 没吃完（消费 {txsm.Consumed}/{txsm.Length}，残余 {txsm.Residual} B）：文字可能不全" +
                          (txsm.Unknown is null ? "" : $"，卡在这里：{txsm.Unknown}"));
            var perStyleSizes = txsm.Paragraphs.SelectMany(p => p.Styles).Where(s => s.FontSizePt is not null)
                .Select(s => s.FontSizePt!.Value).ToList();
            if (perStyleSizes.Count > 0)
                notes.Add("txsm 字符样式自己带了字号 " + string.Join("/", perStyleSizes.Select(s => Fmt(s, 4))) +
                          " pt（has_font_size 置位）；本通道仍以段落样式记录串出的字号为准，两者不一致时要对账");

            // ---- 字号链：段落 style_id →（按值）stlt records →（按值）stlt fonts → 字号 raw ----
            styleFont = CdrBinaryCodecs.ResolveStyleFont(styleTable, txsm.Paragraphs.FirstOrDefault(), fontBlocks);
            if (styleFont.SizeOk)
            {
                o.Text.Font = new CdrxFont
                {
                    SizePt = styleFont.SizePt,
                    Name = styleFont.FontName,
                    Bold = styleFont.Bold,
                    Italic = styleFont.Italic,
                };
                notes.Add($"字号链：txsm 段落 style_id 0x{styleFont.ParagraphStyleId:x8} →stlt records#{styleFont.StyleRecordIndex}" +
                          $"（名「{styleFont.StyleRecordName}」）→font_rec_id 0x{styleFont.FontRecId:x8} →fonts#{styleFont.FontEntryIndex}" +
                          $" 字号 raw {styleFont.SizeRaw}（1/10000 mm）= {Fmt(styleFont.SizePt, 4)} pt" +
                          $" = {Fmt(CdrBinaryUnits.Mm(styleFont.SizeRaw), 4)} mm，样式旗标 {styleFont.FlagsHex}" +
                          $"（{styleFont.FlagsName}）；Corel 显示口径＝把 pt 舍到 3 位 = {Fmt(Math.Round(styleFont.SizePt, 3, MidpointRounding.AwayFromZero), 3)} pt");
                if (styleFont.FontName is not null)
                    notes.Add($"字体名「{styleFont.FontName}」：按 font_id={styleFont.FontTableId} 查文档 font 块表，取自{styleFont.FontNameSource}");
                else
                    notes.Add("字体名不填：" + (styleFont.FontNameUnknown ?? styleFont.SizeUnknown));
            }
            else
            {
                notes.Add("字号链没串上 → Text.Font 不填（不是「这只对象没有字号」）：" + styleFont.SizeUnknown);
            }
            foreach (var n in styleFont.Notes) notes.Add("字号链旁注：" + n);
            foreach (var p in txsm.Paragraphs)
                if (p.Framing.Contains("启发式", StringComparison.Ordinal))
                    notes.Add($"文字分帧退回顾式：{p.Framing}");
            if (txsm.Paragraphs.Count > 1)
                notes.Add($"这只对象有 {txsm.Paragraphs.Count} 个段落样式，字号按第 1 段（与 A 侧 Shape.Font 同一取法）；" +
                          "其余段落：" + string.Join("/", txsm.Paragraphs.Skip(1).Select(p => $"0x{p.StyleId:x8}")));
        }

        if (hasOlel)
        {
            o.Visible = true;     // 明写：读不出内容不等于该藏起来
            notes.Add("lgob 里有 olel（18 B）＝内嵌 OLE 对象；内容在 ole2 的内嵌复合文档里，两张参照表都没登记过，解不出" +
                      (lodaType is null ? "" : $"（loda 类型号 0x{lodaType.Value:X2}＝{lodaType.Value}）") +
                      "。本通道只拿到盒，kind=unknown，不判隐形");
        }
        else if (lodaType is not null && lodaTypeName is null)
        {
            o.Visible = true;
            notes.Add($"loda 类型号 0x{lodaType.Value:X2}（{lodaType.Value}）不在已知的对象类型表里 → kind=unknown（这只对象照样保留、不判隐形）");
        }
        if (hasOle2) notes.Add($"ole2 {ole2Len:N0} B：ksy 与 libcdr 两张表都没有这个名字，原样登记，不套语义");

        if (loda is not null)
        {
            var coords = loda.Arg("loda_coords");
            if (coords?.Unknown is not null) notes.Add($"loda 的 loda_coords 参数取不出来：{coords.Unknown}（几何不等于没有）");
            else if (coords is not null) AppendGeometryNote(o, notes, lodaTypeName, coords);
            if (loda.Arg("fill_style") is not null)
                notes.Add("loda 带 fill_style(类型号 20) 参数 → 指向文档 LIST[filt]/LIST[filc] 里的 fild 记录，本轮未解 CMYK，Ink=null");
            if (loda.Arg("line_style") is not null)
                notes.Add("loda 带 line_style(类型号 10) 参数 → 指向 LIST[otlt] 里的 outl 记录，本轮未解描边宽度/颜色，Stroke=null");
            if (loda.Arg("style") is { Length: >= 4 } styleArg)
                notes.Add($"loda 的 style 参数(类型号 200) 值 0x{BinaryPrimitives.ReadUInt32LittleEndian(styleArg.Body):x8}" +
                          "（与 txsm 段落 style_id 同族 ID）；六份实测<b>文字对象的 loda 根本没带这个参数</b>，" +
                          "字号链走的是 txsm 段落 style_id 那一条，这里只登记不套用");
            if (!loda.ChunkLengthOk)
                notes.Add($"loda 声明长度 {loda.ChunkLength} ≠ 体长 {loda.Args.Count}，参数表可能整体错位");
        }

        var fact = new CdrBinaryObjectFacts
        {
            SegmentIndex = seg.Index,
            ContainerAt = container.At,
            ContainerLength = container.Length,
            Depth = container.Depth,
            Path = container.Path,
            IsGroup = isGroup,
            BboxRaw = bboxRaw,
            ObbxRaw = obbx,
            Box = frame,
            LodaTypeRaw = lodaType,
            LodaType = lodaTypeName,
            Loda = loda,
            Trafos = trafos,
            Txsm = txsm,
            StyleFont = styleFont,
            HasOlel = hasOlel,
            HasOle2 = hasOle2,
            Ole2Length = ole2Len,
            ChildNames = childNames,
            Object = o,
        };
        if (notes.Count > 0) o.Notes = notes;
        return (o, fact);
    }

    /// <summary>loda 类型号 → cdrx kind。认不得的<b>只能</b>是 unknown，并且由调用方点名类型号。</summary>
    static string KindOf(int? raw, string? typeName) => typeName switch
    {
        "rectangle" => CdrxKinds.Rect,
        "ellipse" => CdrxKinds.Ellipse,
        "artistic_text" => CdrxKinds.Text,
        "paragraph_text" => CdrxKinds.Text,
        "bitmap" => CdrxKinds.Image,
        "polygon_coords" => CdrxKinds.Polygon,
        "line_and_curve" => CdrxKinds.Curve,
        "path" => CdrxKinds.Curve,
        "spline" => CdrxKinds.Curve,
        _ => CdrxKinds.Unknown,
    };

    /// <summary>
    /// 几何参数：长度自洽的点列才填 <c>Curve</c>（六份实测 0 支自洽），解不出就把原样字节与原因写进 Notes。
    /// </summary>
    static void AppendGeometryNote(CdrxObject o, List<string> notes, string? lodaTypeName, CdrLodaArg coords)
    {
        var b = coords.Body.AsSpan();
        switch (lodaTypeName)
        {
            case "polygon_coords":
            case "line_and_curve":
            {
                if (b.Length < 4)
                {
                    notes.Add($"loda_coords 体不足 4 字节（{b.Length} B），连点数域都读不出");
                    return;
                }
                var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4));
                var need = 4L + n * 8L + n;
                if (n is > 0 and < 200000 && need == b.Length)
                {
                    var sub = new CdrxSubpath { Closed = true };
                    for (var i = 0; i < n; i++)
                    {
                        var x = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(4 + i * 8, 4));
                        var y = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(4 + i * 8 + 4, 4));
                        sub.Nodes.Add(new CdrxNode
                        {
                            X = CdrBinaryUnits.Mm(x),
                            Y = CdrBinaryUnits.Mm(y),
                        });
                    }
                    o.Curve = new CdrxCurve { Subpaths = new List<CdrxSubpath> { sub } };
                    notes.Add($"点列按 n×(s4,s4) 自洽取出（{n} 点），但节点类型旗标（其后的 n 个 u1）含义未标定 → CdrxNode.Type 与控制柄不填");
                }
                else
                {
                    notes.Add($"点列解不出：loda 参数表按 ksy 的逆序对应取到的 {b.Length} B 不成" +
                              $"「u32 点数({n}) + n×(s4,s4) + n×u1 = {need:N0} B」的自洽形状，换正序也不成" +
                              $"（原样前 48 B：{CdrBinaryCodecs.HexSpaced(b.Slice(0, Math.Min(48, b.Length)))}）");
                }
                return;
            }
            case "rectangle":
            {
                if (b.Length < 24)
                {
                    notes.Add($"rectangle 参数只有 {b.Length} B < 24，圆角读不出");
                    return;
                }
                var v = new double[6];
                for (var i = 0; i < 6; i++) v[i] = CdrBinaryUnits.Mm(BinaryPrimitives.ReadInt32LittleEndian(b.Slice(i * 4, 4)));
                notes.Add("loda rectangle 的 6×coord(mm)=[x0,y0,r3,r2,r1,r0]=[" + string.Join(", ", v.Select(x => Fmt(x, 4))) +
                          "]；v<1500 走 rect_old_ext，r0..r3 的圆角语义未标定（探针 §10 未验证⑦）→ Geom 不填");
                return;
            }
            case "artistic_text":
            {
                if (b.Length < 8)
                {
                    notes.Add($"artistic_text 参数只有 {b.Length} B < 8，插入点读不出");
                    return;
                }
                var x = CdrBinaryUnits.Mm(BinaryPrimitives.ReadInt32LittleEndian(b.Slice(0, 4)));
                var y = CdrBinaryUnits.Mm(BinaryPrimitives.ReadInt32LittleEndian(b.Slice(4, 4)));
                notes.Add($"美术字插入点（Corel 定点折 mm，页中心口径）=({Fmt(x, 4)}, {Fmt(y, 4)})；" +
                          "基线/对齐锚点未标定 → 不拿它当文字的落位用，盒仍以 bbox 为准");
                return;
            }
            case "bitmap":
            {
                if (b.Length < 16)
                {
                    notes.Add($"bitmap 参数只有 {b.Length} B < 16");
                    return;
                }
                notes.Add($"位图对象：loda 的 4×coord 参数（{b.Length} B）已登记；像素流在另一段 LIST/cmpr 的 LIST[bmpt]/'bmp ' 里，本轮不解码 → Image 不填");
                return;
            }
            default:
                notes.Add($"loda 类型 {(lodaTypeName ?? "未知")} 的几何参数（{b.Length} B）本轮没写解法，原样登记：" +
                          CdrBinaryCodecs.HexSpaced(b.Slice(0, Math.Min(32, b.Length))));
                return;
        }
    }

    static string Fmt(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    /// <summary>这个拉伸倍率能不能落进模板模型（区间与 <see cref="TemplateValidator"/> 的拉伸校验同源）。</summary>
    static bool Carriable(double s) =>
        !double.IsNaN(s) && !double.IsInfinity(s) &&
        s >= TemplateValidator.MinStretch && s <= TemplateValidator.MaxStretch;
}
