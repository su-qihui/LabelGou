using LabelGou.App.Export;
using LabelGou.App.Rendering;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 85 棒①：关掉折行之后，那一行<strong>仍然要会缩字</strong>。
/// <para>用户 2026-09-19 在 ④ 步「全部行」里圈出三格重影：长货号按 130mm 的折行宽度折成两行，
/// 而那一格的行带只有 8.1mm 高，第二行直接压在下一格 <c>QTY:</c> 上。他要"即使超出也不折行"，
/// 又拍了"两种方案都做出来、优先自动第一种"——于是这里钉四件事：</para>
/// <list type="number">
/// <item>同一份长值：允许折行 → 两行；关掉 → 一行；</item>
/// <item>关掉折行<strong>不是</strong>关掉缩字：装不下先按折行宽度缩（这是 A 档，也是从前 NoWrap 那条路完全没有的一半）；</item>
/// <item>缩到下限仍装不下 → 单行照实伸出，且<strong>不打省略号、不标红</strong>（越界归裁切与两道墨迹闸管）；</item>
/// <item>把「自动缩字」也关掉 → 原字号直接伸出（这是他要保留的 B 档）。</item>
/// </list>
/// <para>夹具的行带高度都刻意给够（30mm），让"高度装不下"那条判据永远不参与——
/// 否则缩字号到底是宽度逼的还是高度逼的分不清，判据就成了假绿（§五-151 同族）。</para>
/// </summary>
public class NoWrapStillShrinksTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>一条 130mm 折行宽度的明细行；<paramref name="allowWrap"/> 为 null 时走默认（=允许折行）。</summary>
    private static TextItem Row(string content, double sizePt, bool? allowWrap, bool shrink = true) => new(
        content, 5, 5, 130, 30, "Microsoft YaHei", sizePt,
        false, HorizontalAlign.Left, ShrinkToFit: shrink, MaxLines: 3, WrapWidthMm: 130,
        AllowWrap: allowWrap ?? true);

    /// <summary>20pt 下自然宽度约 217mm 的一条值：折 130mm 就得缩到六成，正好落在下限之上。</summary>
    private const string LongValue = "CONSIGNEE: PT MAKMUR JAYA ABADI SENTOSA 2026 BLOCK C7 NO 12";

    [Fact]
    public void TheSameLongValueWrapsWhenAllowedAndStaysOnOneLineWhenNot() => OnSta(() =>
    {
        var wrapped = TextFit.Solve(Row(LongValue, 20, allowWrap: true), 1.0, 1.0);
        var flat = TextFit.Solve(Row(LongValue, 20, allowWrap: false), 1.0, 1.0);
        Assert.NotNull(wrapped);
        Assert.NotNull(flat);

        Assert.True(wrapped!.LineCount > 1, "这份夹具没真的折行——那两条分支比的是个没被考验的洞（判据不合格）");
        Assert.True(flat!.LineCount == 1, "关掉折行还折成两行 = 用户圈出来的那格重影没修掉");
        return true;
    });

    /// <summary>
    /// A 档的核心：不折行<strong>仍按折行宽度缩字</strong>。
    /// 从前 <c>NoWrap</c> 那条路的宽度判据比的是无限宽，永远不触发缩字——这条就是那半件事的机器形状。
    /// </summary>
    [Fact]
    public void ANonWrappedLineStillShrinksToItsWidthBudget() => OnSta(() =>
    {
        var fit = TextFit.Solve(Row(LongValue, 20, allowWrap: false), 1.0, 1.0);
        Assert.NotNull(fit);
        Assert.True(fit!.ShrinkRatio < 0.999,
            $"一字没缩（{fit.ShrinkRatio:0.###}）：折行宽度没被拿去当缩字目标，长值只能原字号伸出去");
        Assert.True(fit.ShrinkRatio > TextFit.MinSingleLineEmSizeRatio,
            "缩到了单行下限 = 这条值本该靠缩字解决，却被当成塞不下的例外");
        Assert.Equal(1, fit.LineCount);
        Assert.False(fit.Truncated, "整条都印得出来（缩过字），不该挂截断的警示色");
        return true;
    });

    /// <summary>缩到下限仍装不下：宁可单行伸出纸外，也不折行、也不打省略号。</summary>
    [Fact]
    public void EvenAtTheFloorItStaysOnOneLineAndOverflowsWithoutAnEllipsis() => OnSta(() =>
    {
        var huge = string.Join(" ", Enumerable.Repeat(LongValue, 4));
        var fit = TextFit.Solve(Row(huge, 20, allowWrap: false), 1.0, 1.0);
        Assert.NotNull(fit);
        Assert.Equal(1, fit!.LineCount);
        // 夹具自证：这条得真的撞到地板，否则测不到"缩到底还是装不下"那一档。
        // 地板是"30% 比率"与"9pt 绝对可读下限"里较高的那个——20pt 的这一档是 9pt（45%），不是 30%；
        // 循环是"下一档就跌破 → 停在当前档"，所以落点在地板之上不到一档处。
        var floorRatio = Math.Max(TextFit.MinSingleLineEmSizeRatio, TextFit.MinReadablePt / 20);
        Assert.True(fit.ShrinkRatio <= floorRatio / TextFit.ShrinkStep + 0.001,
            $"只缩到 {fit.ShrinkRatio:0.###}，没撞到可读地板（{floorRatio:0.###}）= 这条测的是个没被考验的分支");
        Assert.True(fit.InkDiu.Width > Mm.ToDiu(130),
            "墨迹没伸出 130mm 的折行宽度 = 这条没测到伸出");
        Assert.False(fit.Truncated, "伸出纸外由裁切与越界闸接手，不该在这里偷偷换成省略号");
        return true;
    });

    /// <summary>B 档（他点名要保留的第二种方案）：把「自动缩字」也关掉，就原字号伸出去。</summary>
    [Fact]
    public void TurningOffAutoShrinkLeavesTheTypeSizeAlone() => OnSta(() =>
    {
        var fit = TextFit.Solve(Row(LongValue, 20, allowWrap: false, shrink: false), 1.0, 1.0);
        Assert.NotNull(fit);
        Assert.Equal(1.0, fit!.ShrinkRatio, 9);
        Assert.Equal(1, fit.LineCount);
        Assert.True(fit.InkDiu.Width > Mm.ToDiu(130));
        return true;
    });

    /// <summary>
    /// 默认值不许漂：<c>TextItem</c> 不写 <c>AllowWrap</c> 时必须仍是"允许折行"，
    /// 否则存量模板与 ②③ 步那些直接造版面项的调用点会集体换行为。
    /// </summary>
    [Fact]
    public void ATextItemBuiltWithoutTheFlagStillWraps() => OnSta(() =>
    {
        var plain = new TextItem(LongValue, 5, 5, 130, 30, TemplateElement.DefaultFont, 20,
            false, HorizontalAlign.Left, ShrinkToFit: true, MaxLines: 3, WrapWidthMm: 130);
        Assert.True(plain.Wraps);
        Assert.False(plain.NoWrap);
        var fit = TextFit.Solve(plain, 1.0, 1.0);
        Assert.NotNull(fit);
        Assert.True(fit!.LineCount > 1, "默认那一路不再折行 = 这次改动动了存量模板的出纸行为");
        return true;
    });
}
