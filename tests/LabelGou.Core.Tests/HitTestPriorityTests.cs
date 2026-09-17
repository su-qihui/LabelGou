using LabelGou.Core.Editing;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 83 棒①：点选命中的优先级——<strong>谁的字看得见，点它就选谁</strong>。
/// <para>用户拿截图圈出来：他想点第一行 <c>ITEM NO:AD-20</c>，可第二行 <c>QTY:{{col:QTY}}PCS</c> 那条
/// 130×123.37mm 的<strong>隐形行带</strong>盖在整张纸上，而且它在图层里压着第一行——于是点第一行的字
/// 只选中了第二行，"点击也是只能被第二行控制"。</para>
/// <para>第 44 棒定"点选按宽容盒（整条行带）"是为了"点文字旁边的空白也选得中这一行"；第 63 棒把墨迹盒
/// <em>并</em>进命中范围是为了解决"右半段抓不住"。两条叠在一起就出现今天这个洞：<strong>宽容盒会把别人的字抢走</strong>。
/// 修法是把"宽容"降到第二遍——第一遍只认看得见的那一块。</para>
/// </summary>
public class HitTestPriorityTests
{
    private static LabelTemplate Paper() => new()
    {
        Id = "user.hit", Name = "两行叠着", WidthMm = 140, HeightMm = 100, PaddingMm = 5, BorderMm = 0,
    };

    private static TemplateElement Band(double x, double y, double w, double h, string text) => new()
    {
        Kind = ElementKind.Text, Text = text, X = x, Y = y, Width = w, Height = h, FontSizePt = 12, WrapWidthMm = w,
    };

    /// <summary>用户那份的形状：01 在上层被 02 的通栏行带整个盖住。</summary>
    private static LabelTemplate TwoLines(out TemplateElement lower, out TemplateElement upper)
    {
        var template = Paper();
        // 01：先画（最底层），看得见的一行字在左上
        lower = Band(5, 5, 130, 20, "第一行");
        // 02：压在上面的那条——行带 130×123.37 从 y=−5.4 起，盖住整张纸；它的字其实在下面
        upper = Band(19.5, -5.4, 130, 123.37, "第二行");
        template.Elements.Add(lower);
        template.Elements.Add(upper);
        return template;
    }

    private static readonly (double X, double Y, double Width, double Height) LowerInk = (10, 10, 40, 9);
    private static readonly (double X, double Y, double Width, double Height) UpperInk = (25, 60, 40, 9);

    private static Func<TemplateElement, (double X, double Y, double Width, double Height)?> Ink
        => e => e.Text == "第一行" ? LowerInk : UpperInk;

    [Fact]
    public void ClickingTheVisibleTextOfTheLowerLinePicksThatLine()
    {
        var template = TwoLines(out var lower, out var upper);

        // 点 (30,14)：在 01 的墨迹里，同时也在 02 那条通栏行带里——从前这里返回 02（用户报的现象）
        var picked = EditGeometry.TopmostAt(template, 30, 14, inkBoxes: Ink);

        Assert.Equal(template.Elements.IndexOf(lower), picked);
        Assert.NotSame(upper, template.Elements[picked]);
    }

    /// <summary>第二遍仍要宽容：点字旁边的空白（谁都碰不到字）时，照旧按最上层行带走。</summary>
    [Fact]
    public void ClickingEmptySpaceStillFallsBackToTheGenerousBand()
    {
        var template = TwoLines(out _, out var upper);

        var picked = EditGeometry.TopmostAt(template, 100, 16, inkBoxes: Ink);

        Assert.Equal(template.Elements.IndexOf(upper), picked);
    }

    /// <summary>量不到墨迹（隐藏、变量全空）的那一行不能因为"第一遍没命中"就再也点不中。</summary>
    [Fact]
    public void ALineWhoseInkCannotBeMeasuredIsStillPickable()
    {
        var template = Paper();
        var text = Band(5, 5, 60, 20, "空值的一行");
        template.Elements.Add(text);

        Assert.Equal(0, EditGeometry.TopmostAt(template, 40, 12, inkBoxes: _ => null));
    }

    [Fact]
    public void NonTextElementsKeepTheirOwnBox()
    {
        var template = Paper();
        var rect = new TemplateElement { Kind = ElementKind.Rect, X = 10, Y = 10, Width = 20, Height = 10 };
        template.Elements.Add(rect);

        // 真实调用方（App 的 DisplayBoxOf）对非文本给 null——照它喂，否则测的是"盒子被并到矩形身上"那件事
        var picked = EditGeometry.TopmostAt(template, 15, 15, inkBoxes: _ => null);
        Assert.Equal(0, picked);
        Assert.Equal(-1, EditGeometry.TopmostAt(template, 60, 60, inkBoxes: _ => null));
    }

    /// <summary>转过的字：第一遍认的是"转完之后看得见那块"的外接，与画出来的框同一个。</summary>
    [Fact]
    public void ARotatedLinesInkBoxIsTheOneDrawnBox()
    {
        var template = Paper();
        var text = Band(5, 5, 130, 20, "转过的行");
        text.RotationDeg = 90;
        template.Elements.Add(text);

        // 墨迹盒 (10,10,40,9) 绕自己的中心转 90° → 变成竖着的一条：宽 9、高 40，中心 (30,14.5) 不变
        Assert.Equal(0, EditGeometry.TopmostAt(template, 32, 30, inkBoxes: _ => LowerInk));
        // 反证：这一点既不在转过的墨迹外接里、也不在那条 130×20 的行带里（行带只到 y=25）
        Assert.Equal(-1, EditGeometry.TopmostAt(template, 100, 90, inkBoxes: _ => LowerInk));
    }
}
