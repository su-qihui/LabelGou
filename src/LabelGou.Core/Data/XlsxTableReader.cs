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
        var (rowsByIndex, maxCol) = ReadSheetCells(zip, target.EntryPath, sharedStrings, styleInfo);

        return Materialize(rowsByIndex, maxCol);
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
            // 纯 <t> 或富文本 <r><t>…</t></r> 拼接
            var sb = new StringBuilder();
            foreach (var t in si.Descendants(Main + "t")) sb.Append(t.Value);
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

        var cellXfs = doc.Descendants(Main + "cellXfs").FirstOrDefault();
        if (cellXfs is not null)
        {
            foreach (var xf in cellXfs.Elements(Main + "xf"))
            {
                info.CellXfNumFmtIds.Add(ParseInt((string?)xf.Attribute("numFmtId"), 0));
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

    /// <summary>去掉引号字面量与方括号条件后，看是否还剩日期/时间占位符。</summary>
    private static bool FormatLooksLikeDate(string formatCode)
    {
        var stripped = StripFormatLiterals(formatCode);
        return stripped.IndexOfAny(new[] { 'y', 'Y', 'd', 'D', 'h', 'H', 'm', 's' }) >= 0
               && !stripped.Contains("0.0", StringComparison.Ordinal);
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

    private static int BuiltinDecimalPlaces(int numFmtId)
        => numFmtId switch
        {
            2 => 0,
            3 or 4 => 2,
            7 or 8 or 9 or 10 => 0,
            11 or 12 => 2,
            _ => -1,
        };

    // ---------- worksheet ----------

    private static (Dictionary<int, Dictionary<int, string>> Rows, int MaxCol) ReadSheetCells(
        ZipArchive zip, string entryPath, List<string> sharedStrings, StyleInfo styles)
    {
        var entry = FindEntry(zip, entryPath) ?? FindEntry(zip, "xl/worksheets/sheet1.xml");
        if (entry is null) throw new InvalidDataException($"XLSX 里找不到工作表条目：{entryPath}");

        var rows = new Dictionary<int, Dictionary<int, string>>();
        var maxCol = -1;
        // 合并单元格：锚点值稍后传播到整个区域
        var merges = new List<(int MinRow, int MinCol, int MaxRow, int MaxCol)>();

        using var stream = entry.Open();
        var doc = XDocument.Load(stream, LoadOptions.None);

        foreach (var rowEl in doc.Descendants(Main + "row"))
        {
            var rowIndex = ParseInt((string?)rowEl.Attribute("r"), 1) - 1;   // 行号转 0 基，与 mergeCell 解析保持一致
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

        // 合并单元格：把锚点值复制到区域内所有格（表头跨列时，每列都能取到该标题）
        foreach (var (minRow, minCol, maxRow, maxCol2) in merges)
        {
            if (!rows.TryGetValue(minRow, out var anchorBucket)) continue;
            if (!anchorBucket.TryGetValue(minCol, out var anchorValue)) continue;
            if (string.IsNullOrEmpty(anchorValue)) continue;

            for (var r = minRow; r <= maxRow; r++)
            {
                if (!rows.TryGetValue(r, out var bucket))
                {
                    bucket = new Dictionary<int, string>();
                    rows[r] = bucket;
                }
                for (var c = minCol; c <= maxCol2; c++)
                {
                    if (!bucket.ContainsKey(c) || string.IsNullOrEmpty(bucket[c])) bucket[c] = anchorValue;
                    if (c > maxCol) maxCol = c;
                }
            }
        }

        return (rows, maxCol);
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
