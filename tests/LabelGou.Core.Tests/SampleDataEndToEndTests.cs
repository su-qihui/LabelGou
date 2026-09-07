using LabelGou.Core.Data;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using LabelGou.Core.Numbering;
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
    public void 全部内置模板套真实样例数据都不越界不空白()
    {
        var data = TableImporter.Import(LocateSample("样例-唛头装箱单.csv"));
        var mapped = RecordMapper.Map(data, MappingSuggester.Suggest(data.Headers));

        foreach (var template in BuiltInTemplates.All)
        {
            for (var i = 0; i < mapped.Count; i++)
            {
                var layout = LayoutEngine.Build(template, mapped.Records[i], new LayoutContext(i + 1, mapped.Count));
                var visible = layout.Items.OfType<TextItem>().ToList();

                // 下限从 3 改成 2：行式骨架里的「大字两行 160×120」抄的就是郑小姐那张真样张，
                // 它本来就只有两行超大字——要求第三行等于否认真样张。真正要守的是“不空白且不越界”。
                Assert.True(visible.Count >= 2, $"{template.Name} 几乎没内容");
                Assert.All(visible, t => Assert.False(string.IsNullOrWhiteSpace(t.Content), $"{template.Name} 有一行是空的"));
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

    [Fact]
    public void 样例装箱单跑通编号到整版链路()
    {
        var data = TableImporter.Import(LocateSample("样例-唛头装箱单.csv"));
        var mapped = RecordMapper.Map(data, MappingSuggester.Suggest(data.Headers, "样例方案"));
        var template = BuiltInTemplates.Standard100x80();

        // —— 沿用模式：一行一张，件号完全按数据走（M1 行为不得回退）
        var keep = NumberingEngine.Apply(mapped.Records, new NumberingRule { Mode = NumberingMode.KeepData });
        Assert.Equal(9, keep.LabelCount);
        Assert.Equal("120", keep.Labels[0].GetText(MarkFieldKey.CartonTotal));

        // —— 按合同号分组重排：四份合同各自从 1 起号、各自算总件数
        var grouped = NumberingEngine.Apply(mapped.Records, new NumberingRule
        {
            Mode = NumberingMode.ForceSequence,
            Scope = NumberingScope.PerGroup,
            GroupByField = MarkFieldKey.ContractNo,
        });
        Assert.Equal(4, grouped.GroupCount);
        Assert.Equal(new[] { "1", "2", "3", "1", "2", "1", "2", "1", "2" },
            grouped.Labels.Select(l => l.GetText(MarkFieldKey.CartonNo)));
        Assert.Equal(new[] { "3", "3", "3", "2", "2", "2", "2", "2", "2" },
            grouped.Labels.Select(l => l.GetText(MarkFieldKey.CartonTotal)));

        // —— 按箱数展开：样例里每行都写着整单总箱数，展开后是 1050 箱
        var expanded = NumberingEngine.Apply(mapped.Records, new NumberingRule { Mode = NumberingMode.ExpandByCartonTotal });
        Assert.Equal(1050, expanded.CartonCount);
        Assert.Equal(1050, expanded.LabelCount);

        // —— 拼版：A4 旋转省料后每页 4 枚 → 263 页，末页 2 枚
        var plan = ImpositionEngine.Build(BuiltInSheetSpecs.A4(), template.WidthMm, template.HeightMm, expanded.LabelCount);
        Assert.Equal(4, plan.PerPage);
        Assert.Equal(263, plan.PageCount);
        Assert.Equal(2, plan.LabelsLastPage);
        Assert.True(plan.UtilizationPercent > 39, plan.Describe());

        // —— 跨层一致性：整版上任意一枚与单标签预览用的是同一份版面
        var fifth = plan.PlacementsOnPage(2)[1];
        var layout = LayoutEngine.Build(template, expanded.Labels[fifth.LabelIndex - 1],
            new LayoutContext(fifth.LabelIndex, expanded.LabelCount, Path.GetFileName(data.SourceFile)));
        var texts = layout.Items.OfType<TextItem>().Select(t => t.Content).ToList();
        Assert.Contains(texts, t => t == $"C/NOS. {fifth.LabelIndex} / 1050");
        Assert.DoesNotContain(texts, t => t.Contains("{{"));
        Assert.False(layout.HasUnconfirmed);
    }

    [Fact]
    public void 映射方案能把编号规则一起存下来()
    {
        var data = TableImporter.Import(LocateSample("样例-唛头装箱单.csv"));
        var profile = MappingSuggester.Suggest(data.Headers, "带编号规则");
        profile.Numbering = new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            PadDigits = 4,
            Scope = NumberingScope.PerGroup,
            GroupByField = MarkFieldKey.ContractNo,
        };

        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "labelgou-m2-" + Guid.NewGuid().ToString("N")[..8]));
        var reloaded = store.Load(store.Save(profile))!;

        Assert.NotNull(reloaded.Numbering);
        Assert.Equal(NumberingMode.ExpandByCartonTotal, reloaded.Numbering!.Mode);
        Assert.Equal(4, reloaded.Numbering.PadDigits);
        Assert.Equal(MarkFieldKey.ContractNo, reloaded.Numbering.GroupByField);

        // 没存过编号规则的旧方案照样能读回来（字段缺失不报错）
        reloaded.Numbering = null;
        Assert.Null(store.Load(store.Save(reloaded))!.Numbering);
    }
}
