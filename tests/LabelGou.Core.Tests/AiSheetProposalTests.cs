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
        Assert.Contains(p.Notes, n => n.Contains("表头", StringComparison.Ordinal));
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
        var asSaid = HeaderRowDetector.Detect(NoHeaderVendorGrid(), new SheetLayoutChoice(HasHeader: false));
        var cut = HeaderRowDetector.Detect(NoHeaderVendorGrid(),
            new SheetLayoutChoice(HasHeader: false, ExcludedRawRows: new[] { 2 }));

        Assert.Equal(2, auto.DataRows.Count);              // 自动猜：首行被当表头吃掉 → 少一张
        Assert.Equal(3, asSaid.DataRows.Count);            // 按指令：三行都是货
        Assert.Equal(2, cut.DataRows.Count);               // 合计行剔掉，不多印那张
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
}
