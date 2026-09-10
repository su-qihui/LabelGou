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

    private static AiSheetProposal Parse(string json) => AiSheetProposal.Parse(json, null, 13, AiProposalStage.Layout, Specs);

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
        var p = AiSheetProposal.Parse(OneMapping, ThreeColumns, 13, AiProposalStage.Layout, null);

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
        var p = AiSheetProposal.Parse(OneMapping, ThreeColumns, 13, AiProposalStage.Layout, null);

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

        var p = AiSheetProposal.Parse(json, ThreeColumns, 13, AiProposalStage.Layout, null);

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

        var p = AiSheetProposal.Parse(json, ThreeColumns, 13, AiProposalStage.Layout, null);

        var only = Assert.Single(p.Mappings);
        Assert.Equal(2, only.ColumnIndex);                    // 留着的是第一条（C 列），不是后一条
        Assert.Contains(p.Notes, n => n.Contains("不止一列"));
    }

    [Fact]
    public void 没有列画像时一条绑定都不采纳_不许拿列字母瞎对()
    {
        var p = AiSheetProposal.Parse(OneMapping, null, 13, AiProposalStage.Layout, null);

        Assert.Empty(p.Mappings);
        Assert.Contains(p.Notes, n => n.Contains("列清单"));
    }

    // ───────────────── 逐条事实与字号（第 31 棒：用户说"它的说法"讲得混乱 / 差在字体大小） ─────────────────

    [Fact]
    public void facts逐条解析_一条一件_而且facts说了就不再播reason()
    {
        const string json = """
            {
              "headerRow": 1,
              "facts": [ "第1列是货号", "第2列是每箱数量", "F列那4行是标签上印什么字的样例抄写" ],
              "reason": "第1列是货号、第2列是每箱数量、F列那4行是标签样例抄写（五件事挤一句那种）"
            }
            """;

        var p = AiSheetProposal.Parse(json, ThreeColumns, 13, AiProposalStage.Layout, null);

        Assert.Equal(3, p.Facts.Count);                    // 一条一件，不挤成一句
        Assert.Contains("第1列是货号", p.Facts);

        // 同一件事不播两遍：facts 有了，reason 就不再作为"它的说法"重复出现（那正是"讲得混乱"的一半原因）。
        Assert.DoesNotContain(p.Explain(), line => line.StartsWith("它的说法：", StringComparison.Ordinal));
    }

    [Fact]
    public void 版式卡上写得出每行字号_撑满行如实写撑满()
    {
        const string json = """
            {
              "sheetSpec": "280×200 一开四",
              "rows": [ { "content": "BOLAROM", "weight": 1.6, "stretch": true, "bold": true },
                        { "content": "Item no：{{ItemNo}}", "weight": 1, "sizePt": 12, "stretch": false } ]
            }
            """;

        var p = AiSheetProposal.Parse(json, ThreeColumns, 13, AiProposalStage.Layout, Specs);

        var card = p.DescribeChanges(new AiChangeContext(RawRowCount: 13))
            .Single(c => c.Kind == AiChangeKind.Layout);

        // 用户 2026-09-10：「AI 排版效果差，差在字体大小」——而卡片上原来一个字号都没写，点 ✅ 前根本看不见。
        Assert.Contains("字号", card.After);
        Assert.Contains("撑满", card.After);                 // 撑满行的字号由行高反算，不假装等于某个 pt
        Assert.Contains("12pt", card.After);
    }

    // ───────────────── 版式逐行核对（第 32 棒：用户要"联系上下文校验哪个位置填哪一列"） ─────────────────

    [Fact]
    public void 版式逐行核对_对得上的写清列表头与列字母()
    {
        const string json = """
            {
              "mappings": [ { "column": "货号 ITEM NO:", "field": "ItemNo" } ],
              "rows": [ { "content": "BOLAROM" },
                        { "content": "Item no：{{ItemNo}}" },
                        { "content": "G.W.：{{col:毛重G.W.(kg)}}" } ]
            }
            """;

        var p = AiSheetProposal.Parse(json, ThreeColumns, 13, AiProposalStage.Layout, null);
        var lines = p.DescribeLayoutRows(ThreeColumns);

        Assert.Equal(3, lines.Count);
        Assert.Contains("固定文字", lines[0]);                              // 一整行没引用数据
        Assert.Contains("货号/款号 → 货号 ITEM NO:（B列）", lines[1]);        // 字段 → 它这一次绑的那一列
        Assert.Contains("毛重G.W.(kg)（C列）", lines[2]);                    // col: 直取某一列
        Assert.DoesNotContain("⚠", string.Join("", lines));
    }

    [Fact]
    public void 版式逐行核对_字段没说读哪一列时当场带警告()
    {
        const string json = """
            {
              "rows": [ { "content": "Item no：{{col:表里没有的列}}" },
                        { "content": "QTY：{{Quantity}}" } ]
            }
            """;

        var p = AiSheetProposal.Parse(json, ThreeColumns, 13, AiProposalStage.Layout, null);
        var lines = p.DescribeLayoutRows(ThreeColumns);

        // 第一层防线在解析器：表里根本没有的列名**当场剥掉**（放行等于让模型编数据），并记一句 Note。
        // 所以那一行到这里已经变成"固定文字"了——这条断言把两层防线的分工钉住。
        Assert.Contains("固定文字", lines[0]);
        Assert.Contains(p.Notes, n => n.Contains("表里没有的列"));

        // 第二层是这里的逐行核对：字段认得出（每箱数量），但它这一次没说读哪一列 → 必须带 ⚠，
        // 这正是用户要的「哪个位置填哪一列数据的校验」。
        Assert.Contains("⚠", lines[1]);
        Assert.Contains("每箱数量", lines[1]);
    }

    [Fact]
    public void 版式逐行核对_没有列清单时不装作核对过()
    {
        const string json = """
            { "rows": [ { "content": "Item no：{{col:随便一列}}" } ] }
            """;

        var p = AiSheetProposal.Parse(json, null, 13, AiProposalStage.Layout, null);
        var lines = p.DescribeLayoutRows(null);

        Assert.Single(lines);
        Assert.Contains("核不了", lines[0]);              // 拿不到列清单就说不确定，不编一个"对得上"
    }

    // ───────── 软件兜底问那条该问的（第 34 棒：用户抱怨「* 号后删不删也不问」） ─────────

    /// <summary>货号那一列的样例里带 *（真实现场：<c>b5011*16 INVISTUC</c>）。</summary>
    private static readonly IReadOnlyList<ColumnPortrait> StarColumns = new[]
    {
        new ColumnPortrait(0, "ITEM NO", "ITEM NO", null, null, new[] { "b5011*16 INVISTUC" }, 2),
    };

    [Fact]
    public void 货号列里带星号_模型没问软件也替你问()
    {
        // 第 40 棒起第一步（读表）就会问这条，那时候还没有版式；这里给的是第二步那种带 rows 的回包，
        // 钉的是「模型自己没问时软件补问」这半边，两步都一样。
        const string json = """{ "rows": [ { "content": "ITEM No: {{col:ITEM NO}}" } ] }""";

        var p = AiSheetProposal.Parse(json, StarColumns, 13, AiProposalStage.Layout, null);

        var q = Assert.Single(p.Questions);
        Assert.Equal(AiSheetQuestion.ActionItemNoTail, q.Action);
        Assert.Contains("* 号", q.Text);
        Assert.Contains(p.Notes, n => n.Contains("软件替你补问"));
    }

    [Fact]
    public void 模型自己问过了就不重复问()
    {
        const string json = """
            {
              "rows": [ { "content": "ITEM No: {{col:ITEM NO}}" } ],
              "questions": [ { "text": "货号里*号要不要保留", "action": "itemno-tail" } ]
            }
            """;

        var p = AiSheetProposal.Parse(json, StarColumns, 13, AiProposalStage.Layout, null);

        Assert.Single(p.Questions);     // 只留模型问的那一条，不叠加成两条
    }

    [Fact]
    public void 读表阶段没有版式也要问星号那条_答复等版式出来再落()
    {
        // 第 40 棒翻掉了「有版式才问」这个前置：两阶段拆分后**第一步本来就没有版式**，
        // 而这条恰恰是用户点名要问的（「*号后面的是否保留」）——按老规矩他就永远被问不到。
        // mappings 要给：MergeLayout 补落那次替换得先知道货号是哪一列，不知道就如实说改不了（不猜）。
        var read = AiSheetProposal.Parse(
            """{ "headerRow": 1, "mappings": [ { "column": "ITEM NO", "field": "ItemNo" } ] }""",
            StarColumns, 13, AiProposalStage.Read);

        var q = Assert.Single(read.Questions);
        Assert.Equal(AiSheetQuestion.ActionItemNoTail, q.Action);

        // 拍「不印 * 后面」：这一步没有版式可改，**只记账，不假装办了**。
        var answered = read.WithAnswer(q, yes: false);
        Assert.Single(answered.Answers);
        Assert.Null(answered.Layout);
        Assert.Contains(answered.Notes, n => n.Contains("记下了"));

        // 第二步版式一到，那条答复就地补落：{{col:ITEM NO}}（原样，连 * 后面）换成 {{ItemNo}}（软件清洗过）。
        var layout = AiSheetProposal.Parse(
            """{ "rows": [ { "content": "ITEM No: {{col:ITEM NO}}" } ] }""",
            StarColumns, 13, AiProposalStage.Layout, null, null, answered);
        var merged = AiSheetProposal.MergeLayout(answered, layout);

        Assert.Contains("{{ItemNo}}", merged.Layout!.Rows[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{col:ITEM NO}}", merged.Layout.Rows[0].Content, StringComparison.Ordinal);
        Assert.Contains(merged.Notes, n => n.Contains("只印 * 前面"));
    }

    [Fact]
    public void 不知道货号是哪一列时_星号那条如实说改不了_不猜一列去改()
    {
        // 猜错一列就是印错货，所以宁可报一句"改不了"并说清下一步。
        var read = AiSheetProposal.Parse("""{ "headerRow": 1 }""", StarColumns, 13, AiProposalStage.Read);
        var q = Assert.Single(read.Questions);
        var answered = read.WithAnswer(q, yes: true);

        var layout = AiSheetProposal.Parse(
            """{ "rows": [ { "content": "ITEM No: {{col:ITEM NO}}" } ] }""",
            StarColumns, 13, AiProposalStage.Layout, null, null, answered);
        var merged = AiSheetProposal.MergeLayout(answered, layout);

        Assert.Contains("{{col:ITEM NO}}", merged.Layout!.Rows[0].Content, StringComparison.Ordinal);   // 一个字没动
        Assert.Contains(merged.Notes, n => n.Contains("改不了"));
    }

    // ───────── 他的决定要真的回到模型手里（第 35 棒：用户说"选择了也是无效的"） ─────────

    [Fact]
    public void 老板拍过板的决定会写进提示词()
    {
        // 根因钉在这里：提案那条路原来只发 [system, user]，一个字的上下文都不带 ——
        // 于是他答完问题，模型下一轮看到的还是原来那张表那句话，"答了等于没答"。
        // 第 40 棒起决定有两条路回去：这一节（跨请求累积），以及提案自己身上的 Answers
        // （第二步提示词里那节「老板已经就这些拍过板了」，见 AiSheetProposalTests）。
        var prompt = AiSheetProposalPrompt.BuildRead(
            "整张表 12 行 × 3 列", 12, 1, 0,
            decisions: new[] { "货号里 * 号后面那截要不要印？ → 不用", "件数末尾总数 155 要不要印？ → 不需要" });

        Assert.Contains("老板已经就下面这些拍过板", prompt);
        Assert.Contains("货号里 * 号后面那截要不要印？ → 不用", prompt);
        Assert.Contains("不要再问一遍", prompt);
    }

    [Fact]
    public void 没有决定时不写那一节_少占字()
    {
        var prompt = AiSheetProposalPrompt.BuildRead("整张表 12 行 × 3 列", 12, 1, 0);

        Assert.DoesNotContain("老板已经就下面这些拍过板", prompt);
    }
}
