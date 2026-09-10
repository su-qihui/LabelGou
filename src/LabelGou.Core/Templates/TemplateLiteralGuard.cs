using System.Text.RegularExpressions;

namespace LabelGou.Core.Templates;

/// <summary>
/// 模板写死文字的跨客户对值判据（第 24 棒，§六 活账 A-1 的最小改法）。
/// <para><strong>它拦的是什么</strong>：模板里 <c>{{字段}}</c> 会随这批数据变，而字面量永远不变——
/// 拿一张带着别家客户名（如金沐那行的 BOLAROM）的模板去排这批单，能安静地把别家的名字印上箱。
/// 旧代码 <c>MissingValueLines()</c> 只看 <c>{{字段}}</c> 与 <c>{{col:…}}</c> 有没有值，字面量一个字不看。</para>
/// <para><strong>只提醒，不自动改</strong>（活账原口径）：产出的全是人话 Warning 字符串，
/// 由界面挂进 ② 步橙色区与 ⑤ 复核闸门；删改模板文字永远是人的决定，不是这个函数的。</para>
/// <para><strong>宁漏勿错喊</strong>（与 <see cref="Data.SummaryRowSpotter"/> 同一个不对称）：
/// 只认两种形状——a) 整行剥掉占位符后只剩一个孤零零的西文单词（客户名那一行的形状：
/// 三字母以上、纯西文、不带数字单位，如 BOLAROM）；b) 整行是 <c>MADE IN X</c>。
/// 带中文/带数字/多词的行一律放过（「ITEM：香水 perfume」这类品名行误报率高，交给 AI 提案那条路去说）。
/// 「整行单词」先与这批已知收货人（字段值+整批固定值）对，再与全表任何格子的内容对——
/// 表里出现过这个词就不报（金沐的表里 F 列本来抄着 BOLAROM，那是自家的单，不该被喊）。</para>
/// </summary>
public static class TemplateLiteralGuard
{
    /// <summary>剥占位符用的形状：{{字段}} 与 {{col:…}} 一律视为「会随数据变的部分」。</summary>
    private static readonly Regex TokenStrip = new(@"\{\{[^{}]*\}\}", RegexOptions.Compiled);

    /// <summary>孤零零的西文单词：字母开头，三字符以上，允许连字符与&（客户简称常见 O'brien、SA-PLUS 形态）。</summary>
    private static readonly Regex LoneWord = new(@"^[A-Za-z][A-Za-z0-9&'\-\.]{2,}$", RegexOptions.Compiled);

    /// <summary>产地行的形状：MADE IN CHINA / MADE IN JAPAN（整行只有这个，剥完占位符后仍完整）。</summary>
    private static readonly Regex MadeIn = new(
        @"^MADE\s*IN\s+([A-Za-z][A-Za-z\.]*(?:\s+[A-Za-z][A-Za-z\.]*)*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 对一遍这张模板：写死的字面量与这批货对不对得上。返回可直接上屏的人话告警（可空表 = 没话说）。
    /// </summary>
    /// <param name="template">当前选中的模板。</param>
    /// <param name="knownConsignees">这批已知的收货人/客户（字段值 + 整批固定值，可空集）。</param>
    /// <param name="knownOrigins">这批已知的产地（同上；空集时 MADE IN 行不报——产地常量不在表里是常态）。</param>
    /// <param name="tableCellTexts">这张表里出现过的全部格子原文（数据行 + 表头以上批注；含别家字样的模板列也算「表里出现过」）。</param>
    public static IReadOnlyList<string> Check(
        LabelTemplate template,
        IReadOnlyCollection<string> knownConsignees,
        IReadOnlyCollection<string> knownOrigins,
        IReadOnlyCollection<string> tableCellTexts)
    {
        var warnings = new List<string>();
        var consignees = Clean(knownConsignees);
        var origins = Clean(knownOrigins);
        var cells = Clean(tableCellTexts);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in template.Elements)
        {
            if (element.Kind != ElementKind.Text || string.IsNullOrWhiteSpace(element.Text)) continue;
            var literal = TokenStrip.Replace(element.Text, " ").Trim();
            if (literal.Length == 0) continue;
            var compressed = Regex.Replace(literal, @"\s+", " ");

            var made = MadeIn.Match(compressed);
            if (made.Success)
            {
                var country = made.Groups[1].Value.Trim();
                // 只在批次自己报过产地且对不上时才喊：MADE IN CHINA 是行业常量，表里没有它是常态，不疑神疑鬼。
                if (origins.Count > 0
                    && !origins.Any(o => o.Contains(country, StringComparison.OrdinalIgnoreCase)
                                         || country.Contains(o, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add($"模板整行写死「MADE IN {country}」，这批的产地是「{string.Join("、", origins)}」——"
                                 + "换单前核对这一行是不是别家留下的（不自动改，只提醒）。");
                }
                continue;
            }

            if (!LoneWord.IsMatch(compressed)) continue;
            var word = compressed;
            if (!reported.Add(word)) continue;
            // 表里任何格子含这个词（整词按包含算，够宽容）→ 这就是这批货自己的字，不喊。
            if (cells.Any(c => c.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            // 与已知收货人对得上也不喊。
            if (consignees.Any(c => c.Contains(word, StringComparison.OrdinalIgnoreCase)
                                    || word.Contains(c, StringComparison.OrdinalIgnoreCase))) continue;

            warnings.Add(consignees.Count > 0
                ? $"模板整行写死的「{word}」在这批数据里一个字都没出现，这批的收货人是「{string.Join("、", consignees)}」——"
                  + "换单前核对这是不是别家模板留下的字（不自动改，只提醒）。"
                : $"模板整行写死的「{word}」在这张表里一个字都没出现——核对它属不属于这批货（不自动改，只提醒）。");
        }
        return warnings;
    }

    /// <summary>清洗输入：去空白项、trim、压缩内部空白、去重（大小写不敏感）。</summary>
    private static List<string> Clean(IReadOnlyCollection<string>? values)
    {
        var list = new List<string>();
        if (values is null) return list;
        foreach (var raw in values)
        {
            var v = Regex.Replace((raw ?? string.Empty).Trim(), @"\s+", " ");
            if (v.Length > 0 && !list.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) list.Add(v);
        }
        return list;
    }
}
