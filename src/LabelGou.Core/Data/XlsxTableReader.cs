using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace LabelGou.Core.Data;

/// <summary>
/// 自建 XLSX 读取器：<strong>零外部依赖</strong>（只用 <see cref="ZipArchive"/> 与 <see cref="XDocument"/>）。
/// <para>
/// 决策依据：打印店机器环境不可控、要求离线与免安装，而本项目只需"把一张平面表读成字符串网格"。
/// 自建实现避免了 ClosedXML→DocumentFormat.OpenXml→字体度量库 这类传递依赖与其授权纠缠。
/// </para>
/// <para>
/// 覆盖的真实场景：共享字符串、富文本、内联字符串、公式缓存值、日期样式还原、
/// 合并单元格取值传播（工厂唛头表头极常合并）、稀疏大表（只物化有内容的行，
/// 避免被 Excel 记录的 1048576 行 dimension 撑爆内存）。
/// </para>
/// </summary>
public static class XlsxTableReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelPackage = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace RelDoc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>列出工作簿中的工作表名（按显示顺序）。</summary>
    public static List<string> ListSheetNames(string filePath)
    {
        using var zip = ZipFile.OpenRead(filePath);
        return ReadWorkbook(zip).Select(s => s.Name).ToList();
    }

    /// <summary>
    /// 读取指定工作表（null 表示第一张）为原始网格。
    /// </summary>
    public static List<string[]> ReadRawGrid(string filePath, string? sheetName = null)
    {
        using var zip = ZipFile.OpenRead(filePath);

        var sheets = ReadWorkbook(zip);
        if (sheets.Count == 0) throw new InvalidDataException("XLSX 中没有任何工作表。");

        var target = sheetName is null
            ? sheets[0]
            : sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidDataException($"找不到工作表「{sheetName}」，现有：{string.Join("、", sheets.Select(s => s.Name))}");

        var sharedStrings = ReadSharedStrings(zip);
        var styleInfo = ReadStyles(zip);
        var (rowsByIndex, maxCol, _) = ReadSheetCells(zip, target.EntryPath, sharedStrings, styleInfo);

        return Materialize(rowsByIndex, maxCol);
    }

    /// <summary>
    /// 「这张表里哪几块的字长得跟别处不一样」——字号 / 粗体 / 居中的依据（第 31 棒）。
    /// <para><strong>为什么要有它</strong>：用户 2026-09-10 真机实测指出「AI 排版效果差，差在字体大小」，
    /// 并且自己判断出了根因——*"可能存在问题原因 AI 读不到表格中字体,粗细,居中等"*。<strong>确实如此</strong>：
    /// 在这之前 <c>styles.xml</c> 只被用来认日期与小数位，字体、粗体、对齐一个字都没读。
    /// 而表里那块"抄标签"的样例（哪行加粗、哪行大、哪行居中）**正是"标签该长什么样"的唯一依据**，
    /// 不读它，AI 只能凭空猜字号。</para>
    /// <para>口径：**只报跟同一列主流格式不一样的格子**（按列归并成几行）。理由是别的写法没法用——
    /// 逐格流水账会把画像淹掉，而且真表里"整列都是 11pt"这种默认格式报出来纯属噪音。
    /// 拿不准（文件读不了、没有样式表）就返回空，**不编**。</para>
    /// </summary>
    /// <param name="filePath">xlsx 路径（CSV 没有格式可言，调用方不该走到这里）。</param>
    /// <param name="sheetName">工作表名；null = 第一个。</param>
    public static IReadOnlyList<string> DescribeCellFormats(string filePath, string? sheetName = null)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var sheets = ReadWorkbook(zip);
            if (sheets.Count == 0) return Array.Empty<string>();
            var target = sheetName is null
                ? sheets[0]
                : sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                  ?? sheets[0];

            var styles = ReadStyles(zip);
            if (styles.Fonts.Count == 0 && styles.CellXfAligns.Count == 0) return Array.Empty<string>();

            var sharedStrings = ReadSharedStrings(zip);
            var (rows, maxCol, stylesUsed) = ReadSheetCells(zip, target.EntryPath, sharedStrings, styles);
            if (stylesUsed.Count == 0) return Array.Empty<string>();

            // 每列一格一格排好（行序），标出它的格式；再找这一列的"主流格式"当基准。
            var byColumn = new SortedDictionary<int, List<(int Row, (double Size, bool Bold, string? Align)? Format)>>();
            foreach (var ((row, col), styleIndex) in stylesUsed)
            {
                if (!byColumn.TryGetValue(col, out var list))
                {
                    list = new List<(int, (double, bool, string?)?)>();
                    byColumn[col] = list;
                }
                list.Add((row, styles.FormatOf(styleIndex)));
            }

            var lines = new List<string>();
            foreach (var (col, cells) in byColumn)
            {
                cells.Sort((a, b) => a.Row.CompareTo(b.Row));
                // 只在这列**真有格式**时才说话：全是默认格式的列没什么可报的。
                // 基准取"这一列里非默认格式中最常见的那个"——为什么不把"默认"也算进候选：
                // 表头那一格通常是默认格式，混进来会让"表头跟明细不一样"变成一条噪音（实测踩到过）。
                var formatted = cells.Where(c => c.Format is not null).ToList();
                if (formatted.Count == 0) continue;
                var dominant = formatted
                    .GroupBy(c => Describe(c.Format))
                    .OrderByDescending(g => g.Count())
                    .First().Key;

                // 把连续同格式的格子并成一段，只报与主流不同的段。
                for (var i = 0; i < formatted.Count; i++)
                {
                    var text = Describe(formatted[i].Format);
                    if (string.Equals(text, dominant, StringComparison.Ordinal)) continue;
                    var start = formatted[i].Row;
                    var end = start;
                    var j = i;
                    while (j + 1 < formatted.Count
                           && string.Equals(Describe(formatted[j + 1].Format), text, StringComparison.Ordinal)
                           && formatted[j + 1].Row == formatted[j].Row + 1)
                    {
                        j++;
                        end = formatted[j].Row;
                    }
                    i = j;
                    var where = start == end ? $"第 {start + 1} 行" : $"第 {start + 1}~{end + 1} 行";
                    lines.Add($"{ColumnName(col)} 列{where}：{text}（这一列其余格是{dominant}）");
                    if (lines.Count >= MaxFormatLines)
                    {
                        lines.Add($"（还有更多格式差异，只列了前 {MaxFormatLines} 处）");
                        return lines;
                    }
                }
            }
            return lines;

            static string Describe((double Size, bool Bold, string? Align)? format)
            {
                if (format is not { } f) return "默认（跟工作簿一样）";
                var parts = new List<string>();
                if (f.Size > 0) parts.Add($"{f.Size:0.#}pt");
                parts.Add(f.Bold ? "粗体" : "常规");
                if (!string.IsNullOrEmpty(f.Align)) parts.Add(AlignText(f.Align!));
                return string.Join(" ", parts);
            }

            static string AlignText(string align) => align switch
            {
                "center" => "居中",
                "centerContinuous" => "跨列居中",
                "right" => "右对齐",
                "left" => "左对齐",
                "justify" => "两端对齐",
                _ => align,
            };
        }
        catch (Exception)
        {
            // 格式读不出来不该挡主流程：画像少这一段，AI 照旧按文字排（顶多字号回到"猜"）。
            return Array.Empty<string>();
        }
    }

    /// <summary>格式差异最多报几处（多了等于没有，而且会把画像淹掉）。</summary>
    private const int MaxFormatLines = 8;

    /// <summary>列号 → 列名（0→A）。</summary>
    private static string ColumnName(int col)
    {
        var name = string.Empty;
        for (var c = col; c >= 0; c = c / 26 - 1) name = (char)('A' + c % 26) + name;
        return name;
    }

    // ---------- workbook ----------

    private sealed record SheetInfo(string Name, string EntryPath);

    private static List<SheetInfo> ReadWorkbook(ZipArchive zip)
    {
        var result = new List<SheetInfo>();
        var workbookEntry = FindEntry(zip, "xl/workbook.xml");
        if (workbookEntry is null) return result;

        using var stream = workbookEntry.Open();
        var doc = XDocument.Load(stream);

        // r:id -> zip 内实际路径
        var relMap = ReadWorkbookRelationships(zip);

        foreach (var sheet in doc.Descendants(Main + "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? "Sheet";
            var rid = (string?)sheet.Attribute(RelDoc + "id");
            var path = rid is not null && relMap.TryGetValue(rid, out var p) ? p : null;

            if (path is null)
            {
                // 退而求其次：按 sheetId 顺序猜 worksheets/sheetN.xml
                var index = result.Count + 1;
                path = $"xl/worksheets/sheet{index}.xml";
            }
            result.Add(new SheetInfo(name, path));
        }
        return result;
    }

    private static Dictionary<string, string> ReadWorkbookRelationships(ZipArchive zip)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = FindEntry(zip, "xl/_rels/workbook.xml.rels");
        if (entry is null) return map;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var rel in doc.Descendants(RelPackage + "Relationship"))
        {
            var id = (string?)rel.Attribute("Id");
            var rawTarget = (string?)rel.Attribute("Target");
            if (id is null || rawTarget is null) continue;
            map[id] = ResolvePartPath(rawTarget);
        }
        return map;
    }

    /// <summary>把关系文件里的相对/绝对 Target 归一成 zip 条目路径。</summary>
    private static string ResolvePartPath(string target)
    {
        var t = target.Replace('\\', '/');
        if (t.StartsWith("/xl/", StringComparison.OrdinalIgnoreCase)) return t.TrimStart('/');
        if (t.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) return t;
        if (t.StartsWith("/")) return t.TrimStart('/');
        // 相对 xl/ 解析，如 "worksheets/sheet1.xml"、"../xl/worksheets/sheet1.xml"
        var segments = new List<string>("xl".Split('/'));
        foreach (var seg in t.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(seg);
        }
        return string.Join('/', segments);
    }

    // ---------- 工作表里贴的图（模板截图 / 效果照片） ----------

    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace DrawingMain = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>一张表最多认几张图（真样件表贴十几张截图是常态，上百张说明这文件不是给唛头用的）。</summary>
    public const int MaxSheetImages = 40;

    /// <summary>
    /// 读出工作表里贴着的图片及其锚点。缺任何一环（没 drawing、没媒体）就返回空表，
    /// <strong>绝不抛</strong>：一张表没图是常态，不是文件坏了，不能因为它读不到图就把数据导入挡掉。
    /// </summary>
    public static IReadOnlyList<SheetImage> ReadSheetImages(string filePath, string? sheetName = null)
    {
        using var zip = ZipFile.OpenRead(filePath);

        var sheets = ReadWorkbook(zip);
        if (sheets.Count == 0) return Array.Empty<SheetImage>();
        var target = sheetName is null
            ? sheets[0]
            : sheets.FirstOrDefault(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase));
        if (target is null) return Array.Empty<SheetImage>();

        // 链路是 sheet -> drawing -> media，两张关系表都得走一遍
        var drawingRel = ReadPartRelationships(zip, target.EntryPath)
            .FirstOrDefault(r => r.Type.EndsWith("/drawing", StringComparison.OrdinalIgnoreCase));
        if (drawingRel.Target is null || drawingRel.Target.Length == 0) return Array.Empty<SheetImage>();

        var mediaByRid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in ReadPartRelationships(zip, drawingRel.Target))
            if (rel.Type.EndsWith("/image", StringComparison.OrdinalIgnoreCase)) mediaByRid[rel.Id] = rel.Target;
        if (mediaByRid.Count == 0) return Array.Empty<SheetImage>();

        var drawingEntry = FindEntry(zip, drawingRel.Target);
        if (drawingEntry is null) return Array.Empty<SheetImage>();
        XDocument drawing;
        using (var stream = drawingEntry.Open()) drawing = XDocument.Load(stream);
        if (drawing.Root is null) return Array.Empty<SheetImage>();

        var list = new List<SheetImage>();
        var bytesByPart = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var anchor in drawing.Root.Elements())
        {
            if (list.Count >= MaxSheetImages) break;
            var embed = anchor.Descendants(DrawingMain + "blip")
                .Select(b => (string?)b.Attribute(RelDoc + "embed"))
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));
            if (embed is null || !mediaByRid.TryGetValue(embed, out var mediaPath)) continue;

            var bytes = LoadBytes(zip, mediaPath, bytesByPart);
            if (bytes is null || bytes.Length == 0) continue;

            var from = anchor.Element(Xdr + "from");
            var col = from is null ? -1 : ParseInt((string?)from.Element(Xdr + "col"), -1);
            var row = from is null ? -1 : ParseInt((string?)from.Element(Xdr + "row"), -1);
            var altName = anchor.Descendants(Xdr + "cNvPr").FirstOrDefault()?.Attribute("name")?.Value;

            list.Add(new SheetImage(
                mediaPath,
                Path.GetFileName(mediaPath),
                MimeOf(mediaPath),
                bytes,
                row,
                col,
                string.IsNullOrWhiteSpace(altName) ? null : altName.Trim()));
        }
        return list;
    }

    private static byte[]? LoadBytes(ZipArchive zip, string partPath, Dictionary<string, byte[]> cache)
    {
        if (cache.TryGetValue(partPath, out var hit)) return hit;
        var entry = FindEntry(zip, partPath);
        if (entry is null) return null;
        using var ms = new MemoryStream();
        using (var stream = entry.Open()) stream.CopyTo(ms);
        var bytes = ms.ToArray();
        cache[partPath] = bytes;
        return bytes;
    }

    /// <summary>按扩展名给真实 MIME：写死 png 的话，严格的云端遇到 .jpg 会直接拒收。</summary>
    private static string MimeOf(string partPath)
    {
        var ext = Path.GetExtension(partPath).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream",
        };
    }

    /// <summary>
    /// 读某个部件（sheet / drawing）自己的关系表。Target 按<strong>该部件所在目录</strong>解析：
    /// drawing 里的 <c>../media/image1.png</c> 要落成 <c>xl/media/image1.png</c>，
    /// 沿用按 <c>xl/</c> 解析的那一份会算出 <c>media/image1.png</c> 这个不存在的条目（图就凭空消失）。
    /// </summary>
    private static List<(string Id, string Type, string Target)> ReadPartRelationships(ZipArchive zip, string partPath)
    {
        var result = new List<(string, string, string)>();
        var dir = ParentDirOf(partPath);
        var fileName = partPath.Replace('\\', '/').Split('/')[^1];
        var relsPath = (dir.Length == 0 ? string.Empty : dir + "/") + "_rels/" + fileName + ".rels";
        var entry = FindEntry(zip, relsPath);
        if (entry is null) return result;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var rel in doc.Descendants(RelPackage + "Relationship"))
        {
            var id = (string?)rel.Attribute("Id");
            var type = (string?)rel.Attribute("Type") ?? string.Empty;
            var rawTarget = (string?)rel.Attribute("Target");
            if (id is null || rawTarget is null) continue;
            if (string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add((id, type, ResolvePartPath(rawTarget, dir)));
        }
        return result;
    }

    private static string ParentDirOf(string path)
    {
        var normalized = path.Replace('\\', '/');
        var i = normalized.LastIndexOf('/');
        return i <= 0 ? string.Empty : normalized[..i];
    }

    private static string ResolvePartPath(string target, string baseDir)
    {
        var t = target.Replace('\\', '/');
        if (t.StartsWith("/xl/", StringComparison.OrdinalIgnoreCase)) return t.TrimStart('/');
        if (t.StartsWith('/') || t.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) return t.TrimStart('/');
        var segments = new List<string>();
        foreach (var seg in baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries)) segments.Add(seg);
        foreach (var seg in t.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(seg);
        }
        return string.Join('/', segments);
    }

    // ---------- shared strings ----------

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = FindEntry(zip, "xl/sharedStrings.xml");
        if (entry is null) return list;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Descendants(Main + "si"))
        {
            // 纯 <t> 或富文本 <r><t>…</t></r> 拼接;注音 run <rPh> 里也是 <t>,
            // 吃进来会把拼音/furigana 拼进单元格值(第 23 棒)。
            var sb = new StringBuilder();
            foreach (var t in si.Descendants(Main + "t"))
            {
                if (t.Ancestors(Main + "rPh").Any()) continue;
                sb.Append(t.Value);
            }
            list.Add(sb.ToString());
        }
        return list;
    }

    // ---------- styles (日期与小数位识别) ----------

    private sealed class StyleInfo
    {
        /// <summary>cellXfs 索引 → numFmtId。</summary>
        public List<int> CellXfNumFmtIds { get; } = new();

        /// <summary>自定义 numFmtId → formatCode。</summary>
        public Dictionary<int, string> CustomFormats { get; } = new();

        /// <summary>fonts 顺序 → (字号磅, 是否粗体)。第 31 棒加：以前只读数字格式，字体一个字没读。</summary>
        public List<(double SizePt, bool Bold)> Fonts { get; } = new();

        /// <summary>cellXfs 索引 → fontId。</summary>
        public List<int> CellXfFontIds { get; } = new();

        /// <summary>cellXfs 索引 → 水平对齐（<c>center</c>/<c>left</c>/<c>right</c>；没写就是 null）。</summary>
        public List<string?> CellXfAligns { get; } = new();

        public bool IsDateStyle(int styleIndex)
        {
            var fmtId = StyleToNumFmt(styleIndex);
            return IsDateFormat(fmtId, CustomFormats);
        }

        /// <summary>该样式小数位数（用于还原 "0.50" 这类显示精度），无法判断返回 -1。</summary>
        public int DecimalPlaces(int styleIndex)
        {
            var fmtId = StyleToNumFmt(styleIndex);
            if (CustomFormats.TryGetValue(fmtId, out var code)) return CountDecimals(code);
            return BuiltinDecimalPlaces(fmtId);
        }

        /// <summary>
        /// 这一格的**可见格式**：字号、粗体、水平对齐（第 31 棒）。
        /// <para>返回 null = 跟工作簿默认一个样（fontId 是默认那个、也没单独设对齐），不必上报——
        /// 否则每张表都会刷出一屏"11pt 常规"，把真正要紧的那几格淹掉。</para>
        /// </summary>
        public (double SizePt, bool Bold, string? Align)? FormatOf(int styleIndex)
        {
            if (styleIndex < 0 || styleIndex >= CellXfFontIds.Count) return null;
            var fontId = CellXfFontIds[styleIndex];
            var align = styleIndex < CellXfAligns.Count ? CellXfAligns[styleIndex] : null;
            if (fontId <= 0 && align is null) return null;                 // 全默认
            var (size, bold) = fontId >= 0 && fontId < Fonts.Count ? Fonts[fontId] : (0d, false);
            return (size, bold, align);
        }

        private int StyleToNumFmt(int styleIndex)
            => styleIndex >= 0 && styleIndex < CellXfNumFmtIds.Count ? CellXfNumFmtIds[styleIndex] : 0;
    }

    private static StyleInfo ReadStyles(ZipArchive zip)
    {
        var info = new StyleInfo();
        var entry = FindEntry(zip, "xl/styles.xml");
        if (entry is null) return info;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        var numFmts = doc.Descendants(Main + "numFmt");
        foreach (var nf in numFmts)
        {
            var id = ParseInt((string?)nf.Attribute("numFmtId"), -1);
            var code = (string?)nf.Attribute("formatCode");
            if (id > 0 && !string.IsNullOrEmpty(code)) info.CustomFormats[id] = code;
        }

        // 第 31 棒：字体表（字号 + 粗体）。用户 2026-09-10 自己判断出「AI 读不到表格中字体、粗细、居中」——
        // 而表里那块"抄标签"的样例（哪行加粗、哪行大）正是模板该长什么样的唯一依据。
        var fonts = doc.Descendants(Main + "fonts").FirstOrDefault();
        if (fonts is not null)
        {
            foreach (var font in fonts.Elements(Main + "font"))
            {
                var size = ParseDouble((string?)font.Element(Main + "sz")?.Attribute("val"), 0);
                var bold = font.Element(Main + "b") is not null;
                info.Fonts.Add((size, bold));
            }
        }

        var cellXfs = doc.Descendants(Main + "cellXfs").FirstOrDefault();
        if (cellXfs is not null)
        {
            foreach (var xf in cellXfs.Elements(Main + "xf"))
            {
                info.CellXfNumFmtIds.Add(ParseInt((string?)xf.Attribute("numFmtId"), 0));
                info.CellXfFontIds.Add(ParseInt((string?)xf.Attribute("fontId"), 0));
                info.CellXfAligns.Add((string?)xf.Element(Main + "alignment")?.Attribute("horizontal"));
            }
        }
        return info;
    }

    /// <summary>ECMA-376 内置日期/时间格式号。</summary>
    private static bool IsBuiltinDateFormat(int id)
        => (id >= 14 && id <= 22) || (id >= 27 && id <= 36) || (id >= 45 && id <= 47) || (id >= 50 && id <= 58);

    private static bool IsDateFormat(int numFmtId, Dictionary<int, string> customFormats)
    {
        if (IsBuiltinDateFormat(numFmtId)) return true;
        if (numFmtId >= 164 && customFormats.TryGetValue(numFmtId, out var code)) return FormatLooksLikeDate(code);
        return false;
    }

    /// <summary>去掉引号字面量与方括号条件后，看是否还剩日期/时间占位符。
    /// <para>硬前提:剥完还剩数字占位符(<c>#</c>、<c>0</c>)就不是日期——<c>#,##0"mm"</c>、<c>0"pcs"</c>
    /// 这类带单位后缀的数值格式剥掉单位后剩 <c>m</c>/<c>s</c>,曾命中日期判断,数值 144 被印成 1900/5/23(第 23 棒)。</para></summary>
    private static bool FormatLooksLikeDate(string formatCode)
    {
        var stripped = StripFormatLiterals(formatCode);
        if (stripped.IndexOfAny(new[] { '#', '0' }) >= 0) return false;
        return stripped.IndexOfAny(new[] { 'y', 'Y', 'd', 'D', 'h', 'H', 'm', 's' }) >= 0;
    }

    private static string StripFormatLiterals(string code)
    {
        var sb = new StringBuilder();
        var quote = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"') { quote = !quote; continue; }
            if (quote) continue;
            if (c == '[') { bracket = true; continue; }
            if (c == ']') { bracket = false; continue; }
            if (bracket) continue;
            if (c == '\\') { i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static int CountDecimals(string code)
    {
        var stripped = StripFormatLiterals(code);
        var dot = stripped.IndexOf('.');
        if (dot < 0) return 0;
        var n = 0;
        for (var i = dot + 1; i < stripped.Length && (stripped[i] == '0' || stripped[i] == '#'); i++)
        {
            if (stripped[i] == '0') n++;
        }
        return Math.Min(n, 8);
    }

    /// <summary>
    /// ECMA-376 内置数字格式的显示小数位。
    /// <para>第 23 棒修正:旧表整体错位(2="0.00" 被记成零位、3="#,##0" 被记成两位)——
    /// 毛重 5.05 印成 5、千分位列 1234 印成 1234.00。百分比(9/10,存的是 ×100 前的比率,
    /// 不做 ×100 就不该替人格式化)、科学计数(11)、分数(12/13)一律不猜,交回原值。</para>
    /// </summary>
    private static int BuiltinDecimalPlaces(int numFmtId)
        => numFmtId switch
        {
            1 => 0,             // "0"
            2 => 2,             // "0.00"
            3 => 0,             // "#,##0"
            4 => 2,             // "#,##0.00"
            7 or 8 => 2,        // "$#,##0.00 类货币变体(币符丢弃,小数位保真)
            _ => -1,
        };

    // ---------- worksheet ----------

    private static (Dictionary<int, Dictionary<int, string>> Rows, int MaxCol, Dictionary<(int Row, int Col), int> Styles) ReadSheetCells(
        ZipArchive zip, string entryPath, List<string> sharedStrings, StyleInfo styles)
    {
        var entry = FindEntry(zip, entryPath) ?? FindEntry(zip, "xl/worksheets/sheet1.xml");
        if (entry is null) throw new InvalidDataException($"XLSX 里找不到工作表条目：{entryPath}");

        var rows = new Dictionary<int, Dictionary<int, string>>();
        var maxCol = -1;
        // 第 31 棒：格子 → 样式下标（只有"哪块字长得不一样"要它，见 DescribeCellFormats）。
        var stylesUsed = new Dictionary<(int Row, int Col), int>();
        // 合并单元格：锚点值稍后传播到整个区域
        var merges = new List<(int MinRow, int MinCol, int MaxRow, int MaxCol)>();

        using var stream = entry.Open();
        var doc = XDocument.Load(stream, LoadOptions.None);

        // 行号接续:极简生成器会省略 row 的 r 属性,旧写法缺属性全落第 0 行、后行覆盖前行(静默丢行)。
        // worksheet 的 row 恒按出现顺序排列,缺 r 就接上一行的下一行。
        var expectedRowIndex = 0;
        foreach (var rowEl in doc.Descendants(Main + "row"))
        {
            int rowIndex;
            if (int.TryParse((string?)rowEl.Attribute("r"), out var oneBased) && oneBased >= 1)
                rowIndex = oneBased - 1;                                    // 行号转 0 基,与 mergeCell 解析保持一致
            else
                rowIndex = expectedRowIndex;
            expectedRowIndex = rowIndex + 1;
            if (rowIndex < 0) continue;

            Dictionary<int, string>? bucket = null;
            foreach (var cell in rowEl.Elements(Main + "c"))
            {
                var reference = (string?)cell.Attribute("r");
                var colIndex = reference is null ? -1 : ColumnOf(reference);
                if (colIndex < 0) continue;

                var styleIndex = ParseInt((string?)cell.Attribute("s"), 0);
                var type = (string?)cell.Attribute("t");
                var value = ExtractCellValue(cell, type, styleIndex, sharedStrings, styles);

                if (string.IsNullOrEmpty(value)) continue;

                bucket ??= new Dictionary<int, string>();
                bucket[colIndex] = value;
                if (colIndex > maxCol) maxCol = colIndex;
                // 第 31 棒：样式下标顺手留在旁边（原来它是用完就丢的）——"这块字长得不一样"要有据可查。
                stylesUsed[(rowIndex, colIndex)] = styleIndex;
            }

            if (bucket is { Count: > 0 }) rows[rowIndex] = bucket;
        }

        foreach (var mc in doc.Descendants(Main + "mergeCell"))
        {
            var reference = (string?)mc.Attribute("ref");
            if (reference is null) continue;
            var range = reference.Split(':');
            if (range.Length != 2) continue;
            var a = ParseCellRef(range[0]);
            var b = ParseCellRef(range[1]);
            if (a is null || b is null) continue;
            merges.Add((a.Value.row, a.Value.col, b.Value.row, b.Value.col));
        }

        // 合并单元格：只把锚点值复制到**同一行的其他格**（表头跨列时每列都能取到该标题）。
        // 刻意不做纵向传播（第 9 棒批次一-7）：真表里的纵向合并多是「合计/批注整列只填一次」，
        // 复制进每一行就是把 155 当成每行的件数；而为了传播去新建行更会让空行复活成记录，
        // 行数虚高、页数与 {{CartonTotal}} 跟着一起错。纵向只当参考，不往数据里复制。
        foreach (var (minRow, minCol, maxRow, maxCol2) in merges)
        {
            if (minRow != maxRow) continue;
            if (!rows.TryGetValue(minRow, out var bucket)) continue;
            if (!bucket.TryGetValue(minCol, out var anchorValue)) continue;
            if (string.IsNullOrEmpty(anchorValue)) continue;

            for (var c = minCol; c <= maxCol2; c++)
            {
                if (!bucket.ContainsKey(c) || string.IsNullOrEmpty(bucket[c])) bucket[c] = anchorValue;
                if (c > maxCol) maxCol = c;
            }
        }

        return (rows, maxCol, stylesUsed);
    }

    private static string ExtractCellValue(
        XElement cell, string? type, int styleIndex, List<string> sharedStrings, StyleInfo styles)
    {
        switch (type)
        {
            case "s":
                var idx = ParseInt(cell.Element(Main + "v")?.Value, -1);
                return idx >= 0 && idx < sharedStrings.Count ? sharedStrings[idx] : string.Empty;

            case "inlineStr":
                var sb = new StringBuilder();
                var isEl = cell.Element(Main + "is");
                if (isEl is not null)
                {
                    foreach (var t in isEl.Descendants(Main + "t")) sb.Append(t.Value);
                }
                return sb.ToString();

            case "str":
            case "e":
                return cell.Element(Main + "v")?.Value ?? string.Empty;

            case "b":
                var bv = cell.Element(Main + "v")?.Value;
                return bv == "1" ? "TRUE" : bv == "0" ? "FALSE" : (bv ?? string.Empty);

            case "d":
                // ISO8601 日期串（少数导出会用）
                return NormalizeIsoDate(cell.Element(Main + "v")?.Value ?? string.Empty);

            default:
                return FormatNumeric(cell.Element(Main + "v")?.Value, styleIndex, styles);
        }
    }

    private static string FormatNumeric(string? raw, int styleIndex, StyleInfo styles)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        if (styles.IsDateStyle(styleIndex)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial > 0 && serial < 2958466)
        {
            try
            {
                var dt = DateTime.FromOADate(serial);
                return dt switch
                {
                    _ when dt.TimeOfDay == TimeSpan.Zero => dt.ToString("yyyy/M/d", CultureInfo.InvariantCulture),
                    _ when dt.Date == new DateTime(1899, 12, 31) => dt.ToString("H:mm", CultureInfo.InvariantCulture),
                    _ => dt.ToString("yyyy/M/d H:mm", CultureInfo.InvariantCulture),
                };
            }
            catch (ArgumentException)
            {
                // 超范围当普通数字处理
            }
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            return raw;

        var decimals = styles.DecimalPlaces(styleIndex);
        if (decimals >= 0)
        {
            // 还原显示精度：0.5 存成数值但格式是 0.00 → 打印该显 "0.50"
            return num.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }
        // 无明确格式：去掉浮点噪声，保持整数不带小数点
        if (Math.Abs(num - Math.Round(num)) < 1e-9 && Math.Abs(num) < 1e15)
            return ((long)Math.Round(num)).ToString(CultureInfo.InvariantCulture);
        return num.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static string NormalizeIsoDate(string iso)
    {
        if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.TimeOfDay == TimeSpan.Zero
                ? dt.ToString("yyyy/M/d", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy/M/d H:mm", CultureInfo.InvariantCulture);
        }
        return iso;
    }

    private static List<string[]> Materialize(Dictionary<int, Dictionary<int, string>> rowsByIndex, int maxCol)
    {
        if (rowsByIndex.Count == 0 || maxCol < 0) return new List<string[]>();

        var minRow = int.MaxValue;
        var maxRow = int.MinValue;
        foreach (var key in rowsByIndex.Keys)
        {
            if (key < minRow) minRow = key;
            if (key > maxRow) maxRow = key;
        }

        var width = maxCol + 1;
        var grid = new List<string[]>(maxRow - minRow + 1);
        for (var r = minRow; r <= maxRow; r++)
        {
            var line = new string[width];
            for (var c = 0; c < width; c++) line[c] = string.Empty;
            if (rowsByIndex.TryGetValue(r, out var bucket))
            {
                foreach (var kv in bucket)
                {
                    if (kv.Key < width) line[kv.Key] = kv.Value;
                }
            }
            grid.Add(line);
        }
        return GridNormalizer.Normalize(grid);
    }

    // ---------- 小工具 ----------

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var e in zip.Entries)
        {
            if (string.Equals(e.FullName.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        // 容错：大小写/前缀差异时做后缀匹配
        foreach (var e in zip.Entries)
        {
            if (e.FullName.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double ParseDouble(string? text, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>"AB12" → 列号 0 起（A=0，Z=25，AA=26）。</summary>
    private static int ColumnOf(string cellRef)
    {
        var col = 0;
        var seen = false;
        foreach (var ch in cellRef)
        {
            if (char.IsLetter(ch))
            {
                col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                seen = true;
            }
            else if (char.IsDigit(ch))
            {
                break;
            }
            else
            {
                return -1;
            }
        }
        return seen ? col - 1 : -1;
    }

    /// <summary>"B3" → (row=2, col=1)，均为 0 起。</summary>
    private static (int row, int col)? ParseCellRef(string cellRef)
    {
        var col = ColumnOf(cellRef);
        if (col < 0) return null;
        var digits = new string(cellRef.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowNumber)) return null;
        return (rowNumber - 1, col);
    }
}
