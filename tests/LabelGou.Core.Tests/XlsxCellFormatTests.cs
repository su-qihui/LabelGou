using System.IO;
using System.Linq;
using LabelGou.Core.Data;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 「AI 得看得见表格里的字体」这一层的钉子（第 31 棒）。
/// <para>用户 2026-09-10 真机实测后自己判断出了根因：*"可能存在问题原因 AI 读不到表格中字体,粗细,居中等"*
/// ——**确实如此**。在这之前 <c>styles.xml</c> 只被用来认日期与小数位，字体、粗体、对齐一个字都没读；
/// 而表里那块"抄标签"的样例（哪行加粗、哪行大、哪行居中）**正是"标签该长什么样"的唯一依据**。
/// 用户对效果的要求也说得很具体：*"应该第一行字为粗及大，下 3 行为同样大小同细"*。</para>
/// <para>这里钉两件事：① 单元格的字号/粗体/居中都读得出来；② **只报跟同一列主流格式不一样的格子**
/// （逐格流水账会把画像淹掉，而"整列都是 11pt"这种默认格式报出来纯属噪音）。</para>
/// </summary>
public sealed class XlsxCellFormatTests
{
    private static string WriteToTemp(byte[] bytes, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void 读出大字粗体居中_而且一句话就把这一列的格式说全()
    {
        // 第 1 行是表头（默认格式），第 2 行大字粗体居中，第 3/4 行 10pt 常规左对齐。
        var rows = new[]
        {
            new[] { "ITEM NO", "甲列", "样例" },
            new[] { "olu830-35", "x", "BOLAROM" },
            new[] { "olu830-36", "x", "Item no: olu830-35" },
            new[] { "olu830-37", "x", "QTY: 144 pcs" },
        };
        var bytes = XlsxFixture.Build(rows,
            bigBoldCells: new[] { "C2" },
            smallCells: new[] { "C3", "C4" });
        var path = WriteToTemp(bytes, "fmt.xlsx");

        var lines = XlsxTableReader.DescribeCellFormats(path);

        // 这一列只报一行就够：报"跟多数不同的那一块"，并且把"其余格是什么"一并说清——
        // 两句话会把同一件事拆开说两遍（用户 2026-09-10 刚抱怨过"讲得混乱"）。
        var cLine = Assert.Single(lines, l => l.Contains("C 列"));
        Assert.Contains("第 2 行", cLine);
        Assert.Contains("14pt", cLine);        // 用户的要求："应该第一行字为粗及大"
        Assert.Contains("粗体", cLine);
        Assert.Contains("居中", cLine);
        Assert.Contains("10pt", cLine);        // "下 3 行为同样大小同细" —— 其余格的字号也要说
        Assert.Contains("常规", cLine);
        Assert.Contains("左对齐", cLine);

        // A 列（货号列）整列都是默认格式 → 一个字都不该冒出来（不然画像会被"11pt 常规"刷屏）。
        Assert.DoesNotContain(lines, l => l.Contains("A 列"));
    }

    [Fact]
    public void 整列格式都一样时不报_那是噪音不是依据()
    {
        var rows = new[]
        {
            new[] { "ITEM NO", "样例" },
            new[] { "olu830-35", "BOLAROM" },
            new[] { "olu830-36", "Item no: olu830-35" },
        };
        // 整列都是大字粗体：没有"跟别处不一样"的格子，就不该有输出。
        var bytes = XlsxFixture.Build(rows, bigBoldCells: new[] { "B2", "B3" });
        var path = WriteToTemp(bytes, "fmt-uniform.xlsx");

        var lines = XlsxTableReader.DescribeCellFormats(path);

        Assert.DoesNotContain(lines, l => l.Contains("B 列"));
    }

    [Fact]
    public void 读不了的文件不抛_返回空让画像少一段而已()
    {
        var lines = XlsxTableReader.DescribeCellFormats(Path.Combine(Path.GetTempPath(), "根本没有这个文件.xlsx"));

        Assert.Empty(lines);      // 格式读不出来不该挡主流程
    }
}
