using LabelGou.Core.Data;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 8 棒后半段（用户拿真界面截图逐条圈出来后补的两件事）：
/// ① 货号尾巴上的 <c>*144</c> 之类备注要在导入时切掉，而且**不许静默**——必须留一条告警把原值写清楚；
/// ② 五家厂商的样张各不相同，内置模板必须一家一套，字号/对齐/冒号形态要钉得住。
/// <para>这里只钉数据与模板定义本身；渲染出来好不好看仍要靠人眼（本机拿不到界面截图，对接文档 §五-70）。</para>
/// </summary>
public class ItemNoTailTests
{
    private static TabularData TwoRows() => new(
        "样例.xlsx", "Sheet1",
        new[] { "货号\nITEM NO:", "件数\nCTN", "数量\nQTY" },
        new IReadOnlyList<string>[]
        {
            new[] { "olu830-35*144", "5", "144" },
            new[] { "olu830-142", "5", "144" },
        },
        0);

    private static MappingResult Map()
    {
        var data = TwoRows();
        return RecordMapper.Map(data, MappingSuggester.Suggest(data.Headers, "金沐 一开四"));
    }

    [Fact]
    public void 货号星号尾巴被切掉()
    {
        var records = Map().Records;

        Assert.Equal("olu830-35", records[0].GetText(MarkFieldKey.ItemNo));
        // 本来就没有星号的值一个字符都不许动
        Assert.Equal("olu830-142", records[1].GetText(MarkFieldKey.ItemNo));
    }

    [Fact]
    public void 切掉的尾巴必须以告警形式留痕()
    {
        var issues = Map().Issues;

        var tail = Assert.Single(issues, i => i.Field == MarkFieldKey.ItemNo);
        Assert.Equal(IssueSeverity.Warning, tail.Severity);
        // 原值和保留值都要出现在文案里，用户才知道少了什么、少了会不会影响他
        Assert.Contains("olu830-35*144", tail.Message);
        Assert.Contains("olu830-35", tail.Message);
        Assert.Equal(1, tail.RowNumber);
    }

    [Theory]
    [InlineData("olu830-35*144", "olu830-35", "*144")]
    [InlineData("b5006*16\n VESCAGA ERRAS", "b5006", "*16")]
    [InlineData("b5011*16 INVISTUC", "b5011", "*16 INVISTUC")]
    [InlineData("olu830-142", null, null)]
    [InlineData("*144 开头", null, null)]
    [InlineData("OLU830-137", null, null)]
    public void 切分规则只认第一个星号且不动干净的值(string raw, string? kept, string? stripped)
    {
        var actual = RecordMapper.StripItemNoTail(raw);

        if (kept is null)
        {
            Assert.Null(actual);
            return;
        }
        Assert.NotNull(actual);
        Assert.Equal(kept, actual!.Value.Kept);
        Assert.Equal(stripped, actual.Value.Stripped);
    }

    [Fact]
    public void 金沐三行明细同字号且明显小于首行()
    {
        var details = TextRows(BuiltInTemplates.RowsFour140x100());

        Assert.Equal(4, details.Count);
        // 用户圈出的"字体大小不统一"：三行明细必须一模一样，且都比首行小
        Assert.Equal(details[1].FontSizePt, details[2].FontSizePt);
        Assert.Equal(details[2].FontSizePt, details[3].FontSizePt);
        Assert.True(details[0].FontSizePt > details[1].FontSizePt * 1.2,
            $"首行应当明显更大，实测 {details[0].FontSizePt} vs {details[1].FontSizePt}");
        // 真件里明细左对齐、只有首行居中
        Assert.Equal(HorizontalAlign.Center, details[0].Align);
        Assert.All(details.Skip(1), d => Assert.Equal(HorizontalAlign.Left, d.Align));
    }

    [Fact]
    public void 五家样件各自一套模板且冒号形态按家固定()
    {
        var jinMu = TextRows(BuiltInTemplates.RowsFour140x100());
        var qiu = TextRows(BuiltInTemplates.QiuRows140x100());
        var olu = TextRows(BuiltInTemplates.OluRows160x120());
        var top = TextRows(BuiltInTemplates.TopRows140x100());

        // 全角：金沐、邱总；半角+空格：TOP；无冒号：OLU
        Assert.Contains("：", string.Concat(jinMu.Select(t => t.Text)));
        Assert.Contains("：", string.Concat(qiu.Select(t => t.Text)));
        Assert.Contains(": ", string.Concat(top.Select(t => t.Text)));
        Assert.DoesNotContain(":", string.Concat(olu.Select(t => t.Text)));

        // 邱总四行全居中；TOP 四行全常规字重（五家里字最细的一家）
        Assert.All(qiu, r => Assert.Equal(HorizontalAlign.Center, r.Align));
        Assert.All(top, r => Assert.False(r.Bold));
    }

    [Fact]
    public void 每家模板尺寸对得上真样张的纸面()
    {
        Assert.Equal((140d, 100d), Wh(BuiltInTemplates.RowsFour140x100()));
        Assert.Equal((140d, 100d), Wh(BuiltInTemplates.QiuRows140x100()));
        Assert.Equal((140d, 100d), Wh(BuiltInTemplates.TopRows140x100()));
        Assert.Equal((160d, 120d), Wh(BuiltInTemplates.OluRows160x120()));
        Assert.Equal((160d, 120d), Wh(BuiltInTemplates.RowsBigTwo160x120()));
    }

    [Fact]
    public void 内置模板清单里五家都在且名字带厂牌()
    {
        var ids = BuiltInTemplates.All.Select(t => t.Id).ToList();

        Assert.Contains(BuiltInTemplates.IdRowsFour, ids);
        Assert.Contains(BuiltInTemplates.IdQiuRows, ids);
        Assert.Contains(BuiltInTemplates.IdOluRows, ids);
        Assert.Contains(BuiltInTemplates.IdTopRows, ids);
        Assert.Contains(BuiltInTemplates.IdRowsBigTwo, ids);
        // 名字里带厂牌，用户在下拉里能一眼认出"这就是我给我的那张样张"
        Assert.Contains(BuiltInTemplates.All, t => t.Id == BuiltInTemplates.IdQiuRows && t.Name.StartsWith("邱总"));
        Assert.Contains(BuiltInTemplates.All, t => t.Id == BuiltInTemplates.IdOluRows && t.Name.StartsWith("OLU"));
        Assert.Contains(BuiltInTemplates.All, t => t.Id == BuiltInTemplates.IdTopRows && t.Name.StartsWith("TOP"));
    }

    private static List<TemplateElement> TextRows(LabelTemplate template) =>
        template.Elements.Where(e => e.Kind == ElementKind.Text).ToList();

    private static (double W, double H) Wh(LabelTemplate t) => (t.WidthMm, t.HeightMm);
}
