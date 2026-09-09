using System.Text.RegularExpressions;

namespace LabelGou.Core.Marks;

/// <summary>
/// 表格文本里的数字提取口径，两处共用：NumberingEngine 的展开张数与 RecordMapper 的量纲校验。
/// <para>§五-74 的「取第一个连续数字串，不是把所有数字拼起来」仍然成立（<c>12.0</c> 不得读成 120）；
/// 第 23 棒补一条：千分位整串（<c>1,234</c>）要先于它认出来——否则读成 1，一箱变一张，
/// <c>1,250 KGS</c> 也被「克当千克」告警漏放。小数逗号（<c>12,5</c>）仍按小数点，是既定口径。</para>
/// </summary>
public static class NumericText
{
    /// <summary>整数口径（张数用）：千分位整串优先，不带小数——<c>12.0</c> 仍只取 12。</summary>
    public static readonly Regex Integer = new(
        @"\d{1,3}(?:,\d{3})+(?![\d,])|\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>数值口径（重量/体积校验用）：千分位整串优先，其余容忍小数逗号与小数点。</summary>
    public static readonly Regex Number = new(
        @"[-+]?\d{1,3}(?:,\d{3})+(?:\.\d+)?(?![\d,])|[-+]?\d+(?:[.,]\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 命中千分位整串形态（<c>1,234</c> / <c>-1,234.56</c>）的值剥掉逗号，其余原样返回。
    /// 交给调用方再决定按 int 还是 double 解析。
    /// </summary>
    public static string WithoutThousandsSeparators(string token)
    {
        var body = token.StartsWith('-') ? token[1..] : token;
        if (!Regex.IsMatch(body, @"^\d{1,3}(?:,\d{3})+(?:\.\d+)?$")) return token;
        var head = token.StartsWith('-') ? "-" : string.Empty;
        return head + body.Replace(",", string.Empty);
    }
}
