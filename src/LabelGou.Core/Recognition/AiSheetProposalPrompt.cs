using System.Text;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 「让 AI 读这张表」那两步请求的提示词（第 21 棒起，第 40 棒拆成两步）。
/// <para>放在 Core 而不是写在界面里，理由跟行式版式那段提示词（<c>RowLayoutPrompt</c>）一样：
/// 这段文字决定模型会不会回一份能解析的东西，它必须能被单测钉住，而不是藏在某个按钮的事件里。</para>
/// <para><strong>为什么第 40 棒要拆成两份</strong>：用户 2026-09-10 给的方向是
/// 「用户发送表格 → AI 读取和理解 → 指出表格存在的问题（<strong>这层先不要对预览纸张进行调整</strong>）
/// → 收到用户反馈后再次理解 → 理解后进行自动排版」。
/// 而那时一次请求要它同时吐「这张表是什么」与「标签怎么排、纸怎么摆」十三四个字段：
/// 它还没拿到老板的答复，就得先把行数与纸规猜出来，猜的还照样被自动落地——
/// 他实测的评语是「效果仍然和以前一样乱改模版乱提问题」。
/// 所以拆成 <see cref="BuildRead"/>（只理解、只提问、<strong>不许给排版</strong>）与
/// <see cref="BuildLayout"/>（带着答复专管排版）。</para>
/// <para><strong>光靠提示词拦不住</strong>：<see cref="AiSheetProposal.Parse"/> 在读表阶段会直接把
/// rows 与纸规字段丢掉，模型不听话也落不了地。这里是交代分工，那里是执行分工。</para>
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
    /// <strong>第一步：读表理解</strong>。只要「每一列是什么、这张表该怎么切、有哪些风险要老板拍板」。
    /// <para><strong>明确禁止给排版方案</strong>（rows / 纸规名 / 纸张毫米数 / 每页几枚）：
    /// 这一步老板还没拍板，给了软件也不采纳（<see cref="AiSheetProposal.Parse"/> 会丢掉并记一句人话）。
    /// 这就是用户要的「这层先不要对预览纸张进行调整」。</para>
    /// </summary>
    /// <param name="portrait">整张表的画像（含批注行与贴图清单），来自 <see cref="TablePortrait.Describe"/>。</param>
    /// <param name="rawRowCount">原表总行数（1 起口径的边界）。</param>
    /// <param name="detectedHeaderRow">软件目前猜的表头行（1 起），0 表示还没切过。</param>
    /// <param name="imageCount">这条消息随附几张图（0 时要它老实说没参照，不许凭列名编设计）。</param>
    /// <param name="decisions">老板之前已经拍过板的决定（重跑这一步时带上，免得再问一遍）。</param>
    public static string BuildRead(
        string portrait,
        int rawRowCount,
        int detectedHeaderRow,
        int imageCount,
        IReadOnlyList<string>? decisions = null)
    {
        var sb = new StringBuilder();
        if (decisions is { Count: > 0 })
        {
            sb.Append("**老板已经就下面这些拍过板了（照这些来，不要再问一遍，也不要按相反的做）：**\n");
            foreach (var d in decisions.Take(12)) sb.Append("  - ").Append(d).Append('\n');
            sb.Append('\n');
        }

        sb.Append("这一步的任务：**只读这张表、只指出风险**。给「每一列是什么字段、这张表该怎么切」，")
          .Append("再把所有拿不准、要老板拍一下的事列成问题。\n");
        sb.Append("**这一步不要给排版方案**：不要 rows（标签上印哪几行）、不要纸规名、不要纸张毫米数、")
          .Append("不要每页几枚。老板还没拍板，你现在给了软件也不会用——排版本来就是下一步的事，")
          .Append("下一步会带着他的答复单独问你。\n\n");

        sb.Append(portrait).Append('\n');
        sb.Append("\n原表一共 ").Append(rawRowCount).Append(" 行；软件目前把列名猜在第 ")
          .Append(detectedHeaderRow > 0 ? detectedHeaderRow.ToString() : "？").Append(" 行。\n");
        sb.Append(imageCount > 0
            ? $"本条随附 {imageCount} 张图（表里贴的效果图/模板截图或用户拍的样张）：看一眼标签上大概印哪几行，"
              + "但**这一步不用你排出来**，只要说清「模板抄在表里哪一列/哪一块」或「照哪张图」。\n"
            : "本条没附图。但表里常常自己就带着模板：某一列或某一块把标签上的字抄了一遍\n"
              + "（例如右侧几行出现 BOLAROM / Item no：olu830-35 / QTY：144 pcs / Ctns：5件 这种）。\n"
              + "有这一块 → 说清它抄在哪一列（templateSource）就够了，**不要在这一步把 rows 排出来**。\n"
              + "表里确实没有这一块、也没附图 → 就老实说没参照，并把这件事列成一条问题问老板。\n");

        // 第 30 棒：AI 模式下"由 AI 绑定"是**主动作**（用户的原话：导入后不该先绑定，先让 AI 理解再绑），
        // 所以这一段要写得硬：按列里的真实内容判，拿不准宁可少报。
        sb.Append("\n最重要的一件事：**指出每一列是什么字段**（mappings）。\n")
          .Append("  列名常常写成「货号 ITEM NO:」「毛重G.W.(kg)」这种中英混排，也可能是纯英文、缩写，或者根本不着调——\n")
          .Append("  要按列里的**真实内容**判断它是什么，别只看列名。\n")
          .Append("  field 只能从下面「字段清单」里选。拿不准的列**不要硬塞**：软件宁可少一个绑定，也不要错一个——\n")
          .Append("  错一列就是数错张数、印错货，老板要按这一列出纸。\n")
          .Append("  拿不准就列成问题问他（下面白名单里有这一类），别自己拍。\n");

        sb.Append("\n只回这样一个 JSON 对象（字段可省略，行号一律用**原表行号、从 1 起**，与人看 Excel 的口径一致）：\n");
        sb.Append("{\n");
        sb.Append("  \"dataCols\": 4,                        // 真正有用的数据几列（不算空白列、不算抄模板那一块）\n");
        sb.Append("  \"mappings\": [                        // 哪一列是哪个字段：column 写列字母/列号/表头原样都行，field 只能写字段清单里的键\n");
        sb.Append("    { \"column\": \"B\", \"field\": \"CartonTotal\" },\n");
        sb.Append("    { \"column\": \"毛重G.W.(kg)\", \"field\": \"GrossWeight\" } ],\n");
        sb.Append("  \"hasHeader\": true 或 false,          // false = 这张表没有列名行，第一行也是货\n");
        sb.Append("  \"headerRow\": 1,                      // 列名在原表第几行\n");
        sb.Append("  \"totalRows\": [34],                   // 表尾「合计/TOTAL/小计」这类不该出标签的行，原表行号\n");
        sb.Append("  \"templateSource\": \"F列\",             // 模板抄在哪一列/哪一块（软件下一步要去那一列量字有多大）\n");
        sb.Append("  \"qtyColumn\": \"B\",                   // 每个货出几张纸按哪一列数（写列字母、列号或表头原样都行）\n");
        sb.Append("  \"paperText\": \"一开四--28*20--2*2--14*10\", // 表里自己写的那句纸的话，原样抄过来（没有就省略）\n");
        sb.Append("  \"facts\": [                            // 你从这张表看出来的判断，**一条只说一件事**（最多 5 条）\n");
        sb.Append("    \"第1列是货号\", \"第2列是每箱数量\", \"第3列是这票总共几箱（按它算纸张数）\",\n");
        sb.Append("    \"F列那4行是标签上印什么字的样例抄写\" ],\n");
        sb.Append("  \"questions\": [ … 见下面「只许问这几类」 … ],\n");
        sb.Append("  \"warnings\": [ \"…\" ],                // 你看出来的毛病但不必他拍板的（可省）\n");
        sb.Append("  \"reason\": \"为什么这么判（可省：facts 已经说清了就别再说一遍）\"\n");
        sb.Append("}\n");

        // ── 第 40 棒：questions 白名单。以前是模型自由发挥，所以它爱问什么问什么（用户评语「乱提问题」）；
        // 更坏的是软件只接得住四个动作，它问的其余那些会被静默丢掉——他想问的「x列是纸张张数列吗」
        // 那时根本没有对应动作，问了也白问。现在七类都有动作，也都给了「什么时候该问」的判据。
        sb.Append("\n**只许问下面这几类**（action 照抄，写别的软件接不住，那条问题会被丢掉）：\n");
        sb.Append("  1. `itemno-tail`——货号里有 `*` 或别的怪尾巴：后面那截要不要印上纸？\n");
        sb.Append("     { \"text\": \"货号里 * 号及后面那一截要不要印\", \"no\": \"不印\", \"yes\": \"原样印\", \"action\": \"itemno-tail\" }\n");
        sb.Append("  2. `qty-column`——出几张纸按哪一列数。表里常常同时有「每箱装几个」与「这票共几箱」两列，\n");
        sb.Append("     数错就是纸张数错。**认准了就不必问**；两列都像、或一列里混了两种数，才问。\n");
        sb.Append("     { \"text\": \"出几张纸是按 B 列（这票共几箱）数吗\", \"no\": \"不是这列\", \"yes\": \"是\", \"action\": \"qty-column\", \"value\": \"B\" }\n");
        sb.Append("  3. `row-keep`——表尾像「合计/TOTAL/小计」的行，或夹在货中间的批注行：要不要当货印？必须带 row。\n");
        sb.Append("     { \"text\": \"最后一行是合计 155，要不要印\", \"no\": \"不印\", \"yes\": \"要印\", \"action\": \"row-keep\", \"row\": 34 }\n");
        sb.Append("  4. `template-source`——标签上的字抄在表里哪一块。找到了一块就不用问；\n");
        sb.Append("     一块都找不到、或者有两块都像，才问。\n");
        sb.Append("     { \"text\": \"表里没找到抄标签的那一块，标签上印什么字你说了算吗\", \"no\": \"我另外给样张\", \"yes\": \"照 F 列那几行\", \"action\": \"template-source\", \"value\": \"F列\" }\n");
        sb.Append("  5. `header-row`——这张表有没有列名行、列名在第几行。第一行就是货的表不少见，判错了整张表少印或多印一张。\n");
        sb.Append("     { \"text\": \"第一行是列名还是第一票货\", \"no\": \"第一行就是货\", \"yes\": \"第一行是列名\", \"action\": \"header-row\" }\n");
        sb.Append("  6. `fixed-value`——标签上某一行是每张都印的死字（如 MADE IN CHINA、J.P），还是跟着货变的那一列的值？\n");
        sb.Append("     { \"text\": \"标签上 J.P 这两个字是每张都印，还是跟着货变\", \"no\": \"跟着货变\", \"yes\": \"每张都印\", \"action\": \"fixed-value\" }\n");
        sb.Append("  7. `column-meaning`——某一列里混了几样东西、列名与内容对不上、或者该有的列没有。\n");
        sb.Append("     { \"text\": \"C 列上半截是箱数下半截是重量，这一列到底按什么算\", \"no\": \"按重量\", \"yes\": \"按箱数\", \"action\": \"column-meaning\" }\n");
        sb.Append("\n问的规矩（第 34 棒定过、这里再说一遍）：\n")
          .Append("  · **拿不准就必须问**，别自己拍——尤其上面 1、2、3、5 这四类，判错了是印错货、数错纸。\n")
          .Append("  · **确定的事不许拿来问**：你已经在 facts 里说清了的，不要再变成一条问题去烦老板。\n")
          .Append("  · 最多 5 条，一条只问一件事，❌/✅ 两个答案都要写成人话（老板扫一眼就知道点哪个）。\n")
          .Append("  · 不属于上面七类的事：写进 warnings 或 facts，不要编一个 action。\n");

        // 第 34 棒：用户 2026-09-10 的抱怨是「思考完回答啥也没做」——后台它想了一大段（表格里某一列混了几样东西），
        // 但最终回的那段 JSON 里 facts 与 questions 都是空的，软件就没有任何东西摆给他看。
        sb.Append("\n**哪怕你什么都不改，也必须回 facts**（你从这张表看出来的判断，一条一件，最多 5 条）。")
          .Append("表里有毛病（某列混了几样东西、缺列、列名对不上、数字列里有文字）就写成 questions 让老板拍板——")
          .Append("**只写在思考过程里等于没给**：软件只把你最终回的那段 JSON 摆给老板看。\n");

        sb.Append("\n字段清单（mappings 的 field 只能用这些）：\n  ");
        sb.Append(string.Join(" ", MarkFieldCatalog.Mappable.Select(d => d.Key + "(" + d.ChineseName + ")"))).Append('\n');
        sb.Append("硬性约束：行号必须在 1~").Append(rawRowCount).Append(" 之间；列名那一行不能同时被列进 totalRows；")
          .Append("不要输出毫米坐标、不要输出纸张尺寸、不要点名用哪张纸（那是下一步的事）；")
          .Append("看不清就说看不清，宁可省略字段。\n");
        sb.Append("说话要求：用中文大白话；不要出现 rows、JSON、字段英文名这些词。")
          .Append("facts 一条只说一件事——**不要把几件事挤进一句话**（老板是一行一行扫的，挤成一句他就读不懂了）。\n");
        sb.Append("一律用中文。");
        return sb.ToString();
    }

    /// <summary>
    /// <strong>第二步：自动排版</strong>。老板把风险问题都拍完了，这一步只要「标签上印哪几行、这张纸怎么摆」。
    /// <para>上一步读懂的东西与老板拍过的板都原样带过去当<strong>既成事实</strong>：不许再问一遍，也不许推翻。
    /// 这一步<strong>不提问</strong>——排版是直接落进预览的，看得见也退得回，有疑点写进 warnings 让老板核。</para>
    /// </summary>
    /// <param name="portrait">整张表的画像（这一步仍要看得到表，才知道每行该填哪一列）。</param>
    /// <param name="sheetSpecNames">这台机器上真有的纸规名（内置 + 用户自建）。模型只能从这份清单里点名。</param>
    /// <param name="rawRowCount">原表总行数（1 起口径的边界）。</param>
    /// <param name="read">读表阶段那份提案（含老板拍过的板）。</param>
    /// <param name="currentLabelSizeText">当前模板尺寸那句人话（如「140×100 mm」），让模型知道改的是什么。</param>
    /// <param name="imageCount">这条消息随附几张图。</param>
    public static string BuildLayout(
        string portrait,
        IReadOnlyList<string> sheetSpecNames,
        int rawRowCount,
        AiSheetProposal read,
        string currentLabelSizeText,
        int imageCount)
    {
        var sb = new StringBuilder();
        sb.Append("这一步的任务：**只给排版**——标签上印哪几行、这张纸怎么摆。\n");
        sb.Append("上一步已经读过这张表了，下面这些是**既成事实**，不要再问一遍，也不要推翻：\n");
        foreach (var line in read.DescribeUnderstanding()) sb.Append("  - ").Append(line).Append('\n');

        if (read.Answers.Count > 0)
        {
            sb.Append("\n**老板已经就这些拍过板了（照这些排，不要再问）：**\n");
            foreach (var a in read.Answers.Take(12)) sb.Append("  - ").Append(a.Line).Append('\n');
        }

        sb.Append('\n').Append(portrait).Append('\n');
        sb.Append("\n原表一共 ").Append(rawRowCount).Append(" 行；当前标签尺寸 ").Append(currentLabelSizeText).Append("。\n");
        sb.Append(imageCount > 0
            ? $"本条随附 {imageCount} 张图：标签上印哪几行照图上的行序与字面，图上没有的行不要造。\n"
            : "本条没附图。");
        var source = read.Readout.TemplateSource;
        sb.Append(string.IsNullOrWhiteSpace(source)
            ? "表里没有抄标签的那一块，也没附图：按上面既成事实里那些字段排一版能用的，"
              + "并在 warnings 里自报这是**无参照猜测版**，让老板知道要核。\n"
            : $"表里抄标签的那一块在 {source}：照它抄了哪几行、什么顺序、哪行加粗居中排出来；"
              + "哪一行其实是某一列的值就写成占位符。有几行看不清也照你看见的排，在 reason 里说哪几行没看清。\n");

        sb.Append("\n只回这样一个 JSON 对象（**不要**再报 dataCols / hasHeader / headerRow / totalRows / ")
          .Append("mappings / qtyColumn / templateSource——那些上一步定了，报了软件也按上一步的算）：\n");
        sb.Append("{\n");
        sb.Append("  \"rows\": [ { \"content\": \"BOLAROM\" },\n");
        sb.Append("             { \"content\": \"Item no：{{col:货号ITEM NO:}}\" },\n");
        sb.Append("             { \"content\": \"QTY：{{col:每箱数量}} pcs\" } ],\n");
        sb.Append("              // 模板逐行：只写**印什么字**，顺序照标签上从上到下。\n");
        sb.Append("              // {{字段}} 用下面清单里的键；要某一列原样就写 {{col:表头原样}}\n");
        sb.Append("  \"sheetSpec\": \"一页一枚（纸面跟标签走）\", // 只能从下面纸规清单里原样选一个名字\n");
        sb.Append("  \"columns\": 2,                        // 每行几枚（0 或省略 = 由纸宽自动算）\n");
        sb.Append("  \"paperRows\": 2,                      // 每页几行（0 或省略 = 由纸高自动算）\n");
        sb.Append("  \"followsLabel\": true,                // 纸面跟着标签走（一页一枚）时才写\n");
        sb.Append("  \"warnings\": [ \"…\" ],                // 这一版有什么要老板核的（无参照猜测版必须自报）\n");
        sb.Append("  \"reason\": \"为什么这么排（可省）\"\n");
        sb.Append("}\n");
        sb.Append("**这一步不要提问**（不要 questions）：排版是直接落进预览的，老板看得见、也能一键撤回，")
          .Append("再拿「要不要重排」「要不要换纸」去问他是白问一遍——他心里有数就会点撤回，或者说一句哪儿不对让你只改这一步。")
          .Append("真有疑点（哪一行看不清、没有参照只能猜、这张纸摆不下这么多枚）写进 warnings，")
          .Append("软件会把它们摆在落地结果旁边让他核。\n");

        if (sheetSpecNames.Count > 0)
        {
            sb.Append("\n纸规清单（只能选这些名字，写别的会被拒）：\n");
            foreach (var name in sheetSpecNames.Take(20)) sb.Append("  - ").Append(name).Append('\n');
        }
        sb.Append("\n字段清单（rows 里的 {{键}} 只能用这些）：\n  ");
        sb.Append(string.Join(" ", MarkFieldCatalog.Mappable.Select(d => d.Key + "(" + d.ChineseName + ")"))).Append('\n');
        sb.Append("硬性约束：不要输出毫米坐标、不要改纸张几何（只点名用哪张纸）；")
          .Append("行数只由「标签上真印了哪几行」决定，**不要为了字号大小去增删行数**；看不清就说看不清。\n");
        // 第 39 棒：字号/粗细/居中**不问模型**。第 31 棒把格式读出来了，却把量到的数字压成散文、
        // 再教模型「照它的大小关系排」——等于请一个概率模型口算回填软件手里的量测，
        // 这就是用户 2026-09-10 圈的「AI 排版效果差，差在字体大小」的来路（每轮还都不一样）。
        // 现在软件自己去量 templateSource 那一列（RowFormatEvidence），所以这里只交代分工。
        sb.Append("字多大、要不要粗、居不居中：**你不用管，软件自己会算**——")
          .Append("它会去量上面既成事实里说的那一列（模板抄在哪一块）里那几行字的真实字号、粗细、居中，照量到的排，比你填的数准。")
          .Append("你要做的只有一件：**认出标签上印哪几行、什么顺序、每行读哪一列**。")
          .Append("rows 里的 sizePt / weight / bold / align 写了也不扔（软件量不到时拿它兜底），但别再为它们费心。\n");
        sb.Append("说话要求：用中文大白话；不要出现 rows、JSON、字段英文名、毫米坐标这些词。一律用中文。");
        return sb.ToString();
    }
}
