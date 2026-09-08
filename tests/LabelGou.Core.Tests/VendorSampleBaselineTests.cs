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
        // 货号尾巴上的 *144 是厂内“每箱装多少”的备注，真样张上没印它（导入时会被切掉并留一条告警）。
        Assert.Equal("olu830-35", expanded.Labels[0].GetText(MarkFieldKey.ItemNo));
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

        // 这一行现在与 CDR 真件逐字一致：`Item no：olu830-35`（*144 被清掉，144 由下一行 QTY 承载）
        Assert.Contains("Item no：olu830-35", printed);
        Assert.Contains("QTY：144 pcs", printed);
        Assert.Contains("Ctns：5件", printed);
        // 已知缺口（对接文档 §十-A-12，已于第 8 棒用整批固定值补上）：BOLAROM 这类"整批共用的客户名"在表里不是一个列，
        // 所以本文件的基准测试刻意不填固定值：这一行只能是空的并被丢掉——不许偷偷印成空白行。
        // （填了固定值之后的四行齐全由 `_probe\m7-sheet\` 肉眼复核，不靠单测假证。）
        Assert.All(printed, t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }

    [Fact]
    public void 一开四纸规把十五枚标签混排成四页()
    {
        var (data, profile) = LoadJinMu();
        var labels = NumberingEngine.Apply(RecordMapper.Map(data, profile).Records, new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountField = MarkFieldKey.CartonTotal,
        }).Labels;

        var template = BuiltInTemplates.RowsFour140x100();
        var mixed = BuiltInSheetSpecs.Cut4_280x200();
        mixed.RepeatSameLabelPerPage = false;      // 本条守的是真样张量出来的那张几何（一页四枚不同货号）
        var plan = ImpositionEngine.Build(mixed, template.WidthMm, template.HeightMm, labels.Count);

        Assert.False(plan.Issues.HasError(), string.Join(" | ", plan.Issues.ErrorMessages()));
        Assert.Equal(4, plan.PerPage);
        Assert.Equal(15, plan.LabelCount);
        Assert.Equal(4, plan.PageCount);           // 15 ÷ 4 = 3 页满 + 1 页 3 枚
        Assert.Equal(3, plan.LabelsLastPage);      // 末页 3 枚，空出 1 个位
        Assert.Equal(1, plan.EmptySlotsLastPage);
    }

    [Fact]
    public void 一开四默认档把每枚唛头铺满一页()
    {
        // 用户 2026-09-08：「开四就是一张排 4 个一模一样的」。同样这张 280×200 的刀模纸，
        // 开着默认档时 15 枚唛头就是 15 张纸，每张 4 份全同（一箱四面），上纸量是混排的 4 倍。
        var (data, profile) = LoadJinMu();
        var labels = NumberingEngine.Apply(RecordMapper.Map(data, profile).Records, new NumberingRule
        {
            Mode = NumberingMode.ExpandByCartonTotal,
            ExpandCountField = MarkFieldKey.CartonTotal,
        }).Labels;

        var template = BuiltInTemplates.RowsFour140x100();
        var plan = ImpositionEngine.Build(BuiltInSheetSpecs.Cut4_280x200(), template.WidthMm, template.HeightMm, labels.Count);

        Assert.True(plan.OneLabelPerPage);
        Assert.Equal(15, plan.PageCount);
        Assert.Equal(60, plan.PhysicalLabelCount);
        Assert.Equal(4, plan.MixedPageCount);                      // 与上一条基准接得上：关掉就是 4 页
        Assert.Equal(100, plan.UtilizationPercent, 1);             // 铺满了，不再报那句 21.2%
        Assert.All(plan.PlacementsOnPage(1), p => Assert.Equal(1, p.LabelIndex));
    }
}
