using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Layout;

/// <summary>
/// <strong>按模板补全的示意样例</strong>：在「手上还没有真数据」的场合（模板编辑器画布、未导入时的示意预览），
/// 给出一份能让模板<strong>每一行都显示出来</strong>的样例记录。
/// <para>
/// 为什么需要它（第 42 棒，用户报的第三条问题）：没有真数据时，界面一律拿
/// <see cref="SampleRecords.StandardSample"/> 那份<strong>写死的</strong>样例去排版，而它里面只有一个
/// <c>col:托盘号</c>。可 AI 排出来的行式模板几乎全在用 <c>{{col:列名}}</c> 直引表格列
/// （实测用户模板库 10 份里有 9 份引用 <c>col:QTY</c>、<c>col:数量</c>、<c>col:件数</c>、<c>col:ITEM NO</c> 等），
/// 写死样例里一个都没有 → <see cref="LayoutEngine"/> 那条「含变量但变量全空就整行隐藏」的硬规矩生效
/// → <strong>元素框还在、字没了</strong>。用户看到的正是「编辑模板时框架显示但效果是空的，预览却有字」
/// （预览那时已经导入了真表，记录里带着 <c>col:</c> 键）。
/// </para>
/// <para>
/// 那条隐藏规矩本身<strong>是对的、一个字不改</strong>：它防的是把 <c>G.W.:  KG</c> 这种半截残句印上纸。
/// 要补的是「示意用的数据不够全」，不是「隐藏规矩太严」。
/// </para>
/// </summary>
/// <remarks>
/// <strong>不许用在任何"能不能出纸"的判定上。</strong>
/// <see cref="LabelGou.Core.Layout.LayoutEngine"/> 的隐藏规矩与出纸闸
/// （<c>MainViewModel.TemplatePrintsAnything</c>，第 38 棒三道闸之一）靠的是「真数据取不到值 = 这一行印不出来」；
/// 拿补全过的样例去喂它们，空版就会被判成「有内容」而放行，印出一张白纸。
/// 判据一句话：<strong>补全样例只服务"给人看版面形状"，绝不服务"这张纸能不能印"。</strong>
/// </remarks>
public static class TemplateSample
{
    /// <summary>
    /// 造一份样例记录：标准字段沿用 <see cref="SampleRecords.StandardSample"/>，
    /// 再<strong>按这份模板实际引用到的列</strong>补上对应样例值。
    /// </summary>
    /// <remarks>
    /// 补的值<strong>就是列名本身</strong>（换行折成「 / 」），不是编出来的假数据：
    /// ① 不猜（三道闸之二）——软件不知道 <c>QTY</c> 那一列真会填什么，编一个数字就是在骗人；
    /// ② 用户一眼能认出「这一行绑的是哪一列」，比看一串假数字更有用；
    /// ③ 长度与真实值同量级，字号收缩与折行的表现仍有参考价值。
    /// </remarks>
    public static MarkRecord ForTemplate(LabelTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var builder = SampleRecords.StandardSample().ToBuilder();
        foreach (var column in EnumerateColumnTokens(template))
        {
            builder.SetCustom("col:" + column, ColumnLabel.SingleLine(column));
        }
        return builder.Build();
    }

    /// <summary>
    /// 这份模板（含条码元素）引用到的所有 <c>col:</c> 列名，去重、保持首次出现顺序。
    /// <para>
    /// 只收 <c>col:</c> 一种：标准字段（<c>{{ItemNo}}</c> 等）写死样例里本来就全有；
    /// 内置计算量（<c>{{NoXofY}}</c> 等）由 <see cref="LayoutContext"/> 的行号/总数算出来，不查记录；
    /// 而<strong>白名单外的未知字段一律不补</strong>——那种令牌校验器本来就报 Error 存不进库，
    /// 给它编个值等于把「引用了不存在的字段」这个真问题藏起来。
    /// </para>
    /// <para>列名取令牌原文 trim 后的形式，与 <c>LayoutEngine.ResolveToken</c> 的查键口径一致（同样 trim），
    /// 否则补了也命中不了。</para>
    /// </summary>
    private static IEnumerable<string> EnumerateColumnTokens(LabelTemplate template)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in template.Elements)
        {
            // 条码的数据表达式与文本共用同一套占位符（{{col:条码列}}），所以两类都要扫。
            if (element.Kind is not (ElementKind.Text or ElementKind.Barcode)) continue;

            foreach (var token in TemplateTokenizer.EnumerateTokens(element.Text))
            {
                if (!token.StartsWith("col:", StringComparison.OrdinalIgnoreCase)) continue;
                var column = token[4..].Trim();
                if (column.Length == 0) continue;
                if (!seen.Add(column)) continue;
                yield return column;
            }
        }
    }
}
