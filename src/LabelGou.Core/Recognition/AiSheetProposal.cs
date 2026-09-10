using System.Globalization;
using System.Text.Json;
using LabelGou.Core.Data;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 这份提案是<strong>哪一步</strong>要来的（第 40 棒把一次请求拆成两次）。
/// <para>用户 2026-09-10 给的方向是「用户发送表格 → AI 读取和理解 → 指出表格存在的问题
/// （<strong>这层先不要对预览纸张进行调整</strong>）→ 收到反馈后再次理解 → 理解后进行自动排版」。
/// 以前一次请求把这两步的字段全要了，模型还没拿到答复就得先把纸规与行数猜出来，猜的也照样被落地——
/// 这就是他说的「乱改模板、乱提问题」。</para>
/// </summary>
public enum AiProposalStage
{
    /// <summary>读表理解：只要「这张表是什么、有哪些风险要人拍板」。<strong>排版字段一律不采纳</strong>。</summary>
    Read,

    /// <summary>排版落地：只要版式与纸规。理解字段缺失不算错（那一步已经问过了）。</summary>
    Layout,
}

/// <summary>
/// AI 对「这张表该怎么切、这张纸该怎么摆」的一份提案（第 21 棒）。
/// <para><strong>它从哪来</strong>：模型看着整张表的画像（含表头以上的批注、右侧贴的效果图）回的 JSON，
/// 由 <see cref="Parse"/> 清洗成这份结构。<strong>它到哪去</strong>：确认窗上逐条摊给人看，
/// 人点「用这个」之后才变成 <see cref="SheetLayoutChoice"/>（切表指令）与纸规改动。</para>
/// <para><strong>为什么权限这么大还要一层校验</strong>：用户 2026-09-09 的原话是「把权限都给 AI，
/// 手动调整效果很差」。但看错一张表的代价是重印一垛箱子，所以这里守住三条硬线：
/// ① 行号必须落在真实存在的行里，且不许把表头自己剔掉；② 剔完必须还剩至少一行货；
/// ③ 纸规只能从软件里<em>已有</em>的那些里选，纸张尺寸给范围，绝不接受模型随手写的任意毫米数。</para>
/// </summary>
/// <param name="HeaderRow">表头在<b>原表</b>的第几行（1 起，与人看 Excel 的口径一致）；null = 模型没说。</param>
/// <param name="HasHeader">这张表有没有表头行。false = 首行也当数据（就是「少印一张」那个 bug 的解）。</param>
/// <param name="TotalValueRows">要当合计/批注行剔除的<b>原表</b>行号（1 起）。</param>
/// <param name="Layout">行式版式（标签尺寸在它里面，不留第二个真源）。null = 模型没给可出版的行。</param>
/// <param name="SheetSpecName">点名要用的现有纸规名（必须命中清单，见 <see cref="Errors"/>）。</param>
/// <param name="PaperWidthMm">提议的纸张宽（毫米，可空）。只在清单里一张都不合适时才有意义。</param>
/// <param name="PaperHeightMm">提议的纸张高（毫米，可空）。</param>
/// <param name="Columns">每行几枚（0 = 由宽度自动算）。</param>
/// <param name="Rows">每页几行（0 = 由高度自动算）。</param>
/// <param name="FollowsLabel">纸面跟着标签走（一页一枚）。</param>
/// <param name="Warnings">模型自己报的疑点（如「模板顶部写死的 BOLAROM 与这批客户不符」「末行像合计」）。</param>
/// <param name="Reason">模型说的一句人话：为什么这么判。</param>
/// <param name="Notes">软件替它改了什么（夹范围、去重、忽略越界行号）——一条条写清，不许悄悄修好。</param>
/// <param name="Errors">这条提案不能用的原因。非空时确认窗只准看不准「用这个」。</param>
/// <param name="Readout">这份读表结果里「给人看的那五行」的料（几列、模板抄在哪一列、按哪列数张数）。</param>
/// <param name="Questions">拿不准、要人二选一的那几条（第 22 棒：用户要的「⚠️ 一条问题 + ❌/✅ 两个按钮」）。</param>
/// <param name="Mappings">
/// 它报的**字段绑定**（第 30 棒）：哪一列是哪个唛头字段。
/// <para>用户 2026-09-10 的诉求是「AI 模式导入表格后不应该直接绑定，先把表格给 AI 理解后由 AI 绑定」，
/// 这一份就是"由 AI 绑定"的载体——经白名单与列回查校验后，逐条变成改动卡等人 ✅。</para>
/// </param>
/// <param name="Facts">
/// 它对这张表**看出来的判断**，一条一件（第 31 棒）。
/// <para>为什么会多出这一份：用户 2026-09-10 截图里那段「它的说法」读着混乱——根因是提示词要求
/// <c>reason</c>「一句说完」，模型被逼着把「第1列是货号 + 第2列是每箱数量 + 第3列是总箱数 + F列那4行是模版 +
/// JP/品名按死文本处理」五件事挤进一句。所以改成**逐条**：一条只说一件事，人一眼扫得完。</para>
/// </param>
public sealed record AiSheetProposal(
    int? HeaderRow,
    bool? HasHeader,
    IReadOnlyList<int> TotalValueRows,
    RowLayoutSpec? Layout,
    string? SheetSpecName,
    double? PaperWidthMm,
    double? PaperHeightMm,
    int? Columns,
    int? Rows,
    bool? FollowsLabel,
    IReadOnlyList<string> Warnings,
    string? Reason,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Errors,
    SheetReadout Readout,
    IReadOnlyList<AiSheetQuestion> Questions,
    IReadOnlyList<AiFieldBinding> Mappings,
    IReadOnlyList<string> Facts)
{
    /// <summary>
    /// 老板已经拍过板的答复（第 40 棒）。用 init 属性而不是第 19 个位置参数：
    /// 现有构造点一个都不用动，而这份东西本来就是"提案生成之后才长出来的"。
    /// </summary>
    public IReadOnlyList<AiAnswer> Answers { get; init; } = Array.Empty<AiAnswer>();

    /// <summary>能用才允许落地（与行式版式那份 <c>RowLayoutProposal.HasSpec</c> 同一条纪律）。</summary>
    public bool IsUsable => Errors.Count == 0;

    /// <summary>是不是什么都没提（模型只回了话没回指令）。什么都没提就别去动用户的表。</summary>
    public bool IsEmpty =>
        HeaderRow is null && HasHeader is null && TotalValueRows.Count == 0 && Layout is null &&
        SheetSpecName is null && PaperWidthMm is null && PaperHeightMm is null &&
        Columns is null && Rows is null && FollowsLabel is null && Readout.QtyColumn is null &&
        Questions.Count == 0 && Mappings.Count == 0 && Facts.Count == 0;

    /// <summary>
    /// 老板拍了一条问题：<strong>改这份提案，不碰活表</strong>（第 40 棒）。
    /// <para>用户的红线是「指出表格存在的问题……<strong>这层先不要对预览纸张进行调整</strong>」，
    /// 所以读表阶段的答复一律只改这份提案里的理解，等排版那一步出来再一起落地。
    /// 以前这一条走的是主窗口里的逐条落地（真的去改剔行名单、真的存模板、真的换纸），
    /// 于是问题还没答完，模板与纸就已经被第一轮提案改过一遍了——他说的「乱改模板」就是这个顺序。</para>
    /// <para><strong>能确定性办的就地办，办不了的老实记账</strong>：剔行、有无列名行、张数列、模板来源列、
    /// 货号占位符这五件软件自己就能改对；<c>fixed-value</c> 与 <c>column-meaning</c> 改不动这份提案里的任何字段，
    /// 就只把那句决定记下来喂给排版那一步——不假装办了。</para>
    /// </summary>
    /// <param name="columns">
    /// 这张表的列画像（第 40 棒补）。<c>qty-column</c> 答「是」而读表时那一列没落进 <see cref="SheetReadout.QtyColumn"/>
    /// （模型只在问题里提了一嘴、或当时没对回表里的列）时，要用它把问题带的列（<see cref="AiSheetQuestion.Value"/>）
    /// 真对回表里那一列再设上去——没有列画像就对不回，只能如实说设不了，绝不猜一列（猜错 = 数错张数 = 印错货）。
    /// </param>
    public AiSheetProposal WithAnswer(AiSheetQuestion q, bool yes, IReadOnlyList<ColumnPortrait>? columns = null)
    {
        var extra = new List<string>();
        var totalRows = TotalValueRows;
        var hasHeader = HasHeader;
        var readout = Readout;

        switch (q.Action)
        {
            case AiSheetQuestion.ActionRowKeep:
                totalRows = RowKeepAnswered(TotalValueRows, q, yes, HeaderRow, extra);
                break;

            case AiSheetQuestion.ActionHeaderRow:
                hasHeader = yes;
                extra.Add(yes ? "照你说的，这张表有列名行" : "照你说的，第一行就是货（这张表没有列名行）");
                break;

            case AiSheetQuestion.ActionQtyColumn:
                if (yes)
                {
                    // 它认的那一列若在读表时已经落进 Readout，就直接用；没落（只在问题里提了一嘴、
                    // 或那一列当时没对回表里）就照问题里带的列（q.Value）补认一次。补认也要真对回表里的列——
                    // 对不上就如实说设不了，绝不猜一列（猜错 = 数错张数 = 印错货）。
                    // 用户实测的「AI 问了是否将 x 列设为张数、结果仍是模版那一张」就卡在这：以前答「是」只记一句话，
                    // 没把那一列设上去，OutputCounter 拿到空的张数列就只能出一张。
                    if (Readout.QtyColumn is not { Length: > 0 })
                    {
                        var (qtyHeader, qtyIndex) = ResolveColumn(q.Value, columns);
                        if (qtyHeader is null)
                        {
                            extra.Add("你说按它认的那一列数张数，可它没报出是哪一列、表里也没对上 —— 这一列我设不了，"
                                    + "排版落地后你在「② 连接字段」里自己点一列当张数");
                            break;
                        }
                        readout = Readout with { QtyColumn = qtyHeader, QtyColumnIndex = qtyIndex };
                    }
                    extra.Add($"照你说的，出几张纸就按「{readout.QtyColumn}」这一列数");
                }
                else
                {
                    readout = Readout with { QtyColumn = null, QtyColumnIndex = null };
                    extra.Add("照你说的，不按它认的那一列数张数——那一步先不算张数，"
                            + "等排版落地后你在「② 连接字段」里自己点一列");
                }
                break;

            case AiSheetQuestion.ActionTemplateSource:
                if (yes) extra.Add("照你说的，模板就抄在那一块");
                else
                {
                    readout = Readout with { TemplateSource = null };
                    extra.Add("照你说的，那块不是模板——软件就没处去量字号了，排版时只能用它填的数，字号可能不准");
                }
                break;

            case AiSheetQuestion.ActionItemNoTail:
                if (Layout is { } tailSpec) ApplyItemNoTail(tailSpec, Mappings, yes, extra);
                else extra.Add(yes
                    ? "记下了：货号连 * 后面一起原样印（版式出来时软件自己改占位符，不用再问它一遍）"
                    : "记下了：货号只印 * 前面（版式出来时软件自己改占位符，不用再问它一遍）");
                break;

            default:
                // fixed-value / column-meaning / retemplate / paper：改不动这份提案里的字段，
                // 只把决定记下来。喂给排版那一步的请求，由它按这句决定出 rows。
                extra.Add($"记下了：{q.Text} → {(yes ? q.YesLabel : q.NoLabel)}");
                break;
        }

        return this with
        {
            Answers = new List<AiAnswer>(Answers) { new(q, yes) },
            TotalValueRows = totalRows,
            HasHeader = hasHeader,
            Readout = readout,
            Notes = extra.Count == 0 ? Notes : Notes.Concat(extra).ToList(),
        };
    }

    /// <summary>
    /// 「这一行要不要当货印」的答复折成剔行名单（原表行号，1 起）。
    /// <para>列名那一行永远不进名单：模型确实会一边说「列名在第 1 行」一边把第 1 行报成合计行，
    /// 老板点了「不印」也不能真把列名剔掉（与 <see cref="BuildExcluded"/> 同一条护栏）。</para>
    /// </summary>
    private static IReadOnlyList<int> RowKeepAnswered(
        IReadOnlyList<int> current, AiSheetQuestion q, bool yes, int? headerRow, List<string> extra)
    {
        var list = current.ToList();
        if (yes)
        {
            if (!list.Remove(q.Row)) extra.Add($"第 {q.Row} 行本来就在印，什么都没改");
            else extra.Add($"照你说的，第 {q.Row} 行当货印");
            return list;
        }
        if (headerRow is int h && q.Row == h)
        {
            extra.Add($"第 {q.Row} 行是列名那一行，不能当合计行剔掉——这条没照办");
            return current;
        }
        if (list.Contains(q.Row)) extra.Add($"第 {q.Row} 行本来就不印，什么都没改");
        else
        {
            list.Add(q.Row);
            extra.Add($"照你说的，第 {q.Row} 行不印（当合计/批注行剔掉）");
        }
        return list;
    }

    /// <summary>
    /// 货号里 <c>*</c> 后面那截留不留：<strong>一次确定性的占位符替换</strong>。
    /// <para><c>{{ItemNo}}</c> = 软件清洗过的货号（只印 <c>*</c> 前面），
    /// <c>{{col:表头原样}}</c> = 那一列原样（连 <c>*</c> 后面一起印）。
    /// 这件事第 22 棒就在主窗口里做过一遍，第 40 棒挪进 Core：答复驱动、可单测，
    /// 而且<strong>不再交给模型</strong>——把一次字符串替换丢给概率模型是可靠性倒退。</para>
    /// <para>货号是哪一列只认它自己报过的绑定；报不出就如实说改不了，不猜一列去改（猜错就是印错货）。</para>
    /// </summary>
    private static void ApplyItemNoTail(
        RowLayoutSpec spec, IReadOnlyList<AiFieldBinding> mappings, bool yes, List<string> extra)
    {
        var header = mappings.FirstOrDefault(m => m.Field == MarkFieldKey.ItemNo)?.ColumnHeader;
        if (string.IsNullOrWhiteSpace(header))
        {
            extra.Add("货号那一列它没报过绑定，* 号这条改不了——排版落地后你在「② 连接字段」里连上货号，再回来点这条");
            return;
        }
        var colToken = "{{col:" + header + "}}";
        var changed = 0;
        foreach (var row in spec.Rows)
        {
            if (yes && row.Content.Contains("{{ItemNo}}", StringComparison.OrdinalIgnoreCase))
            {
                row.Content = row.Content.Replace("{{ItemNo}}", colToken);
                changed++;
            }
            else if (!yes && row.Content.Contains(colToken, StringComparison.OrdinalIgnoreCase))
            {
                row.Content = row.Content.Replace(colToken, "{{ItemNo}}");
                changed++;
            }
        }
        extra.Add(changed == 0
            ? (yes ? "模板里没用到货号那一列，已经是「连 * 后面一起印」了，没改"
                   : "模板里没用到货号那一列，已经是「只印 * 前面」了，没改")
            : (yes ? $"货号改成连 * 后面一起原样印（按表里「{header}」那一列）"
                   : $"货号改成只印 * 前面（把「{header}」那一列换成软件清洗过的货号）"));
    }

    /// <summary>
    /// 把<strong>读表阶段的理解</strong>与<strong>排版阶段的版式</strong>合成一份可落地的提案（第 40 棒）。
    /// <para>为什么要合：两次请求各回一份，而落地只认一份（<see cref="ToChoice"/> + 版式 + 纸规）。
    /// 理解那半边一律取读表阶段的——它是老板逐条拍过板的；版式与纸那半边取排版阶段的。
    /// 字段绑定两边都可能有：读表阶段的优先（拍过板），排版阶段只补它没给的那些字段。</para>
    /// <para><strong>攒着的货号答复在这里补落</strong>：读表阶段版式还不存在，那次替换办不成，
    /// 版式一到就照答复改占位符，不用再问模型一遍。</para>
    /// </summary>
    public static AiSheetProposal MergeLayout(AiSheetProposal read, AiSheetProposal layout)
    {
        var notes = read.Notes.Concat(layout.Notes).ToList();

        var mappings = read.Mappings.ToList();
        foreach (var m in layout.Mappings)
            if (!mappings.Any(x => x.Field == m.Field)) mappings.Add(m);

        // 版式出来了才念得出「一行 BOLAROM 加粗居中」这种话，所以这几行取排版阶段的；
        // 张数列与模板来源列取读表阶段的——老板可能已经把它们改掉了。
        var readout = read.Readout with
        {
            TemplateLines = layout.Readout.TemplateLines.Count > 0
                ? layout.Readout.TemplateLines
                : read.Readout.TemplateLines,
        };

        var merged = read with
        {
            Layout = layout.Layout,
            SheetSpecName = layout.SheetSpecName,
            PaperWidthMm = layout.PaperWidthMm,
            PaperHeightMm = layout.PaperHeightMm,
            Columns = layout.Columns,
            Rows = layout.Rows,
            FollowsLabel = layout.FollowsLabel,
            Mappings = mappings,
            Readout = readout,
            Questions = layout.Questions,
            Warnings = read.Warnings.Concat(layout.Warnings).ToList(),
            Notes = notes,
            Errors = layout.Errors,
        };

        if (merged.Layout is { } spec)
        {
            var extra = new List<string>();
            foreach (var answer in merged.Answers.Where(a => a.Question.Action == AiSheetQuestion.ActionItemNoTail))
                ApplyItemNoTail(spec, merged.Mappings, answer.Yes, extra);
            if (extra.Count > 0) merged = merged with { Notes = merged.Notes.Concat(extra).ToList() };
        }
        return merged;
    }

    /// <summary>
    /// 把<strong>读表阶段定下来的那几件事</strong>念成几行人话（第 40 棒）。
    /// <para>两处共用同一份：排版那一步的提示词把它当<em>既成事实</em>带过去（不许模型再问一遍、也不许推翻），
    /// 面板把它摆给老板看（他得知道软件到底把这张表读成了什么）。两处各写一份早晚说法不一致，
    /// 那就是 §五 里那类「面板说的一套、落地的一套」。</para>
    /// <para>老板拍过板之后这些行是<strong>改过之后的</strong>（<see cref="WithAnswer"/> 会改剔行名单、
    /// 有无列名行、张数列、模板来源列），所以念出来就是他认下的那一版，不是模型最初那一版。</para>
    /// </summary>
    public IReadOnlyList<string> DescribeUnderstanding()
    {
        var lines = new List<string>();
        if (Readout.DataCols is int cols) lines.Add($"真正有用的数据是 {cols} 列");
        lines.Add(HasHeader == false
            ? "这张表没有列名行，第一行就是货"
            : HeaderRow is int hr ? $"列名在原表第 {hr} 行" : "列名在第几行还没定");
        if (TotalValueRows.Count > 0)
            lines.Add("这些行不当货印（合计/批注行）：第 " + string.Join("、", TotalValueRows) + " 行");
        lines.Add(Readout.TemplateSource is { Length: > 0 } src
            ? $"标签上的字抄在表里 {src}"
            : "表里没有抄标签的那一块");
        if (Readout.QtyColumn is { Length: > 0 } qty) lines.Add($"出几张纸按「{qty}」这一列数");
        else lines.Add("出几张纸按哪一列数还没定");
        if (Mappings.Count > 0)
            lines.Add("字段绑定：" + string.Join("；", Mappings.Select(m => $"{m.ColumnHeader} 是{m.FieldName}")));
        if (Readout.PaperText is { Length: > 0 } paper) lines.Add($"表里自己写的纸那句话是「{paper}」");
        foreach (var fact in Facts.Take(5)) lines.Add("它看出来的：" + fact);
        return lines;
    }

    /// <summary>
    /// 折成切表指令。<paramref name="rawRowCount"/> 是原表总行数（用来把 1 起的行号换成 0 起并挡住越界）。
    /// <para>表头那一行永远从剔除名单里去掉：模型确实会一边说「表头在第 1 行」一边把第 1 行列进合计行。</para>
    /// <para><strong>落地只有这一条路</strong>：逐条确认（阶段 29 第 1 棒）也走它，只是在 App 侧按"被点名的类"
    /// 把结果合到当前切法上（见 <c>MainViewModel.ChoiceFrom(p, only)</c>）——Core 不再出一份"只落部分"的
    /// 平行实现，两套落地口径长得不一样正是 §五-62 那类"换路复发"的根。</para>
    /// </summary>
    public SheetLayoutChoice ToChoice(int rawRowCount)
    {
        var (rowIndex, hasHeader) = BuildHeaderSide(rawRowCount);
        return new SheetLayoutChoice(rowIndex, hasHeader, BuildExcluded(rawRowCount));
    }

    /// <summary>
    /// 表头那一侧折成 <see cref="SheetLayoutChoice"/> 要的两个值。
    /// <para>「首行不是列名」时 <c>HeaderRowIndex</c> 记 0（第 24 棒定的口径：第 0 行 = 第一行也当数据），
    /// 但**剔除名单的护栏要按"没有表头行"算**（没有哪一行需要被保护），两者不是同一个值，别合并。</para>
    /// </summary>
    private (int? RowIndex, bool HasHeader) BuildHeaderSide(int rawRowCount)
        => HasHeader == false
            ? (0, false)
            : (HeaderIndexForExclusion(rawRowCount), true);

    /// <summary>剔除名单里要挡住的表头行下标（0 起）；null = 没表头行可挡。</summary>
    private int? HeaderIndexForExclusion(int rawRowCount)
        => HasHeader == false ? null
            : HeaderRow is int hr ? Math.Clamp(hr - 1, 0, Math.Max(0, rawRowCount - 1)) : null;

    /// <summary>提案点名的剔除名单折成 0 起下标（越界的不理、表头行不剔、去重）。</summary>
    private IReadOnlyList<int>? BuildExcluded(int rawRowCount)
    {
        var headerIndex = HeaderIndexForExclusion(rawRowCount);
        var excluded = new List<int>();
        foreach (var row in TotalValueRows)
        {
            if (row < 1 || row > rawRowCount) continue;         // 越界的一律不理（Parse 里已经记过 Note）
            if (headerIndex is int hi && row - 1 == hi) continue;   // 表头自己不能又被剔掉
            if (!excluded.Contains(row - 1)) excluded.Add(row - 1);
        }
        return excluded.Count == 0 ? null : excluded;
    }

    /// <summary>
    /// 确认窗上的「会改这几件事」清单（一条一项，人点头前谁也不生效）。
    /// <para><strong>为什么字字都是大白话</strong>（2026-09-09 用户圈图反馈）：「你需要理解使用者不是技术人员，
    /// 他们不知道 rows 什么的需要简洁明了」。所以这里不出现 rows、JSON、字段英文名、
    /// 「数据行」这类口径；提醒与软件改动不挤在这一段里（见 <see cref="Explain"/>），
    /// 否则一屏全是「⚠」，人反而一个都不会去看。</para>
    /// </summary>
    public IReadOnlyList<string> DescribeItems(int rawRowCount)
    {
        var items = new List<string>();
        if (HasHeader == false) items.Add("这张表第一行不是列名，是货 —— 改成它也出一张标签（会多出 1 张）");
        else if (HeaderRow is int hr) items.Add($"列名按你说的算：在原来那张表的第 {hr} 行");
        if (TotalValueRows.Count > 0)
            items.Add("这几行不当货印（它们不是箱子，是合计或备注）：第 " + string.Join("、", TotalValueRows) + " 行");
        if (Layout is { } spec)
        {
            var lines = string.Join("；", spec.Rows.Select(r => r.Content));
            items.Add($"标签上印这几行：{Shrink(lines, 120)}（标签大小 {spec.WidthMm:0.#}×{spec.HeightMm:0.#} 毫米）");
        }
        if (SheetSpecName is not null) items.Add($"一张纸怎么摆：换成「{SheetSpecName}」这一张");
        else if (PaperWidthMm is double pw && PaperHeightMm is double ph)
        {
            var perRow = (Columns ?? 0) <= 0 ? "自己算" : Columns + " 张";
            var perPage = (Rows ?? 0) <= 0 ? "自己算" : Rows + " 行";
            var follow = FollowsLabel == true ? "（纸跟着标签走，一张纸一张标签）" : string.Empty;
            items.Add($"一张纸 {pw / 10:0.#}×{ph / 10:0.#} 厘米，每行摆 {perRow}、每页 {perPage}{follow}");
        }
        if (rawRowCount > 0 && TotalValueRows.Count > 0)
            items.Add($"改完之后会出 {Math.Max(0, rawRowCount - (HasHeader == false ? 0 : 1) - TotalValueRows.Count)} 张标签（以软件重切结果为准）");
        return items;
    }

    /// <summary>
    /// 把提案折成逐条「原值 → 新值」的改动清单（阶段 29 第 1 棒）。
    /// <para><strong>与 <see cref="DescribeItems"/> 只差一件事，但就是最要命的那件</strong>：那一份只说
    /// 「它会改成什么」，人没法判断该不该点头；这一份带 <paramref name="ctx"/> 里的原值，才审得动 ——
    /// 用户 2026-09-10 的原话是「AI 会弹出「修改 xxxx ✅/❌」这样的 UI 来确认取消」。</para>
    /// <para><strong>无变化不出卡</strong>：两边一样、或它这次压根没提这一类，都不进清单。卡上出现
    /// 「改成一样的东西」等于逼人白审一条（与 <see cref="AiSheetQuestion"/>「不点报成功其实没改」同一条口径）。</para>
    /// <para>纯函数、不抛：拿不到原值就显示「还没定」，绝不编一个数出来。</para>
    /// </summary>
    /// <param name="ctx">软件此刻的状态快照。null = 什么都不知道（原值一律显示成"还没定"）。</param>
    public IReadOnlyList<AiChange> DescribeChanges(AiChangeContext? ctx = null)
    {
        var c = ctx ?? AiChangeContext.Empty;
        var list = new List<AiChange>();

        // ── 列名在第几行（含「这张表首行不是列名」那一档）──
        var beforeHeader = c.HasHeader == false
            ? "这张表第一行不是列名"
            : c.HeaderRow is int chr ? $"列名在第 {chr} 行" : "列名行还没定";
        var afterHeader = HasHeader == false
            ? "这张表第一行不是列名（首行也当货印，会多出 1 张）"
            : HeaderRow is int phr ? $"列名在第 {phr} 行" : null;
        if (afterHeader is not null && !string.Equals(beforeHeader, afterHeader, StringComparison.Ordinal))
            list.Add(new AiChange(AiChangeKind.HeaderRow, "列名在第几行", beforeHeader, afterHeader));

        // ── 哪几行不当货印 ──
        // 落地的口径是**并集**（App 侧第 21 棒定死的：只会多剔，不会把已经剔掉的行放回来）。
        // 所以卡上的"新值"必须写并集之后**真会落成的那一份** —— 写模型的原始清单就是骗人，
        // 人点完 ✅ 会发现第 5 行怎么还剔着（阶段 29 那条「不许点了报成功其实改的不是它」）。
        var effectiveExcluded = UnionRows(c.ExcludedRows, TotalValueRows);
        if (!SameRows(c.ExcludedRows, effectiveExcluded))
            list.Add(new AiChange(AiChangeKind.ExcludedRows, "哪几行不当货印",
                DescribeRows(c.ExcludedRows), DescribeRows(effectiveExcluded)));

        // ── 标签版式（它会排出一版新的，存成模板）──
        if (Layout is { } spec)
        {
            var size = $"{spec.WidthMm:0.#} × {spec.HeightMm:0.#} mm";
            var beforeLayout = c.TemplateName is { Length: > 0 } name
                ? $"现在这张「{name}」"
                : c.LabelWidthMm is double bw && c.LabelHeightMm is double bh
                    ? $"现在这张 {bw:0.#} × {bh:0.#} mm"
                    : "现在这张（还不知道）";
            list.Add(new AiChange(AiChangeKind.Layout, "标签版式",
                beforeLayout, $"它排的新版式 {spec.Rows.Count} 行 · {size} · {DescribeSizes(spec)}"));
        }

        // ── 用哪张纸 ──
        if (SheetSpecName is { } specName
            && !string.Equals(c.SheetSpecName, specName, StringComparison.Ordinal))
        {
            list.Add(new AiChange(AiChangeKind.SheetSpec, "用哪张纸",
                c.SheetSpecName is { Length: > 0 } old ? old : "（软件自动挑一张）", specName));
        }

        // ── 字段绑定（第 30 棒）：一条卡一个字段 ──
        // 现在绑在哪一列由"原值"说清；已经绑在这一列的不出卡（无变化不出卡，同一条纪律）。
        foreach (var m in Mappings)
        {
            var bound = c.Bindings is { } known && known.TryGetValue(m.Field, out var old) ? old : null;
            if (bound is { Length: > 0 }
                && string.Equals(bound.Trim(), m.ColumnHeader.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            var after = m.ColumnHeader + "（" + HeaderRowDetector.ColumnLetter(m.ColumnIndex) + "列）";
            list.Add(new AiChange(AiChangeKind.FieldMapping, "字段绑定：" + m.FieldName,
                bound is { Length: > 0 } ? bound : "没绑", after, m));
        }

        return list;
    }

    /// <summary>剔除名单那句话（给人看的口径，不出现"下标"这种词）。</summary>
    private static string DescribeRows(IReadOnlyList<int>? rows)
        => rows is { Count: > 0 }
            ? "第 " + string.Join("、", rows) + " 行不印"
            : "没剔任何行（整张表都按货印）";

    /// <summary>
    /// 版式**逐行核对**：这一行填的是哪一列 / 哪个字段，对不上的当场标出来（第 32 棒）。
    /// <para><strong>为什么要有它</strong>：用户 2026-09-10 第二次复测的原话是「有些时候 AI 查看表格仍达不到效果，
    /// 当我把图片给他做出排版后，它应该联系上下文对新的排版方式校验（哪个位置填那一列数据）」——
    /// 他要的是"出完版式自己回头看一遍：这一格到底填哪一列"。这里先做**软件侧**那一半：
    /// 判据是**它自己报的绑定（<see cref="Mappings"/>）+ 真表头**，那正是"上下文"；
    /// 免费、确定性、不会编（真让模型再读一遍是另一件事，见 §三-阶段 32 的「不做」）。</para>
    /// <para>四种情形各自说清：对得上（写列表头与列字母）／引用了表里没有的列／引用了还没绑定的字段／
    /// 这一整行是固定文字（没引用任何数据）。</para>
    /// </summary>
    /// <param name="columns">真表头（判"这一列到底存不存在"）。null = 没有列画像，只报它引用了什么。</param>
    public IReadOnlyList<string> DescribeLayoutRows(IReadOnlyList<ColumnPortrait>? columns)
    {
        var lines = new List<string>();
        if (Layout is not { } spec) return lines;
        for (var i = 0; i < spec.Rows.Count; i++)
        {
            var used = new List<string>();
            foreach (System.Text.RegularExpressions.Match match in PlaceholderPattern.Matches(spec.Rows[i].Content))
                used.Add(DescribePlaceholder(match.Groups[1].Value.Trim(), columns));
            var head = $"第{i + 1}行";
            lines.Add(used.Count == 0
                ? $"{head}：固定文字（没引用任何数据）"
                : $"{head}：填 " + string.Join(" ＋ ", used));
        }
        return lines;
    }

    /// <summary>一行里的一个占位符 → 它到底要填哪一列（对不上就带 ⚠，不装作对得上）。</summary>
    private string DescribePlaceholder(string key, IReadOnlyList<ColumnPortrait>? columns)
    {
        // {{col:表头}} 是直取某一列，不经过字段
        if (key.StartsWith("col:", StringComparison.OrdinalIgnoreCase))
        {
            var header = key[4..].Trim();
            var hit = columns?.FirstOrDefault(c =>
                string.Equals(c.Header.Trim(), header, StringComparison.OrdinalIgnoreCase));
            if (columns is null || columns.Count == 0) return $"{header}（这次没拿到列清单，核不了）";
            return hit is null
                ? $"⚠「{header}」这一列表里没有"
                : $"{hit.Header}（{HeaderRowDetector.ColumnLetter(hit.Index)}列）";
        }

        // 字段键：先认字段，再看它这一次打算绑在哪一列
        var field = ResolveField(key);
        if (field is null) return $"⚠「{key}」认不出是哪个字段，也不是某一列";
        var name = MarkFieldCatalog.Get(field.Value).ChineseName;
        var bound = Mappings.FirstOrDefault(m => m.Field == field.Value);
        return bound is null
            ? $"⚠「{name}」这一次没说它读哪一列"
            : $"{name} → {bound.ColumnHeader}（{HeaderRowDetector.ColumnLetter(bound.ColumnIndex)}列）";
    }

    /// <summary>
    /// 版式那一行的**字号摘要**（第 31 棒）。
    /// <para>为什么必须写出来：用户 2026-09-10 说"AI 排版效果差，差在字体大小"，而卡片上**一个字号都没写**
    /// （只有"新版式 4 行 · 140 × 100 mm"）——人点 ✅ 之前根本看不见字号，只能印出来才发现，
    /// 这正是第 29/30 棒那条「原值 → 新值」要治的同一个毛病。</para>
    /// <para>撑满行的字号**由行高反算**（大字唛头就是这么来的），所以如实写"撑满"，不假装它等于某个 pt。
    /// 字号档数**不做限制**——用户的口径是"一个模板有多种字体字号"是常态（上大下小、右下角更小）。</para>
    /// </summary>
    private static string DescribeSizes(RowLayoutSpec spec)
    {
        var parts = spec.Rows.Select(r => r.Stretch ? "撑满" : $"{r.SizePt:0.#}pt").ToList();
        return "字号 " + string.Join(" / ", parts);
    }

    /// <summary>两份剔除名单是不是同一件事（顺序不同不算变，重复不算变）。</summary>
    private static bool SameRows(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
    {
        var x = a ?? Array.Empty<int>();
        var y = b ?? Array.Empty<int>();
        return x.Count == y.Count && !x.Except(y).Any();
    }

    /// <summary>两份剔除名单并起来（排好序）——落地口径就是并集，卡上也照它写。</summary>
    private static IReadOnlyList<int> UnionRows(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
        => (a ?? Array.Empty<int>()).Union(b ?? Array.Empty<int>()).OrderBy(i => i).ToList();

    /// <summary>
    /// 它自己说的话：提醒（去重、最多 <see cref="MaxExplainLines"/> 条）加一句理由。
    /// <para>与改动清单分开，是为了让人先看清「要改哪几件事」，再看「它担心什么」。</para>
    /// </summary>
    public IReadOnlyList<string> Explain()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        foreach (var w in Warnings)
        {
            var key = w.Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;
            unique.Add(Shrink(key, 90));
        }
        var list = unique.Take(MaxExplainLines).ToList();
        if (unique.Count > list.Count) list.Add($"（还有 {unique.Count - list.Count} 条提醒，点「复制全部」能看到）");
        // 第 31 棒：facts 已经逐条说过了，就不再播 reason —— 同一件事播两遍正是用户说"讲得混乱"的一半原因；
        // 而且那句原文是"一句说完"逼出来的长句，截断到 90 字还会在句子中间断开（用户截图里那截"…先按死文本处"）。
        if (Facts.Count == 0 && !string.IsNullOrWhiteSpace(Reason))
            list.Add("它的说法：" + Shrink(Reason.Trim(), 160));
        return list;
    }

    /// <summary>提醒最多列几条（多了等于没有，没人会逐字看）。</summary>
    public const int MaxExplainLines = 5;

    /// <summary>中文序号：一行、二行……（用户写模板那一句就是这个口径）。</summary>
    private static readonly string[] CnOrdinal = { "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };

    private static string Ordinal(int i) => i < CnOrdinal.Length ? CnOrdinal[i] + "行" : $"第 {i + 1} 行";

    /// <summary>
    /// 给人看的那五行——用户 2026-09-09 逐字指定的句式，一字不改地照办：
    /// 「表格有效数据31行4列 / 纸张:一开四--28*20--2*2--14*10 / 模版:F列:一行BOLAROM加粗居中,…
    /// 二行Item no：(A列)… / 张数:绑定B列 / 预览:31个模板,155张」。
    /// <para>行与张数用 <paramref name="labels"/>/<paramref name="sheets"/>（App 从真表算出来的），
    /// 不信模型报的数——它说 155 而表里加出来 160 时，错的那一个不能上屏。</para>
    /// </summary>
    public IReadOnlyList<string> SummaryLines(int? labels = null, int? sheets = null)
    {
        var lines = new List<string>
        {
            $"表格有效数据{labels?.ToString() ?? "?"}行{Readout.DataCols?.ToString() ?? "?"}列",
            "纸张:" + (Readout.PaperText ?? ComposePaper()),
        };
        lines.Add(Readout.TemplateLines.Count > 0
            ? "模版:" + (Readout.TemplateSource is { Length: > 0 } src ? src + ":" : string.Empty)
              + string.Join(",", Readout.TemplateLines.Select((t, i) => Ordinal(i) + t))
            : "模版:这次没给（它没在表里找到抄了标签文字的那一块）");
        lines.Add(Readout.QtyColumn is { } qty
            ? $"张数:绑定{HeaderRowDetector.ColumnLetter(Readout.QtyColumnIndex ?? 0)}列（{qty}）"
            : "张数:没说要按哪一列数");
        lines.Add($"预览:{labels?.ToString() ?? "?"}个模板,{sheets?.ToString() ?? "?"}张");
        return lines;
    }

    /// <summary>模型没给「纸张」那句展示文字时，软件拿自己知道的那些数拼一句（缺的就留缺，不编）。</summary>
    private string ComposePaper()
    {
        var parts = new List<string>();
        if (SheetSpecName is { } n) parts.Add(n);
        if (PaperWidthMm is double pw && PaperHeightMm is double ph) parts.Add($"{pw / 10:0.#}*{ph / 10:0.#}");
        if (Columns is > 0 || Rows is > 0)
            parts.Add($"{(Columns is > 0 ? Columns.ToString() : "自")}*{(Rows is > 0 ? Rows.ToString() : "自")}");
        if (Layout is { } l) parts.Add($"{l.WidthMm / 10:0.#}*{l.HeightMm / 10:0.#}");
        return parts.Count > 0 ? string.Join("--", parts) : "没说要换哪张纸";
    }

    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern =
        new(@"\{\{([^{}]+)\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 把一版模板翻成「一行 BOLAROM 加粗居中」「二行 Item no：(A列)」这种写法。
    /// <para>占位符翻成列字母是因为用户看 Excel 就看字母（他写的就是「Item no：(A列)」），
    /// 而 <c>{{ItemNo}}</c> 那种字段名只有工程师看得懂；认不出的占位符（自动编号那类）原样留着，
    /// 不假装它是某一列。</para>
    /// </summary>
    internal static IReadOnlyList<string> FriendlyTemplateLines(
        RowLayoutSpec spec, IReadOnlyList<ColumnPortrait>? columns)
    {
        var list = new List<string>();
        foreach (var row in spec.Rows)
        {
            var text = PlaceholderPattern.Replace(row.Content, m => ColumnHint(m.Groups[1].Value.Trim(), columns));
            var style = row.Align == HorizontalAlign.Center
                ? (row.Bold ? " 加粗居中" : " 居中")
                : row.Bold ? " 加粗" : string.Empty;
            list.Add(text.TrimEnd() + style);
        }
        return list;
    }

    private static string ColumnHint(string key, IReadOnlyList<ColumnPortrait>? columns)
    {
        if (columns is null) return "{{" + key + "}}";
        var name = key.StartsWith("col:", StringComparison.OrdinalIgnoreCase) ? key[4..].Trim() : key;
        var hit = columns.FirstOrDefault(c => string.Equals(c.Header, name, StringComparison.OrdinalIgnoreCase))
            ?? (key.StartsWith("col:", StringComparison.OrdinalIgnoreCase)
                ? null
                : columns.FirstOrDefault(c => string.Equals(c.BoundField, key, StringComparison.OrdinalIgnoreCase)));
        return hit is null ? "{{" + key + "}}" : $"({HeaderRowDetector.ColumnLetter(hit.Index)}列)";
    }

    private static string Shrink(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>模型爱用的同义键 → 我们的名字。认不出的宁可当作没提，也不猜一个意思相近的字段塞进去。</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hasHeader"] = "hasHeader", ["hasHeaderRow"] = "hasHeader", ["noHeader"] = "noHeader",
        ["没有表头"] = "noHeader", ["无表头"] = "noHeader",
        ["headerRow"] = "headerRow", ["headerLine"] = "headerRow", ["headerRowIndex"] = "headerRow", ["表头行"] = "headerRow",
        ["totalRows"] = "totalRows", ["totalRow"] = "totalRows", ["summaryRows"] = "totalRows", ["ignoreRows"] = "totalRows",
        ["skipRows"] = "totalRows", ["合计行"] = "totalRows",
        ["sheetSpec"] = "sheetSpec", ["paperSpec"] = "sheetSpec", ["纸规"] = "sheetSpec", ["sheet"] = "sheetSpec",
        ["paperWidthMm"] = "paperWidthMm", ["paperW"] = "paperWidthMm", ["纸张宽"] = "paperWidthMm",
        ["paperHeightMm"] = "paperHeightMm", ["paperH"] = "paperHeightMm", ["纸张高"] = "paperHeightMm",
        ["columns"] = "columns", ["cols"] = "columns", ["每行枚数"] = "columns", ["perRow"] = "columns",
        // 不叫 "rows"：那个键在版式那段里是「哪几行文字」，一个键两个意思以后必错（同 §五-62 那条形状）。
        ["rowsPerPage"] = "paperRows", ["每页行数"] = "paperRows", ["perSheetRows"] = "paperRows", ["paperRows"] = "paperRows",
        ["rows"] = "rows",
        ["followsLabel"] = "followsLabel", ["一页一枚"] = "followsLabel", ["paperFollowsLabel"] = "followsLabel",
        ["warnings"] = "warnings", ["risks"] = "warnings", ["提醒"] = "warnings", ["notes"] = "warnings",
        ["reason"] = "reason", ["why"] = "reason", ["理由"] = "reason",
        // 第 22 棒那五行新加的键（都是「给人看」那一侧，落地仍走上面那些老键）
        ["dataCols"] = "dataCols", ["有效列数"] = "dataCols", ["数据列数"] = "dataCols",
        ["templateSource"] = "templateSource", ["模版列"] = "templateSource", ["模板来源"] = "templateSource",
        ["qtyColumn"] = "qtyColumn", ["张数列"] = "qtyColumn", ["数量列"] = "qtyColumn", ["按列数张数"] = "qtyColumn",
        ["paperText"] = "paperText", ["纸张"] = "paperText",
        ["questions"] = "questions", ["问题"] = "questions", ["待确认"] = "questions",
        // 第 30 棒：字段绑定那一段。模型会写成 bindings / fields / 字段绑定，都收。
        ["mappings"] = "mappings", ["mapping"] = "mappings", ["bindings"] = "mappings",
        ["binding"] = "mappings", ["fields"] = "mappings", ["columnMapping"] = "mappings",
        ["字段绑定"] = "mappings", ["绑定"] = "mappings", ["列绑定"] = "mappings", ["字段"] = "mappings",
        // 第 31 棒：逐条事实（用户说原来那段"它的说法"讲得混乱，改成一条一件）。
        ["facts"] = "facts", ["fact"] = "facts", ["findings"] = "facts",
        ["事实"] = "facts", ["判断"] = "facts", ["要点"] = "facts", ["逐条"] = "facts",
    };

    /// <summary>
    /// 解析模型原文。<b>从不抛</b>：形状问题一律变成 <see cref="Errors"/> 里的一句人话。
    /// <para>行式版式那一段直接交给 <see cref="RowLayoutJsonParser.Parse"/>——同一份 JSON 两个读者，
    /// 不另写第二套清洗逻辑（两套一定会长得不一样，那是 §五-62 那类「换路复发」的根）。</para>
    /// </summary>
    /// <param name="modelText">模型回的那一段（带围栏或前后解释都收）。</param>
    /// <param name="columns">列画像，透传给版式解析（用来把 <c>{{col:列名}}</c> 对回真表头）。</param>
    /// <param name="rawRowCount">原表总行数（1 起口径的边界，用来挡越界行号）。</param>
    /// <param name="sheetSpecNames">软件里真有的纸规名（不在这份清单里的名字一律拒，见 <see cref="Errors"/>）。</param>
    /// <param name="cellFormats">
    /// 逐格量到的字号/粗体/居中（<see cref="XlsxTableReader.ReadCellFormats"/>），第 39 棒加。
    /// <strong>给了就由软件照它算标签上每行字该多大</strong>，不再用模型填的那几个数（见 <see cref="RowFormatEvidence"/>）；
    /// CSV 与量不到的场合传 null，照旧用模型填的。
    /// </param>
    /// <param name="stage">
    /// 这份回包是<strong>哪一步</strong>要来的（第 40 棒）。<see cref="AiProposalStage.Read"/> 只采纳理解与问题，
    /// <strong>排版字段一律不采纳</strong>——模型不听话硬给了 rows 与纸规，也只记一句 Note 说明"没理它"，
    /// 不让它落地（用户的红线是「这层先不要对预览纸张进行调整」，光靠提示词拦不住，解析层要再拦一道）。
    /// <see cref="AiProposalStage.Layout"/> 反过来：只要版式与纸规，理解字段缺失不算错（那一步已经问过了）。
    /// <para><strong>刻意不给默认值</strong>：给了默认（无论哪个）都会让漏改的调用点<em>静默</em>丢掉半边字段
    /// ——按 Read 解析就丢 rows 与纸规，按 Layout 解析就丢掉"这一步不许动模板"那道闸。
    /// 必填才能让编译器把每个调用点一次点出来。</para>
    /// </param>
    /// <param name="read">
    /// 排版阶段用：<strong>读表阶段那份提案</strong>。这一步的模型不必再重复报「模板抄在哪一列」「列名在第几行」，
    /// 可软件量字号偏偏要这两件（<see cref="RowFormatEvidence"/>），所以从上一步带过来——
    /// 不带就会重犯第 39 棒修过的坑：列名那一格混进标签那块、行数对不上、整块证据白量。
    /// </param>
    public static AiSheetProposal Parse(
        string? modelText,
        IReadOnlyList<ColumnPortrait>? columns,
        int rawRowCount,
        AiProposalStage stage,
        IReadOnlyList<string>? sheetSpecNames = null,
        IReadOnlyList<CellFormat>? cellFormats = null,
        AiSheetProposal? read = null)
    {
        var notes = new List<string>();
        var errors = new List<string>();

        var json = ExtractJsonObject(modelText);
        if (json is null)
            return Bad("它这次回的话里没有可执行的方案，什么都没改。你可以再点一次，或把图附上让它重看。");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            return Bad($"它回的那段方案没读完（格式不对），什么都没改：{ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Bad("它回的内容不是可执行的方案，什么都没改。");

            var fields = Normalize(doc.RootElement);

            // ── 表头 ──
            bool? hasHeader = BoolField(fields, "hasHeader");
            if (BoolField(fields, "noHeader") == true) hasHeader = false;
            var headerRow = IntField(fields, "headerRow");
            if (headerRow is int hr)
            {
                // 模型常给 0 起的下标。0 不可能是合法的 Excel 行号（那等于第 0 行），
                // 而它又确实想指第一行，所以 0 折算成 1 并说明——这比拒掉一条提案有用，也不静默。
                if (hr == 0) { headerRow = 1; notes.Add("它把行号写成 0 了，按第一行算（表里没有第 0 行）"); }
                if (hr > rawRowCount)
                {
                    notes.Add($"它说列名在表里第 {hr} 行，可这张表一共只有 {rawRowCount} 行 —— 这条没采纳");
                    headerRow = null;
                }
            }

            // ── 合计/批注行 ──
            var totalRows = new List<int>();
            foreach (var v in IntListField(fields, "totalRows"))
            {
                if (v <= 0) { notes.Add($"它给了一个不存在的行号（{v}），这条没采纳"); continue; }
                if (v > rawRowCount) { notes.Add($"它要去掉表里第 {v} 行，可这张表一共只有 {rawRowCount} 行 —— 这条没采纳"); continue; }
                if (!totalRows.Contains(v)) totalRows.Add(v);
            }
            if (headerRow is int h && totalRows.Contains(h))
            {
                totalRows.Remove(h);
                notes.Add($"第 {h} 行是列名那一行，不能又当合计行去掉 —— 这条没采纳");
            }

            // ── 版式（含标签尺寸）：交给那一份已有的解析 ──
            // 只有它真给了 rows 那段才拿版式的错去整份拒——提示词明说「没参照时把版式那段省略」，
            // 省略不是错，拿它当错等于把「只报事实」这份提案也拒了。
            var hasRows = fields.ContainsKey("rows");
            // 第 40 棒：**读表这一步不许动模板**。模型不听话硬给了 rows 也不解析——解析出来就会被落地，
            // 那就是用户圈的「乱改模板」。提示词里已经交代过分工，但模型会不听话，所以解析层再拦一道。
            RowLayoutProposal? layout = null;
            if (stage == AiProposalStage.Read)
            {
                if (hasRows)
                    notes.Add("它这一步就想改你的标签内容，我没理它——按你要的先只读表、只提问，模板一个字没动");
            }
            else
            {
                layout = hasRows ? RowLayoutJsonParser.Parse(modelText, columns) : null;
                if (layout is not null)
                {
                    foreach (var n in layout.Notes) notes.Add("标签内容：" + n);
                    foreach (var e in layout.Errors) errors.Add("标签内容：" + e);
                }
                else
                {
                    notes.Add("这次没重排你的标签内容（它没说标签上该印哪几行），模板还是你现在用的那张");
                }
            }

            // ── 字号/粗细/居中：由软件照表里量到的算，不用模型填的（第 39 棒）──
            // 必须排在下面 FriendlyTemplateLines 之前：那是把这份 spec 念给人听的一段话，
            // 先念后改就又是「面板说的一套、落地的一套」两张皮（§五 里这类账不止一笔）。
            // templateSource 也顺势提到这里读——原来它在末尾构造 SheetReadout 时才读，
            // 一份 JSON 里同一个键读两次，早晚有一处改了另一处忘。
            var templateSource = TextField(fields, "templateSource", 20) ?? read?.Readout.TemplateSource;
            if (layout?.Spec is { } evidenceSpec && cellFormats is { Count: > 0 })
            {
                var (_, templateColumn) = ResolveColumn(templateSource, columns);
                // 抄标签那块通常就贴在列名下面，连着量会多出一格（1 格列名 + 4 行标签读成 5 行），
                // 于是跟标签的 4 行对不上、整块证据白量。列名在第几行上面已经解析过了，
                // 传下去不是猜；这一步没报就从读表阶段那份里兜底（第 40 棒），两边都没有才传 -1
                // 让那边按"认不出就整块不改"走。
                var evidenceHeaderRow = headerRow ?? read?.HeaderRow;
                var evidenceHasHeader = hasHeader ?? read?.HasHeader;
                var headerIndex0 = evidenceHasHeader == false || evidenceHeaderRow is null
                    ? -1
                    : evidenceHeaderRow.Value - 1;
                RowFormatEvidence.TryApply(evidenceSpec, cellFormats, templateColumn ?? -1, notes, headerIndex0);
            }

            // ── 纸规：只能选真有的，或给一张合法的新纸 ──
            // 第 40 棒：读表阶段**一律不采纳**（用户的红线是「这层先不要对预览纸张进行调整」）。
            // 这里刻意**不往 errors 里加**：读表阶段报一句「你说的那张纸这台机器上没有」会把整份提案判成不可用，
            // 用户看到的就是「这次没采纳它的方案」——比当没看见更坏。忽略 + 记一句人话就够。
            string? specName = null;
            double? paperW = null, paperH = null;
            int? cols = null, rowsPerPage = null;
            bool? followsLabel = null;
            if (stage == AiProposalStage.Read)
            {
                if (fields.ContainsKey("sheetSpec") || fields.ContainsKey("paperWidthMm")
                    || fields.ContainsKey("paperHeightMm") || fields.ContainsKey("columns")
                    || fields.ContainsKey("paperRows") || fields.ContainsKey("followsLabel"))
                    notes.Add("它这一步就想换你的纸，我没理它——按你要的先只读表、只提问，纸张一个字没动");
            }
            else
            {
                specName = TextField(fields, "sheetSpec", 60);
                if (specName is not null && sheetSpecNames is { Count: > 0 })
                {
                    var hit = MatchSpec(specName, sheetSpecNames);
                    if (hit is null)
                        errors.Add($"你说的那张纸「{specName}」这台机器上没有，不能凭空造一张。" +
                                   $"现在能选的：{string.Join("、", sheetSpecNames.Take(8))}");
                    else if (!string.Equals(hit, specName, StringComparison.Ordinal))
                    {
                        notes.Add($"它写的纸规名字有点差，按你机器上那张「{hit}」算");
                        specName = hit;
                    }
                }

                paperW = DoubleField(fields, "paperWidthMm");
                paperH = DoubleField(fields, "paperHeightMm");
                foreach (var (val, name) in new[] { (paperW, "纸宽"), (paperH, "纸高") })
                {
                    if (val is null) continue;
                    if (val is < 30 or > 2000)
                        notes.Add($"{name} {val:0.#} 毫米不在合理范围（30~2000），这条没采纳");
                }
                if (paperW is < 30 or > 2000 || paperH is < 30 or > 2000) { paperW = null; paperH = null; }

                cols = Clamp(IntField(fields, "columns"), 0, 40, "每行枚数", notes);
                rowsPerPage = Clamp(IntField(fields, "paperRows"), 0, 40, "每页行数", notes);
                followsLabel = BoolField(fields, "followsLabel");
            }

            // ── 给人看的那五行：几列、模板抄在哪一块、按哪一列数张数、逐条问题 ──
            var dataCols = Clamp(IntField(fields, "dataCols"), 1, 200, "有效列数", notes);
            var qtyWanted = TextField(fields, "qtyColumn", 40);
            var (qtyHeader, qtyIndex) = ResolveColumn(qtyWanted, columns);
            if (qtyWanted is not null && qtyHeader is null)
                notes.Add($"它说按「{qtyWanted}」这一列数张数，可表里没对上这一列 —— 这条没采纳");

            var mappings = ParseMappings(fields, columns, notes);
            // 第 40 棒（补）：排版这一步**不提问**。用户实测：答完第一遍问题，第二步又把同一条问题吐了回来，
            // 于是面板再弹一次——「答了还问」。提示词里写了「第二步不提问」，但模型会不听话，解析层再拦一道：
            // Layout 阶段一律不采纳 questions，软件兜底的「* 号那条」也只在读表阶段问（那时才该问、那时还没版式）。
            List<AiSheetQuestion> questions;
            if (stage == AiProposalStage.Read)
            {
                questions = ParseQuestions(fields, rawRowCount, notes);
                // 第 34 棒：软件兜底问那条该问的（货号列里带 * 就问"* 后那截留不留"）。
                // 第 40 棒：读表阶段就要问出来（那时候还没有版式），答复记在提案上，排版时由 MergeLayout 落。
                AddMissingTailQuestion(questions, columns, mappings, notes);
            }
            else
            {
                questions = new List<AiSheetQuestion>();
                if (fields.ContainsKey("questions"))
                    notes.Add("它这一步又想提问，我没理它——排版这一步不提问，还有疑问它会写进上面的「警告」里");
            }

            return new AiSheetProposal(
                headerRow, hasHeader, totalRows, layout?.Spec, specName,
                paperW, paperH, cols, rowsPerPage, followsLabel,
                StringListField(fields, "warnings"), TextField(fields, "reason", 400),
                notes, errors,
                new SheetReadout(dataCols, templateSource,
                    layout?.Spec is { } spec ? FriendlyTemplateLines(spec, columns) : Array.Empty<string>(),
                    qtyHeader, qtyIndex, TextField(fields, "paperText", 60)),
                questions,
                mappings,
                StringListField(fields, "facts"));
        }

        AiSheetProposal Bad(string why) => new(null, null, Array.Empty<int>(), null, null,
            null, null, null, null, null, Array.Empty<string>(), null, Array.Empty<string>(),
            new[] { why }, SheetReadout.Empty, Array.Empty<AiSheetQuestion>(),
            Array.Empty<AiFieldBinding>(), Array.Empty<string>());
    }

    /// <summary>
    /// 解它报的**字段绑定**（第 30 棒）。两道校验缺一不可：**字段必须命中 19 个标准字段白名单、列必须能对回这张表真有的列**。
    /// <para>认不出的一律只留一句 Note 并丢掉那一条——<strong>猜错一列就是数错张数、印错货</strong>，
    /// 与 <see cref="ResolveColumn"/>「宁可不写那一句」是同一条口径。同一个字段报了两列只留第一条。</para>
    /// <para>没有列画像时一条都不采纳：拿列字母瞎对等于替用户把错误的绑定签了字。</para>
    /// </summary>
    private static IReadOnlyList<AiFieldBinding> ParseMappings(
        Dictionary<string, JsonElement> fields,
        IReadOnlyList<ColumnPortrait>? columns,
        List<string> notes)
    {
        var list = new List<AiFieldBinding>();
        if (!fields.TryGetValue("mappings", out var el) || el.ValueKind != JsonValueKind.Array) return list;
        if (columns is null || columns.Count == 0)
        {
            notes.Add("它报了字段绑定，但这次没拿到这张表的列清单，没法核对 —— 一条都没采纳");
            return list;
        }
        var taken = new HashSet<MarkFieldKey>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var f = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in item.EnumerateObject()) f[prop.Name] = prop.Value;
            string? Get(params string[] keys) => keys
                .Select(k => f.TryGetValue(k, out var v) ? ReadText(v)?.Trim() : null)
                .FirstOrDefault(t => !string.IsNullOrEmpty(t));

            var columnWanted = Get("column", "col", "列", "列名", "列字母");
            var fieldWanted = Get("field", "key", "字段", "字段名");
            if (columnWanted is null || fieldWanted is null) continue;

            var field = ResolveField(fieldWanted);
            if (field is null)
            {
                notes.Add($"它说「{Shrink(columnWanted, 12)}」这一列是「{Shrink(fieldWanted, 16)}」，"
                          + "可这不是个标准唛头字段 —— 这条没采纳");
                continue;
            }
            if (!taken.Add(field.Value))
            {
                notes.Add($"「{MarkFieldCatalog.Get(field.Value).ChineseName}」它说了不止一列，只按第一条算");
                continue;
            }
            var (header, index) = ResolveColumn(columnWanted, columns);
            if (header is null || index is null)
            {
                notes.Add($"它说「{MarkFieldCatalog.Get(field.Value).ChineseName}」读「{Shrink(columnWanted, 16)}」，"
                          + "可表里没对上这一列 —— 这条没采纳");
                taken.Remove(field.Value);      // 没采纳就把名额放回去，后面那条还有机会
                continue;
            }
            list.Add(new AiFieldBinding(field.Value, index.Value, header, Get("reason", "理由", "why")));
        }
        return list;
    }

    /// <summary>
    /// 把模型口里的字段名解析成 <see cref="MarkFieldKey"/>：**只认白名单里的那 19 个**。
    /// <para>先按枚举名（<c>ItemNo</c>，走 <see cref="MarkFieldCatalog.TryParseKey"/>），
    /// 再按中文名 / 英文标记 / 别名——用户看 Excel 就说"货号"，模型也常这么写；中文名还常写成
    /// 「货号/款号」这种两说，所以拆开逐段再比一轮。</para>
    /// <para>认不出就返回 null：<strong>宁可丢一条，也不猜一个"意思相近"的字段</strong>。</para>
    /// </summary>
    internal static MarkFieldKey? ResolveField(string? wanted)
    {
        if (MarkFieldCatalog.TryParseKey(wanted, out var direct)) return direct;
        var text = wanted?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var squeezed = text.Replace(" ", string.Empty);
        foreach (var d in MarkFieldCatalog.Mappable)
        {
            if (string.Equals(d.ChineseName, text, StringComparison.OrdinalIgnoreCase)) return d.Key;
            if (string.Equals(d.ChineseName.Replace(" ", string.Empty), squeezed, StringComparison.OrdinalIgnoreCase)) return d.Key;
            if (string.Equals(d.EnglishLabel, text, StringComparison.OrdinalIgnoreCase)) return d.Key;
            foreach (var alias in d.Aliases)
                if (string.Equals(alias.Replace(" ", string.Empty), squeezed, StringComparison.OrdinalIgnoreCase)) return d.Key;
        }
        foreach (var d in MarkFieldCatalog.Mappable)
            foreach (var part in d.ChineseName.Split('/', '|', '｜', '、'))
                if (string.Equals(part.Trim(), squeezed, StringComparison.OrdinalIgnoreCase)) return d.Key;
        return null;
    }

    /// <summary>同义词→认得的动作（模型会写 keepRow、也会写「重排」）。第 40 棒补上读表阶段那五个。</summary>
    private static readonly Dictionary<string, string> ActionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["row-keep"] = AiSheetQuestion.ActionRowKeep, ["keeprow"] = AiSheetQuestion.ActionRowKeep,
        ["keep-row"] = AiSheetQuestion.ActionRowKeep, ["row"] = AiSheetQuestion.ActionRowKeep,
        ["droprow"] = AiSheetQuestion.ActionRowKeep, ["这一行"] = AiSheetQuestion.ActionRowKeep,
        ["totalrow"] = AiSheetQuestion.ActionRowKeep, ["合计行"] = AiSheetQuestion.ActionRowKeep,
        ["retemplate"] = AiSheetQuestion.ActionRetemplate, ["re-template"] = AiSheetQuestion.ActionRetemplate,
        ["template"] = AiSheetQuestion.ActionRetemplate, ["重排"] = AiSheetQuestion.ActionRetemplate,
        ["paper"] = AiSheetQuestion.ActionPaper, ["sheet"] = AiSheetQuestion.ActionPaper,
        ["换纸"] = AiSheetQuestion.ActionPaper,
        ["itemno-tail"] = AiSheetQuestion.ActionItemNoTail, ["tail"] = AiSheetQuestion.ActionItemNoTail,
        ["itemno"] = AiSheetQuestion.ActionItemNoTail, ["星号"] = AiSheetQuestion.ActionItemNoTail,
        // ── 第 40 棒：读表阶段的五类风险。模型写不中这些别名，那条问题就被丢掉，白名单等于没给 ──
        ["qty-column"] = AiSheetQuestion.ActionQtyColumn, ["qtycolumn"] = AiSheetQuestion.ActionQtyColumn,
        ["qty"] = AiSheetQuestion.ActionQtyColumn, ["sheetscolumn"] = AiSheetQuestion.ActionQtyColumn,
        ["哪列数张数"] = AiSheetQuestion.ActionQtyColumn, ["张数列"] = AiSheetQuestion.ActionQtyColumn,
        ["template-source"] = AiSheetQuestion.ActionTemplateSource,
        ["templatesource"] = AiSheetQuestion.ActionTemplateSource,
        ["sourcecolumn"] = AiSheetQuestion.ActionTemplateSource,
        ["模板在哪列"] = AiSheetQuestion.ActionTemplateSource, ["抄在哪列"] = AiSheetQuestion.ActionTemplateSource,
        ["header-row"] = AiSheetQuestion.ActionHeaderRow, ["headerrow"] = AiSheetQuestion.ActionHeaderRow,
        ["header"] = AiSheetQuestion.ActionHeaderRow, ["noheader"] = AiSheetQuestion.ActionHeaderRow,
        ["列名行"] = AiSheetQuestion.ActionHeaderRow, ["表头"] = AiSheetQuestion.ActionHeaderRow,
        ["fixed-value"] = AiSheetQuestion.ActionFixedValue, ["fixedvalue"] = AiSheetQuestion.ActionFixedValue,
        ["fixed"] = AiSheetQuestion.ActionFixedValue, ["死字"] = AiSheetQuestion.ActionFixedValue,
        ["column-meaning"] = AiSheetQuestion.ActionColumnMeaning, ["columnmeaning"] = AiSheetQuestion.ActionColumnMeaning,
        ["column"] = AiSheetQuestion.ActionColumnMeaning, ["这一列是什么"] = AiSheetQuestion.ActionColumnMeaning,
    };

    /// <summary>
    /// **软件兜底问那条该问的**（第 34 棒）：货号那一列里带 <c>*</c>，就问"* 号后面那截留不留"。
    /// <para>为什么要有它：用户 2026-09-10 的抱怨是「**\* 号后删不删也不问**」——这条问题第 22 棒就定成了
    /// 标准动作（<c>itemno-tail</c>），但"要不要问"全看模型心情；它这次没问，用户就什么都被没问到，
    /// 而货号里那截 <c>*16</c> 该不该印上纸是**必须有人拍板**的事（印错了是印错货）。</para>
    /// <para>改成本地**确定性判定**：货号那列的样例里真出现 <c>*</c> 就问，哪怕模型没提。</para>
    /// <para><strong>第 40 棒去掉了「先有版式才问」这个前置</strong>：两阶段拆分后读表阶段本来就没有版式，
    /// 而这条恰恰是用户点名要问的（「*号后面的是否保留」）。答复先记在提案上（<see cref="AiAnswer"/>），
    /// 等排版那一步版式出来时由 <see cref="MergeLayout"/> 确定性地把占位符换掉——不是再问模型一遍。</para>
    /// <para>模型已经问过就不重复问。</para>
    /// </summary>
    private static void AddMissingTailQuestion(
        List<AiSheetQuestion> questions,
        IReadOnlyList<ColumnPortrait>? columns,
        IReadOnlyList<AiFieldBinding> mappings,
        List<string> notes)
    {
        if (questions.Any(q => q.Action == AiSheetQuestion.ActionItemNoTail)) return;
        if (columns is null || columns.Count == 0) return;

        // 货号那一列：优先用它自己报的绑定；没绑就找样例里带 * 的那一列（模型没绑也兜得住）。
        var itemNoIndex = mappings.FirstOrDefault(m => m.Field == MarkFieldKey.ItemNo)?.ColumnIndex;
        var column = itemNoIndex is int idx
            ? columns.FirstOrDefault(c => c.Index == idx)
            : columns.FirstOrDefault(c => c.Samples.Any(s => s.Contains('*')));
        if (column is null) return;
        if (!column.Samples.Any(s => s.Contains('*'))) return;   // 没有 * 就别拿一条没头没脑的问题去烦人

        questions.Add(new AiSheetQuestion(
            $"{HeaderRowDetector.ColumnLetter(column.Index)} 列的货号里带 * 号，* 号和后面那一截要不要印？",
            "不用", "要", AiSheetQuestion.ActionItemNoTail, 0, null));
        notes.Add($"{HeaderRowDetector.ColumnLetter(column.Index)} 列的货号里带 * 号，软件替你补问了一条（模型这次没问）");
    }

    /// <summary>
    /// 解「要人二选一」那一段。接不住的动作不假装能办：不进问题列表，只留一句 Note 说清楚。
    /// <para>行号越界、没给动作、超过 <see cref="MaxExplainLines"/> 条，都是丢掉那一条而不是拒整份提案——
    /// 一条问不对不该连带把切表与换纸也挡掉。</para>
    /// </summary>
    private static List<AiSheetQuestion> ParseQuestions(
        Dictionary<string, JsonElement> fields, int rawRowCount, List<string> notes)
    {
        var list = new List<AiSheetQuestion>();
        if (!fields.TryGetValue("questions", out var el) || el.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var f = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in item.EnumerateObject()) f[prop.Name] = prop.Value;
            string? Get(params string[] keys) => keys
                .Select(k => f.TryGetValue(k, out var v) ? ReadText(v)?.Trim() : null)
                .FirstOrDefault(t => !string.IsNullOrEmpty(t));

            var text = Get("text", "问题", "题面", "q");
            if (string.IsNullOrEmpty(text)) continue;
            var rawAction = Get("action", "动作", "do") ?? string.Empty;
            if (!ActionAliases.TryGetValue(rawAction.Replace(" ", string.Empty), out var action))
            {
                notes.Add($"它问的「{Shrink(text, 24)}」这条我接不住（软件里没有对应的开关），只当提醒告诉你一声");
                continue;
            }
            var row = Get("row", "行", "行号") is { } rowText
                && int.TryParse(rowText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rv) ? rv : 0;
            if (action == AiSheetQuestion.ActionRowKeep && (row < 1 || row > rawRowCount))
            {
                notes.Add($"它问的那一行号不在表里（{row}），这条没采纳");
                continue;
            }
            if (list.Count >= MaxExplainLines)
            {
                notes.Add($"它提的问题多于 {MaxExplainLines} 条，只列前 {MaxExplainLines} 条给你选（其余在「复制全部」里能看到原文）");
                break;
            }
            list.Add(new AiSheetQuestion(
                text, Get("no", "❌", "否") ?? "不用", Get("yes", "✅", "是") ?? "要",
                action, row, Get("value", "值", "参数")));
        }
        return list;
    }

    /// <summary>
    /// 把人/模型口里的列指法翻成表里真存在的那一列：字母（B / B列）、序号（2）、表头文字都收。
    /// <para>对不上就返回 null（宁可不写那一句），因为猜错一列 = 数错张数，比不说更坏。</para>
    /// </summary>
    internal static (string? Header, int? Index) ResolveColumn(string? wanted, IReadOnlyList<ColumnPortrait>? columns)
    {
        var text = wanted?.Trim().TrimEnd('列', ' ').Trim();
        if (string.IsNullOrEmpty(text) || columns is null || columns.Count == 0) return (null, null);
        if (text.Length <= 2 && text.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'))
        {
            var idx = 0;
            foreach (var c in text.ToUpperInvariant()) idx = idx * 26 + (c - 'A' + 1);
            var hit = columns.FirstOrDefault(c => c.Index == idx - 1);
            return hit is null ? (null, null) : (hit.Header, hit.Index);
        }
        if (int.TryParse(text, out var n) && n is >= 1 and <= 200)
        {
            var hit = columns.FirstOrDefault(c => c.Index == n - 1);
            if (hit is not null) return (hit.Header, hit.Index);
        }
        var squeezed = text.Replace(" ", string.Empty);
        var byHeader = columns.FirstOrDefault(c =>
                string.Equals(c.Header.Replace(" ", string.Empty), squeezed, StringComparison.OrdinalIgnoreCase))
            ?? columns.FirstOrDefault(c => c.Header.Contains(squeezed, StringComparison.OrdinalIgnoreCase));
        return byHeader is null ? (null, null) : (byHeader.Header, byHeader.Index);
    }

    private static int? Clamp(int? value, int min, int max, string what, List<string> notes)
    {
        if (value is null) return null;
        if (value < min || value > max)
        {
            notes.Add($"{what} {value} 不在合理范围（{min}~{max}），这条没采纳");
            return null;
        }
        return value;
    }

    /// <summary>纸规名的宽松匹配：去空格后精确 → 互相包含。认不出就返回 null（拒，不猜）。</summary>
    private static string? MatchSpec(string wanted, IReadOnlyList<string> names)
    {
        var w = wanted.Replace(" ", string.Empty);
        foreach (var n in names)
            if (string.Equals(n.Replace(" ", string.Empty), w, StringComparison.OrdinalIgnoreCase)) return n;
        foreach (var n in names)
            if (n.Contains(w, StringComparison.OrdinalIgnoreCase) || w.Contains(n, StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    private static string? ExtractJsonObject(string? text)
        // 第 23 棒:改用按深度配平的扫描(与 RowLayoutJsonParser 同一口径)。
        // 旧的「第一个 { 到最后一个 }」会被散文里的花括号毒死——模型回「建议{注意}:…{真JSON}」时整份提案变 Bad。
        => RowLayoutJsonParser.ExtractJsonObject(text);

    private static Dictionary<string, JsonElement> Normalize(JsonElement root)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.EnumerateObject())
            if (Aliases.TryGetValue(prop.Name, out var key)) map[key] = prop.Value;
        return map;
    }

    private static bool? BoolField(Dictionary<string, JsonElement> f, string key)
    {
        if (!f.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b
                : el.GetString()?.Trim() is "1" or "yes" or "true" ? true
                : el.GetString()?.Trim() is "0" or "no" or "false" ? false : (bool?)null,
            JsonValueKind.Number => el.TryGetInt32(out var n) ? n != 0 : null,
            _ => null,
        };
    }

    private static int? IntField(Dictionary<string, JsonElement> f, string key)
    {
        if (!f.TryGetValue(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        var text = ReadText(el);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? DoubleField(Dictionary<string, JsonElement> f, string key)
    {
        if (!f.TryGetValue(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        var text = ReadText(el);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? ReadText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => el.GetRawText(),
        _ => null,
    };

    private static IReadOnlyList<int> IntListField(Dictionary<string, JsonElement> f, string key)
    {
        var list = new List<int>();
        if (!f.TryGetValue(key, out var el)) return list;
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (int.TryParse(ReadText(item), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) list.Add(v);
            return list;
        }
        // 也收 "412,413" 这种把数组写成一句话的（模型真的会这么干）
        foreach (var part in (ReadText(el) ?? string.Empty).Split(new[] { ',', '，', '、', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) list.Add(v);
        return list;
    }

    private static IReadOnlyList<string> StringListField(Dictionary<string, JsonElement> f, string key)
    {
        var list = new List<string>();
        if (!f.TryGetValue(key, out var el)) return list;
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var text = ReadText(item)?.Trim();
                if (!string.IsNullOrEmpty(text) && text.Length > 1) list.Add(text.Length > 200 ? text[..200] + "…" : text);
            }
            return list;
        }
        var one = ReadText(el)?.Trim();
        if (!string.IsNullOrEmpty(one)) list.Add(one.Length > 200 ? one[..200] + "…" : one);
        return list;
    }

    private static string? TextField(Dictionary<string, JsonElement> f, string key, int maxChars)
    {
        if (!f.TryGetValue(key, out var el)) return null;
        var text = ReadText(el)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > maxChars ? text[..maxChars] + "…" : text;
    }
}
