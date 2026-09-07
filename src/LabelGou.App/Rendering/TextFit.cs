using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LabelGou.Core.Layout;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// 一段文字最终"怎么落"的决定结果：用几号字、往下挪多少、排好的 <see cref="FormattedText"/> 是哪一份。
/// <para>坐标都在设备单位（DIU）域，<see cref="BoxDiu"/> 是元素框，<see cref="TextTopDiu"/> 已经含垂直居中偏移。</para>
/// </summary>
public sealed class TextFitResult
{
    /// <summary>按调用方 scale 缩放后的元素框。</summary>
    public required Rect BoxDiu { get; init; }

    /// <summary>最终字号（DIU），已含缩字号结果与 scale。</summary>
    public required double EmSizeDiu { get; init; }

    /// <summary>未缩放（scale=1）时的字号（DIU）。矢量出口用它反算毫米，与预览同源。</summary>
    public required double CanonicalEmSizeDiu { get; init; }

    /// <summary>文字顶边的绝对 Y（DIU），可直接交给 <c>DrawText</c>。</summary>
    public required double TextTopDiu { get; init; }

    /// <summary>请求字号与实际字号之比（1 = 没缩；小于 1 = 触发过缩字号收敛）。</summary>
    public required double ShrinkRatio { get; init; }

    /// <summary>排好版的文本，已按 <see cref="BoxDiu"/> 宽度做换行与省略号截断。</summary>
    public required FormattedText Formatted { get; init; }

    /// <summary>是否因放不下而缩过字号。</summary>
    public bool Shrunk => ShrinkRatio < 0.999;
}

/// <summary>
/// 文字排版的唯一决定者（M5 定案 D10）：缩字号收敛 + 垂直居中 + 换行截断，只此一份实现。
/// <para>
/// 为什么必须抽出来：预览、位图导出、打印之外，本棒又加了 SVG 矢量出口。
/// 如果"这行字最后用几号字、被截成什么样"在出口里再算一遍，两条出口必然漂移（§七-11 铁律）。
/// 现在 <see cref="LabelRenderer"/> 与 <c>SheetSvgWriter</c> 都调 <see cref="Solve"/>，
/// 前者直接 <c>DrawText</c>，后者拿 <see cref="FormattedText.BuildGeometry"/> 转曲。
/// </para>
/// <para>
/// <strong>关键设计：适配决定恒在 scale=1 的规范域里做</strong>，再按比例乘回调用方的 scale。
/// 原来的实现把 <c>formatted.Height &lt;= box.Height + 0.5</c> 这个带绝对容差的判断放在缩放后的域里，
/// 于是屏幕放大倍数会改变"放得下几行"的判定——同一个模板，预览缩了字而导出没缩。
/// 抽出来顺手把这个不一致消掉：屏幕上看见的字号比例，就是印出来的比例。
/// </para>
/// </summary>
public static class TextFit
{
    /// <summary>缩字号收敛最多试这么多轮（每轮 <see cref="ShrinkStep"/>）。</summary>
    public const int MaxShrinkSteps = 24;

    /// <summary>每轮缩字号的乘数。</summary>
    public const double ShrinkStep = 0.92;

    /// <summary>最多只缩到请求字号的 62%，再小就宁可截断也不印蚂蚁字。</summary>
    public const double MinEmSizeRatio = 0.62;

    /// <summary>判定"放得下"时允许的余量（DIU，规范域），抵掉行高的浮点噪声。</summary>
    public const double HeightSlackDiu = 0.5;

    /// <summary>规范域的取样密度：1 像素 = 1 DIU。决定拟合结果，不影响最终绘制清晰度。</summary>
    public const double CanonicalPixelsPerDip = 1.0;

    /// <summary>请求字号（磅）→ 规范域字号（DIU）。1pt = 96/72 DIU。</summary>
    public static double RequestedEmSizeDiu(double fontSizePt) => Mm.ToDiu(Mm.PointToMm(fontSizePt));

    /// <summary>元素框（毫米）→ 指定 scale 下的 DIU 矩形。</summary>
    public static Rect BoxInDiu(TextItem text, double scale) => new(
        Mm.ToDiu(text.X) * scale, Mm.ToDiu(text.Y) * scale,
        Math.Max(0, Mm.ToDiu(text.Width) * scale), Math.Max(0, Mm.ToDiu(text.Height) * scale));

    /// <summary>字体族回退 + 粗体，两条出口共用（同一段文字不能预览用宋体、导出用回退字体）。</summary>
    public static Typeface TypefaceFor(TextItem text) => new(
        RenderRules.SafeFontFamily(text.FontFamily),
        FontStyles.Normal,
        text.Bold ? FontWeights.Bold : FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>
    /// 解出一段文字的落位。<paramref name="scale"/> 只影响输出坐标，不影响"缩了几档"的决定。
    /// </summary>
    /// <param name="text">版面里的文字项。</param>
    /// <param name="scale">调用方的显示/渲染缩放倍数（屏幕预览为 zoom，导出与打印为 1）。</param>
    /// <param name="pixelsPerDip">当前 DPI 系数，只用于最终那份 <see cref="FormattedText"/> 的光栅化参数。</param>
    public static TextFitResult? Solve(TextItem text, double scale, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return null;

        var box = BoxInDiu(text, scale);
        var canonicalBox = BoxInDiu(text, 1.0);
        if (canonicalBox.Width <= 0 || canonicalBox.Height <= 0) return null;   // 零面积元素画不出东西，交给校验器报越界

        var typeface = TypefaceFor(text);
        var foreground = ForegroundFor(text);
        var requested = RequestedEmSizeDiu(text.FontSizePt);
        var minEmSize = requested * MinEmSizeRatio;

        // —— 规范域（scale=1）里收敛字号，得到与缩放无关的唯一决定 ——
        var emSize = requested;
        var probe = Build(text, typeface, foreground, emSize, canonicalBox.Width, CanonicalPixelsPerDip);
        if (text.ShrinkToFit)
        {
            var guard = 0;
            // 下一档就跌破下限 → 停在当前档：宁可截断，也不印 62% 以下的蚂蚁字
            while (guard++ < MaxShrinkSteps && probe.Height > canonicalBox.Height + HeightSlackDiu && emSize * ShrinkStep >= minEmSize)
            {
                emSize *= ShrinkStep;
                probe = Build(text, typeface, foreground, emSize, canonicalBox.Width, CanonicalPixelsPerDip);
            }
        }

        var centeredOffset = Math.Max(0, (canonicalBox.Height - probe.Height) / 2);

        // —— 按调用方的 scale 出最终那一份（换行/省略号由 MaxTextWidth 驱动，这里必须用缩放后的宽度）——
        var finalEmSize = emSize * scale;
        var formatted = Build(text, typeface, foreground, finalEmSize, box.Width, pixelsPerDip);
        var offsetY = box.Top + centeredOffset * scale;

        return new TextFitResult
        {
            BoxDiu = box,
            EmSizeDiu = finalEmSize,
            CanonicalEmSizeDiu = emSize,
            TextTopDiu = offsetY,
            ShrinkRatio = requested > 0 ? emSize / requested : 1,
            Formatted = formatted,
        };
    }

    /// <summary>需人工核对的字段标红，其余黑字。</summary>
    public static Brush ForegroundFor(TextItem text) => text.Flagged ? RenderRules.FlagInk : RenderRules.Ink;

    /// <summary>
    /// 排出来实际几行。WPF 的 <see cref="FormattedText"/> 没有 <c>LineCount</c> 属性，
    /// 只能拿 <c>Height / LineHeight</c> 反推（多行文字在 SVG 出口里不能写成 <c>&lt;text&gt;</c>，靠它判定）。
    /// </summary>
    public static int LineCountOf(FormattedText formatted)
        => formatted.LineHeight > 0
            ? Math.Max(1, (int)Math.Round(formatted.Height / formatted.LineHeight, MidpointRounding.AwayFromZero))
            : 1;

    /// <summary>
    /// 建一份 <see cref="FormattedText"/>。
    /// <para>WPF 的 FormattedText 没有 TextWrapping 成员（属性/构造参都没有），
    /// 换行完全由 MaxTextWidth 驱动；行数上限用 MaxLineCount + Trimming。</para>
    /// </summary>
    public static FormattedText Build(
        TextItem text, Typeface typeface, Brush foreground, double emSize, double maxWidth, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text.Content,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            emSize,
            foreground,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            TextAlignment = text.Align switch
            {
                HorizontalAlign.Center => TextAlignment.Center,
                HorizontalAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
            Trimming = TextTrimming.CharacterEllipsis,
        };

        if (text.MaxLines > 0) formatted.MaxLineCount = text.MaxLines;
        return formatted;
    }
}
