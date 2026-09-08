using System.Linq;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 批次三第 32 条与批次四第 38 条：件号分数什么时候不该印、标红该跟着谁标、内置计算量名单只有一份。
/// <para>这三件事都是「看起来完全正常但印出来不对」的量级：一行无声消失、整片红得看不出要看哪一格、
/// 编辑器递给用户一个插进去就报错的字段名。</para>
/// </summary>
public class LayoutFlagAndCartonTests
{
    private static MarkRecord RecordWith(params (MarkFieldKey Key, string Text, bool NeedsReview)[] values)
    {
        var builder = MarkRecord.Builder().SetRow(1, "样例.xlsx");
        foreach (var (key, text, flagged) in values)
        {
            builder.Set(key, new MarkValue(text, ValueOrigin.ExcelImport)
            {
                NeedsReview = flagged,
                Warning = flagged ? "模型给的，查无原文" : null,
            });
        }
        return builder.Build();
    }

    private static LabelTemplate TwoTextRows() => new()
    {
        Id = "test.two-rows",
        Name = "两行测试",
        WidthMm = 100,
        HeightMm = 60,
        BorderMm = 0,
        Elements =
        {
            new TemplateElement
            {
                Kind = ElementKind.Text, Text = "PORT {{DestinationPort}}",
                X = 4, Y = 4, Width = 90, Height = 10,
            },
            new TemplateElement
            {
                Kind = ElementKind.Text, Text = "ITEM {{ItemNo}}",
                X = 4, Y = 20, Width = 90, Height = 10,
            },
        },
    };

    [Fact]
    public void 只有一个字段待核时不把全片文字都标红()
    {
        var record = RecordWith(
            (MarkFieldKey.DestinationPort, "LOS ANGELES", true),
            (MarkFieldKey.ItemNo, "b5006", false));

        var layout = LayoutEngine.Build(TwoTextRows(), record, new LayoutContext(1, 2));
        var items = layout.Items.OfType<TextItem>().ToList();

        Assert.Equal(2, items.Count);
        var port = items.Single(i => i.Content.Contains("LOS ANGELES", System.StringComparison.Ordinal));
        var item = items.Single(i => i.Content.Contains("b5006", System.StringComparison.Ordinal));

        Assert.True(port.Flagged, "这个字段本身待核，它那一格该标红");
        Assert.Contains("目的港", port.FlagReason, System.StringComparison.Ordinal);
        Assert.False(item.Flagged, "上一版按整条记录判：一个字段待核，一张里全部文字都红，用户反而看不出要看哪一格");
        Assert.Null(item.FlagReason);
    }

    [Fact]
    public void 分子分母同一个数时不印分数()
    {
        // 「总件数=1 不印 x/y」这条以前从来没生效过：写成 y=="1" || y == RecordCount && RecordCount <= 1，
        // 被 && 的优先级顶成一个永远轮不到说话的死条件。
        var one = RecordWith((MarkFieldKey.CartonNo, "1", false), (MarkFieldKey.CartonTotal, "1", false));
        Assert.Equal("1", LayoutEngine.FormatCarton(one, new LayoutContext(1, 1)));

        var sameNumber = RecordWith((MarkFieldKey.CartonNo, "7", false), (MarkFieldKey.CartonTotal, "7", false));
        Assert.Equal("7", LayoutEngine.FormatCarton(sameNumber, new LayoutContext(7, 9)));
    }

    [Fact]
    public void 正常的分数与缺总件数都照旧()
    {
        var normal = RecordWith((MarkFieldKey.CartonNo, "3", false), (MarkFieldKey.CartonTotal, "12", false));
        Assert.Equal("3 / 12", LayoutEngine.FormatCarton(normal, new LayoutContext(3, 12)));

        var noTotal = RecordWith((MarkFieldKey.CartonNo, "5", false));
        Assert.Equal("5", LayoutEngine.FormatCarton(noTotal, new LayoutContext(5, 8)));
    }

    [Fact]
    public void 内置计算量名单就是解析器认的那一份()
    {
        // 编辑器「插入字段」的下拉由它生成。上一版编辑器自己手写了一份，里面 TotalCarton / TotalQty
        // 这两个名字本引擎根本不认，插进去就是校验 Error。
        Assert.NotEmpty(TemplateTokenizer.BuiltInTokens);
        Assert.All(TemplateTokenizer.BuiltInTokens, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Token));
            Assert.True(TemplateTokenizer.IsBuiltInToken(t.Token), $"{t.Token} 在清单里却不是内置量");
            Assert.DoesNotContain("{{", t.Token, System.StringComparison.Ordinal);
        });

        Assert.False(TemplateTokenizer.IsBuiltInToken("TotalCarton"));
        Assert.False(TemplateTokenizer.IsBuiltInToken("TotalQty"));
    }
}
