using System.Text;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 「让 AI 读这张表并给一份提案」那条请求的提示词（第 21 棒）。
/// <para>放在 Core 而不是写在界面里，理由跟行式版式那段提示词（<c>RowLayoutPrompt</c>）一样：
/// 这段文字决定模型会不会回一份能解析的东西，它必须能被单测钉住，而不是藏在某个按钮的事件里。</para>
/// <para><strong>为什么现在才走这条路</strong>：用户 2026-09-09 的原话是
/// 「先让软件内的 AI 查看表格而不是根据程序写的自动来」+「把权限都给 AI，因为手动调整效果很差」。
/// 所以这里明确把三件事的裁量权交出去（表头行、合计行、整张纸），
/// 同时把软件能接受的形状写死（行号口径、纸规只能从清单里选、不许写任意毫米数）。</para>
/// </summary>
public static class AiSheetProposalPrompt
{
    /// <summary>系统那一句：只要方案，而且要说大白话（看的人是不懂技术的打印店老板）。</summary>
    public const string SystemText =
        "你是外贸纸箱唛头排版的现场工程师。你看得到整张表（含列名以上的批注、右侧贴的效果图）。" +
        "只输出一个 JSON 对象，不要解释文字、不要 Markdown 围栏。没把握的字段就省略，不要编。" +
        "读你回答的人是不懂电脑的打印店老板：questions 里的问题与 reason 一律用中文大白话，" +
        "不要出现 rows、JSON、字段英文名、毫米坐标这类术语，也不要用技术报告的句式（写成「最后一行像合计，建议不印」而不是「第 N 行为 summary row」）。";

    /// <summary>
    /// 组装用户那一句。
    /// </summary>
    /// <param name="portrait">整张表的画像（含批注行与贴图清单），来自 <see cref="TablePortrait.Describe"/>。</param>
    /// <param name="sheetSpecNames">这台机器上真有的纸规名（内置 + 用户自建）。模型只能从这份清单里点名。</param>
    /// <param name="rawRowCount">原表总行数（1 起口径的边界）。</param>
    /// <param name="detectedHeaderRow">软件目前猜的表头行（1 起），0 表示还没切过。</param>
    /// <param name="currentLabelSizeText">当前模板尺寸那句人话（如「140×100 mm」），让模型知道改的是什么。</param>
    /// <param name="imageCount">这条消息随附几张图（0 时要它老实说没参照，不许凭列名编设计）。</param>
    public static string Build(
        string portrait,
        IReadOnlyList<string> sheetSpecNames,
        int rawRowCount,
        int detectedHeaderRow,
        string currentLabelSizeText,
        int imageCount,
        IReadOnlyList<string>? decisions = null)
    {
        var sb = new StringBuilder();
        // 第 35 棒：**老板拍过板的决定必须回到模型手里**（这就是他要的"将信息回馈给你"）。
        // 以前提案那条路一个字的上下文都不带，于是他答完问题，模型下一轮看到的还是原来那张表那句话——
        // "选择了也是无效的"就是这么来的。
        if (decisions is { Count: > 0 })
        {
            sb.Append("**老板已经就下面这些拍过板了（照这些来，不要再问一遍，也不要再按相反的做）：**\n");
            foreach (var d in decisions.Take(12)) sb.Append("  - ").Append(d).Append('\n');
            sb.Append('\n');
        }
        sb.Append("任务：看下面这张表，给出「每一列是什么字段、这张表该怎么切、这张纸该怎么摆」的提案。\n\n");
        sb.Append(portrait).Append('\n');
        sb.Append("\n原表一共 ").Append(rawRowCount).Append(" 行；软件目前把表头猜在第 ")
          .Append(detectedHeaderRow > 0 ? detectedHeaderRow.ToString() : "？").Append(" 行；当前标签尺寸 ")
          .Append(currentLabelSizeText).Append("。\n");
        sb.Append(imageCount > 0
            ? $"本条随附 {imageCount} 张图（表里贴的效果图/模板截图或用户拍的样张）：标签上印哪几行照图上的行序与字面，图上没有的行不要造。\n"
            : "本条没附图。但表里常常自己就带着模板：某一列或某一块把标签上的字抄了一遍\n"
              + "（例如右侧几行出现 BOLAROM / Item no：olu830-35 / QTY：144 pcs / Ctns：5件 这种）。\n"
              + "有这一块 → 它就是模板（文字参照，和附图同等待遇）：抄了哪几行、哪行加粗居中、哪行其实是某一列的值（写成占位符），都照它给出 rows，"
              + "同时说明它抄在哪一列（templateSource）。\n"
              + "就算这一块有几行被截断或看不清，也照你看见的那几行给出 rows，在 reason 里说哪几行没看清——不要因为它看不全就省略 rows。\n"
              + "表里确实没有这一块 → rows 省略，只报你在表里看到的事实，不要凭列名编设计。\n");

        // 第 30 棒：AI 模式下"由 AI 绑定"是**主动作**（用户的原话：导入后不该先绑定，先让 AI 理解再绑），
        // 所以这一段要写得比版式那段更硬：按列里的真实内容判，拿不准宁可少报。
        sb.Append("\n最重要的一件事：**指出每一列是什么字段**（mappings）。\n")
          .Append("  列名常常写成「货号 ITEM NO:」「毛重G.W.(kg)」这种中英混排，也可能是纯英文、缩写，或者根本不着调——\n")
          .Append("  要按列里的**真实内容**判断它是什么，别只看列名。\n")
          .Append("  field 只能从下面「字段清单」里选。拿不准的列**不要硬塞**：软件宁可少一个绑定，也不要错一个——\n")
          .Append("  错一列就是数错张数、印错货，老板要按这一列出纸。\n");

        sb.Append("\n只回这样一个 JSON 对象（字段可省略，行号一律用**原表行号、从 1 起**，与人看 Excel 的口径一致）：\n");
        sb.Append("{\n");
        sb.Append("  \"dataCols\": 4,                        // 真正有用的数据几列（不算空白列、不算抄模板那一块）\n");
        sb.Append("  \"mappings\": [                        // 哪一列是哪个字段：column 写列字母/列号/表头原样都行，field 只能写字段清单里的键\n");
        sb.Append("    { \"column\": \"B\", \"field\": \"CartonTotal\" },\n");
        sb.Append("    { \"column\": \"毛重G.W.(kg)\", \"field\": \"GrossWeight\" } ],\n");
        sb.Append("  \"paperText\": \"一开四--28*20--2*2--14*10\", // 纸那一句怎么写给人看：名字--纸厘米--每行*每页--标签厘米\n");
        sb.Append("  \"templateSource\": \"F列\",             // 模板抄在哪一列/哪一块\n");
        sb.Append("  \"qtyColumn\": \"B\",                   // 每个货出几张纸按哪一列数（写列字母、列号或表头原样都行）\n");
        sb.Append("  \"hasHeader\": true 或 false,          // false = 这张表没有列名行，第一行也是货\n");
        sb.Append("  \"headerRow\": 1,                      // 列名在原表第几行\n");
        sb.Append("  \"totalRows\": [34],                   // 表尾「合计/TOTAL/小计」这类不该出标签的行，原表行号\n");
        sb.Append("  \"sheetSpec\": \"一页一枚（纸面跟标签走）\", // 只能从下面清单里原样选一个名字\n");
        sb.Append("  \"questions\": [                      // 你拿不准、要老板拍一下的（最多 5 条，没把握才问，不要把确定的事拿来问）\n");
        sb.Append("    { \"text\": \"件数末尾总数155\", \"no\": \"不需要\", \"yes\": \"需要\", \"action\": \"row-keep\", \"row\": 34 },\n");
        sb.Append("    { \"text\": \"目前用的模板与表格相近，要不要重新排版\", \"action\": \"retemplate\" },\n");
        sb.Append("    { \"text\": \"货号里*号及后面要不要保留\", \"action\": \"itemno-tail\" } ],\n");
        sb.Append("  \"facts\": [                            // 你从这张表看出来的判断，**一条只说一件事**（最多 5 条）\n");
        sb.Append("    \"第1列是货号\", \"第2列是每箱数量\", \"第3列是这票总共几箱（按它算纸张数）\",\n");
        sb.Append("    \"F列那4行是标签上印什么字的样例抄写\" ],\n");
        sb.Append("  \"reason\": \"为什么这么判（可省：facts 已经说清了就别再说一遍）\",\n");
        sb.Append("  \"rows\": [ { \"content\": \"Item no：{{col:货号ITEM NO:}}\" },\n");
        sb.Append("             { \"content\": \"QTY：{{col:每箱数量}} pcs\" } ]  // 模板逐行：只写**印什么字**，顺序照标签上从上到下；{{字段}} 用下面清单里的键，要某列原样写 {{col:表头原样}}\n");
        sb.Append("}\n");
        sb.Append("action 只能用这四个（写别的软件接不住，会被当成一句提醒丢掉）：\n")
          .Append("  row-keep（那一行要不要当货印，要带 row）/ retemplate（要不要按你给的 rows 重排模板）/\n")
          .Append("  paper（要不要换成你点的那张纸）/ itemno-tail（货号里 * 后面那截留不留）。\n");
        sb.Append("问题与方案要配套：questions 里问「要不要重排」（retemplate）就必须同时给出 rows——\n")
          .Append("老板点了「要」而你没有 rows，软件只能报一句空话；给不出 rows 就别问这条，把事实写在 reason 里。\n");
        // 第 34 棒：用户 2026-09-10 的抱怨是「思考完回答啥也没做」——后台它想了一大段（表格里某一列混了几样东西），
        // 但最终回的那段 JSON 里 facts 与 questions 都是空的，软件就没有任何东西摆给他看，只剩一句"它没给可执行的改动"。
        sb.Append("**哪怕你一个字段都不改，也必须回 facts**（你从这张表看出来的判断，一条一件，最多 5 条）。")
          .Append("表里有毛病（某列混了几样东西、缺列、列名对不上、数字列里有文字）就写成 questions 让老板拍板——")
          .Append("**只写在思考过程里等于没给**：软件只把你最终回的那段 JSON 摆给老板看。\n");

        if (sheetSpecNames.Count > 0)
        {
            sb.Append("\n纸规清单（只能选这些名字，写别的会被拒）：\n");
            foreach (var name in sheetSpecNames.Take(20)) sb.Append("  - ").Append(name).Append('\n');
        }
        sb.Append("\n字段清单（mappings 的 field 与 rows 里的 {{键}} 都只能用这些）：\n  ");
        sb.Append(string.Join(" ", MarkFieldCatalog.Mappable.Select(d => d.Key + "(" + d.ChineseName + ")"))).Append('\n');
        sb.Append("硬性约束：行号必须在 1~").Append(rawRowCount).Append(" 之间；列名那一行不能同时被列进 totalRows；")
          .Append("不要输出毫米坐标、不要改纸张几何（只点名用哪张纸）；看不清就说看不清，宁可省略字段。\n");
        sb.Append("说话要求：用中文大白话；不要出现 rows、JSON、字段英文名这些词（软件自己会把你回的话写成五行：")
          .Append("表格有效数据 / 纸张 / 模版 / 张数 / 预览，那五行由软件拼，你不用写）。\n");
        sb.Append("facts 一条只说一件事——**不要把几件事挤进一句话**（老板是一行一行扫的，挤成一句他就读不懂了）。\n");
        // 第 39 棒：字号/粗细/居中**不再问模型**。第 31 棒把格式读出来了，却把量到的数字压成散文、
        // 再教模型「照它的大小关系排」——等于请一个概率模型口算回填软件手里的量测，
        // 这就是用户 2026-09-10 圈的「AI 排版效果差，差在字体大小」的来路（每轮还都不一样）。
        // 现在软件自己去量 templateSource 那一列（RowFormatEvidence），所以这里只交代分工，
        // 并且把 templateSource 的分量提上来：那一列就是软件要去量的地方，它说不出来软件就没处量。
        sb.Append("字多大、要不要粗、居不居中：**你不用管，软件自己会算**——")
          .Append("它会去量 templateSource 那一列里那几行字的真实字号、粗细、居中，照量到的排，比你填的数准。")
          .Append("所以 templateSource 务必写准（模板抄在哪一列）：写不出来软件就没处量，只能退回用你填的数。")
          .Append("你要做的只有一件：**认出标签上印哪几行、什么顺序、每行读哪一列**。")
          .Append("rows 里的 sizePt / weight / bold / align 写了也不扔（软件量不到时拿它兜底），但别再为它们费心，")
          .Append("更不要为了字号大小去增删行数——行数只由「标签上真印了哪几行」决定。\n");
        sb.Append("一律用中文。");
        return sb.ToString();
    }
}
