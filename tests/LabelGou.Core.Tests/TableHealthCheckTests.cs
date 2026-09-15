using System.Globalization;
using LabelGou.Core.Data;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 导入层第 1 棒 · 一级校验。
/// <para>夹具全部按 <c>labelgou-CL\</c> 五家真表的<strong>形状复刻</strong>（值取自 2026-09-13 的逐格取证，
/// 探针在 <c>labelgou-other\_probe\import-audit\</c>）。刻意不读真文件：环境门控的测试会静默假绿（§十-A-16）。</para>
/// </summary>
public class TableHealthCheckTests
{
    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    // ── 复刻件 ────────────────────────────────────────────────────────

    /// <summary>合法 EAN-13（校验位按 GS1 模 10 算好）——TOP E 列原样就是这种 13 位码。</summary>
    private static readonly string[] Ean13 =
    {
        "6936664300009", "6936664300016", "6936664300023", "6936664300030",
        "6936664300047", "6936664300054", "6936664300061", "6936664300078",
    };

    /// <summary>TOP 傲晖：411 行，列名写在<strong>最后一行</strong>，货在它上面，最末还挂一个对不上账的总数。</summary>
    private static (List<string[]> Grid, int HeaderRow, int TotalRow, int Sum) TopShape()
    {
        var grid = new List<string[]>();
        var sum = 0;
        for (var i = 0; i < 409; i++)
        {
            var ctn = i % 3 == 0 ? 2 : 1;
            sum += ctn;
            grid.Add(new[]
            {
                N(11150560 + i), $"b5{i:000}*16\n BRAND", N(ctn), "16",
                Ean13[i % Ean13.Length], "1",
            });
        }
        grid.Add(new[] { "客户货号", "产品名称", "件数\nCTN", "装件数\nPCS/CTN", "条形码", "张数\n一开四", "正侧各一" });
        grid.Add(new[] { "", "", N(sum - 1), "", "", "" });   // 差 1 件：工厂改过某行没回头改尾数
        return (grid, 409, 410, sum);
    }

    /// <summary>金沐：表头在第 2 行（一格中英两行），D 列混指令，F 列抄着标签，货号带 *144 重复尾巴。</summary>
    private static List<string[]> JinMuShape()
    {
        var grid = new List<string[]>
        {
            new string[6],
            new[] { "货号\nITEM NO:", "件数\nCTN", "数量\nQTY", "一开四", "", "" },
        };
        string[] tails = { "BOLAROM", "Item no：olu830-35", "QTY：144 pcs", "Ctns：5件" };
        for (var i = 0; i < 12; i++)
        {
            var row = new[] { $"olu830-{35 + i}*144", "5", "144", i == 0 ? "张数等于件数" : "", "", "" };
            if (i < 4) row[5] = tails[i];
            grid.Add(row);
        }
        grid.Add(new[] { "", "60", "", "", "", "" });   // 件数列合计（5×12=60）
        return grid;
    }

    /// <summary>邱总：列名行只有三格，第六列抄着标签样子，货号尾巴 *16 与 QTY 列重复。</summary>
    private static List<string[]> QiuShape()
    {
        var grid = new List<string[]>
        {
            new[] { "ITEM NO", "QTY", "一开四", "", "", "" },
        };
        string[] block = { "JP", "ITEM：香水 perfume", "ITEM NO：aym6101", "QTY：96 PCS" };
        for (var i = 0; i < 10; i++)
        {
            var row = new[] { $"b50{i:000}*16 INVISTUC", "16", "2", "", "", "" };
            if (i < 4) row[5] = block[i];
            grid.Add(row);
        }
        return grid;
    }

    /// <summary>OLU：列名只到 L 列，M 列却装着外箱尺寸；B 列是 WPS 嵌图占位符。</summary>
    private static List<string[]> OluShape()
    {
        var grid = new List<string[]>
        {
            new[] { "", "产品图片\nPictures", "产品名称\nITEM NO", "件数\nCTN", "装件数\nPCS/CTN", "单价\nUNIT PRICE",
                    "总金额\nT/AMOUNT", "单件毛重\nG.W.", "总重量\nT/G.W.", "单件体积\nCBM", "总体积\nT/CBM", "物料代码", "" },
        };
        for (var i = 0; i < 8; i++)
        {
            var row = new string[13];
            row[1] = i == 0 ? "" : "=DISPIMG(\"ID_ABC" + N(i) + "\",1)";
            row[2] = i == 0 ? "olu830-3*144" : $"olu930-{i}*144   停产 Stop production";
            row[3] = "10";
            row[4] = "144";
            row[5] = "3.7";
            row[6] = "5328";
            row[7] = "16.91";
            row[8] = "169.1";
            row[9] = "0.0287";
            row[10] = "0.287";
            row[11] = $"02.01.olu830{i:000}144";
            if (i == 0) row[12] = "35.4*27*30.1";   // 有数据、没列名
            grid.Add(row);
        }
        return grid;
    }

    /// <summary>郑小姐：A 列一格装了两段流水号，F 列写着"一个流水号一张"。</summary>
    private static List<string[]> ZhengShape()
    {
        var grid = new List<string[]>
        {
            new[] { "流水号", "ITEM NO:", "件数", "QTY", "", "" },
        };
        for (var i = 0; i < 8; i++)
        {
            var row = new[] { $"QI YUE: AJ{1 + i * 3}-QI YUE: AJ{3 + i * 3}", $"CC8{30 + i}", "3", "96", "", "" };
            if (i == 0) row[5] = "开二 ";
            if (i == 2) row[5] = "一个流水号一张";
            grid.Add(row);
        }
        return grid;
    }

    private static HealthFinding? Find(HeaderRowDetector.DetectionResult cut, List<string[]> grid, HealthKind kind)
        => TableHealthCheck.Scan(grid, cut).FirstOrDefault(f => f.Kind == kind);

    // ── K-1 列名在表尾 ─────────────────────────────────────────────────

    [Fact]
    public void 中英两行写在一格的列名要认得出_整格归一化会粘成不认识的词()
    {
        Assert.True(HeaderRowDetector.IsKnownFieldName("件数\nCTN"));
        Assert.True(HeaderRowDetector.IsKnownFieldName("货号\nITEM NO:"));
        Assert.True(HeaderRowDetector.IsKnownFieldName("数量"));
        Assert.False(HeaderRowDetector.IsKnownFieldName("6936664304106"));
        // 精确相等口径没放宽：包含匹配会抢列（§五-66）
        Assert.False(HeaderRowDetector.IsKnownFieldName("装件数\nPCS/CTN"));
    }

    [Fact]
    public void 列名写在表尾的TOP形状_认在那一行且行号惩罚不压死它()
    {
        var (grid, headerRow, _, _) = TopShape();

        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        Assert.Equal(headerRow, cut.HeaderRowIndex);
        Assert.Contains("件数", cut.Headers[2]);
        // 行号惩罚不封顶的话：409 × 0.15 = 61 分，会把这一行压到第一行那 2.86 分以下
        Assert.True(cut.HeaderRowIndex > 40);
    }

    [Fact]
    public void 列名在末尾而货在它上面_数据要取表头之上的那些行()
    {
        var (grid, _, totalRow, _) = TopShape();

        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        // 不补这一支，认出列名反而切出零行的表
        Assert.Equal(409, cut.DataRows.Count);
        Assert.Equal(0, cut.DataRowRawIndexes[0]);
        Assert.DoesNotContain(totalRow, cut.DataRowRawIndexes);
        Assert.Empty(cut.PreambleRows);   // 翻转这一支没有"表头以上的批注"可言
    }

    [Fact]
    public void 普通表头在上的形状_切法一字不变_表头以上仍只当批注()
    {
        var grid = JinMuShape();

        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        Assert.Equal(1, cut.HeaderRowIndex);
        Assert.Equal(12, cut.DataRows.Count);   // 合计行被兜底剔掉，不进数据
        Assert.Single(cut.PreambleRows);        // 第 1 行空行仍原样摊着
    }

    // ── K-2 表尾多余的总数 ─────────────────────────────────────────────

    [Fact]
    public void 表尾总数与列和差一件也要认出来_理由要说清差多少()
    {
        var (grid, headerRow, totalRow, sum) = TopShape();

        var hits = SummaryRowSpotter.Find(grid, headerRow);

        var hit = Assert.Single(hits);
        Assert.Equal(totalRow, hit.RawRowIndex);
        Assert.Contains(N(sum - 1), hit.Reason);
        Assert.Contains("差 1", hit.Reason);   // 不许假装它们相等
    }

    [Fact]
    public void 差得多的孤数不当合计_那是少写了货号的真货行()
    {
        var grid = new List<string[]>
        {
            new[] { "件数" }, new[] { "50" }, new[] { "51" }, new[] { "52" }, new[] { "", "150" },
        };

        Assert.Empty(SummaryRowSpotter.Find(grid, 0));
    }

    [Fact]
    public void 合计行没被兜底剔掉时报成可修建议_带着要剔的行号()
    {
        var grid = JinMuShape();
        var off = HeaderRowDetector.Detect(grid, new SheetLayoutChoice(SkipSummaryRows: false));

        var findings = TableHealthCheck.Scan(grid, off);

        var f = Assert.Single(findings, x => x.Kind == HealthKind.SummaryRowNotSkipped);
        Assert.Equal(HealthLevel.Suggestion, f.Level);
        Assert.NotNull(f.Fix);
        var fix = f.Fix!;
        Assert.Equal(HealthFixKind.ExcludeSummaryRows, fix.Kind);
        Assert.Equal(new[] { 14 }, fix.Rows);   // 复刻件里那条合计在第 15 行
    }

    // ── K-3 有数据没列名 / 列名格写指令 ─────────────────────────────────

    [Fact]
    public void 有数据却没列名的列要报出来_OLU的M列外箱尺寸()
    {
        var grid = OluShape();
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        var f = Assert.Single(TableHealthCheck.Scan(grid, cut), x => x.Kind == HealthKind.ColumnWithoutHeader);

        Assert.Equal(12, f.RawColumn);
        Assert.Contains("35.4*27*30.1", f.Evidence);   // 原样引用，不编
        Assert.Equal(HealthLevel.Warning, f.Level);    // 列名要人给，软件不猜
    }

    [Fact]
    public void 列名格写的是一句开法指令_既报缺列名也报它是排版指令()
    {
        var grid = QiuShape();
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        var f = Assert.Single(TableHealthCheck.Scan(grid, cut), x => x.Kind == HealthKind.HeaderIsInstruction);

        Assert.Equal(2, f.RawColumn);   // C 列
        Assert.Contains("一开四", f.Fact);
    }

    // ── K-5 右侧块：文字模板 ≠ 脏数据 ───────────────────────────────────

    [Fact]
    public void 表内文字模板不进面板_也不被当成没列名的脏数据()
    {
        var grid = QiuShape();
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        var findings = TableHealthCheck.Scan(grid, cut);

        // 用户 2026-09-13：「F 列的是模板是给 AI 排版用的所以不用管，不用写」
        Assert.DoesNotContain(HealthKind.SideTextTemplate, findings.Select(f => f.Kind));
        Assert.DoesNotContain("F 列", findings.Select(f => f.Fact));
    }

    [Fact]
    public void 指令点名了来源列_建议就写成把那列的数填过来()
    {
        var grid = JinMuShape();
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        var f = Assert.Single(TableHealthCheck.Scan(grid, cut), x => x.Kind == HealthKind.SideInstruction);

        Assert.Equal(HealthLevel.Suggestion, f.Level);
        Assert.Equal("D 列数值改成 B 列数值", f.Fact);   // 照用户的写法，短到一行读完
        Assert.Contains("张数等于件数", f.Evidence);
        Assert.NotNull(f.Fix);
        var rule = Assert.Single(f.Fix!.Rules!);
        Assert.Equal(ValueRuleKind.FillColumnFrom, rule.Kind);
        Assert.Equal(3, rule.TargetColumn);
        Assert.Equal(1, rule.SourceColumn);
    }

    [Fact]
    public void 值改写只动视图_把D列填成B列并删掉星号尾巴()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "olu830-35*144", "5", "144", "" },
            new[] { "olu830-19*144", "7", "144", "" },
        };

        var after = TableHealthCheck.ApplyValueRules(rows, new[]
        {
            new ValueRule(ValueRuleKind.FillColumnFrom, 3, 1, "张数等于件数"),
            new ValueRule(ValueRuleKind.StripStarTail, 0, Reason: "重复尾巴"),
        });

        Assert.Equal("5", after[0][3]);     // D 列 = B 列
        Assert.Equal("7", after[1][3]);
        Assert.Equal("olu830-35", after[0][0]);   // * 号及后面删掉
        Assert.Equal("olu830-19", after[1][0]);
        Assert.Equal("olu830-35*144", rows[0][0]);   // 传进来的那份不许被改掉（原件永远不动）
    }

    [Fact]
    public void 星号后面是品名或规格的一律不进清单()
    {
        var row = new[] { "OLU4014 *CANDY CLOUDS", "36" };
        Assert.False(TableHealthCheck.IsDuplicateStarTail(row[0], row, 0, out _));
        var size = new[] { "35.4*27*30.1", "3" };
        Assert.False(TableHealthCheck.IsDuplicateStarTail(size[0], size, 0, out _));
        var dup = new[] { "olu830-35*144", "144" };
        Assert.True(TableHealthCheck.IsDuplicateStarTail(dup[0], dup, 0, out var at));
        Assert.Equal(9, at);
    }

    [Fact]
    public void 圈成右侧列后切表指令要描述得出来_且默认指令不受影响()
    {
        var block = new SideBlock(5, SideBlockKind.TextTemplate, new[] { 3 }, "JP");

        var applied = new HealthFix(HealthFixKind.MarkSideColumns, SideBlocks: new[] { block })
            .Apply(SheetLayoutChoice.Auto);

        Assert.True(applied.IsSideColumn(5));
        Assert.False(applied.IsSideColumn(0));
        Assert.False(applied.IsDefault);
        Assert.Contains("表内文字模板", applied.Describe());
        Assert.True(SheetLayoutChoice.Auto.IsDefault);   // 入参不可变：Apply 不许把公用的 Auto 改掉
    }

    // ── K-4 `*` 号尾巴的边界 ────────────────────────────────────────────

    [Fact]
    public void 星号尾巴的数字同行别处也写着_报成重复()
    {
        var grid = QiuShape();
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        var f = Assert.Single(TableHealthCheck.Scan(grid, cut), x => x.Kind == HealthKind.DuplicateStarTail);

        Assert.Equal(HealthLevel.Suggestion, f.Level);
        Assert.Contains("处：删掉 * 号及后面", f.Fact);
        Assert.Contains("b50000*16 INVISTUC", f.Evidence);   // 原样第一例
        Assert.Equal(ValueRuleKind.StripStarTail, Assert.Single(f.Fix!.Rules!).Kind);
    }

    [Fact]
    public void 星号后是品名或规格的不算重复_一条都不许报()
    {
        var grid = new List<string[]>
        {
            new[] { "货号", "件数" },
            new[] { "OLU4014 *CANDY CLOUDS", "36" },
            new[] { "OLU4016 *YARA", "36" },
            new[] { "35.4*27*30.1", "3" },
            new[] { "AL013 *100ml MOUSUF", "48" },
        };
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        Assert.DoesNotContain(HealthKind.DuplicateStarTail,
            TableHealthCheck.Scan(grid, cut).Select(x => x.Kind));
    }

    // ── K-7 条码列 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("6936664304106", true)]    // TOP E 列原样一例
    [InlineData("6936664304107", false)]   // 校验位错一位
    [InlineData("12345670", true)]         // EAN-8
    [InlineData("489713805200", false)]    // 12 位不在此列
    public void GTIN模十校验位按规范算(string digits, bool expected)
        => Assert.Equal(expected, TableHealthCheck.GtinCheckOk(digits));

    [Fact]
    public void 整列十三位纯数字认成条码列_并提醒不许当数值格式化()
    {
        var grid = TopShape().Grid;
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        var f = Assert.Single(TableHealthCheck.Scan(grid, cut), x => x.Kind == HealthKind.BarcodeColumn);

        Assert.Equal(4, f.RawColumn);
        Assert.Contains("条码", f.Fact);
        Assert.Contains("数值格式化", f.Evidence);
    }

    // ── K-8 / K-9 ──────────────────────────────────────────────────────

    [Fact]
    public void WPS嵌图占位符要报出来_别当文本绑上字段()
    {
        var grid = OluShape();
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        var f = Assert.Single(TableHealthCheck.Scan(grid, cut), x => x.Kind == HealthKind.EmbeddedImagePlaceholder);

        Assert.Equal(HealthLevel.Warning, f.Level);
        Assert.Contains("cellimages", f.Evidence);
    }

    [Fact]
    public void 一格装两段流水号汇总成一条_一行出几张由人定()
    {
        var grid = ZhengShape();
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        var f = Assert.Single(TableHealthCheck.Scan(grid, cut), x => x.Kind == HealthKind.MultipleValuesInOneCell);

        Assert.Equal(HealthLevel.Warning, f.Level);   // 软件不猜该出几张
        Assert.Contains("8 格", f.Fact);             // 八行同类问题只报一条，不刷屏
        Assert.Contains("QI YUE: AJ1-QI YUE: AJ3", f.Evidence);
    }

    // ── K-10 多 sheet ──────────────────────────────────────────────────

    [Fact]
    public void 空表与多张都有数据的表都要说()
    {
        var findings = TableHealthCheck.ScanSheets(new[]
        {
            ("Sheet1", 30), ("Sheet2", 0), ("Sheet3", 12),
        });

        Assert.Equal(2, findings.Count);
        Assert.Single(findings, f => f.Kind == HealthKind.SheetWithoutData);
        var multi = Assert.Single(findings, f => f.Kind == HealthKind.MultipleSheetsWithData);
        Assert.Contains("Sheet3", multi.Evidence);
        Assert.DoesNotContain("Sheet2", multi.Evidence);   // 空表不混进"有数据的"清单
    }

    [Fact]
    public void 只有一张有数据时不报多张_但空表仍要说()
    {
        var findings = TableHealthCheck.ScanSheets(new[] { ("Sheet1", 30), ("Sheet2", 0) });

        Assert.Single(findings);
        Assert.Equal(HealthKind.SheetWithoutData, findings[0].Kind);
    }

    // ── 修复动作 ───────────────────────────────────────────────────────

    [Fact]
    public void 挪表头的修复落到指令上_并保留已点名剔掉的行()
    {
        var start = new SheetLayoutChoice(ExcludedRawRows: new[] { 7 });

        var applied = new HealthFix(HealthFixKind.MoveHeaderRow, HeaderRowIndex: 409).Apply(start);

        Assert.Equal(409, applied.HeaderRowIndex);
        Assert.True(applied.HasHeader);
        Assert.Equal(new[] { 7 }, applied.ExcludedRawRows);   // 不冲掉用户点过名的那些
    }

    [Fact]
    public void 剔行修复与已点名行取并集且去重升序()
    {
        var start = new SheetLayoutChoice(ExcludedRawRows: new[] { 14, 3 });

        var applied = new HealthFix(HealthFixKind.ExcludeSummaryRows, Rows: new[] { 14, 9 }).Apply(start);

        Assert.Equal(new[] { 3, 9, 14 }, applied.ExcludedRawRows);
    }

    // ── 不打扰：干净表不该冒出一堆建议 ─────────────────────────────────

    [Fact]
    public void 一张干净的表不该冒出可修建议()
    {
        var grid = new List<string[]>
        {
            new[] { "货号", "件数", "毛重" },
            new[] { "A1", "5", "12.5" },
            new[] { "A2", "7", "18" },
            new[] { "A3", "3", "9" },
        };
        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        Assert.DoesNotContain(HealthLevel.Suggestion, TableHealthCheck.Scan(grid, cut).Select(f => f.Level));
    }
}
