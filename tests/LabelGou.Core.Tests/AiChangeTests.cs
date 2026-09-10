using LabelGou.Core.Data;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 「AI 改动确认卡」这一层的钉子（阶段 29 第 1 棒）。
/// <para>用户 2026-09-10 定的三条决议里，这一条是<strong>红线</strong>：AI 的每一次写都要弹一张
/// 「改哪里 + 原值 → 新值」的 ✅/❌ 卡，人点 ✅ 才落地。他还当场纠正过一版写法——
/// 「人手动改后也可能出错，所以不该靠『人的改动一定对』来防冲突，该做的是让 AI 的每次写都可确认可取消」。</para>
/// <para>这里钉的三件事，每一件都是「卡要是假的，人就会白点」：
/// ① <strong>无变化不出卡</strong>（两边一样还出一张卡 = 逼人白审一条）；
/// ② <strong>原值不许编</strong>（拿不到就说"还没定"，绝不填一个看起来很像的数）；
/// ③ <strong>卡上写的"新值"就是真会落成的那一份</strong>——剔除行按并集落地，卡就得写并集，
/// 写模型的原始清单会让人点完 ✅ 才发现"第 5 行怎么还剔着"。</para>
/// <para>逐条落地本身在 App 侧的 <c>MainViewModel.ChoiceFrom(p, only)</c>（复用第 21 棒那份并集口径），
/// 钉子见 <c>LabelGou.App.Tests.AiChangeCardTests</c>。</para>
/// </summary>
public class AiChangeTests
{
    private const string WithLayout = """
        {
          "hasHeader": true,
          "headerRow": 3,
          "totalRows": [12, 13],
          "sheetSpec": "280×200 一开四",
          "reason": "第 3 行别名命中最多，末尾两行是合计",
          "rows": [ { "content": "Ctns No.{{CartonNo}}/{{CartonTotal}}", "sizePt": 14, "weight": 1 },
                    { "content": "ITEM NO.{{ItemNo}}", "sizePt": 12, "weight": 1 } ]
        }
        """;

    /// <summary>只报事实、不给版式的那一份（提示词允许它省略版式段）。</summary>
    private const string FactsOnly = """
        { "hasHeader": true, "headerRow": 3, "totalRows": [12, 13], "sheetSpec": "280×200 一开四" }
        """;

    private const string NoHeader = """
        { "hasHeader": false, "totalRows": [1] }
        """;

    private static readonly IReadOnlyList<string> Specs =
        new[] { "280×200 一开四", "A4 底纸", "一页一枚（纸面跟标签走）" };

    private static AiSheetProposal Parse(string json) => AiSheetProposal.Parse(json, null, 13, Specs);

    [Fact]
    public void 无变化不出卡_两边一样时清单是空的()
    {
        // 提案说的与软件现在做的一模一样：一句都不该出卡。
        var ctx = new AiChangeContext(
            RawRowCount: 13, HeaderRow: 3, HasHeader: true, ExcludedRows: new[] { 12, 13 },
            SheetSpecName: "280×200 一开四");

        var changes = Parse(FactsOnly).DescribeChanges(ctx);

        Assert.Empty(changes);
    }

    [Fact]
    public void 有变化就成对出卡_原值和新值都在()
    {
        var ctx = new AiChangeContext(
            RawRowCount: 13, HeaderRow: 1, HasHeader: true, ExcludedRows: null,
            TemplateName: "一开四 140×100", SheetSpecName: "A4 底纸");

        var changes = Parse(WithLayout).DescribeChanges(ctx);

        Assert.Equal(4, changes.Count);                       // 表头行 / 剔除行 / 版式 / 纸，四类各一

        var header = changes.Single(c => c.Kind == AiChangeKind.HeaderRow);
        Assert.Equal("列名在第 1 行", header.Before);          // 原值来自上下文，不是猜的
        Assert.Equal("列名在第 3 行", header.After);
        Assert.False(header.IsNoop);

        var rows = changes.Single(c => c.Kind == AiChangeKind.ExcludedRows);
        Assert.Equal("没剔任何行（整张表都按货印）", rows.Before);
        Assert.Equal("第 12、13 行不印", rows.After);

        var sheet = changes.Single(c => c.Kind == AiChangeKind.SheetSpec);
        Assert.Equal("A4 底纸", sheet.Before);
        Assert.Equal("280×200 一开四", sheet.After);

        var layout = changes.Single(c => c.Kind == AiChangeKind.Layout);
        Assert.Contains("一开四 140×100", layout.Before);      // 现在这张的名字进原值
        Assert.Contains("2 行", layout.After);                 // 它排的是两行
    }

    [Fact]
    public void 拿不到原值就说还没定_不许编一个像样的数()
    {
        var changes = Parse(WithLayout).DescribeChanges(null);   // 上下文整个没有

        Assert.NotEmpty(changes);
        var header = changes.Single(c => c.Kind == AiChangeKind.HeaderRow);
        Assert.Equal("列名行还没定", header.Before);              // 宁可不说话，也不填"第 1 行"
        Assert.Equal("（软件自动挑一张）", changes.Single(c => c.Kind == AiChangeKind.SheetSpec).Before);
    }

    [Fact]
    public void 剔除行按并集落地_卡上写的就是真会落成的那一份()
    {
        // 软件此刻已经剔了第 5 行（人工或上一轮 AI 剔的），模型这次只说了 12、13。
        // 落地口径是并集（只会多剔，不会把已剔的放回来），所以卡上必须写"第 5、12、13 行不印"——
        // 写"第 12、13 行"就是骗人：人点完 ✅ 会发现第 5 行还剔着。
        var ctx = new AiChangeContext(
            RawRowCount: 13, HeaderRow: 3, HasHeader: true, ExcludedRows: new[] { 5 },
            SheetSpecName: "280×200 一开四");

        var rows = Parse(FactsOnly).DescribeChanges(ctx).Single(c => c.Kind == AiChangeKind.ExcludedRows);

        Assert.Equal("第 5 行不印", rows.Before);
        Assert.Equal("第 5、12、13 行不印", rows.After);
    }

    [Fact]
    public void 越界行号与表头行仍被挡在剔除名单外()
    {
        // 模型一边说「表头在第 3 行」一边把第 3 行列进合计行，还多给一个表里没有的 99 行。
        const string json = """
            { "hasHeader": true, "headerRow": 3, "totalRows": [3, 12, 99] }
            """;

        var choice = Parse(json).ToChoice(13);

        Assert.Equal(2, choice.HeaderRowIndex);              // 第 3 行 → 下标 2
        Assert.Equal(new[] { 11 }, choice.ExcludedRawRows);   // 表头行(下标 2)与越界行(99)都没进来
    }

    [Fact]
    public void 首行也当货时_表头下标记0且剔除护栏不挡它()
    {
        var choice = Parse(NoHeader).ToChoice(13);

        Assert.False(choice.HasHeader);
        Assert.Equal(0, choice.HeaderRowIndex);               // 第 24 棒定的口径：0 = 第一行也当数据
        // 首行当货时没有哪一行需要被保护：它说第 1 行是合计行，那就真剔第 1 行（下标 0）。
        Assert.Equal(new[] { 0 }, choice.ExcludedRawRows);
    }

    // ───────────────────── 字段绑定（第 30 棒：AI 模式下由 AI 绑定） ─────────────────────

    /// <summary>三列真表（列序与"已绑了谁"都按真表的样子给）。</summary>
    private static readonly IReadOnlyList<ColumnPortrait> ThreeColumns = new[]
    {
        new ColumnPortrait(0, "流水号", "流水号", null, null, new[] { "AJ1" }, 2),
        new ColumnPortrait(1, "货号 ITEM NO:", "货号 ITEM NO:", "ItemNo", "货号/款号", new[] { "olu830-35" }, 2),
        new ColumnPortrait(2, "毛重G.W.(kg)", "毛重G.W.(kg)", null, null, new[] { "5" }, 2),
    };

    private const string OneMapping = """
        { "mappings": [ { "column": "毛重G.W.(kg)", "field": "GrossWeight" } ] }
        """;

    [Fact]
    public void 字段绑定也出卡_没绑的原值就写没绑()
    {
        var p = AiSheetProposal.Parse(OneMapping, ThreeColumns, 13, null);

        var binding = Assert.Single(p.Mappings);              // 列对回了真表头
        Assert.Equal(MarkFieldKey.GrossWeight, binding.Field);
        Assert.Equal(2, binding.ColumnIndex);

        var card = Assert.Single(p.DescribeChanges(new AiChangeContext(RawRowCount: 13)));
        Assert.Equal(AiChangeKind.FieldMapping, card.Kind);
        Assert.Equal("字段绑定：毛重", card.Target);
        Assert.Equal("没绑", card.Before);
        Assert.Equal("毛重G.W.(kg)（C列）", card.After);
        Assert.NotNull(card.Binding);                         // 落地料跟着卡片走，不靠文案反查
    }

    [Fact]
    public void 已经绑在同一列就不出卡_绑在别列时原值写那一列()
    {
        var p = AiSheetProposal.Parse(OneMapping, ThreeColumns, 13, null);

        var same = new AiChangeContext(RawRowCount: 13,
            Bindings: new Dictionary<MarkFieldKey, string> { [MarkFieldKey.GrossWeight] = "毛重G.W.(kg)" });
        Assert.Empty(p.DescribeChanges(same));                // 无变化不出卡

        var other = new AiChangeContext(RawRowCount: 13,
            Bindings: new Dictionary<MarkFieldKey, string> { [MarkFieldKey.GrossWeight] = "净重" });
        var card = Assert.Single(p.DescribeChanges(other));
        Assert.Equal("净重", card.Before);                     // 原值 = 此刻真绑的那一列
    }

    [Fact]
    public void 字段名写中文别名也认_认不出的字段与列一律丢并记明原因()
    {
        const string json = """
            { "mappings": [
              { "column": "毛重G.W.(kg)", "field": "毛重" },
              { "column": "流水号", "field": "不存在的字段" },
              { "column": "Z", "field": "ItemNo" } ] }
            """;

        var p = AiSheetProposal.Parse(json, ThreeColumns, 13, null);

        var only = Assert.Single(p.Mappings);                 // 只活下来中文别名那一条
        Assert.Equal(MarkFieldKey.GrossWeight, only.Field);
        Assert.Contains(p.Notes, n => n.Contains("不存在的字段"));   // 字段不在白名单
        Assert.Contains(p.Notes, n => n.Contains("没对上这一列"));   // 列 Z 不在表里
    }

    [Fact]
    public void 同一个字段报了两列只留第一条()
    {
        const string json = """
            { "mappings": [
              { "column": "毛重G.W.(kg)", "field": "GrossWeight" },
              { "column": "流水号", "field": "GrossWeight" } ] }
            """;

        var p = AiSheetProposal.Parse(json, ThreeColumns, 13, null);

        var only = Assert.Single(p.Mappings);
        Assert.Equal(2, only.ColumnIndex);                    // 留着的是第一条（C 列），不是后一条
        Assert.Contains(p.Notes, n => n.Contains("不止一列"));
    }

    [Fact]
    public void 没有列画像时一条绑定都不采纳_不许拿列字母瞎对()
    {
        var p = AiSheetProposal.Parse(OneMapping, null, 13, null);

        Assert.Empty(p.Mappings);
        Assert.Contains(p.Notes, n => n.Contains("列清单"));
    }
}
