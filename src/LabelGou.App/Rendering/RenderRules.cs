using System.Windows;
using System.Windows.Media;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// 这一笔画到哪儿去。<strong>只影响"要不要给细线保底"，不影响任何几何位置</strong>。
/// </summary>
public enum RenderTarget
{
    /// <summary>屏幕预览：再细的线也至少留 <see cref="RenderRules.ScreenMinThicknessDiu"/>，否则 96DPI 屏上看不见。</summary>
    Screen,

    /// <summary>位图导出（PNG/TIFF/PDF 页图）：按真实毫米，不做屏幕保底。</summary>
    Bitmap,

    /// <summary>送打印机：按真实毫米。</summary>
    Printer,

    /// <summary>SVG 矢量出口：按真实毫米（线宽直接写进 <c>stroke-width</c>）。</summary>
    Vector,
}

/// <summary>
/// 线宽与颜色的唯一口径（M5 定案 D10）。
/// <para>
/// 抽出来的原因：预览、位图导出、打印、以及本棒新增的 SVG 出口都必须回答同一个问题——
/// "模板里 0.3mm 的框，到最后落多少墨"。以前这个判断散在 <see cref="LabelRenderer"/> 与
/// <see cref="SheetRenderer"/> 里各写了一遍 <c>Math.Max(0.6, ...)</c>，多加一个出口就会多一处漂移。
/// </para>
/// <para>
/// 真值是<strong>毫米</strong>；设备单位（DIU）只是它在特定缩放下的投影。
/// 所以 <see cref="LineWidthMm"/> 用 scale=1 反读 <see cref="LineWidthDiu"/>，两者不可能给出矛盾的答案。
/// </para>
/// </summary>
public static class RenderRules
{
    /// <summary>屏幕上再细的线也至少留这么多 DIU，否则高 dpi 与低 dpi 看着不一样。</summary>
    public const double ScreenMinThicknessDiu = 0.6;

    /// <summary>出片端的下限：约 0.05mm。低于它的线 WPF 会画成发丝线，宁可显式给个下限也不留 0。</summary>
    public const double OutputMinThicknessDiu = 0.2;

    /// <summary>唛头是单色活，墨色就是黑。</summary>
    public static readonly Brush Ink = Brushes.Black;

    /// <summary>需要人工核对的字段：预览里标红 + 淡红底，让它一眼看得见（打印端不会出现，<c>LayoutEngine</c> 已拦截）。</summary>
    public static readonly Brush FlagInk = Frozen(new SolidColorBrush(Color.FromRgb(198, 40, 40)));

    public static readonly Brush FlagBackground = Frozen(new SolidColorBrush(Color.FromArgb(28, 198, 40, 40)));

    /// <summary>参考底图（<c>ReferenceOnly</c>）淡显到这个不透明度：看得见、但绝不会误认为要印的东西。</summary>
    public const double ReferenceOpacity = 0.35;

    /// <summary>
    /// 模板里的毫米线宽 → 设备单位线宽。
    /// </summary>
    /// <param name="thicknessMm">真实印刷线宽（毫米）。</param>
    /// <param name="scale">显示/渲染缩放倍数（1 = 96DPI 实物尺寸；位图导出为 dpi/96）。</param>
    /// <param name="target">落点，决定用哪个保底。</param>
    public static double LineWidthDiu(double thicknessMm, double scale, RenderTarget target)
    {
        var diu = Mm.ToDiu(thicknessMm) * scale;
        if (double.IsNaN(diu) || diu <= 0) diu = 0;
        var floor = target == RenderTarget.Screen ? ScreenMinThicknessDiu : OutputMinThicknessDiu;
        return Math.Max(floor, diu);
    }

    /// <summary>
    /// 真实毫米线宽（SVG 的 <c>stroke-width</c> 与 PDF 那类矢量出口要用）。
    /// <para>导出一律 scale=1，所以这里就是"把 DIU 口径反读回毫米"，与 <see cref="LineWidthDiu"/> 同源。</para>
    /// </summary>
    public static double LineWidthMm(double thicknessMm, RenderTarget target)
        => Mm.FromDiu(LineWidthDiu(thicknessMm, scale: 1, target));

    /// <summary>黑色实线画笔（冻结后可跨线程复用）。</summary>
    public static Pen InkPen(double thicknessMm, double scale, RenderTarget target)
        => Frozen(new Pen(Ink, LineWidthDiu(thicknessMm, scale, target)));

    /// <summary>指定颜色的画笔（线宽口径同上，一次建好就冻结）。</summary>
    public static Pen PenFor(Brush brush, double thicknessMm, double scale, RenderTarget target, DashStyle? dash = null)
        => Frozen(new Pen(brush, LineWidthDiu(thicknessMm, scale, target)) { DashStyle = dash });

    /// <summary>参考底图角上那枚"参考"标记用的淡蓝色。</summary>
    public static readonly Brush ReferenceTint = Frozen(new SolidColorBrush(Color.FromRgb(120, 170, 220)));

    /// <summary>元素边框辅助线（编辑器与预览里的"这有一框"提示，不参与印刷）。</summary>
    public static readonly Brush GuideInk = Frozen(new SolidColorBrush(Color.FromRgb(120, 170, 220)));

    /// <summary>
    /// 系统里找不到指定字体时退回默认字体（打印店机器字体不可控）。
    /// <para>放在这里而不是渲染器里，是因为 SVG 出口取字形几何时也要走同一个回退，否则两条出口用的字体能不一样。</para>
    /// </summary>
    public static FontFamily SafeFontFamily(string family)
    {
        var fallback = new FontFamily(TemplateElement.DefaultFont + ", SimSun, sans-serif");
        if (string.IsNullOrWhiteSpace(family)) return fallback;

        try
        {
            foreach (var existing in Fonts.SystemFontFamilies)
            {
                if (string.Equals(existing.Source, family, StringComparison.OrdinalIgnoreCase))
                    return new FontFamily(family);
            }
        }
        catch (Exception)
        {
            // 枚举系统字体失败（字体服务异常）时用默认字体，不阻断预览
        }
        return fallback;
    }

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
