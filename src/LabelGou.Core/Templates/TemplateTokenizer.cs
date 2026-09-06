using System.Text;

namespace LabelGou.Core.Templates;

/// <summary>
/// 解析模板文本里的变量占位符 <c>{{名字}}</c>。
/// <para>
/// 这是模板层与数据层之间唯一的耦合点：模板只认名字，不认列号也不认来源，
/// 所以同一份模板既能吃 Excel 数据（M1），也能吃 AI 识别结果（M6）。
/// </para>
/// </summary>
public static class TemplateTokenizer
{
    /// <summary>内置计算量（不需要在映射里绑列）。</summary>
    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        /// No. x / y 形式的件号
        "NoXofY",
        /// 只有 x
        "NoX",
        /// 只有 y（总件数，无数据时用记录总数）
        "NoY",
        /// 当前记录序号（1 起）
        "RowIndex",
        /// 本次任务的记录总数
        "RecordCount",
        /// 模板名
        "TemplateName",
        /// 数据源文件名
        "SourceFile",
    };

    /// <summary>依次取出文本中出现的所有占位符名（原样，未 trim 前）。</summary>
    public static IEnumerable<string> EnumerateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) yield break;
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) yield break;

            var token = text[(open + 2)..close].Trim();
            if (token.Length > 0) yield return token;
            i = close + 2;
        }
    }

    public static bool IsBuiltInToken(string token) => BuiltIns.Contains(token.Trim());

    /// <summary>把 <c>{{X}}</c> 形式的名字规范成小写比较用的键。</summary>
    public static string NormalizeToken(string token) => token.Trim();

    /// <summary>
    /// 用解析器替换所有占位符；返回结果文本与未解析成功的占位符列表。
    /// </summary>
    /// <param name="text">含占位符的模板文本。</param>
    /// <param name="resolver">占位符名 → 值；返回 null 表示解析不出来。</param>
    /// <param name="unresolved">收集未解析的占位符名（界面提示用，避免把 <c>{{}}</c> 直接印出去）。</param>
    /// <param name="keepUnresolved">true 时未解析的占位符原样保留（调试用），false 时替换为空串。</param>
    public static string Replace(string? text, Func<string, string?> resolver, List<string>? unresolved = null, bool keepUnresolved = false)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length + 16);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, open - i);

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                // 没有闭合，原样输出剩余
                sb.Append(text, open, text.Length - open);
                break;
            }

            var token = text[(open + 2)..close].Trim();
            var value = token.Length == 0 ? null : resolver(token);

            if (value is null)
            {
                unresolved?.Add(token);
                if (keepUnresolved) sb.Append("{{").Append(token).Append("}}");
            }
            else
            {
                sb.Append(value);
            }
            i = close + 2;
        }

        return sb.ToString();
    }
}
