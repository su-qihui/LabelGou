using LabelGou.Core.Marks;
using LabelGou.Core.Numbering;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 件号编号引擎。件号是唛头上最容易错、错了就真出货损的数字，
/// 所以这里把「不臆造、口径唯一、可回溯」三条规矩逐条钉住。
/// </summary>
public class NumberingEngineTests
{
    private static MarkRecord Row(int rowIndex, string consignee, string? cartonNo = null, string? total = null, string? contract = null)
    {
        var b = MarkRecord.Builder().SetRow(rowIndex, $"Sheet1 第 {rowIndex} 行").Set(MarkFieldKey.Consignee, consignee);
        if (cartonNo is not null) b.Set(MarkFieldKey.CartonNo, cartonNo, ValueOrigin.ExcelImport);
        if (total is not null) b.Set(MarkFieldKey.CartonTotal, total, ValueOrigin.ExcelImport);
        if (contract is not null) b.Set(MarkFieldKey.ContractNo, contract, ValueOrigin.ExcelImport);
        return b.Build();
    }

    [Fact]
    public void 沿用模式_数据里有件号就一个数字都不改()
    {
        var records = new[] { Row(1, "A", cartonNo: "12", total: "99"), Row(2, "B", cartonNo: "13", total: "99") };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.KeepData });

        Assert.False(result.RuleRejected);
        Assert.Equal(2, result.LabelCount);
        Assert.Equal("12", result.Labels[0].GetText(MarkFieldKey.CartonNo));
        Assert.Equal("99", result.Labels[1].GetText(MarkFieldKey.CartonTotal));
        Assert.All(result.Labels, label => Assert.Equal(ValueOrigin.ExcelImport, label.Get(MarkFieldKey.CartonNo)!.Origin));
    }

    [Fact]
    public void 沿用模式_缺件号的行才补号并标成规则来源()
    {
        var records = new[] { Row(1, "A", cartonNo: "7"), Row(2, "B") };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.KeepData, Start = 1 });

        Assert.Equal("7", result.Labels[0].GetText(MarkFieldKey.CartonNo));
        Assert.Equal(ValueOrigin.ExcelImport, result.Labels[0].Get(MarkFieldKey.CartonNo)!.Origin);

        var filled = result.Labels[1].Get(MarkFieldKey.CartonNo)!;
        Assert.Equal("2", filled.Text);
        Assert.Equal(ValueOrigin.Rule, filled.Origin);
        Assert.Contains("编号规则", filled.SourceRef);
    }

    [Fact]
    public void 强制重排_按步长递增且总件数等于箱数()
    {
        var records = new[] { Row(1, "A", cartonNo: "50"), Row(2, "B", cartonNo: "60"), Row(3, "C") };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.ForceSequence, Start = 7, Step = 3 });
        Assert.Equal(new[] { "7", "10", "13" }, result.Labels.Select(l => l.GetText(MarkFieldKey.CartonNo)));
        Assert.All(result.Labels, l => Assert.Equal("3", l.GetText(MarkFieldKey.CartonTotal)));
        Assert.Contains(result.Issues, i => i.Severity == IssueLevel.Info && i.Message.Contains("重排"));
    }

    [Fact]
    public void 按箱数展开_一行十二箱就出十二张标签()
    {
        var records = new[] { Row(1, "A", total: "2"), Row(2, "B", total: "1"), Row(3, "C", total: "3") };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.ExpandByCartonTotal });

        Assert.Equal(3, result.SourceRecordCount);
        Assert.Equal(6, result.CartonCount);
        Assert.Equal(6, result.LabelCount);
        Assert.Equal(new[] { "1", "2", "3", "4", "5", "6" }, result.Labels.Select(l => l.GetText(MarkFieldKey.CartonNo)));
        Assert.All(result.Labels, l => Assert.Equal("6", l.GetText(MarkFieldKey.CartonTotal)));

        // 同一行的其它字段必须原样带走，第 4 张来自第 3 行
        Assert.Equal("C", result.Labels[3].GetText(MarkFieldKey.Consignee));
        Assert.Equal(3, result.Labels[3].SourceRowIndex);
    }

    [Fact]
    public void 展开时箱数读不出来就按一箱并告警()
    {
        var records = new[] { Row(1, "A", total: "12 箱"), Row(2, "B", total: "两箱"), Row(3, "C", total: "0"), Row(4, "D") };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.ExpandByCartonTotal });

        // 第一行提数成功（12 箱），后三行提不出来 → 各按 1 箱，共 15 箱
        Assert.Equal(15, result.CartonCount);
        Assert.Equal(15, result.LabelCount);
        Assert.Equal(3, result.Issues.Count(i => i.Severity == IssueLevel.Warning));
        Assert.Contains(result.Issues, i => i.Message.Contains("两箱"));
    }

    [Fact]
    public void 单行箱数超上限就截断并告警()
    {
        var records = new[] { Row(1, "A", total: "999999") };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.ExpandByCartonTotal });

        Assert.Equal(NumberingRule.MaxExpandPerRecord, result.CartonCount);
        Assert.Contains(result.Issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("截断"));
    }

    /// <summary>"12.0" 这种写法以前被 Where(char.IsDigit) 拼成 "120"，十倍箱数就这么印上去了（批次一-1）。</summary>
    [Theory]
    [InlineData("12.0", 12)]
    [InlineData("12 箱", 12)]
    [InlineData("12/120", 12)]
    [InlineData("0.5", 1)]
    public void 展开只取第一个连续数字串(string total, int expectedCartons)
    {
        var records = new[] { Row(1, "A", total: total) };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.ExpandByCartonTotal });

        Assert.Equal(expectedCartons, result.CartonCount);
    }

    [Fact]
    public void 展开计数列拿不到表里的值就按一箱而不是整批行数()
    {
        // RecordMapper.Map 在「件数」没连列时把 CartonTotal 填成整批行数（来源 Rule）。
        // 一旦当成「本行几箱」，三行表就变成 3×3=9 张（批次一-2）。
        var records = new[] { RuleFilled(1, "3"), RuleFilled(2, "3"), RuleFilled(3, "3") };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.ExpandByCartonTotal });

        Assert.Equal(3, result.CartonCount);
        Assert.Equal(3, result.LabelCount);
        Assert.Contains(result.Issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("不是表格里的值"));
    }

    [Fact]
    public void 沿用模式把行序换成用户设的起始号与补零()
    {
        // 表里没有件号 → Map 先填了 Rule 来源的 "1"。KeepData 若拿 Has() 当判据，
        // 用户设的 起始 101 / 补零 3 / 前缀 No. 就整体失效（批次一-3）。
        var records = new[] { RuleFilled(1, "1"), RuleFilled(2, "2") };

        var result = NumberingEngine.Apply(records, new NumberingRule
        {
            Mode = NumberingMode.KeepData,
            Start = 101,
            PadDigits = 3,
            Prefix = "No.",
        });

        Assert.Equal(new[] { "No.101", "No.102" }, result.Labels.Select(l => l.GetText(MarkFieldKey.CartonNo)));
    }

    [Fact]
    public void 本行箱数只在有表内来源时才写()
    {
        var fromSheet = Row(1, "A", total: "5");
        var onlyBatchTotal = RuleFilled(2, "15");      // Map 兜底的整批行数，不是本行的箱数

        var result = NumberingEngine.Apply(new[] { fromSheet, onlyBatchTotal }, new NumberingRule { Mode = NumberingMode.KeepData });

        Assert.Equal("5", result.Labels[0].GetCustom("col:本行箱数")!.Text);
        Assert.Null(result.Labels[1].GetCustom("col:本行箱数"));   // 宁缺不假：留给界面报「没数据」，不印 15
    }

    [Fact]
    public void 规则被拒也要给模板留下计算量()
    {
        // 步长填 0 → 规则被拒、原样退回；但退回的那批若没经过 BuildRecord，
        // 模板里 Ctns：{{col:本行箱数}}件 会命中「变量全空整条隐藏」而无声少印一行（批次一-11）。
        var records = new[] { Row(1, "A", cartonNo: "7", total: "5") };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.ForceSequence, Step = 0 });

        Assert.True(result.RuleRejected);
        Assert.Equal("5", result.Labels[0].GetCustom("col:本行箱数")!.Text);
        Assert.Equal("1", result.Labels[0].GetCustom("col:组内序")!.Text);
        Assert.Equal("7", result.Labels[0].GetText(MarkFieldKey.CartonNo));      // 件号仍不许被改
        Assert.Equal(ValueOrigin.ExcelImport, result.Labels[0].Get(MarkFieldKey.CartonNo)!.Origin);
    }

    /// <summary>模拟 Map 的兜底产物：值在，但来源是工具自己（Rule）。</summary>
    private static MarkRecord RuleFilled(int rowIndex, string total)
        => MarkRecord.Builder().SetRow(rowIndex, $"Sheet1 第 {rowIndex} 行")
            .Set(MarkFieldKey.CartonTotal, total, ValueOrigin.Rule)
            .Set(MarkFieldKey.CartonNo, rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), ValueOrigin.Rule)
            .Build();

    [Fact]
    public void 补零与前后缀只作用于本箱号不影响总件数()
    {
        var records = new[] { Row(1, "A"), Row(2, "B"), Row(3, "C") };

        var result = NumberingEngine.Apply(records, new NumberingRule
        {
            Mode = NumberingMode.ForceSequence,
            PadDigits = 3,
            Prefix = "No.",
            Start = 7,
        });

        Assert.Equal("No.007", result.Labels[0].GetText(MarkFieldKey.CartonNo));
        Assert.Equal("No.009", result.Labels[2].GetText(MarkFieldKey.CartonNo));
        Assert.Equal("003", result.Labels[0].GetText(MarkFieldKey.CartonTotal));   // 补零跟随，但不吃前后缀
    }

    [Fact]
    public void 一张多份时件号只占一个号且总件数仍是箱数()
    {
        var records = new[] { Row(1, "A", total: "2"), Row(2, "B", total: "1") };

        var result = NumberingEngine.Apply(records, new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            Copies = 2,
        });

        Assert.Equal(3, result.CartonCount);
        Assert.Equal(6, result.LabelCount);                       // 3 箱 × 每箱 2 份
        Assert.Equal("1", result.Labels[0].GetText(MarkFieldKey.CartonNo));
        Assert.Equal("1", result.Labels[1].GetText(MarkFieldKey.CartonNo));        // 同箱两份同号
        Assert.Equal("2", result.Labels[2].GetText(MarkFieldKey.CartonNo));
        Assert.All(result.Labels, l => Assert.Equal("3", l.GetText(MarkFieldKey.CartonTotal)));
    }

    [Fact]
    public void 按合同号分组时组内重新起号且总数按组算()
    {
        var records = new[]
        {
            Row(1, "A", cartonNo: "100", contract: "C1"),
            Row(2, "B", cartonNo: "200", contract: "C2"),
            Row(3, "C", cartonNo: "300", contract: "C1"),
        };

        var result = NumberingEngine.Apply(records, new NumberingRule
        {
            Mode = NumberingMode.ForceSequence,
            Scope = NumberingScope.PerGroup,
            GroupByField = MarkFieldKey.ContractNo,
        });

        Assert.Equal(2, result.GroupCount);
        // 分组保持首次出现顺序：C1 的两行先出，再 C2
        Assert.Equal(new[] { "1", "2", "1" }, result.Labels.Select(l => l.GetText(MarkFieldKey.CartonNo)));
        Assert.Equal(new[] { "2", "2", "1" }, result.Labels.Select(l => l.GetText(MarkFieldKey.CartonTotal)));
        Assert.Equal(new[] { "C1", "C1", "C2" }, result.Labels.Select(l => l.GetText(MarkFieldKey.ContractNo)));
    }

    [Fact]
    public void 规则不合法时拒绝改号并原样退回记录()
    {
        var records = new[] { Row(1, "A", cartonNo: "42"), Row(2, "B", cartonNo: "43") };

        var result = NumberingEngine.Apply(records, new NumberingRule { Mode = NumberingMode.ForceSequence, Step = 0 });

        Assert.True(result.RuleRejected);
        // 退回的是副本（得带上模板要用的计算量，见「规则被拒也要给模板留下计算量」），
        // 但件号与来源必须一字未改：宁可印数据原号也不印错号。
        Assert.Equal("42", result.Labels[0].GetText(MarkFieldKey.CartonNo));
        Assert.Equal(ValueOrigin.ExcelImport, result.Labels[0].Get(MarkFieldKey.CartonNo)!.Origin);
        Assert.Equal("A", result.Labels[0].GetText(MarkFieldKey.Consignee));
        Assert.Contains(result.Issues, i => i.Severity == IssueLevel.Error && i.Message.Contains("步长"));
    }

    [Fact]
    public void 复制记录时不丢人工核对标记与其它字段()
    {
        var flagged = MarkRecord.Builder()
            .SetRow(9, "Sheet1 第 9 行")
            .Set(MarkFieldKey.Consignee, "WALMART")
            .Set(MarkFieldKey.GrossWeight, new MarkValue("-3", ValueOrigin.AiOcr) { NeedsReview = true, Warning = "毛重为负" })
            .SetCustom("col:托盘号", "PLT-7")
            .Build();

        var result = NumberingEngine.Apply(new[] { flagged }, new NumberingRule
        {
            Mode = NumberingMode.ForceSequence,
        });

        var label = Assert.Single(result.Labels);
        Assert.Equal("WALMART", label.GetText(MarkFieldKey.Consignee));
        Assert.Equal("PLT-7", label.GetCustom("col:托盘号")!.Text);
        Assert.Equal(9, label.SourceRowIndex);
        var pending = Assert.Single(label.PendingReview());
        Assert.Equal(MarkFieldKey.GrossWeight, pending.Key);
        Assert.Contains("毛重为负", pending.Value.Warning);
    }

    [Fact]
    public void 份数或补零越界会被规则校验拦住()
    {
        Assert.Contains(new NumberingRule { Copies = 0 }.Validate(), i => i.Severity == IssueLevel.Error);
        Assert.Contains(new NumberingRule { PadDigits = 20 }.Validate(), i => i.Severity == IssueLevel.Error);
        Assert.Contains(new NumberingRule { Start = -5 }.Validate(), i => i.Severity == IssueLevel.Error);
        Assert.DoesNotContain(new NumberingRule().Validate(), i => i.Severity == IssueLevel.Error);
    }

    [Fact]
    public void 沿用模式下起始号与步长照样要校验()
    {
        // 曾经只在重排/展开模式下才校验，结果 KeepData 遇到 Step=0 会把所有缺件号的行补成同一个号
        Assert.Contains(new NumberingRule { Mode = NumberingMode.KeepData, Step = 0 }.Validate(),
            i => i.Severity == IssueLevel.Error);
        Assert.Contains(new NumberingRule { Mode = NumberingMode.KeepData, Start = -1 }.Validate(),
            i => i.Severity == IssueLevel.Error);
    }

    [Fact]
    public void 描述文本能看懂在做什么()
    {
        var rule = new NumberingRule { Mode = NumberingMode.ExpandByCartonTotal, PadDigits = 3, Copies = 2 };
        var text = rule.Describe();
        Assert.Contains("按箱数展开", text);
        Assert.Contains("补 3 位", text);
        Assert.Contains("每张 2 份", text);
    }
}
