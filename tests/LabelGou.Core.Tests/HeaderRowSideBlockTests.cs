using System;
using System.Collections.Generic;
using System.Linq;
using LabelGou.Core.Data;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 90 棒①：表头行被<strong>右侧那块文字模板</strong>带跑（用户 2026-09-20 一句「软件存在乱认列头情况」+ 一张截图）。
/// <para>真表现场（爆朵唛头 -9.20.xlsx / Sheet1）：列名在 Excel 第 1 行（客户唛头｜装件数 PCS/CTN｜张数 一开四），
/// G 列 1~3 行是厂方手抄的标签样子（SHJ / ITEM NO: SHJ-278 / QTY: 144 PCS）。软件认到了<strong>第 3 行</strong>，
/// 于是预览的列名一行写成「SHJ-280 | 144 | 1 | 列 D | 列 E | 列 F | ITEM NO: SHJ-278」——那是一条货。</para>
/// <para>查出来的机制比"右侧块多占一格"更具体：打分给每个<strong>字段别名命中 +4 分</strong>，
/// 而别名判定允许"按空白分段"命中（那条口径是为「件数⏎CTN」这类两行列名建的），
/// 于是 <c>ITEM NO: SHJ-278</c> 里的 <c>ITEM</c> 一段命中了货号别名 → 那条货 5.985 分，
/// 真表头因为三个名字都不在词典里（客户唛头／装件数 PCS/CTN／张数 一开四）只得 2.857 分。</para>
/// <para>两张网：① 选行时<strong>不参与打分</strong>的是稀疏右侧列（数据一格不许少，只是不许用它决定哪行是表头）；
/// ② 冒号后头还带着值的格子不算"列名"——<c>ITEM NO: SHJ-278</c> 是填好的一条内容，<c>货号 ITEM NO:</c> 才是列名。</para>
/// </summary>
public sealed class HeaderRowSideBlockTests
{
    /// <summary>爆朵那张的形状复刻（形状 replica，不读真文件——环境门控的测试会假绿）。</summary>
    private static List<string[]> BaoDuoShape()
    {
        // 截图里那 14 行货：278、280、281…288，然后跳到 294…297（中间那几个号本表就没有）
        var items = new[]
        {
            "SHJ-278", "SHJ-280", "SHJ-281", "SHJ-282", "SHJ-283", "SHJ-284", "SHJ-285",
            "SHJ-286", "SHJ-287", "SHJ-288", "SHJ-294", "SHJ-295", "SHJ-296", "SHJ-297",
        };
        var grid = new List<string[]>
        {
            new[] { "客户唛头", "装件数 PCS/CTN", "张数 一开四", "", "", "", "SHJ" },
        };
        for (var i = 0; i < items.Length; i++)
        {
            // G 列那一块就是厂方手抄的标签样子：SHJ / ITEM NO: SHJ-278 / QTY: 144 PCS
            var side = i switch { 1 => "ITEM NO: SHJ-278", 2 => "QTY: 144 PCS", _ => string.Empty };
            grid.Add(new[] { items[i], "144", "1", "", "", "", side });
        }
        return grid;
    }

    [Fact]
    public void 爆朵那张_列名认第一行而不是被右侧文字模板带跑()
    {
        var cut = HeaderRowDetector.Detect(BaoDuoShape(), SheetLayoutChoice.Auto);

        Assert.Equal(0, cut.HeaderRowIndex);
        Assert.Equal("客户唛头", cut.Headers[0]);
        Assert.Equal("装件数 PCS/CTN", cut.Headers[1]);
        Assert.Equal("张数 一开四", cut.Headers[2]);
    }

    [Fact]
    public void 认对表头之后_行数与货行都是那十四行()
    {
        // 认错表头的另一半代价：Excel 第 2、3 行那两条货被当成表头之上，静默少印两箱。
        var cut = HeaderRowDetector.Detect(BaoDuoShape(), SheetLayoutChoice.Auto);

        Assert.Equal(14, cut.DataRows.Count);
        Assert.Equal("SHJ-278", cut.DataRows[0][0]);
        Assert.Equal("SHJ-297", cut.DataRows[^1][0]);
        Assert.Equal(1, cut.DataRowRawIndexes[0]);      // 网格下标必须等于 Excel 行号－1
        Assert.Equal(14, cut.DataRowRawIndexes[^1]);
    }

    [Fact]
    public void 排除右侧那一列只影响选行_数据一格都不许少()
    {
        // 硬账：那一块是厂方手抄的标签样子，是版式层唯一的表内参照物——认出来、圈出去，绝不删。
        var cut = HeaderRowDetector.Detect(BaoDuoShape(), SheetLayoutChoice.Auto);

        Assert.Equal(7, cut.Headers.Count);
        Assert.Contains("ITEM NO: SHJ-278", cut.DataRows.Select(r => r[6]));
        Assert.Contains("QTY: 144 PCS", cut.DataRows.Select(r => r[6]));
    }

    [Fact]
    public void 冒号后头带着值的格子不算列名_收尾冒号仍算()
    {
        Assert.False(HeaderRowDetector.IsKnownFieldName("ITEM NO: SHJ-278"));
        Assert.False(HeaderRowDetector.IsKnownFieldName("QTY：144 PCS"));
        // 收尾一个冒号（货号 ITEM NO:）照旧算名字
        Assert.True(HeaderRowDetector.IsKnownFieldName("货号 ITEM NO:"));
        Assert.True(HeaderRowDetector.IsKnownFieldName("件数\nCTN"));
        // 这张表的三个真列名一个都不在词典里——这正是它只得 2.857 分、输给一条货的原因（第 90 棒①的现场）
        Assert.False(HeaderRowDetector.IsKnownFieldName("装件数 PCS/CTN"));
        Assert.False(HeaderRowDetector.IsKnownFieldName("张数 一开四"));
        Assert.False(HeaderRowDetector.IsKnownFieldName("客户唛头"));
    }

    [Fact]
    public void 只有稀疏的右侧块被排掉_装着真数据的密列不排()
    {
        // 排错一列 = 那一列再也没法被认成列名（OLU 的 M 列就是真装着外箱尺码的密列，见导入层实测账）。
        var baoDuo = HeaderRowDetector.SideColumnIndexes(BaoDuoShape());
        Assert.Contains(6, baoDuo);                     // G 列：15 行里只占 3 格 = 那块手抄的标签样子

        var grid = new List<string[]>
        {
            new[] { "货号", "件数", "外箱尺码" },
        };
        for (var i = 0; i < 12; i++) grid.Add(new[] { $"a{i}", "1", "35.4*27*30.1" });
        Assert.DoesNotContain(2, HeaderRowDetector.SideColumnIndexes(grid));

        // 只有左边一块的普通表：谁都不排
        Assert.Empty(HeaderRowDetector.SideColumnIndexes(new List<string[]>
        {
            new[] { "客户", "货号", "件数", "毛重" },
            new[] { "邱总", "aym6101", "96", "1200" },
            new[] { "邱总", "aym6102", "48", "600" },
        }));
    }

    [Fact]
    public void 一张没有右侧块的表_选行结果不许被这次改动动过()
    {
        // 五家真表里那四张干净的形状必须照旧（这条是"别顺手改坏别人"的护栏）。
        var grid = new List<string[]>
        {
            new[] { "客户", "货号", "件数", "毛重" },
            new[] { "邱总", "aym6101", "96", "1200" },
            new[] { "邱总", "aym6102", "48", "600" },
        };

        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        Assert.Equal(0, cut.HeaderRowIndex);
        Assert.Equal(2, cut.DataRows.Count);
    }

    [Fact]
    public void 表头在表尾那种形状照旧认得_稀疏判据不许把它挤掉()
    {
        // TOP：411 行表把列名写在第 410 行。行号惩罚有顶，就是为了这一张。
        var grid = new List<string[]>();
        for (var i = 0; i < 409; i++) grid.Add(new[] { $"h{i}", "1", "2" });
        grid.Add(new[] { "货号", "件数 CTN", "毛重" });
        grid.Add(new[] { "合计", "578", "" });

        var cut = HeaderRowDetector.Detect(grid, SheetLayoutChoice.Auto);

        Assert.Equal(409, cut.HeaderRowIndex);
    }
}
