using System.Globalization;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Data;

/// <summary>
/// 表头行识别 + 表头归一化。
/// <para>
/// 工厂发来的表极少"第一行就是干净表头"：常见前导标题行、空行、多行复合表头。
/// 因此在候选前几行中按<strong>字段别名命中数</strong>为主、非空覆盖率与"像不像标签"为辅打分，
/// 选出最可能是表头的那一行，其上方的行一律丢弃。
/// </para>
/// </summary>
public static class HeaderRowDetector
{
    /// <summary>只在前 N 行里找表头（再往后基本就是数据了）。</summary>
    public const int ScanWindow = 8;

    /// <summary>识别结果。</summary>
    /// <param name="HeaderRowIndex">表头所在行（0 起）。</param>
    /// <param name="Headers">归一化后的表头（空标题补成 "列 A"，重复标题加序号）。</param>
    /// <param name="DataRows">表头之下的数据行（已按表头列宽补齐）。</param>
    public sealed record DetectionResult(int HeaderRowIndex, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> DataRows);

    public static DetectionResult Detect(IReadOnlyList<string[]> grid)
    {
        if (grid.Count == 0) return new DetectionResult(0, Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        var width = grid.Max(r => r.Length);
        var candidates = Math.Min(ScanWindow, grid.Count);
        var bestIndex = 0;
        var bestScore = double.MinValue;

        for (var i = 0; i < candidates; i++)
        {
            var score = Score(grid[i], width, i);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        var headers = NormalizeHeaders(grid[bestIndex], width);
        var dataRows = new List<IReadOnlyList<string>>(Math.Max(0, grid.Count - bestIndex - 1));
        for (var r = bestIndex + 1; r < grid.Count; r++)
        {
            var row = grid[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (row.Length == width)
            {
                dataRows.Add(row);
                continue;
            }
            var padded = new string[width];
            Array.Copy(row, padded, Math.Min(row.Length, width));
            for (var c = row.Length; c < width; c++) padded[c] = string.Empty;
            dataRows.Add(padded);
        }

        return new DetectionResult(bestIndex, headers, dataRows);
    }

    /// <summary>
    /// 给一行"像不像表头"打分。命中越多标准字段别名，权重越高——这是最强信号。
    /// </summary>
    private static double Score(string[] row, int width, int rowIndex)
    {
        var nonEmpty = row.Count(c => !string.IsNullOrWhiteSpace(c));
        if (nonEmpty == 0) return double.MinValue;

        var coverage = (double)nonEmpty / width;              // 非空覆盖率
        var labelish = row.Count(c => LooksLikeLabel(c));      // 短、非纯数字
        var aliasHits = row.Count(c => AliasHit(c));           // 命中唛头字段别名

        var score = 0d;
        score += coverage * 3.0;
        score += (double)labelish / width * 2.0;
        score += Math.Min(aliasHits, 8) * 4.0;                // 别名命中主导
        score -= rowIndex * 0.15;                             // 同等条件下偏好在上的行
        if (nonEmpty < 2) score -= 5;                         // 只有一格基本不是表头
        return score;
    }

    private static bool LooksLikeLabel(string cell)
    {
        var text = cell?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > 24) return false;
        // 纯数字/日期更像数据而非表头
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return false;
        return true;
    }

    /// <summary>该文本是否命中某个标准字段的别名。</summary>
    private static bool AliasHit(string cell)
    {
        var text = Normalize(cell);
        if (text.Length == 0) return false;
        foreach (var def in MarkFieldCatalog.All)
        {
            if (string.Equals(text, Normalize(def.ChineseName), StringComparison.Ordinal)) return true;
            if (def.EnglishLabel.Length > 1 && string.Equals(text, Normalize(def.EnglishLabel), StringComparison.Ordinal)) return true;
            foreach (var alias in def.Aliases)
            {
                if (text == Normalize(alias)) return true;
            }
        }
        return false;
    }

    /// <summary>归一化表头：去空白、去内部空格与常见符号，便于匹配与展示。</summary>
    public static string Normalize(string? text)
    {
        var raw = text?.Trim() ?? string.Empty;
        if (raw.Length == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is '：' or ':' or '（' or '(' or '）' or ')' or '、' or '.' or '．' or '。' or '-' or '_' or '/') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static IReadOnlyList<string> NormalizeHeaders(string[] headerRow, int width)
    {
        var result = new List<string>(width);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var c = 0; c < width; c++)
        {
            var raw = c < headerRow.Length ? headerRow[c].Trim() : string.Empty;
            var name = raw.Length == 0 ? "列 " + ColumnLetter(c) : raw;

            // 合并单元格跨列或重名时补序号，保证列标题唯一（映射界面按下标取列）
            if (seen.TryGetValue(name, out var count))
            {
                count++;
                seen[name] = count;
                name = $"{name} ({count})";
            }
            else
            {
                seen[name] = 1;
            }
            result.Add(name);
        }
        return result;
    }

    /// <summary>0→A，25→Z，26→AA。用于给无名列出个可读的字母名。</summary>
    public static string ColumnLetter(int zeroBasedColumn)
    {
        var n = zeroBasedColumn + 1;
        var sb = new System.Text.StringBuilder();
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            n = (n - 1) / 26;
        }
        return sb.ToString();
    }
}
