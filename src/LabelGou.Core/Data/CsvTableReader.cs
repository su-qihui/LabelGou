using System.Text;

namespace LabelGou.Core.Data;

/// <summary>
/// 自建 CSV/TSV 解析器（RFC 4180 子集 + 常见方言容错），零外部依赖。
/// <para>
/// 有意不引入 CsvHelper：本项目只需"读一张平面表"，自写解析器约 100 行、
/// 可单测、且完全符合"打印店离线、依赖最少化"的定位。
/// </para>
/// </summary>
public static class CsvTableReader
{
    /// <summary>读取 CSV 文件为原始网格（每行一个 string[]，已按最宽行补齐）。</summary>
    public static List<string[]> ReadRawGrid(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var text = TextDecoder.Decode(bytes, out var encoding);
        var delimiter = TextDecoder.SniffDelimiter(SplitLines(text));
        var grid = Parse(text, delimiter);
        CurrentEncoding = encoding;
        CurrentDelimiter = delimiter;
        return GridNormalizer.Normalize(grid);
    }

    /// <summary>上一次 <see cref="ReadRawGrid"/> 实际使用的编码（供界面提示"已按 GB18030 打开"）。</summary>
    public static Encoding? CurrentEncoding { get; private set; }

    /// <summary>上一次实际采用的分隔符。</summary>
    public static char CurrentDelimiter { get; private set; } = ',';

    /// <summary>
    /// 核心解析：逐字符扫描，支持引号包裹、<c>""</c> 转义、字段内换行、行尾 CRLF/LF/CR 混用。
    /// </summary>
    public static List<string[]> Parse(string text, char delimiter)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    // "" 是转义的一个引号；单个引号则退出引号态
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                sb.Append(ch);
                i++;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;

                case '\r':
                    // \r\n 当作一个换行；孤立 \r 也算换行
                    EndField(fields, sb);
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    i++;
                    EndRow(rows, fields);
                    break;

                case '\n':
                    EndField(fields, sb);
                    i++;
                    EndRow(rows, fields);
                    break;

                default:
                    if (ch == delimiter)
                    {
                        EndField(fields, sb);
                        i++;
                    }
                    else
                    {
                        sb.Append(ch);
                        i++;
                    }
                    break;
            }
        }

        // 收尾：最后一行没有换行符的情况
        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            sb.Clear();
            EndRow(rows, fields);
        }

        return rows;
    }

    private static void EndField(List<string> fields, StringBuilder sb)
    {
        fields.Add(sb.ToString().Trim());
        sb.Clear();
    }

    private static void EndRow(List<string[]> rows, List<string> fields)
    {
        // 完全空的一行不产出（跳过末尾多余换行）
        if (fields.Count == 1 && fields[0].Length == 0)
        {
            fields.Clear();
            return;
        }
        rows.Add(fields.ToArray());
        fields.Clear();
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\n' or '\r')
            {
                yield return text[start..i];
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        if (start <= text.Length - 1) yield return text[start..];
    }
}

/// <summary>
/// 把参差不齐的原始行补齐成矩形网格（尾行缺列补空串，空行丢弃）。
/// </summary>
public static class GridNormalizer
{
    public static List<string[]> Normalize(List<string[]> raw)
    {
        var kept = raw.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (kept.Count == 0) return kept;

        var width = kept.Max(r => r.Length);
        var result = new List<string[]>(kept.Count);
        foreach (var row in kept)
        {
            if (row.Length == width)
            {
                result.Add(row);
                continue;
            }
            var padded = new string[width];
            Array.Copy(row, padded, row.Length);
            for (var i = row.Length; i < width; i++) padded[i] = string.Empty;
            result.Add(padded);
        }
        return result;
    }
}
