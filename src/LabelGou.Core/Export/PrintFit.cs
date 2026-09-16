namespace LabelGou.Core.Export;

public enum PrintFitLevel
{
    /// <summary>整版能原大放进可打印区，什么都不用改。</summary>
    Exact,
    /// <summary>差一点点（在安全余量内），可按 1:1 打，但边缘裁切线可能吃到纸边。</summary>
    Tight,
    /// <summary>必须缩小才能打进可打印区。</summary>
    NeedsShrink,
    /// <summary>差得太多，缩小后标签尺寸就不准了，应当换纸或改页边。</summary>
    Rejected,
}

/// <summary>
/// 打印落位体检结果：驱动给的"可打印区"往往比纸张小一圈（激光机常见四边各 4~5mm），
/// 而我们的整版是按毫米硬排的，页边距小于这个圈就会打空一截。这里只给结论和建议缩放，
/// 真打不打由用户决定——唛头是认货的，宁可让他知道也不能偷改尺寸。
/// </summary>
public sealed record PrintFitAdvice(
    PrintFitLevel Level,
    double SuggestedScale,
    double OverflowWidthMm,
    double OverflowHeightMm,
    IReadOnlyList<string> Warnings)
{
    public bool IsSafeToPrintAtOneToOne => Level is PrintFitLevel.Exact or PrintFitLevel.Tight;

    /// <summary>给状态栏用的一句话。</summary>
    public string Describe(string pageLabel)
    {
        var text = Level switch
        {
            PrintFitLevel.Exact => $"{pageLabel}可原大打印（1:1）。",
            PrintFitLevel.Tight => $"{pageLabel}勉强放得下，但离纸边很近，裁切线可能被驱动裁掉。",
            PrintFitLevel.NeedsShrink => $"{pageLabel}超出可打印区，需缩到约 {SuggestedScale:P0} 才能打全（标签尺寸会跟着变小）。",
            _ => $"{pageLabel}比可打印区大出 {OverflowWidthMm:F1}×{OverflowHeightMm:F1}mm，缩小就没法保证尺寸，请换纸或减小页边。",
        };
        return Warnings.Count == 0 ? text : text + " " + string.Join(" ", Warnings);
    }
}

/// <summary>
/// 纸张尺寸与驱动可打印区的比对（纯算术，无 UI 依赖，可单测）。
/// </summary>
public static class PrintFit
{
    /// <summary>Tight 与 NeedsShrink 的分界：超出 1mm 以内算"勉强放得下"。</summary>
    public const double TightToleranceMm = 1.0;

    /// <summary>缩放低于此值就判 Rejected：再小标签上的字就变形了，不如让用户改设置。</summary>
    public const double MinimumAcceptableScale = 0.90;

    /// <summary>
    /// 整版超出可打印区、又没勾「放不下就缩放」时，要不要先问一句「照样打」。
    /// <para>
    /// 这一问是第 75 棒加的：用户实测「版排好了但打不出去」——真因往往是**驱动里的纸张尺寸**还停在
    /// A4（我们改不动它），而不是版错了。以前这里直接抛异常，人只看到一句"换纸或减小页边"，
    /// 不知道去哪儿换；现在改成问明白，点「是」就按 1:1 照打（超出部分会被裁），点「否」留时间去改驱动。
    /// </para>
    /// </summary>
    public static string? OverflowConfirmText(
        this PrintFitAdvice advice, double pageWidthMm, double pageHeightMm, bool scaleAllowed)
    {
        if (scaleAllowed || advice.IsSafeToPrintAtOneToOne) return null;
        return $"整版 {pageWidthMm:0.#}×{pageHeightMm:0.#}mm 比这台打印机现在认的纸大出 "
            + $"{advice.OverflowWidthMm:0.#}×{advice.OverflowHeightMm:0.#}mm。\n\n"
            + "点「是」：按 1:1 照样打，标签尺寸不变，超出的一截会被裁掉。\n"
            + "点「否」：先别打。多数情况不是版排错了，而是驱动里的**纸张尺寸**没改（常停在 A4）——"
            + $"进 ⑤ 步的「打印首选项…」把纸张尺寸改成 {pageWidthMm:0.#}×{pageHeightMm:0.#}mm（或你那张标签纸）再打。\n\n"
            + "（勾上「放不下就缩放」是另一条路：会整版缩小，标签尺寸就不准了，一般别勾。）";
    }

    public static PrintFitAdvice Evaluate(
        double pageWidthMm,
        double pageHeightMm,
        double printableWidthMm,
        double printableHeightMm)
    {
        var warnings = new List<string>();
        if (pageWidthMm <= 0 || pageHeightMm <= 0)
        {
            return new PrintFitAdvice(PrintFitLevel.Rejected, 1.0, 0, 0, new[] { "纸张尺寸没读到，无法判断能否打印。" });
        }
        if (printableWidthMm <= 0 || printableHeightMm <= 0)
        {
            // 有些驱动（虚拟打印机、脱机设备）报不出可打印区，只能按"假定等于纸张"处理并说明。
            warnings.Add("这台打印机没报出可打印范围，按整纸可打估算，实际可能四边打不满。");
            return new PrintFitAdvice(PrintFitLevel.Exact, 1.0, 0, 0, warnings);
        }

        var overflowX = Math.Max(0, pageWidthMm - printableWidthMm);
        var overflowY = Math.Max(0, pageHeightMm - printableHeightMm);
        var scale = Math.Min(printableWidthMm / pageWidthMm, printableHeightMm / pageHeightMm);
        scale = Math.Min(1.0, Math.Floor(scale * 1000) / 1000);       // 往下取千分位，别给个刚好卡住的数

        if (overflowX <= 0 && overflowY <= 0)
        {
            var marginX = printableWidthMm - pageWidthMm;
            var marginY = printableHeightMm - pageHeightMm;
            var tight = Math.Min(marginX, marginY) < TightToleranceMm;
            if (tight) warnings.Add("可打印区与纸张几乎等大，边缘 1mm 内的内容有可能丢失。");
            return new PrintFitAdvice(tight ? PrintFitLevel.Tight : PrintFitLevel.Exact, 1.0, 0, 0, warnings);
        }

        if (overflowX <= TightToleranceMm && overflowY <= TightToleranceMm)
        {
            warnings.Add($"超出可打印区约 {Math.Max(overflowX, overflowY):F1}mm，按 1:1 打可能切到最外圈裁切线。");
            return new PrintFitAdvice(PrintFitLevel.Tight, 1.0, overflowX, overflowY, warnings);
        }

        if (scale < MinimumAcceptableScale)
        {
            warnings.Add("建议缩放过低会破坏标签实际尺寸，改成换大一号的纸或减小拼版页边更稳妥。");
            return new PrintFitAdvice(PrintFitLevel.Rejected, scale, overflowX, overflowY, warnings);
        }

        warnings.Add($"按 {scale:P0} 缩放可整版打全（唛头字号会等比变小）。");
        return new PrintFitAdvice(PrintFitLevel.NeedsShrink, scale, overflowX, overflowY, warnings);
    }
}
