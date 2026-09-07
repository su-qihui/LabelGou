using LabelGou.Core.Data;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Mapping;
using LabelGou.Core.Numbering;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 真厂商样件基准（<c>labelgou-CL</c> 那 5 家）能进仓库的部分。
/// <para>
/// 这一条把「厂里真给的表格形状 → 一行展开成件数张 → 行式骨架印出四行」钉死。
/// 以后再改模板、编号或映射，只要把这 5 家里的任意一家改坏了，这里就红。
/// </para>
/// <para>
/// 注：真 <c>.xlsx</c> 有 14MB（OLU 那份）且在仓库外，所以仓库里放的是<strong>按真件逐列复刻的 CSV</strong>
/// （<c>samples\样例-金沐一开四.csv</c>）。真文件的读取验证走 gated 探针，不放这里。
/// </para>
/// </summary>
public class VendorSampleBaselineTests
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
        throw new FileNotFoundException($"找不到样例文件 {fileName}");
    }

    private static (TabularData Data, MappingProfile Profile) LoadJinMu()
    {
        var data = TableImporter.Import(LocateSample("样例-金沐一开四.csv"));
        var profile = MappingProfile.CreateFor(data.Headers, "金沐 一开四");
        // 按真表的列序硬绑：A 货号 / B 件数 CTN / C 数量 QTY（D 列「一开四」是备注，不绑）
        profile.Bind(MarkFieldKey.ItemNo, 0, data.Headers);
        profile.Bind(MarkFieldKey.CartonTotal, 1, data.Headers);
        profile.Bind(MarkFieldKey.Quantity, 2, data.Headers);
        return (data, profile);
    }

    [Fact]
    public void 金沐表三行按件数展开成十五张()
    {
        var (data, profile) = LoadJinMu();
        Assert.Equal(3, data.RowCount);
        Assert.Equal(4, data.ColumnCount);

        var mapped = RecordMapper.Map(data, profile);
        var expanded = NumberingEngine.Apply(mapped.Records, new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountField = MarkFieldKey.CartonTotal,
        });

        // 厂商自己在表末写的合计 155 = 31 个货号 × 5 件，这里同理：3 行 × 5 件 = 15 张
        Assert.Equal(15, expanded.Labels.Count);
        // 展开后 {{CartonTotal}} 是整批总数（给 C/NOS. x / y 用的），本货号自己的 5 件另存一个量
        Assert.Equal("15", expanded.Labels[0].GetText(MarkFieldKey.CartonTotal));
        Assert.Equal("5", expanded.Labels[0].GetCustom("col:本行箱数")!.Text);
        Assert.Equal("olu830-35*144", expanded.Labels[0].GetText(MarkFieldKey.ItemNo));
        // 同一货号的 5 张内容一致（真样张上就没印本箱序号），件号只是内部序号
        Assert.Equal(expanded.Labels[0].GetText(MarkFieldKey.ItemNo), expanded.Labels[4].GetText(MarkFieldKey.ItemNo));
    }

    [Fact]
    public void 行式四行模板印出厂商那四行的内容()
    {
        var (data, profile) = LoadJinMu();
        var mapped = RecordMapper.Map(data, profile);
        var labels = NumberingEngine.Apply(mapped.Records, new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountField = MarkFieldKey.CartonTotal,
        }).Labels;

        var layout = LayoutEngine.Build(BuiltInTemplates.RowsFour140x100(), labels[0], new LayoutContext(1, labels.Count));
        var printed = layout.Items.OfType<TextItem>().Select(t => t.Content).ToList();

        Assert.Contains("Item no：olu830-35*144", printed);
        Assert.Contains("QTY：144 pcs", printed);
        Assert.Contains("Ctns：5件", printed);
        // 已知缺口（对接文档 §十-A-1）：BOLAROM 这类"整批共用的客户名"在表里不是一个列，
        // 现在既不能从表格来也不能在方案里填常量，所以这一行只能是空的并被丢掉——不许偷偷印成空白行。
        Assert.All(printed, t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }

    [Fact]
    public void 一开四纸规把十五枚标签排成四页()
    {
        var (data, profile) = LoadJinMu();
        var labels = NumberingEngine.Apply(RecordMapper.Map(data, profile).Records, new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountField = MarkFieldKey.CartonTotal,
        }).Labels;

        var template = BuiltInTemplates.RowsFour140x100();
        var plan = ImpositionEngine.Build(BuiltInSheetSpecs.Cut4_280x200(), template.WidthMm, template.HeightMm, labels.Count);

        Assert.False(plan.Issues.HasError(), string.Join(" | ", plan.Issues.ErrorMessages()));
        Assert.Equal(4, plan.PerPage);
        Assert.Equal(15, plan.LabelCount);
        Assert.Equal(4, plan.PageCount);           // 15 ÷ 4 = 3 页满 + 1 页 3 枚
        Assert.Equal(3, plan.LabelsLastPage);      // 末页 3 枚，空出 1 个位
        Assert.Equal(1, plan.EmptySlotsLastPage);
    }
}
