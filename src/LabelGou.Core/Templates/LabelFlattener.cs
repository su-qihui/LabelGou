using System.Linq;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Templates;

/// <summary>
/// 把一份模板按<strong>某一行</strong>烤平：从表里取来的值写进文字，软件自己算的量留活。
/// <para>
/// 这是「单张定稿」的地基（用户 2026-09-16 定口径：「把那张纸和原本列表代替符切开，显示的就是那张
/// 那行的内容，修改后对其他没影响」）。产物是一份普通 <see cref="LabelTemplate"/>，所以预览、打印、
/// PDF、位图、SVG 五个出口照旧走 <c>LayoutEngine</c> + 唯一画法，<strong>不开第二套渲染</strong>。
/// </para>
/// <para>
/// <strong>为什么件号不能一起写死</strong>：一行 5 箱要出 5 张纸，那 5 张只差 <c>{{NoX}}</c>。
/// 把它烤平就等于五张全印同一个件号——那是重号，比"全部跟随"更贵。所以内置计算量
/// （<c>NoX</c> / <c>NoY</c> / <c>NoXofY</c> / <c>RowIndex</c> / <c>RecordCount</c> / 模板名 / 来源文件）
/// 一律原样留在定稿里，由编号引擎逐张算。
/// </para>
/// <para>
/// 同一族还有<strong>住在 <c>col:</c> 名下的推算量</strong>（<c>col:组内序</c>、<c>col:本行箱数</c>，
/// 以及被规则改写过的 <c>CartonNo</c> / <c>CartonTotal</c>）：它们的名字看着像表里的列，实际是引擎
/// 逐张写上去的（来源标 <c>ValueOrigin.Rule</c>）。烤平它们 = 五张全印「Ctns：1件」，
/// 与 §五-62 记的那本账同形。所以留活的判据是<strong>来源</strong>，不是名字清单。
/// </para>
/// </summary>
public static class LabelFlattener
{
    /// <param name="Template">烤平后的副本。宽高与元素数与来源逐字相同（拼版按一个标量尺寸排格子，定稿不许改尺寸）。</param>
    /// <param name="UnreviewedFields">被写死时还挂着「需人工核对」的字段。定稿要替出纸闸记住它们——
    /// 烤平不许把 <c>UnconfirmedLabelCount</c> 这道闸绕过去。</param>
    /// <param name="BlankedTokens">这一行取不到值、被写成空白的占位符（第 66 棒纪律：空必须点名，不许静默消失）。</param>
    /// <param name="UnknownTokens">模板里谁也认不出的占位符。原样留着，渲染时按空处理并计入未解析告警，
    /// 定稿不替它编一个值。</param>
    public sealed record Result(
        LabelTemplate Template,
        IReadOnlyList<string> UnreviewedFields,
        IReadOnlyList<string> BlankedTokens,
        IReadOnlyList<string> UnknownTokens);

    /// <summary>哪些元素的数据表达式住在 <c>Text</c> 里：文本与条码是主要的（Line 只用坐标），
    /// 判据写成「不是 Line」而不是枚举两种，将来加 kind 不会漏烤一处。</summary>
    public static bool IsDataBearer(TemplateElement element)
        => element.Kind != ElementKind.Line && !string.IsNullOrEmpty(element.Text);

    /// <summary>
    /// 按 <paramref name="record"/> 那一行的真值烤平 <paramref name="source"/>。
    /// </summary>
    public static Result Flatten(LabelTemplate source, MarkRecord record)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(record);

        var copy = source.CloneAsUserCopy(source.Name);
        var unreviewed = new List<string>();
        var blanked = new List<string>();
        var unknown = new List<string>();

        foreach (var element in copy.Elements)
        {
            if (!IsDataBearer(element) || element.Text is not { Length: > 0 } text) continue;
            if (!TemplateTokenizer.EnumerateTokens(text).Any()) continue;

            var emittedValue = false;
            var hasLiveToken = false;
            var elementBlanks = new List<string>();

            element.Text = TemplateTokenizer.Replace(text, token =>
            {
                var read = ReadRowValue(token, record);
                switch (read.Kind)
                {
                    case RowValueKind.Live:
                        // 软件算的量一律留活（内置计算令牌 + 编号引擎按规则写的推算量），
                        // 渲染时逐张重算——件号与本行箱数被冻住就是重号/错数。
                        hasLiveToken = true;
                        return "{{" + token + "}}";

                    case RowValueKind.Unknown:
                        if (!unknown.Contains(token)) unknown.Add(token);
                        return "{{" + token + "}}";

                    default:
                        if (!string.IsNullOrWhiteSpace(read.Value))
                        {
                            emittedValue = true;
                            if (read.Review && !unreviewed.Contains(read.Label)) unreviewed.Add(read.Label);
                            return read.Value!;
                        }

                        if (!elementBlanks.Contains(read.Label)) elementBlanks.Add(read.Label);
                        return string.Empty;
                }
            });

            if (elementBlanks.Count > 0)
            {
                if (!hasLiveToken && !emittedValue)
                {
                    // 「含变量但全空整条隐藏」这条规矩（M-Layout 硬规矩②）在烤平后没有令牌可依了：
                    // 留着 "G.W.:  KG" 那截壳就是把它印出去。整条置空才与烤平前逐字一致。
                    element.Text = string.Empty;
                }

                foreach (var label in elementBlanks)
                {
                    if (!blanked.Contains(label)) blanked.Add(label);
                }
            }
        }

        return new Result(copy, unreviewed, blanked, unknown);
    }

    /// <summary>一个占位符在这一行上该怎么办。</summary>
    private enum RowValueKind
    {
        /// <summary>软件自己算的量：原样留着，渲染时逐张重算。</summary>
        Live,

        /// <summary>谁也认不出这个占位符：留着让渲染端照旧报未解析，定稿不替它编值。</summary>
        Unknown,

        /// <summary>从表里（或人手上、AI 认的、整批固定值）取来的值：写死进定稿。</summary>
        Text,
    }

    /// <summary>
    /// 这一行在这个占位符上的真值，以及它该不该留活。
    /// <para><strong>留活的判据看来源，不看名字清单</strong>：编号引擎写进去的量一律标
    /// <see cref="ValueOrigin.Rule"/>（<c>col:组内序</c> / <c>col:本行箱数</c> / <c>CartonNo</c> /
    /// <c>CartonTotal</c>…），把它们烤平 = 一行五张全印成同一个件号与同一个「Ctns：1件」
    /// （§五-62 立的那本账）。以后引擎再加推算量，不必回来改这里。</para>
    /// </summary>
    private static (RowValueKind Kind, string? Value, bool Review, string Label) ReadRowValue(string token, MarkRecord record)
    {
        if (TemplateTokenizer.IsBuiltInToken(token)) return (RowValueKind.Live, null, false, token);

        if (token.StartsWith("col:", StringComparison.OrdinalIgnoreCase))
        {
            var key = token[4..].Trim();
            return Classify(record.GetCustom("col:" + key), $"col:{key}");
        }

        if (MarkFieldCatalog.TryParseKey(token, out var field))
        {
            var label = MarkFieldCatalog.TryGet(field, out var def) ? def.ChineseName : field.ToString();
            return Classify(record.Get(field), label);
        }

        return (RowValueKind.Unknown, null, false, token);
    }

    /// <summary>「没这一项」与「有这一项但是软件算的」与「表里的值」是三件事，只有最后一件该写死。</summary>
    private static (RowValueKind, string?, bool, string) Classify(MarkValue? value, string label)
        => value is null
            ? (RowValueKind.Text, string.Empty, false, label)
            : value.Origin == ValueOrigin.Rule
                ? (RowValueKind.Live, null, false, label)
                : (RowValueKind.Text, value.Text, value.NeedsReview, label);
}
