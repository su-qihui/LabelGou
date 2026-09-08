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
    public void 开着时一页不混两款唛头()
    {
        var groups = new[] { 1, 1, 2, 2, 3, 3 };
        var plan = ImpositionEngine.Build(FourUp(), W, H, groups.Length, groups);

        Assert.True(plan.GroupedPerPage);
        Assert.Equal(3, plan.PageCount);                       // 两款挤一页的混排在这里变成三页
        Assert.Empty(plan.PlacementsOnPage(1).Select(p => p.LabelIndex).Except(new[] { 1, 2 }));
        foreach (var page in Enumerable.Range(1, plan.PageCount))
        {
            var onThisPage = plan.PlacementsOnPage(page).Select(p => groups[p.LabelIndex - 1]).Distinct().ToList();
            Assert.Single(onThisPage);                         // 每一页都只有一种源标签
        }
    }

    [Fact]
    public void 关掉开关回到顺序混排()
    {
        var groups = new[] { 1, 1, 2, 2, 3, 3 };
        var plan = ImpositionEngine.Build(FourUp(repeatSame: false), W, H, groups.Length, groups);

        Assert.False(plan.GroupedPerPage);
        Assert.Equal(2, plan.PageCount);                       // ceil(6/4)
        Assert.Equal(4, plan.PlacementsOnPage(1).Count);
        Assert.Equal(2, plan.LabelsLastPage);
    }

    [Fact]
    public void 没递分组键时不静默假装分了组()
    {
        // 引擎不猜：调用方没给每张标签的源行号，就照混排走，并把 GroupedPerPage 留成 false。
        var plan = ImpositionEngine.Build(FourUp(), W, H, 6);

        Assert.False(plan.GroupedPerPage);
        Assert.Equal(2, plan.PageCount);
    }

    [Fact]
    public void 分组页数按实际落位算而不是公式()
    {
        var groups = new[] { 1, 1, 1, 1, 1 };                 // 一款货 5 箱，每页 4 枚
        var plan = ImpositionEngine.Build(FourUp(), W, H, groups.Length, groups);

        Assert.Equal(2, plan.PageCount);
        Assert.Equal(1, plan.LabelsLastPage);                 // 上一版这里会算成 5 - 1*4 = 1（碰巧对），
        Assert.Equal(3, plan.EmptySlotsLastPage);             // 但末页枚数必须来自落位而不是减法
        Assert.Equal(2, plan.MixedPageCount);                 // ceil(5/4) = 2，这一款没多耗纸
    }

    [Fact]
    public void 同组排满一页后下一页从左上角重新开始()
    {
        var groups = new[] { 1, 1, 1, 1, 1 };
        var plan = ImpositionEngine.Build(FourUp(), W, H, groups.Length, groups);
        var fifth = Assert.Single(plan.PlacementsOnPage(2));

        Assert.Equal(5, fifth.LabelIndex);
        Assert.Equal(0, fifth.Row);
        Assert.Equal(0, fifth.Column);
        Assert.Equal(5, fifth.X);                              // 与第一页第一枚同一套落位（页边 5mm）
        Assert.Equal(5, fifth.Y);
    }

    [Fact]
    public void 多耗的纸必须在那句总结里说清楚()
    {
        var groups = new[] { 1, 1, 1, 1, 1, 2, 2, 2, 2, 2 };  // 两款各 5 箱
        var plan = ImpositionEngine.Build(FourUp(), W, H, groups.Length, groups);

        Assert.Equal(4, plan.PageCount);
        Assert.Equal(3, plan.MixedPageCount);
        Assert.Contains("一页只排同一枚", plan.Describe());
        Assert.Contains("多 1 页", plan.Describe());           // 用户看完这句才知道有个开关可以关
    }

    [Fact]
    public void 一页一枚那档开关不改变任何结果()
    {
        // 五家真样张全是一页一枚：PerPage=1 时分组与混排必须一模一样，不然升级就改了所有人的版。
        var groups = new[] { 1, 2, 3 };
        var onSpec = FourUp();
        onSpec.FollowsLabel = true;
        var offSpec = FourUp(repeatSame: false);
        offSpec.FollowsLabel = true;
        var grouped = ImpositionEngine.Build(onSpec, W, H, groups.Length, groups);
        var mixed = ImpositionEngine.Build(offSpec, W, H, groups.Length, groups);

        Assert.Equal(1, grouped.PerPage);
        Assert.Equal(3, grouped.PageCount);
        Assert.Equal(mixed.PageCount, grouped.PageCount);
        Assert.Equal(mixed.MixedPageCount, grouped.MixedPageCount);
        Assert.True(grouped.GroupedPerPage);                   // 开关是开着的，只是这里没差别
    }

    [Fact]
    public void 同一个源行分两段出现时算两组不回头填()
    {
        // 顺序里 1 出现两次（中间夹了 2）就按两组处理：引擎不重排用户的标签顺序。
        var groups = new[] { 1, 2, 1 };
        var plan = ImpositionEngine.Build(FourUp(), W, H, groups.Length, groups);

        Assert.Equal(3, plan.PageCount);                       // 三枚各占一页（每页只准一种源标签）
        Assert.Equal(1, plan.MixedPageCount);                  // 混排下三枚本来能挤同一页
        Assert.Contains("多 2 页", plan.Describe());
    }
}
