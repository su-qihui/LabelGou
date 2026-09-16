using System.Text;
using System.Text.RegularExpressions;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 文本归一化（定案 D14）。
/// <para>本机实测证据：Windows 内置 OCR 把 <c>:</c> 认成全屏 <c>：</c>、把 <c>N.W.</c> 拆成
/// <c>N .W.</c>、把 <c>CONTRACT</c> 掉字成 <c>NTRACT</c>、小数点直接丢（<c>25.5</c> 变成
/// <c>25 ？5</c>）；而大模型给的键大小写混杂（<c>grossWeight</c> 与 <c>GrossWeight</c> 都出现过）。
/// 这些脏活全部集中在这一处处理，绝不允许规则抽取、证据匹配、界面显示各写一套清洗。</para>
/// </summary>
public static class TextNormalizer
{
    private static readonly Regex NumberPattern = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 出现在两个数字之间时按小数点看待的字符。
    /// <para>注意里面必须有半角 <c>.</c> 本身：本机 OCR 给的是全角 <c>．</c>，而 <see cref="ToHalfwidth"/>
    /// 已经先把它转成了 <c>.</c>，字符表若只写全角形就一个也匹不上（真实跌过一跤：
    /// "25 ． 5 KGS" 只修了前一半，重量被读成 25）。</para>
    /// <para><b>逗号不在本表里</b>：一律当小数点会把 <c>1,250 KGS</c> 改成 <c>1.250</c> →
    /// 一千二百五十公斤被印成 1.25（批次一-5）。逗号由下面两个正则分开处理。</para>
    /// </summary>
    private const string DecimalLookalikes = "。．·、.";

    /// <summary>数字 …分隔符… 数字 → 归一成小数点（分隔符两侧允许空格）。</summary>
    private static readonly Regex DecimalGap = new(
        @"(?<=\d)\s*[" + DecimalLookalikes + @"]\s*(?=\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>千分位：逗号后紧跟恰好三位数字且再后不是数字 → 整组是千分位，直接去掉（不当小数点）。</summary>
    private static readonly Regex ThousandsComma = new(
        @"(?<=\d),(?=\d{3}(?!\d))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>小数逗号：逗号右边只有 1~2 位（"25,5"、"1,25"）时按小数点看待（OCR 也会把小数点认成逗号）。</summary>
    private static readonly Regex CommaDecimalGap = new(
        @"(?<=\d)\s*,\s*(?=\d{1,2}(?!\d))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 全角 ASCII（U+FF01~FF5E）与全角空格降到半角；破折号族统一成 <c>-</c>；乘号族统一成 <c>x</c>。
    /// <para>刻意不动汉字：本方法只处理「长得像 ASCII 的全角」，不做中英互译。</para>
    /// </summary>
    public static string ToHalfwidth(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch >= '\uFF01' && ch <= '\uFF5E')
            {
                sb.Append((char)(ch - 0xFEE0));
                continue;
            }

            switch (ch)
            {
                case '\u3000':
                    sb.Append(' ');
                    break;
                case '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' or '\uFE58' or '\uFF0D':
                    sb.Append('-');
                    break;
                case '\u00d7' or '\u2715' or '\u2716':
                    sb.Append('x');
                    break;
                case '\u2018' or '\u2019' or '\u201B' or '\u2032':
                    sb.Append('\'');
                    break;
                case '\u201C' or '\u201D' or '\u2033':
                    sb.Append('"');
                    break;
                case '\u2026':
                    sb.Append("...");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>半角化 + 折叠连续空白 + 去首尾空白。给界面显示与印面文本用。</summary>
    public static string Squeeze(string? text)
    {
        var half = ToHalfwidth(text);
        if (half.Length == 0) return string.Empty;

        var sb = new StringBuilder(half.Length);
        var pendingSpace = false;
        foreach (var ch in half)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 匹配用的压缩形：半角化 + 去掉所有空白 + 去掉非字母数字 + 转小写。
    /// <para>于是 <c>"N .W."</c>、<c>"N.W."</c>、<c>"n w"</c> 全部变成 <c>"nw"</c>——
    /// 这是「证据匹配」能成立的前提（D12）。</para>
    /// </summary>
    public static string Compact(string? text)
    {
        var half = ToHalfwidth(text);
        if (half.Length == 0) return string.Empty;

        var sb = new StringBuilder(half.Length);
        foreach (var ch in half)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把数字之间的小数点变体统一成 <c>.</c>。
    /// <para>本机实测 OCR 的真实形状是带空格的：<c>"25 ． 5 KGS"</c>、<c>"22 · 1 KGS"</c>
    /// （全角句点与中间点都被当成小数点读过），所以上下文必须跨空格找分隔符。
    /// 纯空格不当分隔符使：「60 40 30」这种三段尺寸里的空格是真分隔。</para>
    /// </summary>
    public static string RepairDecimalSeparators(string? text)
    {
        var half = ToHalfwidth(text);
        if (half.Length < 3) return half;
        // 顺序很关键：先剔千分位，再把剩下的逗号当小数点，最后才是其他分隔符形。
        var grouped = ThousandsComma.Replace(half, string.Empty);
        var commaAsDecimal = CommaDecimalGap.Replace(grouped, ".");
        return DecimalGap.Replace(commaAsDecimal, ".");
    }

    /// <summary>按出现顺序抽出全部数字（小数点已归一）。找不到返回空数组。</summary>
    public static List<string> Numbers(string? text)
    {
        var repaired = RepairDecimalSeparators(text);
        var found = new List<string>();
        foreach (Match m in NumberPattern.Matches(repaired)) found.Add(m.Value);
        return found;
    }

    /// <summary>
    /// <paramref name="value"/> 用到的每个数字字符，是否都能在 <paramref name="evidence"/> 里按次数配走。
    /// <para>小数点不参与比对——OCR 恰恰会把小数点弄丢，比数字位置只会到处误报；
    /// 「数字集吻合而小数点位置存疑」是另一种告警，由 <see cref="CrossValidator"/> 单独给。</para>
    /// </summary>
    public static bool DigitsCovered(string? value, string? evidence)
    {
        var need = DigitCounts(value);
        if (need.Count == 0) return true;   // 没有数字的值（港口名、产地）不由这条规则管

        var have = DigitCounts(evidence);
        foreach (var kv in need)
        {
            if (!have.TryGetValue(kv.Key, out var count) || count < kv.Value) return false;
        }

        return true;
    }

    private static Dictionary<char, int> DigitCounts(string? text)
    {
        var map = new Dictionary<char, int>();
        foreach (var ch in ToHalfwidth(text))
        {
            if (!Net6Compat.IsAsciiDigit(ch)) continue;
            map[ch] = map.TryGetValue(ch, out var n) ? n + 1 : 1;
        }

        return map;
    }
}
