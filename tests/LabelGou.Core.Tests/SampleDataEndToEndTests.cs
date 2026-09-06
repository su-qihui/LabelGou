using LabelGou.Core.Data;
using LabelGou.Core.Layout;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 拿仓库里真实的样例数据跑一遍 M1 全链路。
/// 单测各层都绿，不代表串起来就好用——这条就是防"每块都对、连起来不对"。
/// </summary>
public class SampleDataEndToEndTests
{
    private static string LocateSample(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "samples", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"找不到样例文件 {fileName}（从 {AppContext.BaseDirectory} 向上查找）");
    }

    [Fact]
    public void 样例装箱单跑通导入到版面全链路()
    {
        var path = LocateSample("样例-唛头装箱单.csv");

        var data = TableImporter.Import(path);
        var profile = MappingSuggester.Suggest(data.Headers, "样例方案");
        var mapped = RecordMapper.Map(data, profile);
        var layout = LayoutEngine.Build(BuiltInTemplates.Standard100x80(), mapped.Records[0],
            new LayoutContext(1, mapped.Count, Path.GetFileName(path)));

        // —— 导入层：UTF-8 BOM 中文表头，9 条数据
        Assert.Equal(9, data.RowCount);
        Assert.Equal(17, data.ColumnCount);
        Assert.Equal("utf-8", data.Encoding!.WebName.ToLowerInvariant());

        // —— 映射层：关键列自动连对，没连上的进"未映射"而不是丢
        Assert.Equal(0, profile.ColumnIndexOf(MarkFieldKey.Consignee));
        Assert.Equal(1, profile.ColumnIndexOf(MarkFieldKey.ContractNo));
        Assert.Equal(2, profile.ColumnIndexOf(MarkFieldKey.PoNumber));
        Assert.Equal(4, profile.ColumnIndexOf(MarkFieldKey.DestinationPort));
        Assert.Equal(5, profile.ColumnIndexOf(MarkFieldKey.GrossWeight));
        Assert.Equal(9, profile.ColumnIndexOf(MarkFieldKey.CartonNo));
        Assert.Equal(10, profile.ColumnIndexOf(MarkFieldKey.CartonTotal));
        Assert.Equal(-1, profile.ColumnIndexOf(MarkFieldKey.ClientCode));   // 表里确实没有这一列

        // —— 数据层：0.068 这类小数按原样带出，不丢精度
        Assert.Equal(9, mapped.Count);
        Assert.Equal("18.50", mapped.Records[0].GetText(MarkFieldKey.GrossWeight));
        Assert.Equal("0.068", mapped.Records[0].GetText(MarkFieldKey.Measurement));
        Assert.Equal("60×40×25", mapped.Records[0].GetText(MarkFieldKey.BoxSize));
        Assert.All(mapped.Issues, issue => Assert.True(issue.Severity != IssueSeverity.Error, issue.Message));

        // —— 未映射列以 col: 保留，模板可引用
        Assert.Equal("PLT-0007", mapped.Records[0].GetCustom("col:托盘号")!.Text);

        // —— 版面层：中文与英文值都落到印面上，件号是 "x / y"
        var texts = layout.Items.OfType<TextItem>().Select(t => t.Content).ToList();
        Assert.Contains(texts, t => t.Contains("WALMART INC."));
        Assert.Contains(texts, t => t.Contains("SD-2026-0831"));
        Assert.Contains(texts, t => t == "C/NOS. 1 / 120");
        Assert.Contains(texts, t => t == "MADE IN CHINA");
        Assert.DoesNotContain(texts, t => t.Contains("{{"));
        Assert.False(layout.HasUnconfirmed);
    }

    [Fact]
    public void 三套内置模板套真实样例数据都不越界不空白()
    {
        var data = TableImporter.Import(LocateSample("样例-唛头装箱单.csv"));
        var mapped = RecordMapper.Map(data, MappingSuggester.Suggest(data.Headers));

        foreach (var template in BuiltInTemplates.All)
        {
            for (var i = 0; i < mapped.Count; i++)
            {
                var layout = LayoutEngine.Build(template, mapped.Records[i], new LayoutContext(i + 1, mapped.Count));
                var visible = layout.Items.OfType<TextItem>().ToList();

                Assert.True(visible.Count >= 3, $"{template.Name} 第 {i + 1} 条几乎没内容");
                Assert.All(visible, t =>
                {
                    Assert.InRange(t.Y, 0, template.HeightMm);
                    Assert.InRange(t.X + t.Width, 0, template.WidthMm + 0.06);
                });
            }
        }
    }

    [Fact]
    public void 中英混排与含空值的那几条不会把残句印出去()
    {
        var data = TableImporter.Import(LocateSample("样例-唛头装箱单.csv"));
        var mapped = RecordMapper.Map(data, MappingSuggester.Suggest(data.Headers));

        // 第 9 条备注前有前导空格、第 7 条备注为空：都不该产生 "Remarks: " 之类残句
        var template = BuiltInTemplates.Bilingual120x90();
        for (var i = 0; i < mapped.Count; i++)
        {
            var layout = LayoutEngine.Build(template, mapped.Records[i], new LayoutContext(i + 1, mapped.Count));
            Assert.DoesNotContain(layout.Items.OfType<TextItem>(), t => t.Content.Trim().EndsWith(":"));
            Assert.DoesNotContain(layout.Items.OfType<TextItem>(), t => t.Content.Contains("  "));
        }
    }
}
