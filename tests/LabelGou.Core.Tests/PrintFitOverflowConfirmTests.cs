using LabelGou.Core.Export;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 75 棒：整版超出可打印区时那句「照样打吗」。
/// 判据盯的是**话里有没有给出路**——用户实测「版排好了却打不出去」，真因往往是驱动里纸张尺寸还停在 A4，
/// 光说"换纸或减小页边"等于没说。
/// </summary>
public class PrintFitOverflowConfirmTests
{
    /// <summary>店里那台 RICOH：整版 280×200mm，驱动按 A4 报可打印区（宽约 200mm）。</summary>
    private static PrintFitAdvice AgainstA4() => PrintFit.Evaluate(280, 200, 200, 287);

    [Fact]
    public void AsksInsteadOfThrowingAndNamesTheRealFix()
    {
        var ask = AgainstA4().OverflowConfirmText(280, 200, scaleAllowed: false);

        Assert.NotNull(ask);
        Assert.Contains("280×200mm", ask);
        Assert.Contains("照样打", ask);
        Assert.Contains("80", ask);                       // 超出的毫米数要报出来，不许只说"超出了"
        Assert.Contains("打印首选项", ask);                // 出路要点名到界面上那颗按钮
        Assert.Contains("纸张尺寸", ask);
    }

    [Fact]
    public void DoesNotAskWhenItFitsOrWhenScalingWasAllowed()
    {
        Assert.Null(PrintFit.Evaluate(200, 150, 200, 287).OverflowConfirmText(200, 150, false));
        // 勾了「放不下就缩放」：那条路自己会缩到放得下，再问一遍就是重复打扰
        Assert.Null(AgainstA4().OverflowConfirmText(280, 200, scaleAllowed: true));
    }

    [Fact]
    public void TightCaseStillPrintsAtOneToOneWithoutAsking()
    {
        // 差 1mm 以内是 Tight：能 1:1 打，不该拦人（这条防的是"以后有人把 Tight 也拉去问一遍"）
        var advice = PrintFit.Evaluate(200, 150, 199.5, 287);
        Assert.Equal(PrintFitLevel.Tight, advice.Level);
        Assert.Null(advice.OverflowConfirmText(200, 150, false));
    }
}
