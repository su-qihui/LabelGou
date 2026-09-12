using LabelGou.Core.Barcodes;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 42 棒 · 问题 3：编辑器画布「框架显示但效果是空的」。
/// <para>
/// 病根不在渲染、也不在「整行隐藏」那条规矩（那条是对的，防的是把 <c>G.W.:  KG</c> 残句印上纸），
/// 而在<strong>示意样例不够全</strong>：没有真数据时一律用写死的 <see cref="SampleRecords.StandardSample"/>，
/// 它只带一个 <c>col:托盘号</c>，而 AI 行式模板几乎全在用 <c>{{col:列名}}</c> 直引表格列 → 全取不到值 → 整行隐藏。
/// <see cref="TemplateSample.ForTemplate"/> 按模板实际引用的列补样例值，让这些行重新显示出来。
/// </para>
/// </summary>
public class TemplateSampleTests
{
    private static LabelTemplate SingleText(string text) => new()
    {
        Id = "user.test",
        Name = "测试",
        WidthMm = 140,
        HeightMm = 100,
        PaddingMm = 5,
        BorderMm = 0,
        Elements =
        {
            new TemplateElement { Kind = ElementKind.Text, Text = text, X = 5, Y = 5, Width = 130, Height = 20, FontSizePt = 12 },
        },
    };

    /// <summary>问题 3 的正反对照：同一份引用 <c>col:QTY</c> 的模板，写死样例隐藏、补全样例显示。</summary>
    [Fact]
    public void ColumnReferencingRowIsHiddenByStockSampleButShownByTemplateSample()
    {
        var template = SingleText("QTY：{{col:QTY}}");

        // 写死样例：没有 col:QTY → 含变量但变量全空 → 整行隐藏（这正是用户看到的"空"）
        var stock = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1, "样例.xlsx"));
        Assert.Empty(stock.Items.OfType<TextItem>());
        Assert.Equal(1, stock.HiddenElementCount);

        // 补全样例：按模板引用补上 col:QTY → 这一行显示出来
        var filled = LayoutEngine.Build(template, TemplateSample.ForTemplate(template), new LayoutContext(1, 1, "样例.xlsx"));
        var text = Assert.Single(filled.Items.OfType<TextItem>());
        Assert.Equal(0, filled.HiddenElementCount);
        Assert.Contains("QTY", text.Content);
    }

    /// <summary>补的值就是列名本身（换行折成「 / 」），<strong>不编造假数据</strong>——三道闸之二「不猜」。</summary>
    [Fact]
    public void FilledValueIsTheColumnNameItselfNotInventedData()
    {
        var template = SingleText("{{col:ITEM NO}}");

        var record = TemplateSample.ForTemplate(template);

        var value = record.GetCustom("col:ITEM NO");
        Assert.NotNull(value);
        Assert.Equal("ITEM NO", value!.Text);
    }

    /// <summary>真表里表头常带换行（金沐那张「件数(换行)CTN」），补的值要折成单行，别把一行劈成两行。</summary>
    [Fact]
    public void MultilineColumnNameIsFlattenedToSingleLine()
    {
        var template = SingleText("{{col:件数\nCTN}}");

        var record = TemplateSample.ForTemplate(template);

        var value = record.GetCustom("col:件数\nCTN");
        Assert.NotNull(value);
        Assert.DoesNotContain("\n", value!.Text);
        Assert.Contains("件数", value.Text);
        Assert.Contains("CTN", value.Text);
    }

    /// <summary>
    /// 标准字段（<c>{{ItemNo}}</c> 等）写死样例里本来就有，补全样例必须照样带上 ——
    /// 不能因为补了 col: 就把标准字段弄丢，否则引用标准字段的模板反而变空。
    /// </summary>
    [Fact]
    public void StandardFieldsSurviveAlongsideFilledColumns()
    {
        var template = SingleText("{{ItemNo}} / {{col:QTY}}");

        var filled = LayoutEngine.Build(template, TemplateSample.ForTemplate(template), new LayoutContext(1, 1, "样例.xlsx"));

        var text = Assert.Single(filled.Items.OfType<TextItem>());
        Assert.Equal(0, filled.HiddenElementCount);
        Assert.Contains("YOGA-PANT-SS", text.Content);   // 标准字段 ItemNo 的样例值仍在
        Assert.Contains("QTY", text.Content);             // col: 也补上了
    }

    /// <summary>
    /// 不猜闸：白名单外的未知字段令牌<strong>不补值</strong>。那种令牌校验器本来就报 Error 存不进库，
    /// 给它编个值等于把「引用了不存在的字段」这个真问题藏起来，所以让它照旧隐藏。
    /// </summary>
    [Fact]
    public void UnknownFieldTokenIsNotInvented()
    {
        var template = SingleText("{{NoSuchFieldAtAll}}");

        var record = TemplateSample.ForTemplate(template);
        var layout = LayoutEngine.Build(template, record, new LayoutContext(1, 1, "样例.xlsx"));

        Assert.Empty(layout.Items.OfType<TextItem>());          // 仍然隐藏，不假装解析出来
        Assert.Contains("NoSuchFieldAtAll", layout.UnresolvedTokens);
    }

    /// <summary>条码元素的数据表达式与文本共用同一套占位符，也要被补全样例覆盖。</summary>
    [Fact]
    public void BarcodeColumnTokenIsAlsoFilled()
    {
        var template = new LabelTemplate
        {
            Id = "user.test",
            Name = "条码",
            WidthMm = 140,
            HeightMm = 100,
            Elements =
            {
                new TemplateElement
                {
                    Kind = ElementKind.Barcode, Text = "{{col:6901234567892}}",
                    X = 5, Y = 5, Width = 60, Height = 20, Symbology = BarcodeSymbology.Ean13,
                },
            },
        };

        var record = TemplateSample.ForTemplate(template);

        Assert.NotNull(record.GetCustom("col:6901234567892"));
    }

    /// <summary>同一列被多个元素引用只补一次，且 ForTemplate 不改动传入模板。</summary>
    [Fact]
    public void DuplicateColumnsAreFilledOnceAndTemplateIsUntouched()
    {
        var template = new LabelTemplate
        {
            Id = "user.test",
            Name = "重复列",
            WidthMm = 140,
            HeightMm = 100,
            Elements =
            {
                new TemplateElement { Kind = ElementKind.Text, Text = "{{col:QTY}}", X = 5, Y = 5, Width = 60, Height = 10 },
                new TemplateElement { Kind = ElementKind.Text, Text = "{{col:QTY}}", X = 5, Y = 20, Width = 60, Height = 10 },
            },
        };
        var elementCountBefore = template.Elements.Count;

        var record = TemplateSample.ForTemplate(template);

        Assert.Equal(elementCountBefore, template.Elements.Count);   // 没往模板里塞东西
        Assert.NotNull(record.GetCustom("col:QTY"));
    }
}
