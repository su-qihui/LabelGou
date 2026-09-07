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

    /// <summary>
    /// 需要人工核对的字段：红字 + 淡红底，预览/打印/导出五个出口都上。
    /// <para>为什么打印件上也红着：<c>LabelGou.Core.Layout.LayoutEngine</c> 对 <c>NeedsReview</c> 只标记不拦，
    /// 真正的闸门是输出前的复核确认（<c>ExportViewModel.PassesReviewGate</c>）——红字红底上纸是 §五-59
    /// 定的刻意行为：漏网时那张纸上得看得见「这一格没核」，而不是看起来完全正常。</para>
    /// </summary>
    public static readonly Brush FlagInk = Frozen(new SolidColorBrush(Color.FromRgb(198, 40, 40)));

    /// <summary>待核标记的颜色本体（SVG 出口写 <c>fill</c> 要用十六进制，与 <see cref="FlagInk"/> 同源）。</summary>
    public static readonly Color FlagColor = Color.FromRgb(198, 40, 40);

    public static readonly Brush FlagBackground = Frozen(new SolidColorBrush(Color.FromArgb(28, 198, 40, 40)));

    /// <summary>角线：黑（单色活，裁切参考线不该有别的颜色）。</summary>
    public static readonly Color CropMarkColor = Color.FromRgb(0, 0, 0);

    /// <summary>套准十字：蓝。与标签内容同色就会在拼版时看不出哪个是对位用的。</summary>
    public static readonly Color RegistrationColor = Color.FromRgb(0, 120, 200);

    /// <summary>刀模示意线：浅灰，只给预览对位用。</summary>
    public static readonly Color LabelOutlineColor = Color.FromRgb(170, 170, 170);

    /// <summary>
    /// 标记颜色写给 SVG 的十六进制（与上面三个 <see cref="Color"/> 同源）。
    /// <para>第五个出口以前自己手写了一串 <c>"#000000"</c> 当套准色，于是预览里蓝十字、件上黑十字。</para>
    /// </summary>
    public static string HexOf(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

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
