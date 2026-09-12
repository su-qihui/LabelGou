using LabelGou.App.Export;
using LabelGou.App.Rendering;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 单行大字行的「宽度不够 → 缩字号」。
/// <para>
/// 这一组是被真样件肉眼复核逼出来的：M7 第一版渲染出来的金沐唛头把
/// <c>Item no：olu830-35*144</c> 印成了 <c>olu830…</c>，郑小姐那张把
/// <c>AJ7-QI YUE: Aj9</c> 印成了 <c>AJ7-…</c>。唛头上货号被截断等于打错货，
/// 而原来的缩字号循环只比高度不比宽度——撑满大字的行高天生就等于行带高，所以那个循环对单行永远不触发。
/// </para>
/// </summary>
public class SingleLineTextFitTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((progress, token) => work(), null, CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <summary>
    /// 本族测的都是「按宽度缩字/截断」那条路——第 46 棒起它要求显式给了折行宽度
    /// （默认 0 = 永不折行，宽度就不再参与缩字），所以这里把折行宽度摆成与盒宽相同，把前提写显式。
    /// </summary>
    private static TextItem Row(
        string content, double widthMm, double heightMm, double sizePt, int maxLines, bool bold = true) => new(
        content, 5, 5, widthMm, heightMm, "Microsoft YaHei", sizePt,
        bold, HorizontalAlign.Left, ShrinkToFit: true, MaxLines: maxLines, WrapWidthMm: widthMm);

    [Fact]
    public void SingleLineShrinksToFitWidthInsteadOfEllipsising() => OnSta(() =>
    {
        // 金沐那张第二行的真实几何：130mm 宽、18.26mm 行带、38.3pt
        var fit = TextFit.Solve(Row("Item no：olu830-35*144", 130, 18.26, 38.3, 1), 1.0, 1.0);
        Assert.NotNull(fit);
        Assert.True(fit!.Shrunk, "宽度装不下却没缩字号，说明循环还是只看了高度");
        Assert.False(fit.Truncated, "缩一档就装得下，不该被省略号截断");
        Assert.True(fit.CanonicalEmSizeDiu < TextFit.RequestedEmSizeDiu(38.3), "实际字号必须小于行带反算出来的字号");
        return true;
    });

    [Fact]
    public void BigDisplayRowMayShrinkPastTheSixtyTwoPercentRatio() => OnSta(() =>
    {
        // 160×120 那两张超大字：110pt 的 62% 是 68pt，可这行字本来就装不下 68pt。
        // 撑满行的字号是从行带几何反算的，不是人挑的字号，所以单行改用更宽的下限。
        var fit = TextFit.Solve(Row("AJ7-QI YUE: Aj9", 150, 52.5, 110, 1), 1.0, 1.0);
        Assert.NotNull(fit);
        Assert.True(fit!.ShrinkRatio < TextFit.MinEmSizeRatio,
            $"这行还卡在 62% 比率下限上（{fit.ShrinkRatio:0.###}），说明单行没走 MinSingleLineEmSizeRatio");
        Assert.False(fit.Truncated, "郑小姐那张的第二行必须整条印出来");
        return true;
    });

    [Fact]
    public void MultiLineWidthOverflowWrapsInsteadOfShrinking() => OnSta(() =>
    {
        var content = string.Join(" ", Enumerable.Repeat("MADE IN CHINA", 8));
        var wide = TextFit.Solve(Row(content, 200, 40, 12, 3, bold: false), 1.0, 1.0);
        var narrow = TextFit.Solve(Row(content, 20, 40, 12, 3, bold: false), 1.0, 1.0);
        Assert.NotNull(wide);
        Assert.NotNull(narrow);
        Assert.Equal(1.0, wide!.ShrinkRatio, 9);         // 一行就放下，本来就不该缩
        Assert.Equal(1.0, narrow!.ShrinkRatio, 9);       // 折成几行后高度仍装得下：只折行，不动字号
        Assert.True(narrow!.LineCount > 1, "这份夹具得真的折了行，否则比的是个没被考验的分支");
        return true;
    });

    [Fact]
    public void TruncationIsReportedWhenEvenTheFloorCannotFit() => OnSta(() =>
    {
        var fit = TextFit.Solve(Row(new string('国', 200), 24, 5, 12, 1), 1.0, 1.0);
        Assert.NotNull(fit);
        Assert.True(fit!.Truncated, "200 个全角字塞进 24×5mm 不可能装下，这时必须报截断而不是假装没事");
        Assert.True(fit.CanonicalEmSizeDiu >= TextFit.RequestedEmSizeDiu(TextFit.MinReadablePt) - 0.01,
            "不许为了装下内容跌破绝对可读下限");
        return true;
    });

    [Fact]
    public void JinMuBuiltInRowsPrintEveryFieldInFull() => OnSta(() =>
    {
        // 端到端：内置金沐模板 + 真表第一行数据，四行里任何一行都不许出现省略号
        var record = MarkRecord.Builder()
            .SetRow(1, "olu830-35*144")
            .Set(MarkFieldKey.Consignee, "BOLAROM")
            .Set(MarkFieldKey.ItemNo, "olu830-35*144")
            .Set(MarkFieldKey.Quantity, "144")
            .Set(MarkFieldKey.CartonTotal, "15")
            .SetCustom("col:本行箱数", "5")
            .Build();
        var layout = LayoutEngine.Build(BuiltInTemplates.RowsFour140x100(), record, new LayoutContext(1, 15, "金沐.xlsx"));
        var texts = layout.Items.OfType<TextItem>().ToList();
        Assert.Equal(4, texts.Count);

        foreach (var text in texts)
        {
            var fit = TextFit.Solve(text, 1.0, TextFit.CanonicalPixelsPerDip);
            Assert.NotNull(fit);
            Assert.False(fit!.Truncated, $"「{text.Content}」被截断了，唛头不能印半截内容");
        }
        return true;
    });
}
