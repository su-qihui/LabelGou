using System.Linq;
using System.Text.Json;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 13 棒·用户 2026-09-08 点名的两件事：唛头文字大小写三档、一页只排同一枚唛头。
/// <para>两件都落在「看起来正常但印出来不对」那一类：大小写错了要重打一批纸；分页规则错了
/// 会把两款货混在同一张纸上，或者页码与翻页对不上。所以几何与文字两侧都钉住。</para>
/// </summary>
public class TextCaseAndSheetRepeatTests
{
    private static LabelTemplate OneTextRow(string text) => new()
    {
        Id = "test.text-case",
        Name = "大小写测试",
        WidthMm = 100,
        HeightMm = 60,
        BorderMm = 0,
        Elements =
        {
            new TemplateElement
            {
                Kind = ElementKind.Text, Text = text,
                X = 4, Y = 4, Width = 90, Height = 10,
            },
        },
    };

    private static MarkRecord Record(string port) => MarkRecord.Builder()
        .SetRow(1, "样例.xlsx")
        .Set(MarkFieldKey.DestinationPort, port)
        .Build();

    private static string OnlyText(LabelTemplate template, MarkRecord record, MarkTextCase mode)
        => LayoutEngine.Build(template, record, new LayoutContext(1, 1, "样例.xlsx", TextCase: mode))
            .Items.OfType<TextItem>().Single().Content;

    // ---------- 大小写三档 ----------

    [Fact]
    public void 默认档按表格里的原样印不改变历史行为()
    {
        Assert.Equal("PORT los angeles", OnlyText(OneTextRow("PORT {{DestinationPort}}"), Record("los angeles"), MarkTextCase.AsSource));
    }

    [Fact]
    public void 全部大写连固定标签一起转因为样张上标签也是大写()
    {
        Assert.Equal("ITEM NO：OLU830-35",
            OnlyText(OneTextRow("Item no：{{DestinationPort}}"), Record("olu830-35"), MarkTextCase.Upper));
    }

    [Fact]
    public void 全部小写同样作用于整行()
    {
        Assert.Equal("item no：olu830-35",
            OnlyText(OneTextRow("Item NO：{{DestinationPort}}"), Record("OLU830-35"), MarkTextCase.Lower));
    }

    [Fact]
    public void 中文与数字在大小写档位下原样不动()
    {
        Assert.Equal("CTNS：5件", OnlyText(OneTextRow("Ctns：{{DestinationPort}}"), Record("5件"), MarkTextCase.Upper));
    }

    [Fact]
    public void 转大小写绝不改写记录里的原值()
    {
        var template = OneTextRow("PORT {{DestinationPort}}");
        var record = Record("los angeles");

        Assert.Equal("PORT LOS ANGELES", OnlyText(template, record, MarkTextCase.Upper));
        // 上一行要是把 record 洗成大写了，这一行就会跟着错——开关只该作用于印面，不该弄脏用户的表。
        Assert.Equal("PORT los angeles", OnlyText(template, record, MarkTextCase.AsSource));
    }

    [Fact]
    public void 大写档不许把花括号带进印面()
    {
        // LayoutEngine 的硬规矩①（未解析占位符绝不印出去）在新档位下必须仍然成立。
        var layout = LayoutEngine.Build(OneTextRow("{{BatchNo}} ABC"), Record("x"), new LayoutContext(1, 1, TextCase: MarkTextCase.Upper));
        Assert.DoesNotContain(layout.Items.OfType<TextItem>(), t => t.Content.Contains("{{"));
    }

    [Fact]
    public void 变量全空时大写档也照样整条隐藏()
    {
        // 转大小写不能把空文本变成非空，否则 "G.W.:  KG" 这类残句又回来了。
        var layout = LayoutEngine.Build(OneTextRow("G.W.: {{GrossWeight}} KG"), Record("x"), new LayoutContext(1, 1, TextCase: MarkTextCase.Upper));
        Assert.Empty(layout.Items.OfType<TextItem>());
        Assert.Equal(1, layout.HiddenElementCount);
    }

    [Fact]
    public void 三档叫法只有一份界面与核心同源()
    {
        Assert.Equal("按表格里的", MarkTextCase.AsSource.ChineseName());
        Assert.Equal("全部大写", MarkTextCase.Upper.ChineseName());
        Assert.Equal("全部小写", MarkTextCase.Lower.ChineseName());
    }

    // ---------- 一页只排同一枚 ----------

    /// <summary>200×140 的纸、90×60 的标签 → 恰好 2 列 × 2 行 = 每页 4 枚。</summary>
    private static SheetSpec FourUp(bool repeatSame = true) => new()
    {
        Name = "一开四测试",
        PaperWidthMm = 200,
        PaperHeightMm = 140,
        MarginLeftMm = 5,
        MarginTopMm = 5,
        MarginRightMm = 5,
        MarginBottomMm = 5,
        GutterXMm = 2,
        GutterYMm = 2,
        AllowRotate = false,
        RegistrationMarks = false,
        RepeatSameLabelPerPage = repeatSame,
    };

    private const double W = 90, H = 60;

    [Fact]
    public void 开关默认开着()
    {
        Assert.True(new SheetSpec().RepeatSameLabelPerPage);
    }

    [Fact]
    public void 旧纸规文件没这个字段时按开处理而不是悄悄变关()
    {
        // JSON 里缺字段 → 属性初始化器兜住，反序列化出来仍是 true（老用户升级后行为不变）。
        var restored = JsonSerializer.Deserialize<SheetSpec>("""{"Name":"老纸规","PaperWidthMm":210}""")!;
        Assert.True(restored.RepeatSameLabelPerPage);
    }

    [Fact]
    public void 开着时一枚唛头独占一页且页内铺满全同份数()
    {
        // 用户 2026-09-08 拿红框纠正的原话：「开四就是一张排 4 个一模一样的，你这效果只排了一个」。
        var plan = ImpositionEngine.Build(FourUp(), W, H, 3);

        Assert.True(plan.OneLabelPerPage);
        Assert.Equal(3, plan.PageCount);                       // 一枚一页，不是 ceil(3/4)=1
        Assert.Equal(12, plan.PhysicalLabelCount);             // 上纸 12 枚 = 3 × 4
        for (var page = 1; page <= plan.PageCount; page++)
        {
            var on = plan.PlacementsOnPage(page);
            Assert.Equal(4, on.Count);                         // 每页都铺满，不留白格
            Assert.Equal(page, Assert.Single(on.Select(p => p.LabelIndex).Distinct()));
        }
    }

    [Fact]
    public void 关掉开关回到顺序混排()
    {
        var plan = ImpositionEngine.Build(FourUp(repeatSame: false), W, H, 6);

        Assert.False(plan.OneLabelPerPage);
        Assert.Equal(2, plan.PageCount);                       // ceil(6/4)
        Assert.Equal(4, plan.PlacementsOnPage(1).Count);
        Assert.Equal(2, plan.LabelsLastPage);
        Assert.Equal(6, plan.PhysicalLabelCount);              // 混排下一枚标签就上一次纸
    }

    [Fact]
    public void 开关跟着纸规走不靠调用方递分组键()
    {
        // 上一版要调用方递 SourceRowIndex，没递就是个假旋钮；现在纸规说了算，任何四参数调用都照铺满。
        var plan = ImpositionEngine.Build(FourUp(), W, H, 6);

        Assert.True(plan.OneLabelPerPage);
        Assert.Equal(6, plan.PageCount);
        Assert.Equal(24, plan.PhysicalLabelCount);
    }

    [Fact]
    public void 页数按实际落位算而不是公式()
    {
        var plan = ImpositionEngine.Build(FourUp(), W, H, 5);  // 一款货 5 箱 → 5 张整张纸

        Assert.Equal(5, plan.PageCount);
        Assert.Equal(4, plan.LabelsLastPage);                  // 每页都铺满，末页也满
        Assert.Equal(0, plan.EmptySlotsLastPage);
        Assert.Equal(2, plan.MixedPageCount);                  // 关掉它 ceil(5/4)=2 页就够
    }

    [Fact]
    public void 每页都从左上角重新排起且四格几何一致()
    {
        var plan = ImpositionEngine.Build(FourUp(), W, H, 2);
        var first = plan.PlacementsOnPage(1);
        var second = plan.PlacementsOnPage(2);

        Assert.Equal(first.Select(p => (p.Row, p.Column, p.X, p.Y)), second.Select(p => (p.Row, p.Column, p.X, p.Y)));
        Assert.Contains(second, p => p.LabelIndex == 2 && p.Row == 0 && p.Column == 0 && p.X == 5 && p.Y == 5);
    }

    [Fact]
    public void 上纸倍数与混排对照必须写在那句总结里()
    {
        var plan = ImpositionEngine.Build(FourUp(), W, H, 10);

        Assert.Equal(10, plan.PageCount);
        Assert.Equal(3, plan.MixedPageCount);
        var text = plan.Describe();
        Assert.Contains("10 枚唛头 = 10 页 × 每页 4 份全同 = 上纸 40 枚", text);
        Assert.Contains("混排只占 3 页，但一页会混多款", text);   // 用户看完这句才知道有个开关可以关
    }

    [Fact]
    public void 一页一枚那档开关不改变任何结果()
    {
        // 五家真样张全是一页一枚：PerPage=1 时开与关必须一模一样，不然升级就改了所有人的版。
        var onSpec = FourUp();
        onSpec.FollowsLabel = true;
        var offSpec = FourUp(repeatSame: false);
        offSpec.FollowsLabel = true;
        var grouped = ImpositionEngine.Build(onSpec, W, H, 3);
        var mixed = ImpositionEngine.Build(offSpec, W, H, 3);

        Assert.Equal(1, grouped.PerPage);
        Assert.Equal(3, grouped.PageCount);
        Assert.Equal(mixed.PageCount, grouped.PageCount);
        Assert.Equal(mixed.MixedPageCount, grouped.MixedPageCount);
        Assert.Equal(mixed.PhysicalLabelCount, grouped.PhysicalLabelCount);
        Assert.True(grouped.OneLabelPerPage);                   // 开关是开着的，只是这里没差别
    }

    [Fact]
    public void 铺满后的用纸利用率按物理枚数算()
    {
        var on = ImpositionEngine.Build(FourUp(), W, H, 5);
        var off = ImpositionEngine.Build(FourUp(repeatSame: false), W, H, 5);

        // 每页 4 枚 × 5400 ÷ 28000 = 77.1%（页边与间距占掉的那块不会凭空消失）；
        // 上一棒报的那句「21.2%」就是把「有几个不同唛头」当成了上纸量。
        Assert.Equal(77.1, on.UtilizationPercent, 1);
        Assert.True(on.UtilizationPercent > off.UtilizationPercent);
        Assert.Equal(48.2, off.UtilizationPercent, 1);
    }
}
