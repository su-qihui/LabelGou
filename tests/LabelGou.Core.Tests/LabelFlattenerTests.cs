using System.Linq;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 73 棒 · 单张定稿的烤平器。
/// <para>判据盯两件相反的事：表里取来的值要<strong>被写死</strong>（那一行与其他行切开），
/// 软件逐张算的件号要<strong>留着活</strong>（一行 5 箱写死件号会印出五张同一件号，那是重号）。</para>
/// </summary>
public class LabelFlattenerTests
{
    private const string ItemNo = "OLU830-35";

    private static LabelTemplate Mark() => new()
    {
        Id = "user.m65-140x100",
        Name = "一开四 140×100",
        WidthMm = 140,
        HeightMm = 100,
        BorderMm = 0.4,
        Elements =
        {
            new TemplateElement { Kind = ElementKind.Text, Text = "Consignee：{{col:收货人}}", X = 4, Y = 4, Width = 132, Height = 8 },
            new TemplateElement { Kind = ElementKind.Text, Text = "No.{{NoXofY}} {{ItemNo}}", X = 4, Y = 16, Width = 132, Height = 8 },
            new TemplateElement { Kind = ElementKind.Text, Text = "G.W.: {{col:毛重}} KG", X = 4, Y = 28, Width = 132, Height = 8 },
        },
    };

    /// <summary>一行 5 箱里的某一张：件号 x 由编号引擎写在记录上，收货人/毛重/货号是表里的值。</summary>
    private static MarkRecord Carton(int x, int y = 5, string consignee = "BOLAROM", string? grossWeight = null, bool needsReview = false)
    {
        var builder = MarkRecord.Builder()
            .SetRow(3, "样例.xlsx")
            .SetCustom("col:收货人", consignee)
            .Set(MarkFieldKey.ItemNo, ItemNo)
            .Set(MarkFieldKey.CartonNo, x.ToString())
            .Set(MarkFieldKey.CartonTotal, y.ToString());
        if (grossWeight is not null) builder.SetCustom("col:毛重", grossWeight);
        if (needsReview) builder.Set(MarkFieldKey.ItemNo, new MarkValue(ItemNo, ValueOrigin.AiOcr) { NeedsReview = true, Warning = "AI 认的，没核过" });
        return builder.Build();
    }

    private static List<string> TextsOf(LabelTemplate template, MarkRecord record, int rowIndex = 1,
        MarkTextCase mode = MarkTextCase.AsSource)
        => LayoutEngine.Build(template, record, new LayoutContext(rowIndex, 5, "样例.xlsx", TextCase: mode))
            .Items.OfType<TextItem>().Select(i => i.Content).ToList();

    // ── 切开的那一半：表里的值写死 ──────────────────────────────

    [Fact]
    public void 表里的值被写进定稿_不再回头看记录()
    {
        var flat = LabelFlattener.Flatten(Mark(), Carton(1)).Template;

        Assert.Equal("Consignee：BOLAROM", flat.Elements[0].Text);

        // 换一条记录喂它：写死的那个值纹丝不动，这才叫与列表代替符切开了
        Assert.Equal("Consignee：BOLAROM", TextsOf(flat, Carton(2, consignee: "OTHER CO"))[0]);
    }

    [Fact]
    public void 定稿里不许再留表里的占位符()
    {
        var flat = LabelFlattener.Flatten(Mark(), Carton(1, grossWeight: "25.5")).Template;
        var tokens = flat.Elements.SelectMany(e => TemplateTokenizer.EnumerateTokens(e.Text)).ToList();

        Assert.DoesNotContain("col:收货人", tokens);
        Assert.DoesNotContain("col:毛重", tokens);
        Assert.DoesNotContain("ItemNo", tokens);
    }

    // ── 必须留着活的那一半：件号不许冻住 ────────────────────────

    [Fact]
    public void 件号令牌留在定稿里_一行五张各印各的()
    {
        var flat = LabelFlattener.Flatten(Mark(), Carton(1)).Template;

        Assert.Contains("NoXofY", TemplateTokenizer.EnumerateTokens(flat.Elements[1].Text));
        Assert.Equal("No.1 / 5 OLU830-35", TextsOf(flat, Carton(1), rowIndex: 1)[1]);
        // 第 5 张撞上「分子分母同数不印分数」（§五-85 既有定案），所以取第 4 张来证「件号仍是活的」
        Assert.Equal("No.4 / 5 OLU830-35", TextsOf(flat, Carton(4), rowIndex: 4)[1]);
    }

    [Fact]
    public void 同一行同一张_烤平前后逐字相同()
    {
        var record = Carton(3, grossWeight: "25.5");
        var source = Mark();

        var flat = LabelFlattener.Flatten(source, record).Template;

        Assert.Equal(TextsOf(source, record, 3), TextsOf(flat, record, 3));
    }

    [Fact]
    public void 大小写档位对写死的文字照样生效()
    {
        var flat = LabelFlattener.Flatten(Mark(), Carton(1)).Template;

        Assert.Equal("CONSIGNEE：BOLAROM", TextsOf(flat, Carton(2), mode: MarkTextCase.Upper)[0]);
    }

    // ── 空值与认不出的占位符：只点名，不静默 ────────────────────

    [Fact]
    public void 取不到值的整条置空并点名_不留残句()
    {
        var result = LabelFlattener.Flatten(Mark(), Carton(1));

        Assert.Equal(string.Empty, result.Template.Elements[2].Text);
        Assert.Contains("col:毛重", result.BlankedTokens);
        // 烤平前这条因「含变量但全空」整条不出现；烤平后也必须不出现，不许印出 "G.W.:  KG"
        var texts = TextsOf(result.Template, Carton(1));
        Assert.Equal(2, texts.Count);
        Assert.All(texts, t => Assert.DoesNotContain("G.W.", t));
    }

    [Fact]
    public void 认不出的占位符原样留着_不替它编一个值()
    {
        var source = Mark();
        source.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "备注：{{NoSuchThing}}", X = 4, Y = 40, Width = 132, Height = 8 });

        var result = LabelFlattener.Flatten(source, Carton(1));

        Assert.Contains("{{NoSuchThing}}", result.Template.Elements[3].Text);
        Assert.Contains("NoSuchThing", result.UnknownTokens);
    }

    [Fact]
    public void 写死时还挂着待核的值_定稿替出纸闸记住()
    {
        var result = LabelFlattener.Flatten(Mark(), Carton(1, needsReview: true));

        Assert.Contains(result.UnreviewedFields, s => s.Contains("货号"));
    }

    // ── 定稿不许动的东西（拼版按一个标量尺寸排格子）─────────────

    [Fact]
    public void 尺寸与元素位置逐字不动_且不与库里那份混成同一个标识()
    {
        var source = Mark();
        var flat = LabelFlattener.Flatten(source, Carton(1)).Template;

        Assert.Equal(source.WidthMm, flat.WidthMm);
        Assert.Equal(source.HeightMm, flat.HeightMm);
        Assert.Equal(source.BorderMm, flat.BorderMm);
        Assert.Equal(source.Elements.Count, flat.Elements.Count);
        Assert.True(source.Elements.Zip(flat.Elements)
            .All(p => p.First.X == p.Second.X && p.First.Y == p.Second.Y
                && p.First.Width == p.Second.Width && p.First.Height == p.Second.Height));
        Assert.NotEqual(source.Id, flat.Id);
        Assert.False(flat.BuiltIn);
    }

    [Fact]
    public void 条码的数据也写死_制式与框不动()
    {
        var source = Mark();
        source.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode, Text = "{{col:条码}}", Symbology = BarcodeSymbology.Ean13,
            X = 4, Y = 60, Width = 40, Height = 20,
        });
        var record = Carton(1).ToBuilder().SetCustom("col:条码", "6901234567892").Build();

        var flat = LabelFlattener.Flatten(source, record).Template;

        Assert.Equal("6901234567892", flat.Elements[3].Text);
        Assert.Equal(BarcodeSymbology.Ean13, flat.Elements[3].Symbology);
        Assert.Equal(source.Elements[3].Width, flat.Elements[3].Width);
        Assert.Equal(
            LayoutEngine.Build(source, record, new LayoutContext(1, 5)).Items.OfType<BarcodeItem>().Single().Data,
            LayoutEngine.Build(flat, record, new LayoutContext(1, 5)).Items.OfType<BarcodeItem>().Single().Data);
    }

    [Fact]
    public void 纯静态文字的模板烤完逐字不变()
    {
        var source = Mark();
        source.Elements[0].Text = "MADE IN CHINA";

        var flat = LabelFlattener.Flatten(source, Carton(1)).Template;

        Assert.Equal("MADE IN CHINA", flat.Elements[0].Text);
    }

    // ── 住在 col: 名下的推算量：那是软件算的，不许烤平 ──────────

    [Fact]
    public void 本行箱数与组内序留活_换个组内序要跟着变()
    {
        var source = Mark();
        source.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "Ctns：{{col:本行箱数}}件 第{{col:组内序}}箱",
            X = 4, Y = 40, Width = 132, Height = 8,
        });
        var record = Carton(1).ToBuilder()
            .SetCustom("col:本行箱数", "5", ValueOrigin.Rule)
            .SetCustom("col:组内序", "1", ValueOrigin.Rule)
            .Build();

        var flat = LabelFlattener.Flatten(source, record).Template;
        var tokens = TemplateTokenizer.EnumerateTokens(flat.Elements[3].Text).ToList();

        Assert.Contains("col:本行箱数", tokens);
        Assert.Contains("col:组内序", tokens);

        // 第三箱来喂同一份定稿：那一格必须跟着走；烤平过就会五张全印「第1箱」
        var third = record.ToBuilder().SetCustom("col:组内序", "3", ValueOrigin.Rule).Build();
        Assert.Equal("Ctns：5件 第3箱", TextsOf(flat, third, 3).Single(t => t.StartsWith("Ctns")));
    }

    [Fact]
    public void 留活看来源不看名字_表里的件号写死而规则补的件号留活()
    {
        var source = Mark();
        source.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "箱号 {{CartonNo}}", X = 4, Y = 40, Width = 132, Height = 8 });
        var fromTable = Carton(1).ToBuilder().Set(MarkFieldKey.CartonNo, new MarkValue("A7", ValueOrigin.ExcelImport)).Build();
        var fromRule = Carton(1).ToBuilder().Set(MarkFieldKey.CartonNo, new MarkValue("21", ValueOrigin.Rule)).Build();

        var bakedFromTable = LabelFlattener.Flatten(source, fromTable).Template;
        var keptFromRule = LabelFlattener.Flatten(source, fromRule).Template;

        Assert.Equal("箱号 A7", TextsOf(bakedFromTable, fromTable).Single(t => t.StartsWith("箱号")));
        Assert.DoesNotContain("CartonNo", TemplateTokenizer.EnumerateTokens(bakedFromTable.Elements[3].Text));
        Assert.Contains("CartonNo", TemplateTokenizer.EnumerateTokens(keptFromRule.Elements[3].Text));
        // 规则的号换了另一张记录来喂：这一格要跟着变
        Assert.Equal("箱号 22", TextsOf(keptFromRule, fromRule.ToBuilder()
            .Set(MarkFieldKey.CartonNo, new MarkValue("22", ValueOrigin.Rule)).Build()).Single(t => t.StartsWith("箱号")));
    }
}
