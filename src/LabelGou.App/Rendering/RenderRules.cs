using System.Windows;
using System.Windows.Media;
using LabelGou.Core.Colors;
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

    /// <summary>没填颜色的元素用哪支墨：唛头历来是单色活，默认就是黑（第 47 棒起元素才可以自己带色）。</summary>
    public static readonly Brush Ink = Brushes.Black;

    /// <summary>
    /// 元素自己填的那支墨 → 屏幕上的笔色（第 47 棒）。<strong>null = <see cref="Ink"/>（黑）</strong>，
    /// 一个字节都不动老模板的观感。
    /// <para>为什么用 <see cref="LabelColor.R"/>/<see cref="LabelColor.G"/>/<see cref="LabelColor.B"/> 那一半、
    /// 而不是把 CMYK 交给 WPF 去转：实测（<c>labelgou-other\_probe\cmyk-tiff\out.txt</c>）WPF 的 Cmyk32
    /// 隐式换算把 <c>K=100</c> 显示成 <c>RGB(23,23,24)</c>、把"无墨"显示成 <c>RGB(253,254,255)</c>——
    /// 拿它显示，用户看到的就是一句"我明明填了 100% 黑，屏上怎么是灰的"。换算口径只此一家（<c>CmykMath</c>），
    /// 五出口读同一份。</para>
    /// </summary>
    public static Brush InkOf(LabelColor? color) => color is null ? Ink : BrushCache.GetOrAdd(KeyOf(color), Create);

    /// <summary>同一支墨写给 SVG 的十六进制（<strong>与 <see cref="InkOf"/> 同源</strong>，null = 黑）。</summary>
    public static string InkHex(LabelColor? color) => color?.ToHex() ?? "#000000";

    private static int KeyOf(LabelColor color) => (color.R << 16) | (color.G << 8) | color.B;

    /// <summary>
    /// 笔色缓存：整版预览与位图导出会把同一个颜色问上成千上万次，每次新建 <see cref="SolidColorBrush"/>
    /// 既没必要也会把 GC 拖进来；冻结后的画笔可跨线程复用（栅格化跑在后台线程上）。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SolidColorBrush> BrushCache = new();

    private static SolidColorBrush Create(int key) => Frozen(new SolidColorBrush(Color.FromRgb(
        (byte)((key >> 16) & 0xFF), (byte)((key >> 8) & 0xFF), (byte)(key & 0xFF))));

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

    /// <summary>
    /// 指定颜色的画笔（线宽口径同上，一次建好就冻结）。
    /// <para>刻意<strong>不</strong>留一个"黑色画笔"的快捷入口：元素可以自带颜色之后，
    /// 任何直接要笔的地方都该先回答"这一支墨是谁的"，写死黑就是给第五个出口留漂移的口子。</para>
    /// </summary>
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
