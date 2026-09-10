namespace LabelGou.Core.Recognition;

/// <summary>
/// AI 读表后拿不准、必须人拍一下的一条问题（第 22 棒）。
/// <para><strong>为什么是一条一条的问题而不是一段提醒</strong>（2026-09-09 用户把期望格式逐字写出来）：
/// 他要的是「⚠️1.件数末尾总数155 ❌(不需要) ✅(需要)」这种<strong>二选一</strong>——
/// 上一版我把提醒摊成六行文字，他的评语是「改之后更乱了」：文字提醒看完还得自己去点别的按钮，
/// 而一条问题配两个答案，看一眼点一下就完事。</para>
/// <para><strong>两类动作，第 40 棒分开</strong>：读表阶段的问题（<c>qty-column</c> 等五个）答复
/// <strong>不立刻改表</strong>，而是改这份提案里的理解，再交给下一步排版一起落地——用户 2026-09-10 的原话是
/// 「指出表格存在的问题……<strong>这层先不要对预览纸张进行调整</strong>」；
/// 落地阶段的动作（<c>retemplate</c>、<c>paper</c>）到了排版那一步才用得上，那时动模板与纸是本职。</para>
/// <para><strong>动作必须能真落地</strong>：软件里没有对应能力的动作一律不认（进 <c>Notes</c> 说明「这条我没接」），
/// 不做「点了按钮报成功、其实什么都没改」那种假象。<c>row-keep</c> 与 <c>itemno-tail</c> 的落地是
/// <strong>改这份提案</strong>（剔行名单、货号占位符），由 <c>WithAnswers</c> 确定性执行——
/// 把一次字符串替换交给概率模型是可靠性倒退。</para>
/// </summary>
/// <param name="Text">问题本身（一句大白话，人写的口径，如「件数末尾总数155」）。</param>
/// <param name="NoLabel">❌ 那个按钮的字（如「不需要」）。模型没给就用默认。</param>
/// <param name="YesLabel">✅ 那个按钮的字（如「需要」）。</param>
/// <param name="Action">点完之后软件做什么，见 <see cref="Actions"/>。认不出的一律不采纳。</param>
/// <param name="Row">这条针对原表第几行（1 起，0 = 不针对具体行）。<c>row-keep</c> 与 <c>fixed-value</c> 用得上。</param>
/// <param name="Value">动作的附加参数（<c>paper</c> = 纸规名；<c>qty-column</c>/<c>template-source</c> = 它认的那一列）。</param>
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

    // ── 第 40 棒新增：读表阶段该问、以前软件接不住因而被静默丢掉的五类风险 ──
    // 用户 2026-09-10 自己举的例子就是前两类（「x列是纸张张数列吗」「*号后面的是否保留」），
    // 而那时只有 * 号那条有动作，「哪列数张数」问不出来也接不住 → 模型只能乱问或干脆不问。

    /// <summary>出几张纸按哪一列数（✅ 是它认的那一列 / ❌ 不是，别按它数）。</summary>
    public const string ActionQtyColumn = "qty-column";

    /// <summary>模板抄在哪一列/哪一块（✅ 是那里 / ❌ 不是，别照那块排）。软件要去那一列量字号。</summary>
    public const string ActionTemplateSource = "template-source";

    /// <summary>这张表有没有列名行（✅ 有 / ❌ 第一行就是货）。</summary>
    public const string ActionHeaderRow = "header-row";

    /// <summary>标签上某一行是死字还是跟着货变（✅ 每张都印这一句 / ❌ 是某一列的值）。</summary>
    public const string ActionFixedValue = "fixed-value";

    /// <summary>某一列到底装的是什么（混了几样东西 / 列名对不上 / 缺列），要人认一下。</summary>
    public const string ActionColumnMeaning = "column-meaning";

    /// <summary>软件真接得住的动作清单（认不出的进 Notes，不假装能办）。</summary>
    public static readonly string[] Actions =
    {
        ActionRowKeep, ActionRetemplate, ActionPaper, ActionItemNoTail,
        ActionQtyColumn, ActionTemplateSource, ActionHeaderRow, ActionFixedValue, ActionColumnMeaning,
    };

    /// <summary>
    /// 读表阶段（不动模板与纸）该问的那几类。<see cref="AiSheetProposalPrompt.BuildRead"/> 把这份清单当白名单发给模型：
    /// 问不出这几类里的哪一种就别问——以前 <c>questions</c> 是模型自由发挥，所以它爱问什么问什么。
    /// </summary>
    public static readonly string[] ReadStageActions =
    {
        ActionRowKeep, ActionItemNoTail, ActionQtyColumn, ActionTemplateSource,
        ActionHeaderRow, ActionFixedValue, ActionColumnMeaning,
    };

    public bool IsKnownAction => Actions.Contains(Action, StringComparer.OrdinalIgnoreCase);

    /// <summary>这条是不是读表阶段的问题（决定答复后是「改这份提案」还是「等排版那一步再说」）。</summary>
    public bool IsReadStage => ReadStageActions.Contains(Action, StringComparer.OrdinalIgnoreCase);

    /// <summary>面板上那一行的题面（带序号由面板加）。</summary>
    public string Describe() => Text;
}

/// <summary>
/// 老板对某一条问题拍过的板（第 40 棒）。
/// <para><strong>为什么要有这一份</strong>：两阶段拆分之后，读表阶段的答复<strong>不再立刻改活表</strong>
/// （用户原话「这层先不要对预览纸张进行调整」），它得先攒在这份提案上——一方面驱动软件
/// <em>确定性地</em>修正理解（剔行名单、货号占位符），另一方面拼成人话喂给排版那一步的请求。
/// 以前答复只塞进面板的会话历史，而提案那条路根本不读历史，所以他答了等于没答。</para>
/// </summary>
/// <param name="Question">问的是哪一条。</param>
/// <param name="Yes">他点的是 ✅ 还是 ❌。</param>
public sealed record AiAnswer(AiSheetQuestion Question, bool Yes)
{
    /// <summary>他点的那个按钮上的字。</summary>
    public string Choice => Yes ? Question.YesLabel : Question.NoLabel;

    /// <summary>喂给下一步请求、也是摆在面板上的那一句人话。</summary>
    public string Line => $"{Question.Text} → {Choice}";
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
