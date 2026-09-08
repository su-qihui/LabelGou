using LabelGou.Core.Impos;
using LabelGou.Core.Marks;
using LabelGou.Core.Numbering;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 「<strong>张数一般表格里会有一列写的</strong>（例：表格写打印 5 张，那对应的那张就要排出 5 张整张纸）」
/// —— 用户 2026-09-08 这句话点开了一个一直没法用的口子：展开计数列写死在
/// <see cref="NumberingRule.ExpandCountField"/>（封闭的 19 个枚举）且界面上一个入口都没有，
/// 所以表里那列叫「打印张数」时根本选不到。这里钉住按列名取数与读不到数时的说实话行为。
/// </summary>
public class ExpandCountColumnTests
{
    private static MarkRecord Row(
        int rowIndex,
        string consignee,
        string? sheets = null,
        string? total = null,
        ValueOrigin sheetsOrigin = ValueOrigin.ExcelImport,
        string column = "打印张数")
    {
        var b = MarkRecord.Builder().SetRow(rowIndex, $"Sheet1 第 {rowIndex} 行").Set(MarkFieldKey.Consignee, consignee);
        if (sheets is not null) b.SetCustom("col:" + column, sheets, sheetsOrigin);
        if (total is not null) b.Set(MarkFieldKey.CartonTotal, total, ValueOrigin.ExcelImport);
        return b.Build();
    }

    [Fact]
    public void 按列名展开_表里写几张纸就出几张()
    {
        var records = new[] { Row(1, "A", sheets: "5"), Row(2, "B", sheets: "2") };
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "打印张数",
        };

        var result = NumberingEngine.Apply(records, rule);

        Assert.Equal(7, result.LabelCount);
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 2, 2 }, result.Labels.Select(l => l.SourceRowIndex).ToArray());
        Assert.DoesNotContain(result.Issues, i => i.Severity >= IssueLevel.Warning);
    }

    [Fact]
    public void 列名优先于连接好的总件数字段()
    {
        // 表里同时有「件数 1」和「打印张数 4」时，用户选哪列就按哪列，不能被字段映射悄悄顶掉。
        var records = new[] { Row(1, "A", sheets: "4", total: "1") };
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "打印张数",
        };

        Assert.Equal(4, NumberingEngine.Apply(records, rule).LabelCount);
    }

    [Fact]
    public void 不选列时仍按连接好的总件数字段展开()
    {
        // 老行为一行没变：没选列就走 ExpandCountField，不能因为开了新口子把既有任务改了版。
        var records = new[] { Row(1, "A", total: "3"), Row(2, "B", total: "1") };
        var rule = new NumberingRule { Mode = NumberingMode.ExpandByCartonTotal };

        Assert.Equal(4, NumberingEngine.Apply(records, rule).LabelCount);
    }

    [Fact]
    public void 列里读不出整数就逐行报警并按一张纸处理()
    {
        var records = new[] { Row(1, "A", sheets: "三") };
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "打印张数",
        };

        var result = NumberingEngine.Apply(records, rule);

        Assert.Equal(1, result.LabelCount);
        var warning = Assert.Single(result.Issues, i => i.Severity == IssueLevel.Warning);
        Assert.Contains("「打印张数」列＝「三」读不出整数", warning.Message);
    }

    [Fact]
    public void 表里没这一列时说要的是哪一列而不是静默按一()
    {
        var records = new[] { Row(1, "A", sheets: "5") };
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "张数",           // 表里其实没有这一列
        };

        var result = NumberingEngine.Apply(records, rule);

        Assert.Equal(1, result.LabelCount);
        var warning = Assert.Single(result.Issues, i => i.Severity == IssueLevel.Warning);
        Assert.Contains("「张数」列在本行没有值", warning.Message);
    }

    [Fact]
    public void 引擎自己填进列里的规则值不算本行数据()
    {
        // §五-73 那一类：兜底/规则算出来的值必须带来源标记，否则上一轮展开的结果会被下一轮当成输入（N² 张）。
        var records = new[] { Row(1, "A", sheets: "5", sheetsOrigin: ValueOrigin.Rule) };
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "打印张数",
        };

        var result = NumberingEngine.Apply(records, rule);

        Assert.Equal(1, result.LabelCount);
        Assert.Contains(result.Issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("在本行没有值"));
    }

    [Fact]
    public void 列名带空格与中英混排照原样取()
    {
        var records = new[] { Row(1, "A", sheets: "2", column: "张 数 pcs") };
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "张 数 pcs",
        };

        Assert.Equal(2, NumberingEngine.Apply(records, rule).LabelCount);
    }

    [Fact]
    public void 展开出的每张标签配一张整张纸()
    {
        // 端到端一句：编号引擎负责「这行几张纸」，拼版引擎负责「一页铺满几份全同」，两边不重复乘。
        var records = new[] { Row(1, "A", sheets: "3"), Row(2, "B", sheets: "1") };
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "打印张数",
        };
        var labels = NumberingEngine.Apply(records, rule).Labels;

        var plan = ImpositionEngine.Build(FourUp(), 90, 60, labels.Count);

        Assert.Equal(4, plan.PageCount);                       // 3 页 A + 1 页 B
        Assert.Equal(16, plan.PhysicalLabelCount);             // 每页 4 份全同
        Assert.Equal(4, plan.PlacementsOnPage(1).Count);
        Assert.All(plan.PlacementsOnPage(1), p => Assert.Equal(1, p.LabelIndex));
    }

    [Fact]
    public void 那句规则总结要说清按哪一列()
    {
        Assert.Equal("按「打印张数」列展开", new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "打印张数",
        }.Describe().Split(" · ")[0]);
        Assert.StartsWith("按箱数展开", new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
        }.Describe());
    }

    [Fact]
    public void 克隆与序列化往返带上列名()
    {
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "打印张数",
        };

        Assert.Equal("打印张数", rule.Clone().ExpandCountColumn);
    }

    [Fact]
    public void 那一列已经连上字段时按列名也要能取到张数()
    {
        // 真表现状（第 14 棒探针抓出来的）：金沐那列叫「件数\nCTN」且已经连到总件数字段，
        // 连上的列不会另存一份 col: 键。只查 col: 就会对着明明存在的列报 32 条「没有值」。
        var records = new[] { Row(1, "A", total: "5"), Row(2, "B", total: "3") };
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountField = MarkFieldKey.CartonTotal,
            ExpandCountColumn = "件数\nCTN",      // 表里这一列已连上总件数字段，没 col: 键
        };

        var result = NumberingEngine.Apply(records, rule);

        Assert.Equal(8, result.LabelCount);
        Assert.DoesNotContain(result.Issues, i => i.Severity >= IssueLevel.Warning);
    }

    [Fact]
    public void 带换行的列名在告警与总结里折成单行()
    {
        var records = new[] { Row(1, "A", sheets: "三") };
        var rule = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountColumn = "件数\nCTN",
        };

        var result = NumberingEngine.Apply(records, rule);

        var note = Assert.Single(result.Issues, i => i.Severity == IssueLevel.Warning);
        Assert.DoesNotContain("\n", note.Message);
        Assert.Contains("件数 / CTN", note.Message);
        Assert.Contains("件数 / CTN", rule.Describe());
        Assert.DoesNotContain("\n", rule.Describe());
    }

    /// <summary>200×140 的纸、90×60 的标签 → 恰好 2 列 × 2 行 = 每页 4 枚。</summary>
    private static SheetSpec FourUp() => new()
    {
        Name = "一开四测试",
        PaperWidthMm = 200,
        PaperHeightMm = 140,
        MarginLeftMm = 5,
        MarginTopMm = 5,
        MarginRightMm = 5,
        MarginBottomMm = 5,
        GutterXMm = 2,
        GutterYMm = 2,
        AllowRotate = false,
        RegistrationMarks = false,
        RepeatSameLabelPerPage = true,
    };
}
