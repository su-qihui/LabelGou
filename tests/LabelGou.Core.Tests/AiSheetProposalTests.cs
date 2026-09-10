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
        var p = AiSheetProposal.Parse(FullJson, null, 13, Specs);

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

        var p = AiSheetProposal.Parse(text, null, 13, Specs);

        Assert.Null(p.HeaderRow);                          // 表头只存在于 1~13 行，99 是编的
        Assert.Equal(new[] { 5 }, p.TotalValueRows);       // 99 与 -3 丢掉，重复的 5 不排两遍
        Assert.Contains(p.Notes, n => n.Contains("99", StringComparison.Ordinal));
        Assert.Contains(p.Notes, n => n.Contains("-3", StringComparison.Ordinal));
    }

    [Fact]
    public void 表头那一行不许同时被当合计行剔掉()
    {
        const string text = """{"headerRow": 2, "totalRows": [2, 9]}""";

        var p = AiSheetProposal.Parse(text, null, 13, Specs);

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

        var p = AiSheetProposal.Parse(text, null, 13, Specs);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        Assert.Equal(2, p.HeaderRow);
        Assert.Equal(new[] { 9 }, p.TotalValueRows);
    }

    [Fact]
    public void 软件里没有那张纸规就直接拒_不模糊换一张()
    {
        const string text = """{"sheetSpec": "A4 豪华版"}""";

        var p = AiSheetProposal.Parse(text, null, 13, Specs);

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

        var p = AiSheetProposal.Parse(text, null, 13, Specs);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        Assert.Equal(4, p.HeaderRow);
        Assert.Equal(new[] { 11, 12 }, p.TotalValueRows);   // 全角逗号分隔的一句串也算
    }

    [Fact]
    public void 说这张表没表头时_指令里表头行被固定住()
    {
        const string text = """{"hasHeader": false}""";

        var p = AiSheetProposal.Parse(text, null, 13, Specs);
        var choice = p.ToChoice(13);

        Assert.False(choice.HasHeader);
        Assert.Equal(0, choice.HeaderRowIndex);             // 从第一行之后就是数据 → 首行回到数据里
    }

    [Fact]
    public void 一句人话而不是JSON时_判不可用而不是抛异常()
    {
        var p = AiSheetProposal.Parse("这张表看不太懂，你能再说明一下吗？", null, 13, Specs);

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
        var p = AiSheetProposal.Parse(FullJson, null, 13, Specs);
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

        var p = AiSheetProposal.Parse(many, null, 13, Specs);
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

        var p = AiSheetProposal.Parse(json, cols, 34, Specs);
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
        Assert.Equal(new[] { "row-keep", "itemno-tail" }, p.Questions.Select(q => q.Action));
        Assert.Equal(34, p.Questions[0].Row);
        Assert.Equal(("不需要", "需要"), (p.Questions[0].NoLabel, p.Questions[0].YesLabel));
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

        var p = AiSheetProposal.Parse(text, cols, 13, Specs);

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

        var p = AiSheetProposal.Parse(text, null, 13, Specs);

        Assert.Empty(p.Questions);                       // recolor 没这个开关；row-keep 那条行号不在表里
        Assert.Contains(p.Notes, n => n.Contains("接不住", StringComparison.Ordinal));
        Assert.Contains(p.Notes, n => n.Contains("不在表里", StringComparison.Ordinal));
    }

    [Fact]
    public void 提示词把行号口径与纸规清单都写死()
    {
        var text = AiSheetProposalPrompt.Build("整张表 13 行 × 6 列：…", Specs, 13, 1, "140×100 mm", 2);

        Assert.Contains("原表行号、从 1 起", text, StringComparison.Ordinal);
        Assert.Contains("一页一枚（纸面跟标签走）", text, StringComparison.Ordinal);
        Assert.Contains("1~13", text, StringComparison.Ordinal);
        Assert.Contains("只输出一个 JSON 对象", AiSheetProposalPrompt.SystemText, StringComparison.Ordinal);

        var noImage = AiSheetProposalPrompt.Build("…", Specs, 13, 1, "140×100 mm", 0);
        Assert.Contains("不要凭列名编设计", noImage, StringComparison.Ordinal);   // 没参照时那条规矩要在提示词里
    }

    [Fact]
    public void 表内文字模板是参照_看不全也要给rows_retemplate必须配套()
    {
        // 第 28 棒：邱总表 F 列四行标签文字被模型以「看不全」为由丢了 rows，
        // 而它又问了 retemplate——老板点✅后没东西可落。两句硬话钉在提示词里。
        var noImage = AiSheetProposalPrompt.Build("…", Specs, 13, 1, "140×100 mm", 0);
        Assert.Contains("文字参照，和附图同等待遇", noImage, StringComparison.Ordinal);
        Assert.Contains("不要因为它看不全就省略 rows", noImage, StringComparison.Ordinal);
        Assert.Contains("（retemplate）就必须同时给出 rows", noImage, StringComparison.Ordinal);

        // 附图那条分支不许被误伤：配套要求是共用的，但「文字参照」那句只在没图时说
        var withImage = AiSheetProposalPrompt.Build("…", Specs, 13, 1, "140×100 mm", 2);
        Assert.DoesNotContain("文字参照，和附图同等待遇", withImage, StringComparison.Ordinal);
        Assert.Contains("（retemplate）就必须同时给出 rows", withImage, StringComparison.Ordinal);
    }
}
