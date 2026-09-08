using LabelGou.Core.Data;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;
using System.Text;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 「AI 先读懂这张表」这一层的钉子（M7 第 15 棒）。
/// <para>用户 2026-09-08 的 diagnosis：先做了自动绑定，AI 收到的信息就是绑定的结果——
/// 绑对了它只会重复，绑错了它不知道，表里没连上的列对它干脆不存在。
/// 所以这里钉的是：<strong>整张表（含没连上的列）必须如实摊给模型</strong>，
/// 而模型写回来的列名必须能对上真表头，对不上就剥掉（不许它编一个不存在的列）。</para>
/// </summary>
public class TablePortraitAiInputTests
{
    private static TabularData VendorTable() => new(
        "真表.xlsx", "Sheet1",
        new[] { "流水号", "件数\nCTN", "货号 ITEM NO", string.Empty },
        new List<IReadOnlyList<string>>
        {
            new List<string> { "QI YUE: AJ7-QI YUE: AJ9", "3", "b5003*16  BLUE DE CHALLENGE", "开二" },
            new List<string> { "QI YUE: AJ10", "5", "b5004*8", " " },
            new List<string> { "QI YUE: AJ10", "", "b5005", "" },
        },
        headerRowIndex: 0);

    private static MappingProfile Bound => new()
    {
        Mappings =
        {
            new FieldMapping { Field = MarkFieldKey.ItemNo, ColumnIndex = 2, ColumnHeader = "货号 ITEM NO" },
            new FieldMapping { Field = MarkFieldKey.CartonTotal, ColumnIndex = 1, ColumnHeader = "件数\nCTN" },
            new FieldMapping { Field = MarkFieldKey.GrossWeight, ColumnIndex = -1 },   // 没连上，不该混进画像的"已连"里
        },
    };

    [Fact]
    public void 没连上字段的列也进画像而且标明没连()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);

        Assert.Equal(4, columns.Count);
        Assert.Null(columns[0].BoundField);                      // 流水号：软件没用它，但模型必须看得见
        Assert.Equal("ItemNo", columns[2].BoundField);
        Assert.Equal("货号 ITEM NO", columns[2].Header);
        Assert.Equal("总件数", columns[1].BoundFieldName);        // 中文名取自目录，不另写一份
    }

    [Fact]
    public void 摊给模型的文字里写着这一列填了几行和真实样例()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);
        var text = TablePortrait.Describe(columns, rowCount: 3);

        Assert.Contains("3 行 × 4 列", text);
        Assert.Contains("流水号", text);
        Assert.Contains("没连到任何字段", text);
        Assert.Contains("QI YUE: AJ7-QI YUE: AJ9", text);        // 样例是表里的原文，不是我们清洗后的值
        Assert.Contains("整列填了 1 行", text);                    // 第四列只有表尾一个"开二"，模型据此认出那是批注
    }

    [Fact]
    public void 表头带换行时值保持原样只有显示名折平()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);

        Assert.Equal("件数\nCTN", columns[1].Header);            // 渲染端查 col: 键用的就是原样
        Assert.Equal("件数 / CTN", columns[1].Label);            // 给人和模型看的那一行
    }

    [Fact]
    public void 样例去重限量而空白格不计入()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);

        Assert.Equal(2, columns[0].Samples.Count);               // AJ10 出现两次只留一个
        Assert.Equal(3, columns[0].NonEmptyCount);
        Assert.Equal(2, columns[1].NonEmptyCount);               // 第三行那格是空的
        Assert.Equal(1, columns[3].NonEmptyCount);               // 只有一个 " "，按空白算
    }

    [Fact]
    public void 超长样例被截断而不是整段塞进提示词()
    {
        var data = new TabularData("t.xlsx", "S", new[] { "备注" },
            new List<IReadOnlyList<string>> { new List<string> { new string('长', 90) } }, 0);

        var columns = TablePortrait.Build(data, null);

        Assert.Equal(TablePortrait.MaxSampleChars + 1, columns[0].Samples[0].Length);
        Assert.EndsWith("…", columns[0].Samples[0]);
    }

    [Fact]
    public void 列名四档都能对上真表头而认错的不放行()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);

        Assert.Equal("件数\nCTN", TablePortrait.ResolveHeader(columns, "件数\nCTN"));   // 原样
        Assert.Equal("件数\nCTN", TablePortrait.ResolveHeader(columns, "  件数\nCTN ")); // 去首尾空白
        Assert.Equal("件数\nCTN", TablePortrait.ResolveHeader(columns, "件数 / CTN"));   // 折平显示名
        Assert.Equal("货号 ITEM NO", TablePortrait.ResolveHeader(columns, "货号 item no")); // 不分大小写
        Assert.Null(TablePortrait.ResolveHeader(columns, "打印张数"));                  // 表里没这列
        Assert.Null(TablePortrait.ResolveHeader(columns, null));
        Assert.Null(TablePortrait.ResolveHeader(null, "流水号"));
    }

    [Fact]
    public void 提示词带上画像时第一次告诉模型可以直接取列()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);
        var prompt = RowLayoutPrompt.Build(
            new[] { ("ItemNo", "货号", "b5003") }, 140, 100, null,
            TablePortrait.Describe(columns, rowCount: 3));

        Assert.Contains("整张表原样摊开（含没连上的列）", prompt);
        Assert.Contains("{{col:列名}}", prompt);
        Assert.Contains("没连到任何字段", prompt);
        Assert.Contains("流水号", prompt);
        Assert.Contains("不要拿来印", prompt);                    // 哪些不要，也是它要回答的问题
    }

    [Fact]
    public void 没给画像时提示词退回旧口径不凭空提能力()
    {
        var prompt = RowLayoutPrompt.Build(new[] { ("ItemNo", "货号", "b5003") }, 140, 100, null);

        Assert.DoesNotContain("整张表原样摊开", prompt);
        Assert.DoesNotContain("{{col:", prompt);
        Assert.Contains("只能用上面列出的字段键，别的字段一律不要写", prompt);
    }

    [Fact]
    public void 一个字段都没连上时提示词说实话而不是留个空清单()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);
        var prompt = RowLayoutPrompt.Build(Array.Empty<(string, string, string)>(), 140, 100, null,
            TablePortrait.Describe(columns, rowCount: 3));

        Assert.Contains("一个都没连上", prompt);
        Assert.Contains("【取舍】", prompt);
    }

    [Fact]
    public void 模型写的折平列名会被折算成真表头并写明补全()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);

        var p = RowLayoutJsonParser.Parse(
            """{"rows":[{"content":"Ctns：{{col:件数 / CTN}}件"}]}""", columns);

        Assert.True(p.HasSpec, string.Join("；", p.Errors));
        Assert.Equal("Ctns：{{col:件数\nCTN}}件", p.Spec!.Rows[0].Content);   // 带换行的原样，渲染端认这个
        Assert.Contains(p.Notes, n => n.Contains("已按表头原样补全"));
    }

    [Fact]
    public void 表里没有的列名被剥掉并说清为什么()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);

        var p = RowLayoutJsonParser.Parse(
            """{"rows":[{"content":"流水号：{{col:流水号}} 张数：{{col:打印张数}}"}]}""", columns);

        Assert.True(p.HasSpec);
        Assert.Contains("{{col:流水号}}", p.Spec!.Rows[0].Content);            // 没连上的列照样能取
        Assert.DoesNotContain("打印张数", p.Spec.Rows[0].Content);             // 表里没有的不许印
        Assert.Contains(p.Notes, n => n.Contains("在这张表里找不到") && n.Contains("打印张数"));
    }

    [Fact]
    public void 没给表画像时col_的行为与以前一致不新加门槛()
    {
        var p = RowLayoutJsonParser.Parse("""{"rows":[{"content":"{{col:任意列}}"}]}""");

        Assert.True(p.HasSpec);
        Assert.Equal("{{col:任意列}}", p.Spec!.Rows[0].Content);
    }

    [Fact]
    public void 画像不清洗数据也不裁决谁该印()
    {
        var columns = TablePortrait.Build(VendorTable(), Bound);
        var text = TablePortrait.Describe(columns, rowCount: 3);

        // 复合格式（货号*数量 + 品名）与清洗后的值都被区分对待：画像只摊原文。
        Assert.Contains("b5003*16  BLUE DE CHALLENGE", text);
        Assert.DoesNotContain("建议", text);
        Assert.DoesNotContain("应该", text);
        var sb = new StringBuilder(text);
        Assert.True(sb.Length < 4_000, $"整份画像不该撑爆提示词，现在 {sb.Length} 字");
    }
}
