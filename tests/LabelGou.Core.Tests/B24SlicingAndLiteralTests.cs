using LabelGou.Core.Data;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>第 24 棒件②：合计行本地兜底（SummaryRowSpotter + Detect 集成）。</summary>
public class SummaryRowSpotterTests
{
    private static List<string[]> Grid(params string[][] rows) => rows.ToList();

    [Fact]
    public void 写着合计的行被认出_理由带原话()
    {
        var grid = Grid(
            new[] { "货号", "件数" },
            new[] { "A1", "5" },
            new[] { "合计", "155" });

        var hits = SummaryRowSpotter.Find(grid, 0);

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.RawRowIndex);
        Assert.Contains("合计", hit.Reason);
    }

    [Fact]
    public void 带合计字样但满是货值的行不认_宁漏勿错杀()
    {
        // 一箱真实货物：客户名里带 "Total"，还有三个数值格——离"合计行"的形状很远
        var grid = Grid(
            new[] { "客户", "件数", "毛重", "体积" },
            new[] { "TOTAL LOGISTICS CO", "12", "250", "1.5" });

        Assert.Empty(SummaryRowSpotter.Find(grid, 0));
    }

    [Fact]
    public void 孤立数值恰等于整列之和被认出_金沐A34形状()
    {
        var grid = Grid(
            new[] { "货号", "件数" },
            new[] { "olu830-35", "5" },
            new[] { "olu830-36", "5" },
            new[] { "olu830-37", "145" },
            new[] { "", "155" });

        var hits = SummaryRowSpotter.Find(grid, 0);

        var hit = Assert.Single(hits);
        Assert.Equal(4, hit.RawRowIndex);
        Assert.Contains("155", hit.Reason);
        Assert.Contains("B 列", hit.Reason);   // 说清是哪一列加出来的
    }

    [Fact]
    public void 孤立数值不等于列和时不认()
    {
        var grid = Grid(
            new[] { "货号", "件数" },
            new[] { "A1", "5" },
            new[] { "", "9" });           // 9 ≠ 5：像一条缺货号的数据，不猜

        Assert.Empty(SummaryRowSpotter.Find(grid, 0));
    }

    [Fact]
    public void 两条合计行不互当基数_裸的那条仍靠剥掉前者后的列和命中()
    {
        var grid = Grid(
            new[] { "件数" },
            new[] { "10" },
            new[] { "10" },
            new[] { "合计 20" },     // 信号 a 命中
            new[] { "20" });          // 若把「合计 20」也加进基数 → 40≠20 会漏；剥掉它 → 10+10=20 应命中

        var hits = SummaryRowSpotter.Find(grid, 0);

        Assert.Equal(2, hits.Count);
        Assert.Equal(new[] { 3, 4 }, hits.Select(h => h.RawRowIndex).ToArray());
    }

    [Fact]
    public void 千分位孤数与列和相等也认()
    {
        var grid = Grid(
            new[] { "件数" },
            new[] { "700" },
            new[] { "734" },
            new[] { "1,434" });

        var hits = SummaryRowSpotter.Find(grid, 0);
        Assert.Equal(3, Assert.Single(hits).RawRowIndex);
    }

    [Fact]
    public void 带单位的孤数不当合计()
    {
        var grid = Grid(
            new[] { "备注" },
            new[] { "5件" },
            new[] { "8件" });

        Assert.Empty(SummaryRowSpotter.Find(grid, 0));
    }
}

/// <summary>第 24 棒件②③：Detect 按指令切表时兜底生效的方式（并集、报告、旧调用方零变化）。</summary>
public class SheetAutoSkipIntegrationTests
{
    private static List<string[]> VendorGrid() => new()
    {
        new[] { "客户", "货号", "件数" },
        new[] { "WALMART", "A1", "5" },
        new[] { "WALMART", "A2", "7" },
        new[] { "", "", "12" },        // 信号 b：孤数 = 5+7
    };

    [Fact]
    public void 递Auto指令时合计行被兜底剔掉并带理由()
    {
        var r = HeaderRowDetector.Detect(VendorGrid(), SheetLayoutChoice.Auto);

        Assert.Equal(2, r.DataRows.Count);
        var hit = Assert.Single(r.AutoSkippedSummaryRows);
        Assert.Equal(3, hit.RawRowIndex);
        Assert.Contains("12", hit.Reason);
        // 行号对应表里没有被剔的那行：第 2 条数据仍指回原表第 3 行
        Assert.Equal(new[] { 1, 2 }, r.DataRowRawIndexes);
    }

    [Fact]
    public void 递null指令的旧调用方一字不差()
    {
        var r = HeaderRowDetector.Detect(VendorGrid());

        Assert.Equal(3, r.DataRows.Count);
        Assert.Empty(r.AutoSkippedSummaryRows);
    }

    [Fact]
    public void 关掉兜底就一行不剔()
    {
        var r = HeaderRowDetector.Detect(VendorGrid(),
            new SheetLayoutChoice(SkipSummaryRows: false));

        Assert.Equal(3, r.DataRows.Count);
        Assert.Empty(r.AutoSkippedSummaryRows);
    }

    [Fact]
    public void 指令点名的行与兜底取并集_且不重复报()
    {
        var grid = new List<string[]>
        {
            new[] { "货号", "件数" },
            new[] { "A1", "5" },
            new[] { "A2", "7" },
            new[] { "", "12" },        // 兜底会命中
            new[] { "批注", "x" },      // 指令点名要剔
        };

        var r = HeaderRowDetector.Detect(grid,
            new SheetLayoutChoice(ExcludedRawRows: new[] { 4 }));

        Assert.Equal(2, r.DataRows.Count);                           // 点名剔第 4 行 + 兜底剔第 3 行，货行 A1/A2 都在
        var hit = Assert.Single(r.AutoSkippedSummaryRows);           // 兜底只报自己剔的那行
        Assert.Equal(3, hit.RawRowIndex);

        // 指令已经点名过兜底也会命中的行 → 不重复报第二遍
        var r2 = HeaderRowDetector.Detect(grid,
            new SheetLayoutChoice(ExcludedRawRows: new[] { 3, 4 }));
        Assert.Empty(r2.AutoSkippedSummaryRows);
    }

    [Fact]
    public void 关掉兜底是指令级改动_Describe要如实说()
    {
        var off = new SheetLayoutChoice(SkipSummaryRows: false);
        Assert.False(off.IsDefault);                 // 关掉 = 用户点过东西，状态栏要能说清
        Assert.Contains("合计行兜底已关", off.Describe());
        Assert.True(SheetLayoutChoice.Auto.IsDefault);
    }

    [Fact]
    public void 切表结果的人话描述逐行带理由()
    {
        var r = HeaderRowDetector.Detect(VendorGrid(), SheetLayoutChoice.Auto);
        var data = new TabularData("t.csv", "CSV", r.Headers, r.DataRows, r.HeaderRowIndex,
            dataRowRawIndexes: r.DataRowRawIndexes, choice: SheetLayoutChoice.Auto,
            rawRowCount: r.RawRowCount, autoSkippedSummaryRows: r.AutoSkippedSummaryRows);

        var text = data.Describe();
        Assert.Contains("自动跳过 1 行疑似合计", text);
        Assert.Contains("原表第 4 行", text);
    }
}

/// <summary>第 24 棒件①：模板写死文字的跨客户对值判据。</summary>
public class TemplateLiteralGuardTests
{
    private static LabelTemplate Template(params string[] rowTexts)
    {
        var t = new LabelTemplate { Name = "测试模板" };
        var y = 0d;
        foreach (var text in rowTexts)
        {
            t.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = text, X = 4, Y = y, Width = 132, Height = 20 });
            y += 22;
        }
        return t;
    }

    private static readonly string[] KimmuCells = { "olu830-35", "5", "BOLAROM", "Item no：olu830-35" };

    [Fact]
    public void 别家客户名整行写死而表里没有_出一条告警()
    {
        var w = TemplateLiteralGuard.Check(
            Template("BOLAROM", "Item no：{{ItemNo}}"),
            new[] { "OLU TRADING" }, new string[0], new[] { "olu830-35", "5" });

        var line = Assert.Single(w);
        Assert.Contains("BOLAROM", line);
        Assert.Contains("OLU TRADING", line);
        Assert.Contains("不自动改", line);
    }

    [Fact]
    public void 词在表里出现过就不喊_金沐自家的单()
    {
        var w = TemplateLiteralGuard.Check(
            Template("BOLAROM", "Item no：{{ItemNo}}"),
            new[] { "BOLAROM" }, new string[0], KimmuCells);

        Assert.Empty(w);
    }

    [Fact]
    public void 表里出现词但没有已知收货人也不喊()
    {
        var w = TemplateLiteralGuard.Check(
            Template("BOLAROM"), Array.Empty<string>(), Array.Empty<string>(), KimmuCells);

        Assert.Empty(w);
    }

    [Fact]
    public void 同一个词出现多行只喊一条()
    {
        var w = TemplateLiteralGuard.Check(
            Template("BOLAROM", "BOLAROM", "Bolarom"),
            new[] { "别家" }, Array.Empty<string>(), new[] { "货" });

        Assert.Single(w);
    }

    [Fact]
    public void 内置四行模板对正常数据不喊()
    {
        var t = Template("{{Consignee}}", "Item no：{{ItemNo}}", "QTY：{{Quantity}} pcs", "Ctns：{{col:本行箱数}}件");
        var w = TemplateLiteralGuard.Check(t, new[] { "金沐客户" }, new[] { "CHINA" }, KimmuCells);
        Assert.Empty(w);
    }

    [Fact]
    public void MADE_IN_行只在批次报过产地且对不上时才喊()
    {
        var t = Template("MADE IN CHINA");

        Assert.Empty(TemplateLiteralGuard.Check(t, Array.Empty<string>(), Array.Empty<string>(), new[] { "货" }));
        Assert.Empty(TemplateLiteralGuard.Check(t, Array.Empty<string>(), new[] { "MADE IN CHINA" }, new[] { "货" }));
        Assert.Single(TemplateLiteralGuard.Check(t, Array.Empty<string>(), new[] { "VIETNAM" }, new[] { "货" }));
    }

    [Fact]
    public void 带中文与多词的行不喊_那是品名不是客户名形状()
    {
        var w = TemplateLiteralGuard.Check(
            Template("ITEM：香水 perfume", "SHIP TO: WALMART", "JP"),
            new[] { "金沐" }, Array.Empty<string>(), new[] { "货" });

        Assert.Empty(w);   // 「JP」两个字母不进判据；两个都是多词行
    }

    [Fact]
    public void 收货人与词互相包含视为对上()
    {
        var w = TemplateLiteralGuard.Check(
            Template("SA-PLUS"), new[] { "SA-PLUS CO LTD" }, Array.Empty<string>(), new[] { "货" });

        Assert.Empty(w);
    }
}
