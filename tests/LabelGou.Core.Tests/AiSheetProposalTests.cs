using LabelGou.Core.Data;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 「AI 全权提案」这一层的钉子（M7 第 21 棒）。
/// <para>用户 2026-09-09 定：「把权限都给 AI，因为手动调整效果很差」+「先让软件内的 AI 查看表格而不是根据程序写的自动来」。
/// 权限给出去了，那三条会印错货的判断（表头在哪、哪几行是合计行、用哪张纸）就不再靠程序猜——
/// 但<strong>AI 说什么就照做什么</strong>是另一回事：这里钉的正是「照做之前先被边界与清单卡一道」。</para>
/// <para>另外钉住切表指令本身：<c>headerRow</c> 往后挪一位，原来被当表头吃掉的那一行必须回到数据里
/// （那正是「411 行的表只出 410 张」这个 bug 的现场）。</para>
/// </summary>
public class AiSheetProposalTests
{
    private const string FullJson = """
        {
          "hasHeader": true,
          "headerRow": 3,
          "totalRows": [12, 13],
          "sheetSpec": "一页一枚（纸面跟标签走）",
          "warnings": ["模板顶部写死的 BOLAROM 与这批客户不符"],
          "reason": "第 3 行别名命中最多，末尾两行是合计",
          "rows": [ { "content": "Ctns No.{{CartonNo}}/{{CartonTotal}}", "sizePt": 14, "weight": 1 },
                    { "content": "ITEM NO.{{ItemNo}}", "sizePt": 12, "weight": 1 } ]
        }
        """;

    private static readonly IReadOnlyList<string> Specs =
        new[] { "一页一枚（纸面跟标签走）", "A4 一行两枚", "280×200 一开四" };

    [Fact]
    public void 正常提案_行号折成0起且纸规点名成功()
    {
        var p = AiSheetProposal.Parse(FullJson, null, 13, AiProposalStage.Layout, Specs);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        Assert.Equal(3, p.HeaderRow);                      // 与人看 Excel 一致的口径
        Assert.Equal(new[] { 12, 13 }, p.TotalValueRows);
        Assert.Equal("一页一枚（纸面跟标签走）", p.SheetSpecName);
        Assert.NotNull(p.Layout);                          // 行式版式那段也解析出来了
        Assert.Single(p.Warnings);

        var choice = p.ToChoice(13);
        Assert.Equal(2, choice.HeaderRowIndex);            // 0 起：第 3 行
        Assert.True(choice.HasHeader);
        Assert.Equal(new[] { 11, 12 }, choice.ExcludedRawRows);
    }

    [Fact]
    public void 越界行号被忽略并且留痕_不静默修好()
    {
        const string text = """{"headerRow": 99, "totalRows": [5, 99, -3, 5]}""";

        var p = AiSheetProposal.Parse(text, null, 13, AiProposalStage.Layout, Specs);

        Assert.Null(p.HeaderRow);                          // 表头只存在于 1~13 行，99 是编的
        Assert.Equal(new[] { 5 }, p.TotalValueRows);       // 99 与 -3 丢掉，重复的 5 不排两遍
        Assert.Contains(p.Notes, n => n.Contains("99", StringComparison.Ordinal));
        Assert.Contains(p.Notes, n => n.Contains("-3", StringComparison.Ordinal));
    }

    [Fact]
    public void 表头那一行不许同时被当合计行剔掉()
    {
        const string text = """{"headerRow": 2, "totalRows": [2, 9]}""";

        var p = AiSheetProposal.Parse(text, null, 13, AiProposalStage.Layout, Specs);

        Assert.Equal(new[] { 9 }, p.TotalValueRows);
        Assert.Contains(p.Notes, n => n.Contains("列名", StringComparison.Ordinal));
    }

    [Fact]
    public void 散文里带花括号不毒死提案()
    {
        // 旧的「第一个 { 到最后一个 }」会被散文里的花括号夹出坏 JSON,整份提案变 Bad(第 23 棒审计-10)。
        const string text = """
            我的建议{注意}如下：
            { "headerRow": 2, "totalRows": [9] }
            以上{完毕}。
            """;

        var p = AiSheetProposal.Parse(text, null, 13, AiProposalStage.Layout, Specs);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        Assert.Equal(2, p.HeaderRow);
        Assert.Equal(new[] { 9 }, p.TotalValueRows);
    }

    [Fact]
    public void 软件里没有那张纸规就直接拒_不模糊换一张()
    {
        const string text = """{"sheetSpec": "A4 豪华版"}""";

        var p = AiSheetProposal.Parse(text, null, 13, AiProposalStage.Layout, Specs);

        Assert.False(p.IsUsable);
        Assert.Contains(p.Errors, e => e.Contains("A4 豪华版", StringComparison.Ordinal));
        Assert.Contains(p.Errors, e => e.Contains("一页一枚", StringComparison.Ordinal));   // 报错要说清能选哪些
    }

    [Fact]
    public void 围栏与字符串数字与逗号串都收_模型脏三种也能落地()
    {
        const string text = """
            好的，这是我的判断：
            ```json
            { "headerRow": "4", "totalRows": "11，12", "hasHeader": "true" }
            ```
            """;

        var p = AiSheetProposal.Parse(text, null, 13, AiProposalStage.Layout, Specs);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        Assert.Equal(4, p.HeaderRow);
        Assert.Equal(new[] { 11, 12 }, p.TotalValueRows);   // 全角逗号分隔的一句串也算
    }

    [Fact]
    public void 说这张表没表头时_指令里表头行被固定住()
    {
        const string text = """{"hasHeader": false}""";

        var p = AiSheetProposal.Parse(text, null, 13, AiProposalStage.Layout, Specs);
        var choice = p.ToChoice(13);

        Assert.False(choice.HasHeader);
        Assert.Equal(0, choice.HeaderRowIndex);             // 从第一行之后就是数据 → 首行回到数据里
    }

    [Fact]
    public void 一句人话而不是JSON时_判不可用而不是抛异常()
    {
        var p = AiSheetProposal.Parse("这张表看不太懂，你能再说明一下吗？", null, 13, AiProposalStage.Read, Specs);

        Assert.False(p.IsUsable);
        Assert.Single(p.Errors);
        Assert.True(p.IsEmpty);
    }

    // ---------- 切表指令真的能把「被吃掉的首行」放回来 ----------

    private static List<string[]> NoHeaderVendorGrid() => new()
    {
        new[] { "AJ7", "3", "b5003", "", "", "" },        // 真实的第一箱货，没有表头
        new[] { "AJ8", "5", "b5004", "", "", "" },
        new[] { "合计", "8", "", "", "", "" },            // 表尾那句不该出标签
    };

    [Fact]
    public void 按指令当没表头_首行回到数据里_合计行能被剔掉()
    {
        var auto = HeaderRowDetector.Detect(NoHeaderVendorGrid());
        // 第 24 棒同步：递了指令（含这里只说「没表头」的那份）时合计行兜底默认开，
        // 表尾那行「合计 8」不再需要人点名也会被剔——本条的钉点仍是「首行不再被吃掉」。
        var asSaid = HeaderRowDetector.Detect(NoHeaderVendorGrid(), new SheetLayoutChoice(HasHeader: false));
        var cut = HeaderRowDetector.Detect(NoHeaderVendorGrid(),
            new SheetLayoutChoice(HasHeader: false, ExcludedRawRows: new[] { 2 }));

        Assert.Equal(2, auto.DataRows.Count);              // 自动猜：首行被当表头吃掉 → 少一张
        Assert.Equal(new[] { 0, 1 }, asSaid.DataRowRawIndexes);   // 按指令：首行回到数据里（第 3 行被兜底剔）
        Assert.Single(asSaid.AutoSkippedSummaryRows);      //  剔了谁、凭什么逐行报出
        Assert.Equal(2, cut.DataRows.Count);               // 点名的剔法殊途同归
        Assert.Empty(cut.AutoSkippedSummaryRows);          // 被指令点名的行不重复报第二遍
        Assert.Equal(new[] { 0, 1 }, cut.DataRowRawIndexes);   // 剩下的两行对应原表第 1、2 行
        Assert.Equal(-1, asSaid.HeaderRowIndex);           // -1 就是「这张表没表头」的唯一记号
        Assert.Equal(3, asSaid.RawRowCount);               // 原表行数的上界不受剔除影响
    }

    [Fact]
    public void 指令默认时行为一字不变_老表不受牵连()
    {
        var grid = new List<string[]>
        {
            new[] { "件数\nCTN", "货号 ITEM NO", "备注" },
            new[] { "3", "b5003", "开二" },
        };

        var old = HeaderRowDetector.Detect(grid);
        var now = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        Assert.Equal(old.HeaderRowIndex, now.HeaderRowIndex);
        Assert.Equal(old.DataRows.Count, now.DataRows.Count);
        Assert.Equal(old.Headers, now.Headers);
        Assert.True(SheetLayoutChoice.Auto.IsDefault);
    }

    [Fact]
    public void 摊给人的那几行不许出现技术黑话()
    {
        var p = AiSheetProposal.Parse(FullJson, null, 13, AiProposalStage.Layout, Specs);
        var text = string.Join("\n", p.DescribeItems(13)) + "\n" + string.Join("\n", p.Explain());

        // 用户 2026-09-09：「使用者不是技术人员，他们不知道 rows 什么的」
        foreach (var jargon in new[] { "rows", "JSON", "mm", "版式", "数据行", "剔除名单", "sizePt" })
            Assert.DoesNotContain(jargon, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("标签上印这几行", text, StringComparison.Ordinal);
        Assert.Contains("一张纸怎么摆", text, StringComparison.Ordinal);
        Assert.Contains("改完之后会出", text, StringComparison.Ordinal);   // 数量用「几张标签」说，不说「多少行数据」
    }

    [Fact]
    public void 提醒去重并且封顶_不会一屏全是感叹号()
    {
        var many = "{ \"warnings\": [" + string.Join(",", Enumerable.Range(1, 9).Select(i => $"\"疑点 {i}\"")) + ", \"疑点 1\"] }";

        var p = AiSheetProposal.Parse(many, null, 13, AiProposalStage.Layout, Specs);
        var explain = p.Explain();

        Assert.Equal(6, explain.Count);                       // 5 条 + 一句「还有 N 条」（去重后 9 条只列 5 条，重复那条不排第二遍）
        Assert.Contains(explain, e => e.Contains("还有 4 条提醒", StringComparison.Ordinal));
    }

    [Fact]
    public void 那五行按用户逐字指定的句式出来()
    {
        const string json = """
        {
          "dataCols": 4,
          "paperText": "一开四--28*20--2*2--14*10",
          "templateSource": "F列",
          "qtyColumn": "B",
          "rows": [
            { "content": "BOLAROM", "bold": true, "align": "center", "sizePt": 30 },
            { "content": "Item no：{{col:货号}}", "bold": false, "sizePt": 14 },
            { "content": "QTY：{{col:数量}} pcs", "bold": false, "sizePt": 14 },
            { "content": "Ctns：{{col:件数}}件", "bold": false, "sizePt": 14 }
          ],
          "questions": [
            { "text": "件数末尾总数155", "no": "不需要", "yes": "需要", "action": "row-keep", "row": 34 }
          ]
        }
        """;
        var cols = new[]
        {
            new ColumnPortrait(0, "货号", "货号", null, null, new[] { "olu830-35*144" }, 31),
            new ColumnPortrait(1, "件数", "件数 CTN", "CartonTotal", "总件数", new[] { "5" }, 32),
            new ColumnPortrait(2, "数量", "数量", null, null, new[] { "144" }, 31),
            new ColumnPortrait(3, "一开四", "一开四", null, null, new[] { "张数等于件数" }, 1),
            new ColumnPortrait(4, "列E", "列E", null, null, Array.Empty<string>(), 0),
            new ColumnPortrait(5, "列F", "列F", null, null, new[] { "BOLAROM" }, 4),
        };

        var p = AiSheetProposal.Parse(json, cols, 34, AiProposalStage.Layout, Specs);
        var lines = p.SummaryLines(31, 155);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        // 用户 2026-09-09 逐字写的五行，一字不改：行与张数由软件数，列字母由表里的位置推
        Assert.Equal("表格有效数据31行4列", lines[0]);
        Assert.Equal("纸张:一开四--28*20--2*2--14*10", lines[1]);
        Assert.Equal("模版:F列:一行BOLAROM 加粗居中,二行Item no：(A列),三行QTY：(C列) pcs,四行Ctns：(B列)件", lines[2]);
        Assert.Equal("张数:绑定B列（件数）", lines[3]);
        Assert.Equal("预览:31个模板,155张", lines[4]);

        // 第 34 棒起这一列会**多一条**：货号列的样例里带 *（olu830-35*144），软件按确定性判定补问
        // 「* 号后面那截要不要印」——模型这次问的是 row-keep，那条兜底问题照样补上
        // （用户 2026-09-10 抱怨过「* 号后删不删也不问」）。顺序：模型先、软件兜底后。
        // 第 40 棒：提问归**读表阶段**（排版阶段不提问），所以同样这份 JSON 要用读表阶段解才看得到这两条。
        var readP = AiSheetProposal.Parse(json, cols, 34, AiProposalStage.Read, Specs);
        Assert.Equal(new[] { "row-keep", "itemno-tail" }, readP.Questions.Select(q => q.Action));
        Assert.Equal(34, readP.Questions[0].Row);
        Assert.Equal(("不需要", "需要"), (readP.Questions[0].NoLabel, readP.Questions[0].YesLabel));
    }

    [Theory]
    [InlineData("B")]
    [InlineData("B列")]
    [InlineData("2")]
    [InlineData("件数")]
    public void 数张数那一列_三种指法都认得出来(string wanted)
    {
        var cols = new[]
        {
            new ColumnPortrait(0, "货号", "货号", null, null, Array.Empty<string>(), 31),
            new ColumnPortrait(1, "件数", "件数 CTN", null, null, Array.Empty<string>(), 32),
        };
        var text = "{ \"qtyColumn\": \"" + wanted + "\" }";

        var p = AiSheetProposal.Parse(text, cols, 13, AiProposalStage.Layout, Specs);

        Assert.Equal("件数", p.Readout.QtyColumn);
        Assert.Equal(1, p.Readout.QtyColumnIndex);
        Assert.StartsWith("张数:绑定B列（件数）", p.SummaryLines()[3], StringComparison.Ordinal);
    }

    [Fact]
    public void 软件接不住的动作不进问题列表_但留下一句实话()
    {
        const string text = """
        { "questions": [
            { "text": "要不要把 logo 换成蓝色", "action": "recolor" },
            { "text": "最后一行要不要印", "action": "row-keep", "row": 99 } ] }
        """;

        // 第 40 棒：提问归读表阶段（排版阶段一律不提问），所以这条用读表阶段解。
        var p = AiSheetProposal.Parse(text, null, 13, AiProposalStage.Read, Specs);

        Assert.Empty(p.Questions);                       // recolor 没这个开关；row-keep 那条行号不在表里
        Assert.Contains(p.Notes, n => n.Contains("接不住", StringComparison.Ordinal));
        Assert.Contains(p.Notes, n => n.Contains("不在表里", StringComparison.Ordinal));
    }

    [Fact]
    public void 第一步提示词_只许理解与提问_明确禁止给排版()
    {
        var text = AiSheetProposalPrompt.BuildRead("整张表 13 行 × 6 列：…", 13, 1, 2);

        // 行号口径（与人看 Excel 一致）与硬边界照旧写死。
        Assert.Contains("原表行号、从 1 起", text, StringComparison.Ordinal);
        Assert.Contains("1~13", text, StringComparison.Ordinal);
        Assert.Contains("只输出一个 JSON 对象", AiSheetProposalPrompt.SystemText, StringComparison.Ordinal);

        // 第 40 棒的核心：这一步**不许给排版**（用户的红线「这层先不要对预览纸张进行调整」）。
        Assert.Contains("这一步不要给排版方案", text, StringComparison.Ordinal);
        Assert.Contains("不要输出纸张尺寸", text, StringComparison.Ordinal);
        // 纸规清单是第二步的东西，出现在第一步就等于又在诱它选纸。
        Assert.DoesNotContain("纸规清单（只能选这些名字", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 第一步提示词_七类风险都有动作_不许自由发挥()
    {
        // 「乱提问题」的两半根因都钉在这里：① 以前 questions 是模型自由发挥；
        // ② 软件只接得住四个动作，它问的其余那些被静默丢掉——用户想问的「x列是纸张张数列吗」
        //    那时根本没有对应动作，问了也白问。
        var text = AiSheetProposalPrompt.BuildRead("（画像）", 13, 1, 0);

        Assert.Contains("只许问下面这几类", text, StringComparison.Ordinal);
        foreach (var action in new[]
                 {
                     "itemno-tail", "qty-column", "row-keep", "template-source",
                     "header-row", "fixed-value", "column-meaning",
                 })
        {
            Assert.Contains(action, text, StringComparison.Ordinal);
            // 提示词里写的每个 action，软件都得真接得住——不然又是「问了被静默丢掉」。
            Assert.Contains(action, AiSheetQuestion.ReadStageActions, StringComparer.OrdinalIgnoreCase);
        }
        // 问与不问的界也要在：拿不准必须问，确定的不许拿来烦老板。
        Assert.Contains("拿不准就必须问", text, StringComparison.Ordinal);
        Assert.Contains("确定的事不许拿来问", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 第二步提示词_带着既成事实与拍过的板_只要排版不许提问()
    {
        // 读表那份用真的 Parse 走一遍，再真的拍一条板——不手搓 18 个参数的构造。
        var read = AiSheetProposal.Parse(
            """{ "headerRow": 1, "dataCols": 4, "templateSource": "F列" }""", null, 13, AiProposalStage.Read);
        var answered = read.WithAnswer(new AiSheetQuestion(
            "货号里 * 号及后面那一截要不要印", "不印", "原样印", AiSheetQuestion.ActionItemNoTail, 0, null), yes: false);

        var prompt = AiSheetProposalPrompt.BuildLayout("（画像）", Specs, 13, answered, "140×100 mm", 2);

        // 上一步的理解当**既成事实**带过去：不许再问一遍，也不许推翻。
        Assert.Contains("既成事实", prompt, StringComparison.Ordinal);
        Assert.Contains("列名在原表第 1 行", prompt, StringComparison.Ordinal);
        Assert.Contains("标签上的字抄在表里 F列", prompt, StringComparison.Ordinal);
        // 老板拍过的那一条原样回去（第 35 棒：他的决定必须真的回到模型手里）。
        Assert.Contains("老板已经就这些拍过板了", prompt, StringComparison.Ordinal);
        Assert.Contains("货号里 * 号及后面那一截要不要印 → 不印", prompt, StringComparison.Ordinal);
        // 纸规清单是这一步的东西。
        Assert.Contains("一页一枚（纸面跟标签走）", prompt, StringComparison.Ordinal);
        // 这一步不提问：排版直接落地 + 可撤回，拿"要不要重排"去问是白问一遍。
        Assert.Contains("这一步不要提问", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("只许问下面这几类", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void 表内文字模板是参照_看不全也要给rows()
    {
        // 第 28 棒：邱总表 F 列四行标签文字被模型以「看不全」为由丢了 rows。
        // 第 40 棒起这句话在**第二步**（第一步根本不给 rows），钉子跟着搬过来。
        var read = AiSheetProposal.Parse(
            """{ "headerRow": 1, "templateSource": "F列" }""", null, 13, AiProposalStage.Read);

        var withSource = AiSheetProposalPrompt.BuildLayout("…", Specs, 13, read, "140×100 mm", 0);
        Assert.Contains("表里抄标签的那一块在 F列", withSource, StringComparison.Ordinal);
        Assert.Contains("有几行看不清也照你看见的排", withSource, StringComparison.Ordinal);

        // 没有参照时不许假装是照着排的：必须自报「无参照猜测版」（第 38 棒放开的条件）。
        var noSource = AiSheetProposal.Parse("""{ "headerRow": 1 }""", null, 13, AiProposalStage.Read);
        var guessed = AiSheetProposalPrompt.BuildLayout("…", Specs, 13, noSource, "140×100 mm", 0);
        Assert.Contains("无参照猜测版", guessed, StringComparison.Ordinal);
    }

    // ───────── 第 40 棒：两阶段（读表理解 / 排版落地）─────────

    [Fact]
    public void 读表阶段排版字段一律不采纳_而且不静默()
    {
        var p = AiSheetProposal.Parse(FullJson, null, 13, AiProposalStage.Read, Specs);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        // 排版那半边一个都不留（用户红线：「这层先不要对预览纸张进行调整」）
        Assert.Null(p.Layout);
        Assert.Null(p.SheetSpecName);
        Assert.Null(p.PaperWidthMm);
        Assert.Null(p.Columns);
        Assert.Null(p.FollowsLabel);
        // 理解那半边照旧采纳
        Assert.Equal(3, p.HeaderRow);
        Assert.Equal(new[] { 12, 13 }, p.TotalValueRows);
        // 不静默：两句人话说清"它想改、我没理"
        Assert.Contains(p.Notes, n => n.Contains("就想改你的标签内容"));
        Assert.Contains(p.Notes, n => n.Contains("就想换你的纸"));
    }

    [Fact]
    public void 读表阶段纸规名对不上也不把整份判死()
    {
        // 读表阶段报一句「你说的那张纸这台机器上没有」会把整份提案判成不可用，
        // 用户看到的就是「这次没采纳它的方案」——连他最需要的提问也一起被挡掉，比当没看见坏得多。
        var p = AiSheetProposal.Parse(
            """{ "headerRow": 1, "sheetSpec": "A4 豪华版", "facts": ["第1列是货号"] }""",
            null, 13, AiProposalStage.Read, Specs);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        Assert.Null(p.SheetSpecName);
        Assert.DoesNotContain(p.Errors, e => e.Contains("这台机器上没有"));
        Assert.Single(p.Facts);                       // 该摆给他看的一样不少
    }

    [Fact]
    public void 排版阶段理解字段缺失不算错()
    {
        // 第二步只要版式与纸：它不必再报 headerRow / totalRows / mappings，缺了不是错。
        var p = AiSheetProposal.Parse(
            """{ "sheetSpec": "一页一枚（纸面跟标签走）", "rows": [ { "content": "ITEM NO.{{ItemNo}}" } ] }""",
            null, 13, AiProposalStage.Layout, Specs);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        Assert.NotNull(p.Layout);
        Assert.Equal("一页一枚（纸面跟标签走）", p.SheetSpecName);
        Assert.Null(p.HeaderRow);
        Assert.Empty(p.TotalValueRows);
    }

    [Fact]
    public void 老板的答复改的是这份提案_不是活表()
    {
        var read = AiSheetProposal.Parse(
            """{ "headerRow": 1, "totalRows": [12], "templateSource": "F列", "qtyColumn": "B" }""",
            new[] { new ColumnPortrait(1, "件数", "件数", null, null, new[] { "5" }, 4) },
            13, AiProposalStage.Read);
        Assert.Equal("件数", read.Readout.QtyColumn);

        // ① 那一行要印 → 从剔行名单里拿掉
        var kept = read.WithAnswer(new AiSheetQuestion(
            "第 12 行要不要印", "不印", "要印", AiSheetQuestion.ActionRowKeep, 12, null), yes: true);
        Assert.Empty(kept.TotalValueRows);
        Assert.Single(kept.Answers);

        // ② 列名行那一条：答"第一行就是货" → HasHeader 翻成 false
        var noHeader = read.WithAnswer(new AiSheetQuestion(
            "第一行是列名还是货", "第一行就是货", "第一行是列名", AiSheetQuestion.ActionHeaderRow, 0, null), yes: false);
        Assert.False(noHeader.HasHeader);

        // ③ 他说"不是这一列数张数" → 那一列被撤掉，不硬按错的列数纸
        var notQty = read.WithAnswer(new AiSheetQuestion(
            "按 B 列数张数吗", "不是这列", "是", AiSheetQuestion.ActionQtyColumn, 0, "B"), yes: false);
        Assert.Null(notQty.Readout.QtyColumn);
        Assert.Contains(notQty.Notes, n => n.Contains("不按它认的那一列数张数"));

        // ④ 他说"那块不是模板" → 来源列撤掉（软件就没处量字号了，这句实话也要说给他听）
        var notSource = read.WithAnswer(new AiSheetQuestion(
            "模板抄在 F 列吗", "不是", "是", AiSheetQuestion.ActionTemplateSource, 0, "F列"), yes: false);
        Assert.Null(notSource.Readout.TemplateSource);
        Assert.Contains(notSource.Notes, n => n.Contains("没处去量字号"));

        // ⑤ 列名那一行永远不许被当合计行剔掉（哪怕老板点了"不印"）
        var guarded = read.WithAnswer(new AiSheetQuestion(
            "第 1 行要不要印", "不印", "要印", AiSheetQuestion.ActionRowKeep, 1, null), yes: false);
        Assert.Equal(new[] { 12 }, guarded.TotalValueRows);
        Assert.Contains(guarded.Notes, n => n.Contains("不能当合计行剔掉"));
    }

    [Fact]
    public void 合并时理解取第一步_版式与纸取第二步_推翻不了既成事实()
    {
        var read = AiSheetProposal.Parse(
            """{ "headerRow": 3, "totalRows": [12, 13], "dataCols": 4, "templateSource": "F列" }""",
            null, 13, AiProposalStage.Read);
        // 第二步不听话，又想改理解（报了 headerRow 1、想把第 9 行也剔掉）
        var layout = AiSheetProposal.Parse(
            """{ "headerRow": 1, "totalRows": [9], "sheetSpec": "一页一枚（纸面跟标签走）", "rows": [ { "content": "ITEM NO.{{ItemNo}}" } ] }""",
            null, 13, AiProposalStage.Layout, Specs);

        var merged = AiSheetProposal.MergeLayout(read, layout);

        // 版式与纸取第二步
        Assert.NotNull(merged.Layout);
        Assert.Equal("一页一枚（纸面跟标签走）", merged.SheetSpecName);
        // 理解取第一步：它推翻不了
        Assert.Equal(3, merged.HeaderRow);
        Assert.Equal(new[] { 12, 13 }, merged.TotalValueRows);
        Assert.Equal(4, merged.Readout.DataCols);
        Assert.Equal("F列", merged.Readout.TemplateSource);
        // 版式出来了，那几行"念给人听"的话也跟着有
        Assert.NotEmpty(merged.Readout.TemplateLines);
    }

    [Fact]
    public void 排版阶段不提问_模型硬塞的与软件兜底的一律不再冒出来()
    {
        // 用户实测：第一遍问题答完，第二步又把同一条吐回来，面板再弹一次（「答了还问」）。
        // 提示词写了「第二步不提问」，但模型会不听话，解析层再拦一道。
        var cols = new[] { new ColumnPortrait(0, "货号", "货号", null, null, new[] { "olu830-35*144" }, 4) };
        var p = AiSheetProposal.Parse(
            """{ "sheetSpec": "一页一枚（纸面跟标签走）", "rows": [ { "content": "ITEM NO.{{ItemNo}}" } ], "questions": [ { "text": "A 列货号带 * 号，后面那截要不要印", "action": "itemno-tail" } ] }""",
            cols, 13, AiProposalStage.Layout, Specs);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        Assert.NotNull(p.Layout);
        // 模型那条问题一条都不采纳；连软件兜底的「* 号那条」也不在排版阶段补问（cols 里明明有带 * 的货号）
        Assert.Empty(p.Questions);
        Assert.Contains(p.Notes, n => n.Contains("又想提问"));
    }

    [Fact]
    public void 张数列那条答是_而读表时没落那一列_靠问题带的列补认上去()
    {
        // 用户实测：「AI 问了是否将 x 列设为张数、答了是，结果仍是模版那一张。」
        // 根因：模型只在问题里提了一嘴 B 列、没在顶层 qtyColumn 里报，Readout.QtyColumn 是空的；
        // 旧 WithAnswer 答「是」只记一句话，没把那一列设上去 → OutputCounter 拿到空的张数列只能出一张。
        var cols = new[]
        {
            new ColumnPortrait(0, "货号", "货号", null, null, new[] { "olu830-35" }, 4),
            new ColumnPortrait(1, "件数", "件数 CTN", null, null, new[] { "5" }, 4),
        };
        var read = AiSheetProposal.Parse(
            """{ "headerRow": 1, "questions": [ { "text": "出几张纸是按 B 列（件数）数吗", "no": "不是这列", "yes": "是", "action": "qty-column", "value": "B" } ] }""",
            cols, 13, AiProposalStage.Read);
        Assert.Null(read.Readout.QtyColumn);          // 读表时没报 qtyColumn，那一列是空的
        var q = Assert.Single(read.Questions);        // 货号样例不带 *，软件不会再兜底补问，只剩这一条

        // 老板答「是」→ 软件拿列画像把问题里带的 B 列（q.Value）真对回「件数」再设上去
        var answered = read.WithAnswer(q, yes: true, cols);
        Assert.Equal("件数", answered.Readout.QtyColumn);
        Assert.Equal(1, answered.Readout.QtyColumnIndex);
        Assert.Contains(answered.Notes, n => n.Contains("按「件数」这一列数"));
    }

    [Fact]
    public void 张数列答是但那一列对不回表里_如实说设不了_不猜一列()
    {
        // 补认也要真对回表里的列：对不上（或没拿到列画像）就如实说设不了，绝不猜一列——猜错就是数错张数、印错货。
        var cols = new[] { new ColumnPortrait(0, "货号", "货号", null, null, new[] { "x" }, 4) };
        var read = AiSheetProposal.Parse("""{ "headerRow": 1 }""", cols, 13, AiProposalStage.Read);

        var answered = read.WithAnswer(new AiSheetQuestion(
            "出几张纸是按 Z 列数吗", "不是这列", "是", AiSheetQuestion.ActionQtyColumn, 0, "Z"), yes: true, cols);
        Assert.Null(answered.Readout.QtyColumn);
        Assert.Contains(answered.Notes, n => n.Contains("这一列我设不了"));
    }
}
