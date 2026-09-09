namespace LabelGou.Core.Recognition;

/// <summary>
/// AI 读表后拿不准、必须人拍一下的一条问题（第 22 棒）。
/// <para><strong>为什么是一条一条的问题而不是一段提醒</strong>（2026-09-09 用户把期望格式逐字写出来）：
/// 他要的是「⚠️1.件数末尾总数155 ❌(不需要) ✅(需要)」这种<strong>二选一</strong>——
/// 上一版我把提醒摊成六行文字，他的评语是「改之后更乱了」：文字提醒看完还得自己去点别的按钮，
/// 而一条问题配两个答案，看一眼点一下就完事。</para>
/// <para><strong>动作必须能真落地</strong>：软件里没有对应能力的动作一律不认（进 <c>Notes</c> 说明「这条我没接」），
/// 不做「点了按钮报成功、其实什么都没改」那种假象。</para>
/// </summary>
/// <param name="Text">问题本身（一句大白话，人写的口径，如「件数末尾总数155」）。</param>
/// <param name="NoLabel">❌ 那个按钮的字（如「不需要」）。模型没给就用默认。</param>
/// <param name="YesLabel">✅ 那个按钮的字（如「需要」）。</param>
/// <param name="Action">点完之后软件做什么，见 <see cref="Actions"/>。认不出的一律不采纳。</param>
/// <param name="Row">这条针对原表第几行（1 起，0 = 不针对具体行）。只有 <c>row-keep</c> 用得上。</param>
/// <param name="Value">动作的附加参数（<c>paper</c> = 纸规名；其余可空）。</param>
public sealed record AiSheetQuestion(
    string Text,
    string NoLabel,
    string YesLabel,
    string Action,
    int Row,
    string? Value)
{
    /// <summary>这一行到底要不要当货印（✅ 印 / ❌ 不印）。</summary>
    public const string ActionRowKeep = "row-keep";

    /// <summary>要不要按它给出的模板重排（✅ 重排并存进模板库 / ❌ 保持现在用的那张）。</summary>
    public const string ActionRetemplate = "retemplate";

    /// <summary>要不要换成它点的那张纸（✅ 换 / ❌ 不换）。</summary>
    public const string ActionPaper = "paper";

    /// <summary>货号里 <c>*</c> 后面那截留不留（✅ 原样印 / ❌ 只印 <c>*</c> 前面）。</summary>
    public const string ActionItemNoTail = "itemno-tail";

    /// <summary>软件真接得住的动作清单（认不出的进 Notes，不假装能办）。</summary>
    public static readonly string[] Actions =
    {
        ActionRowKeep, ActionRetemplate, ActionPaper, ActionItemNoTail,
    };

    public bool IsKnownAction => Actions.Contains(Action, StringComparer.OrdinalIgnoreCase);

    /// <summary>面板上那一行的题面（带序号由面板加）。</summary>
    public string Describe() => Text;
}

/// <summary>
/// 读表结果里「只给人看」的那一份料（第 22 棒）。
/// <para>为什么单独拉一个记录：用户 2026-09-09 把期望的输出逐字写了出来（「表格有效数据31行4列
/// / 纸张:一开四--28*20--2*2--14*10 / 模版:F列:一行BOLAROM加粗居中,二行Item no：(A列)... /
/// 张数:绑定B列 / 预览:31个模板,155张」）。那是<strong>五行固定句式</strong>，与「落地要用的机器字段」
/// 不是一回事（落地用 <c>SheetSpecName</c>、<c>Layout</c>、<c>TotalValueRows</c>），
/// 混在一个 20 参的记录里只会越滚越大。</para>
/// </summary>
/// <param name="DataCols">它数的有效数据几列（行与张数由软件自己算，不信模型报的数）。</param>
/// <param name="TemplateSource">模板拄在哪一块（如「F列」）——这句话很重要：它说明 AI 看懂了那是模板而不是数据。</param>
/// <param name="TemplateLines">模板逐行的人话写法（「一行 BOLAROM 加粗居中」「二行 Item no：(A列)」）。</param>
/// <param name="QtyColumn">按哪一列数张数（表头原样）。</param>
/// <param name="QtyColumnIndex">那一列的 0 起下标（为了写成「B 列」这种 Excel 口径）。</param>
/// <param name="PaperText">纸那一句的展示写法（模型原话，如「一开四--28*20--2*2--14*10」）；空则软件自己拼。</param>
public sealed record SheetReadout(
    int? DataCols,
    string? TemplateSource,
    IReadOnlyList<string> TemplateLines,
    string? QtyColumn,
    int? QtyColumnIndex,
    string? PaperText)
{
    public static readonly SheetReadout Empty = new(null, null, Array.Empty<string>(), null, null, null);
}
