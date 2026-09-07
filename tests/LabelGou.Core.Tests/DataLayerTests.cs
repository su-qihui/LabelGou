using LabelGou.Core.Data;
using Xunit;

namespace LabelGou.Core.Tests;

public class CsvTableReaderTests
{
    [Fact]
    public void 基本逗号分隔_带引号字段与转义引号()
    {
        const string text = "客户,合同号,备注\nWALMART,SD-001,\"含,逗号的备注\"\n\"带\"\"引号\"\"的客户\",SD-002,ok\n";
        var grid = CsvTableReader.Parse(text, ',');

        Assert.Equal(3, grid.Count);
        Assert.Equal(new[] { "客户", "合同号", "备注" }, grid[0]);
        Assert.Equal("含,逗号的备注", grid[1][2]);
        Assert.Equal("带\"引号\"的客户", grid[2][0]);
    }

    [Fact]
    public void 字段内换行不拆行()
    {
        const string text = "a,b\n\"第一行\n第二行\",2\n";
        var grid = CsvTableReader.Parse(text, ',');

        Assert.Equal(2, grid.Count);
        Assert.Equal("第一行\n第二行", grid[1][0]);
    }

    [Theory]
    [InlineData("a;b;c\n1;2;3\n", ';')]
    [InlineData("a\tb\tc\n1\t2\t3\n", '\t')]
    [InlineData("a|b|c\n1|2|3\n", '|')]
    public void 分隔符嗅探(string text, char expected)
    {
        var lines = text.Split('\n');
        Assert.Equal(expected, TextDecoder.SniffDelimiter(lines));
    }

    [Fact]
    public void GB18030编码的中文CSV能正确解码()
    {
        TextDecoder.EnsureCodePages();
        var gbk = System.Text.Encoding.GetEncoding("GB18030");
        var bytes = gbk.GetBytes("客户,合同号\n深圳顺达,SD-2026-0831\n");

        var decoded = TextDecoder.Decode(bytes, out var encoding);

        Assert.Contains("深圳顺达", decoded);
        // GB18030 提供程序的 WebName 就是 gb18030（不是 UTF-8 就行）
        Assert.Equal("gb18030", encoding.WebName.ToLowerInvariant());
    }

    [Fact]
    public void UTF8带BOM优先于内容嗅探()
    {
        var withBom = new List<byte> { 0xEF, 0xBB, 0xBF };
        withBom.AddRange(System.Text.Encoding.UTF8.GetBytes("客户\n顺达\n"));

        var decoded = TextDecoder.Decode(withBom.ToArray(), out var encoding);

        Assert.Equal(System.Text.Encoding.UTF8.WebName, encoding.WebName);
        Assert.StartsWith("客户", decoded);
    }
}

public class HeaderRowDetectorTests
{
    [Fact]
    public void 跳过前导标题行选真正的表头()
    {
        var grid = new List<string[]>
        {
            new[] { "深圳顺达贸易有限公司 装箱单", "", "", "" },
            new[] { "", "", "", "" },
            new[] { "客户", "合同号", "毛重", "件号" },
            new[] { "WALMART", "SD-001", "18.5", "3" },
        };

        var result = HeaderRowDetector.Detect(grid);

        Assert.Equal(2, result.HeaderRowIndex);
        Assert.Equal(new[] { "客户", "合同号", "毛重", "件号" }, result.Headers);
        Assert.Single(result.DataRows);
        Assert.Equal("WALMART", result.DataRows[0][0]);
    }

    [Fact]
    public void 空表头列补成列字母_重名加序号()
    {
        var grid = new List<string[]>
        {
            new[] { "客户", "", "客户", "合同号" },
            new[] { "A", "b", "C", "d" },
        };

        var result = HeaderRowDetector.Detect(grid);

        Assert.Equal("列 B", result.Headers[1]);
        Assert.Equal("客户 (2)", result.Headers[2]);
    }

    [Fact]
    public void 列字母换算()
    {
        Assert.Equal("A", HeaderRowDetector.ColumnLetter(0));
        Assert.Equal("Z", HeaderRowDetector.ColumnLetter(25));
        Assert.Equal("AA", HeaderRowDetector.ColumnLetter(26));
        Assert.Equal("AB", HeaderRowDetector.ColumnLetter(27));
    }
}

public class XlsxTableReaderTests
{
    [Fact]
    public void 读取共享字符串与内联字符串()
    {
        var shared = new List<string> { "客户", "合同号", "rich:深圳|顺达" };
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "s:0", "s:1" },
            new[] { "s:2", "inline:SD-001" },
        };
        var path = XlsxFixture.WriteToTempFile(XlsxFixture.Build(rows, shared), "basic.xlsx");

        var grid = XlsxTableReader.ReadRawGrid(path);

        Assert.Equal("客户", grid[0][0]);
        Assert.Equal("深圳顺达", grid[1][0]);      // 富文本多段要拼完整
        Assert.Equal("SD-001", grid[1][1]);        // inlineStr
    }

    [Fact]
    public void 合并单元格把锚点值传播到区域内每格()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "收货人 To", "", "", "件号" },
            new[] { "WALMART", "x", "y", "3" },
        };
        var path = XlsxFixture.WriteToTempFile(
            XlsxFixture.Build(rows, merges: new[] { "A1:C1" }), "merged.xlsx");

        var grid = XlsxTableReader.ReadRawGrid(path);

        Assert.Equal("收货人 To", grid[0][0]);
        Assert.Equal("收货人 To", grid[0][1]);
        Assert.Equal("收货人 To", grid[0][2]);
    }

    [Fact]
    public void 纵向合并不往每一行复制值也不把空行复活()
    {
        // 真表里的 F7:F10、备注整列合并这类是「只填一次的批注与合计」。
        // 旧实现把锚点值复制进区域内每一行，还为传播新建行：
        // 155 就变成每行的数、行数虚高 → 页数与 {{CartonTotal}} 跟着错（批次一-7）。
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "客户", "件数", "备注" },
            new[] { "A", "5", "155 合计" },
            new[] { "B", "6", "" },
            Array.Empty<string>(),              // 第 4 行整行没内容（真 XLSX 里根本不会写这一行）
        };
        var path = XlsxFixture.WriteToTempFile(
            XlsxFixture.Build(rows, merges: new[] { "C2:C4" }), "vmerged.xlsx");

        var grid = XlsxTableReader.ReadRawGrid(path);

        Assert.Equal(3, grid.Count);                              // 不能因为传播就把空行救活
        Assert.DoesNotContain("155 合计", grid[2]);      // 第三行的备注仍应为空
    }

    [Fact]
    public void 日期样式还原成日期文本而不是OA序列号()
    {
        var target = new DateTime(2026, 9, 12);
        var oa = target.ToOADate();

        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "出货日期" },
            new[] { "n:" + oa.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
        var path = XlsxFixture.WriteToTempFile(
            XlsxFixture.Build(rows, dateCells: new[] { "A2" }), "dates.xlsx");

        var grid = XlsxTableReader.ReadRawGrid(path);

        Assert.Equal("2026/9/12", grid[1][0]);
    }

    [Fact]
    public void 数值按单元格格式补齐小数位()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "毛重" },
            new[] { "n:0.5" },
        };
        var path = XlsxFixture.WriteToTempFile(
            XlsxFixture.Build(rows, decimalCells: new[] { "A2" }), "decimals.xlsx");

        var grid = XlsxTableReader.ReadRawGrid(path);

        // 存储值是 0.5，但格式 0.00 → 打印要显示 "0.50"
        Assert.Equal("0.50", grid[1][0]);
    }

    [Fact]
    public void 空行被丢弃且尾列补齐()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "客户", "合同号", "毛重" },
            new[] { "A1", "B1" },
            Array.Empty<string>(),
            new[] { "A3", "B3", "C3" },
        };
        var path = XlsxFixture.WriteToTempFile(XlsxFixture.Build(rows), "ragged.xlsx");

        var grid = XlsxTableReader.ReadRawGrid(path);

        // 表头行 + 两条数据行（中间的全空行被丢）
        Assert.Equal(3, grid.Count);
        Assert.Equal(3, grid[1].Length);
        Assert.Equal(string.Empty, grid[1][2]);
    }

    [Fact]
    public void 工作表名可枚举且可指名读取()
    {
        var rows = new List<IReadOnlyList<string>> { new[] { "x" }, new[] { "y" } };
        var path = XlsxFixture.WriteToTempFile(
            XlsxFixture.Build(rows, sheetName: "箱唛表"), "named.xlsx");

        var names = XlsxTableReader.ListSheetNames(path);
        var grid = XlsxTableReader.ReadRawGrid(path, "箱唛表");

        Assert.Equal(new[] { "箱唛表" }, names);
        Assert.Equal("x", grid[0][0]);
    }

    [Fact]
    public void 整个导入链路_从xlsx到TabularData()
    {
        var shared = new List<string> { "客户", "合同号", "毛重", "件号", "WALMART", "SD-001" };
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "深圳顺达装箱单", "", "", "" },
            new[] { "s:0", "s:1", "s:2", "s:3" },
            new[] { "s:4", "s:5", "n:18.5", "n:3" },
        };
        var path = XlsxFixture.WriteToTempFile(XlsxFixture.Build(rows, shared), "pipeline.xlsx");

        var data = TableImporter.Import(path);

        Assert.Equal(1, data.RowCount);
        Assert.Equal(new[] { "客户", "合同号", "毛重", "件号" }, data.Headers);
        Assert.Equal("WALMART", data.GetCell(0, 0));
        Assert.Equal("18.5", data.GetCell(0, 2));
        Assert.Contains("pipeline.xlsx", data.Describe());
    }
}
