using System.Text;

namespace LabelGou.Core.Data;

/// <summary>
/// 从 Excel/CSV 读出的一张"平面表"：表头 + 数据行，全部以字符串承载。
/// <para>
/// 刻意不做类型猜测（数字/日期在读取阶段就转成字符串），因为唛头数据里
/// "0.5" 与 "0.50"、"2026/9/1" 与 "2026-09-01" 的<strong>字面形式本身就是要印的内容</strong>；
/// 类型只在字段映射与校验阶段按需判断。
/// </para>
/// </summary>
public sealed class TabularData
{
    public TabularData(
        string sourceFile,
        string sheetName,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int headerRowIndex,
        Encoding? encoding = null,
        IReadOnlyList<IReadOnlyList<string>>? preamble = null,
        IReadOnlyList<SheetImage>? images = null)
    {
        SourceFile = sourceFile;
        SheetName = sheetName;
        Headers = headers;
        Rows = rows;
        HeaderRowIndex = headerRowIndex;
        Encoding = encoding;
        Preamble = preamble ?? Array.Empty<IReadOnlyList<string>>();
        Images = images ?? Array.Empty<SheetImage>();
    }

    /// <summary>来源文件完整路径。</summary>
    public string SourceFile { get; }

    /// <summary>工作表名（CSV 恒为 "CSV"）。</summary>
    public string SheetName { get; }

    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>表头在原始网格中的行号（0 起），用于向用户解释"跳过了前几行"。</summary>
    public int HeaderRowIndex { get; }

    /// <summary>CSV 实际使用的编码；XLSX 为 null。</summary>
    public Encoding? Encoding { get; }

    /// <summary>
    /// 表头以上的行（原样，行号 = 原始网格行号）。<strong>不参与映射、不出标签</strong>，
    /// 但必须能摊给 AI 看：纸规、总件数、客户名这类批注就写在这些地方（第 20 棒）。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Preamble { get; }

    /// <summary>
    /// 工作表里贴着的图（模板截图 / 效果照片）。CSV 恒为空。
    /// <para>为什么归到数据层：用户 2026-09-09 定的主路径是「AI 先看整张表再出模板」，
    /// 而那张贴图是表的一部分，不是附件。</para>
    /// </summary>
    public IReadOnlyList<SheetImage> Images { get; }

    /// <summary>这张表里有没有可供 AI 对照的视觉参照（没图时 AI 不许造模板，只能要参照）。</summary>
    public bool HasVisualReference => Images.Count > 0;

    public int ColumnCount => Headers.Count;

    public int RowCount => Rows.Count;

    /// <summary>取单元格，越界安全返回空串。</summary>
    public string GetCell(int rowIndex, int colIndex)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count) return string.Empty;
        var row = Rows[rowIndex];
        return colIndex >= 0 && colIndex < row.Count ? row[colIndex] : string.Empty;
    }

    /// <summary>按表头文字找列号；找不到返回 -1。</summary>
    public int IndexOfHeader(string header)
    {
        for (var i = 0; i < Headers.Count; i++)
        {
            if (string.Equals(Headers[i], header, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>预览前 N 行（界面表格用）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Preview(int maxRows = 20)
        => Rows.Count <= maxRows ? Rows : Rows.Take(maxRows).ToList();

    public string Describe()
    {
        var enc = Encoding is null ? "" : $" · {Encoding.WebName}";
        return $"{Path.GetFileName(SourceFile)}[{SheetName}]{enc} · {RowCount} 行 × {ColumnCount} 列";
    }
}

/// <summary>
/// 文本编码嗅探。打印店的 CSV 常见三种：UTF-8（带/不带 BOM）、GBK/GB18030（国内 Excel 另存）、UTF-16。
/// .NET 默认不含 GB18030，需注册 CodePages 编码提供程序。
/// </summary>
public static class TextDecoder
{
    private static bool _registered;
    private static readonly object Gate = new();

    /// <summary>注册代码页编码提供程序（幂等）。</summary>
    public static void EnsureCodePages()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _registered = true;
        }
    }

    /// <summary>
    /// 嗅探并解码字节流为文本。规则：BOM 优先 → 严格 UTF-8 能否解出 → 退 GB18030 → 退系统默认。
    /// </summary>
    public static string Decode(byte[] bytes, out Encoding encoding)
    {
        EnsureCodePages();

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = Encoding.UTF8;
            return encoding.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            return encoding.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            return encoding.GetString(bytes, 2, bytes.Length - 2);
        }

        // 无 BOM：先尝试严格 UTF-8（遇到非法序列会抛），成功即认定 UTF-8。
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var text = strict.GetString(bytes);
            encoding = Encoding.UTF8;
            return text;
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8，按国内 Excel 默认另存编码处理。
        }

        try
        {
            var gbk = Encoding.GetEncoding("GB18030");
            encoding = gbk;
            return gbk.GetString(bytes);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.Default;
            return Encoding.Default.GetString(bytes);
        }
    }

    /// <summary>从若干候选分隔符中挑一个最一致的（按"每行字段数众数占比"评分）。</summary>
    public static char SniffDelimiter(IEnumerable<string> lines)
    {
        var sample = lines.Take(20).Where(l => l.Trim().Length > 0).ToList();
        if (sample.Count == 0) return ',';

        var candidates = new[] { ',', ';', '\t', '|' };
        char best = ',';
        double bestScore = -1;

        foreach (var c in candidates)
        {
            var counts = sample.Select(l => CountTopLevelFields(l, c)).ToList();
            if (counts.All(n => n <= 1)) continue;
            var mode = counts.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            // 得分 = 落在众数字段数上的行占比 × 众数规模（字段越多越可能是真分隔符）
            var score = (double)mode.Count() / counts.Count * Math.Min(mode.Key, 30);
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best;
    }

    /// <summary>统计按给定分隔符切分后的字段数（引号内的分隔符不计）。</summary>
    private static int CountTopLevelFields(string line, char delimiter)
    {
        var fields = 1;
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') i++;
                else inQuotes = !inQuotes;
            }
            else if (ch == delimiter && !inQuotes)
            {
                fields++;
            }
        }
        return fields;
    }
}
