using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 五出口同源的**总闸**（第 88 棒）：按 <see cref="ElementKind"/> 遍历，每种类型都必须真的被排版出来，
/// 而且不许被错认成别的类型。
/// <para>为什么要有这一条：现有的横向判据是"按形状各一条"（<c>RectAppearanceTests</c>、
/// <c>ShapeToolTests</c>、<c>RotationFixTests</c>），<strong>新增一种 kind 不会被任何一条碰到</strong>；
/// 而 <c>LayoutEngine</c> 那个 switch 把 <c>case Text:</c> 和 <c>default:</c> 写在同一格——
/// 新类型忘了加 case 会被<strong>静默当文字排</strong>（内容为空就整条"隐藏"掉，界面上就是"东西不见了"）。
/// 这类丢法编译不报错、老测试不红，只有遍历全部类型才拦得住。</para>
/// </summary>
public class FiveOutletKindParityTests
{
    /// <summary>能被软件自己画出来的那些类型（图片与矢量底图要资源文件，另测）。</summary>
    static readonly ElementKind[] DrawableEverywhere =
    {
        ElementKind.Text, ElementKind.Line, ElementKind.Rect, ElementKind.Ellipse,
        ElementKind.Polygon, ElementKind.Barcode, ElementKind.Placeholder,
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_produces_a_layout_item(ElementKind kind)
    {
        var layout = Build(BigEnoughLabel(One(kind)));
        Assert.True(layout.Items.Count > 0,
            $"{kind} 一个版面项都没产出——它会在预览与出纸上凭空消失（静默丢对象）");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void No_kind_is_silently_treated_as_text(ElementKind kind)
    {
        var layout = Build(BigEnoughLabel(One(kind)));
        if (kind == ElementKind.Text) return;
        Assert.DoesNotContain(layout.Items, item => item is TextItem && ((TextItem)item).Source?.Kind == kind);
    }

    [Fact]
    public void Placeholder_is_emitted_as_its_own_item_and_carries_the_reason()
    {
        var layout = Build(BigEnoughLabel(One(ElementKind.Placeholder)));
        var item = Assert.Single(layout.Items.OfType<PlaceholderItem>());
        Assert.Equal(10.0, item.X, 3);
        Assert.Equal(20.0, item.Width, 3);
        Assert.Contains("OLE", item.Reason, StringComparison.Ordinal);
    }

    /// <summary>打回原样必红：把 <c>case ElementKind.Placeholder</c> 从 switch 里删掉，
    /// 它会落进 <c>case Text: default:</c> 那一格 → 上面三条同时红（占位项变成 0 条或被当成文字）。</summary>
    [Fact]
    public void Every_kind_is_known_to_the_flattener_and_the_validator()
    {
        var e = One(ElementKind.Placeholder);
        Assert.True(e.Visible);
        // 校验器不许因为"既不描边也不填充"就报它有问题：占位框本来就没有墨，这是它的画法。
        var issues = TemplateValidator.Validate(BigEnoughLabel(e));
        Assert.DoesNotContain(issues, i => i.Message.Contains("既不描边也不填充", StringComparison.Ordinal));
        // 也不许把它当"探出标签"的 Error（它是导入来的，位置照 Corel）。
        Assert.DoesNotContain(issues, i => i.Severity == IssueLevel.Error && i.Message.Contains("占位", StringComparison.Ordinal));
    }

    static LabelTemplate BigEnoughLabel(TemplateElement e)
    {
        var t = new LabelTemplate { Name = "总闸", WidthMm = 140, HeightMm = 100, BorderMm = 0 };
        t.Elements.Add(e);
        return t;
    }

    static LabelLayout Build(LabelTemplate template) =>
        LayoutEngine.Build(template, MarkRecord.Builder().SetRow(1, "总闸.xlsx")
            .SetCustom("col:条码", "1234567").Build(), new LayoutContext(1, 1));

    static TemplateElement One(ElementKind kind) => kind switch
    {
        ElementKind.Text => new TemplateElement { Kind = kind, Text = "QTY：144 pcs", X = 10, Y = 10, Width = 60, Height = 10, FontSizePt = 12 },
        ElementKind.Line => new TemplateElement { Kind = kind, X = 10, Y = 10, X2 = 70, Y2 = 10, ThicknessMm = 0.35 },
        ElementKind.Rect => new TemplateElement { Kind = kind, X = 10, Y = 10, Width = 60, Height = 20, ThicknessMm = 0.35 },
        ElementKind.Ellipse => new TemplateElement { Kind = kind, X = 10, Y = 10, Width = 60, Height = 20, ThicknessMm = 0.35 },
        ElementKind.Polygon => new TemplateElement { Kind = kind, X = 10, Y = 10, Width = 60, Height = 20, ThicknessMm = 0.35, PolygonSides = 5 },
        ElementKind.Barcode => new TemplateElement { Kind = kind, Text = "{{col:条码}}", X = 10, Y = 10, Width = 60, Height = 20, FontSizePt = 8 },
        _ => new TemplateElement
        {
            Kind = kind, X = 10, Y = 10, Width = 20, Height = 23,
            Imported = true,
            SourceNotes = new List<string> { "lgob 里有 olel＝内嵌 OLE 对象，本通道只拿到盒" },
        },
    };

    public static TheoryData<ElementKind> Kinds => new(DrawableEverywhere);
}
