using LabelGou.Core.Barcodes;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Layout;

public abstract record LayoutItem;

/// <summary>
/// 已解析好的矩形框（毫米）。<paramref name="Ink"/> 是笔色（null = 黑）；模板外框那一条故意不填（它不属于任何元素）。
/// <para>第 50 棒另带外观：<paramref name="Fill"/>（null = 不填充）、<paramref name="Stroked"/>；
/// 圆角是<strong>逐角四个半径</strong>（补正四，照 CorelDRAW 的圆角泊坞窗）。
/// 和 <c>Arc</c> 一样：<strong>这些判断全在 Core 做完</strong>（<c>TemplateElement.CornerRadii()</c> 是唯一读出口），
/// 五个出口只照着画，不许在渲染端各写一套"什么时候该有填充、哪个角圆多少"。</para>
/// </summary>
public sealed record RectItem(double X, double Y, double Width, double Height, double ThicknessMm,
    Colors.LabelColor? Ink = null,
    Colors.LabelColor? Fill = null,
    bool Stroked = true,
    double RadiusTopLeftMm = 0,
    double RadiusTopRightMm = 0,
    double RadiusBottomRightMm = 0,
    double RadiusBottomLeftMm = 0) : LayoutItem;

/// <summary>
/// 已解析好的椭圆（毫米，第 51 棒）：外接盒 + 那套与矩形共用的外观（<paramref name="Fill"/> null = 不填充、
/// <paramref name="Stroked"/>、<paramref name="Ink"/>）。渲染端不许自己判"圆不圆得下"。
/// </summary>
public sealed record EllipseItem(double X, double Y, double Width, double Height, double ThicknessMm,
    Colors.LabelColor? Ink = null,
    Colors.LabelColor? Fill = null,
    bool Stroked = true) : LayoutItem;

/// <summary>
/// 已解析好的正多边形（毫米，第 51 棒）。<paramref name="Points"/> 是 Core <c>ShapeGeometry</c> 数出来的
/// 顶点表（绝对毫米）——<strong>出口只照着连点</strong>，谁自己再推一遍角度就是第二套口径。
/// </summary>
public sealed record PolygonItem(double X, double Y, double Width, double Height, double ThicknessMm,
    System.Collections.Generic.IReadOnlyList<(double X, double Y)> Points,
    Colors.LabelColor? Ink = null,
    Colors.LabelColor? Fill = null,
    bool Stroked = true) : LayoutItem;

/// <summary>
/// 已解析好的线段（毫米）。<paramref name="Ink"/> 同 <see cref="RectItem"/>：null = 黑。
/// <para><paramref name="Arc"/> 非 null 时是曲线的段序列（第 49 棒），渲染端必须画这份而不是连两端：
/// 预览、位图、打印、PDF、SVG 五个出口共用 Core 算好的这一份，谁自己再解释一遍"曲线怎么连"
/// 就会出现"屏上是弯的、PDF 里是直的"那一类错（§五-62 同族）。null = 直线，与从前逐字同形。</para>
/// </summary>
public sealed record LineItem(double X1, double Y1, double X2, double Y2, double ThicknessMm,
    Colors.LabelColor? Ink = null,
    IReadOnlyList<Templates.CurveSegment>? Arc = null) : LayoutItem;

/// <summary>
/// 已解析好的文本项。
/// </summary>
/// <param name="Content">变量替换后的最终文本（非空）。</param>
/// <param name="Flagged">含需要人工核对的字段值（M6 的 AI 结果未确认）——渲染时标红。</param>
/// <param name="FlagReason">标红原因，界面状态栏/悬浮提示用。</param>
/// <param name="RotationDeg">绕元素中心顺时针旋转的角度（第 43 棒，默认 0）。渲染端套变换，
/// <see cref="LabelGou.App.Rendering.TextFit"/> 的缩字/折行决定<strong>不受它影响</strong>（先排版后变换）。</param>
/// <param name="StretchX">字面横向拉伸倍率（1 = 原样，第 43 棒）。同上：变换在 TextFit 之后，五出口共用。</param>
/// <param name="StretchY">字面纵向拉伸倍率（1 = 原样）。</param>
public sealed record TextItem(
    string Content,
    double X,
    double Y,
    double Width,
    double Height,
    string FontFamily,
    double FontSizePt,
    bool Bold,
    HorizontalAlign Align,
    bool ShrinkToFit,
    int MaxLines,
    bool Flagged = false,
    string? FlagReason = null,
    double RotationDeg = 0,
    double TextScaleX = 1,
    double TextScaleY = 1,
    double WrapWidthMm = 0,
    Colors.LabelColor? Ink = null) : LayoutItem
{
    /// <summary>这一项带不带几何变换（旋转或任一方向拉伸）。渲染端用它决定要不要 Push/Pop 变换组。</summary>
    public bool HasGeometry => RotationDeg != 0 || TextScaleX != 1 || TextScaleY != 1;

    /// <summary>永不折行（第 46 棒）：内容多长排多长，对齐偏移由 <c>TextFit</c> 自己算，不靠 <c>MaxTextWidth</c>。</summary>
    public bool NoWrap => WrapWidthMm <= 0;

    /// <summary>
    /// 生成这一项的模板元素（不参与 JSON 序列化，只给运行时用）。
    /// <para>第 44 棒：编辑器要"按元素找回它的版面项"来量文字墨迹（选中框贴墨迹画，不再框整条行带）。
    /// 版面里文本可能被拆成多条（条码可读数字逐字一格），所以编辑器只认 <c>ReferenceEquals</c> 且
    /// 内容非数字带的那条——用引用而不是"第 i 个元素对第 j 个项"的下标，隐藏元素不会把下标错开。</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Templates.TemplateElement? Source { get; init; }
}

/// <summary>图片项（M1 仅在 Logo 有值且文件存在时产出）。</summary>
/// <param name="ReferenceOnly">true 表示只作对齐参考，不进打印与导出（M5：从 .cdr 抠出来的缩略图）。</param>
/// <param name="RotationDeg">绕元素中心顺时针旋转的角度（第 43 棒，默认 0）。图片的"拉伸"就是宽高本身
/// （画上去填满框），所以只有旋转走这个字段。</param>
public sealed record ImageItem(string AbsolutePath, double X, double Y, double Width, double Height, bool ReferenceOnly = false, double RotationDeg = 0) : LayoutItem;

/// <summary>
/// 矢量底图项（M5 的 C 类模板）：一份从 CDR 导出的 SVG 底稿，坐标已是毫米。
/// <para>渲染端负责把 <paramref name="AbsolutePath"/> 指向的 SVG 画成 WPF Drawing（App 层 <c>SvgDrawableBuilder</c>），
/// 矢量出口则直接写回 SVG；Core 只负责落位，不认 SVG 内容。</para>
/// </summary>
public sealed record VectorItem(string AbsolutePath, double X, double Y, double Width, double Height, bool ReferenceOnly = false, double RotationDeg = 0) : LayoutItem;

/// <summary>
/// 条码项（第 17 棒）。<strong>矩形已经在 Core 算成毫米</strong>，渲染端只负责画，不重算条宽——
/// 这是「预览能扫、印出来也能扫」的唯一保证。
/// </summary>
/// <param name="Bars">黑条（绝对毫米坐标）。<see cref="Error"/> 非空时为空表。每根自带「是不是保护条」。</param>
/// <param name="Data">真正编进去的那一串（可能与表里的原值差一个自动补的校验位）。</param>
/// <param name="SymbologyName">制式短名，给提示与 SVG 注记用。</param>
/// <param name="Error">编不出来：数据里有这个制式装不下的字符、或校验位不对。
/// 非空时这一项被标成待核，打印闸门拦得住它——<strong>宁可不出纸也不出一张错码</strong>。</param>
/// <param name="GuardBarsHeight">保护条的高（UPC/EAN 专有，比 <see cref="BarsHeight"/> 高一截）；0 = 没有保护条。</param>
/// <param name="Hri">可读数字的逐字格子（UPC/EAN 专有）。空 = 这一族整串居中即可。</param>
public sealed record BarcodeItem(
    IReadOnlyList<BarStrip> Bars,
    double X,
    double Y,
    double Width,
    double Height,
    double BarsY,
    double BarsHeight,
    double ModuleMm,
    string Data,
    string SymbologyName,
    bool ShowText,
    string FontFamily,
    double FontSizePt,
    bool Flagged = false,
    string? FlagReason = null,
    string? Warning = null,
    string? Error = null,
    double GuardBarsHeight = 0,
    IReadOnlyList<HriGlyph>? Hri = null,
    Colors.LabelColor? Ink = null) : LayoutItem
{
    /// <summary>保护条实际该画多高：没给（0）就与数据条等高，渲染端不必自己判空。</summary>
    public double EffectiveGuardBarsHeight => GuardBarsHeight > 0 ? GuardBarsHeight : BarsHeight;

    /// <summary>每一根条实际该画多高——保护条比数据条高，这是「和 BARCODE WIZARD 一样」的一半。</summary>
    public double HeightOf(BarStrip bar) => bar.IsGuard ? EffectiveGuardBarsHeight : BarsHeight;
}

/// <summary>
/// 一条记录套一个模板得到的<strong>最终版面</strong>（纯数据、毫米单位、与渲染技术无关）。
/// WPF 预览、PDF 导出、整版图片导出共用它，是"所见即所得"能成立的前提。
/// </summary>
public sealed class LabelLayout
{
    public LabelTemplate Template { get; init; } = new();

    public double WidthMm => Template.WidthMm;

    public double HeightMm => Template.HeightMm;

    public IReadOnlyList<LayoutItem> Items { get; init; } = Array.Empty<LayoutItem>();

    /// <summary>模板引用了但数据里没有的占位符（界面提示"这些字段是空的"）。</summary>
    public IReadOnlyList<string> UnresolvedTokens { get; init; } = Array.Empty<string>();

    /// <summary>被整条隐藏的要素数（变量全空 → 不印，避免 "G.W.:  KG" 这种残句）。</summary>
    public int HiddenElementCount { get; init; }

    /// <summary>对应的记录行号，0 表示示意预览。</summary>
    public int RecordRowIndex { get; init; }

    /// <summary>是否有需要人工核对的字段（M6 消费；M3 打印前闸门读它）。
    /// <para>条码也算：编不出来的码与待核的字段同级别——一张错码上纸比少印一张更贵。</para></summary>
    public bool HasUnconfirmed => Items.OfType<TextItem>().Any(t => t.Flagged)
                                 || Items.OfType<BarcodeItem>().Any(b => b.Flagged);
}

/// <summary>
/// 唛头文字的大小写口径（2026-09-08 用户要「按表格里的 / 全部大写 / 全部小写」三档开关）。
/// <para>作用在 <see cref="LayoutEngine.Build"/> 里<strong>整行合成之后</strong>那一个点：
/// 生产侧只有单标签预览与 <c>PageRasterizer</c> 两处取版面，从这一处走就是五出口一致（§七-11），
/// 不会出现「预览大写、PDF 小写」。</para>
/// <para>改的是<strong>合成后的整行文字</strong>（含用户自己写的固定标签，不只变量值）：
/// 厂商样张上「ITEM NO」这类标签通常也是大写，只洗一半反而不一致。</para>
/// </summary>
public enum MarkTextCase
{
    /// <summary>不动（默认）：表里存的是什么就印什么，等于历史行为。</summary>
    AsSource = 0,

    /// <summary>整行转大写（不变文化：CJK、数字与标点原样，只动拉丁字母）。</summary>
    Upper = 1,

    /// <summary>整行转小写。</summary>
    Lower = 2,
}

/// <summary>界面与提示共用同一份叫法，别让 UI 与 Core 各写一遍中文。</summary>
public static class MarkTextCaseExtensions
{
    public static string ChineseName(this MarkTextCase value) => value switch
    {
        MarkTextCase.Upper => "全部大写",
        MarkTextCase.Lower => "全部小写",
        _ => "按表格里的",
    };
}

/// <summary>
/// 排版上下文（跨记录的公共信息）。</summary>
/// <param name="RowIndex">当前记录序号（1 起）。</param>
/// <param name="RecordCount">本次任务记录总数。</param>
/// <param name="SourceFile">数据源文件名，可为 null。</param>
/// <param name="IncludeReference">
/// 是否把 <see cref="TemplateElement.ReferenceOnly"/> 的元素也算进版面。
/// <para><strong>默认 false</strong>：打印/PDF/图片/SVG 导出一律不含参考图（那是给人对齐用的，不能上纸）。
/// 只有单标签预览、整版预览与模板编辑器画布会传 true。</para>
/// </param>
/// <param name="TextCase">唛头文字大小写口径，<strong>默认按表格里的</strong>（不改变任何已有行为）。</param>
/// <param name="Plate">
/// 分色：这一份版面要出哪一张墨版（第 48 棒）。默认 <see cref="Colors.InkPlate.None"/> = 不分色，
/// 与从前逐字同形；只有 <c>PageRasterizer</c> 出 CMYK 位图时才填非 None 值。
/// </param>
public sealed record LayoutContext(
    int RowIndex, int RecordCount, string? SourceFile = null, bool IncludeReference = false,
    MarkTextCase TextCase = MarkTextCase.AsSource,
    Colors.InkPlate Plate = Colors.InkPlate.None);

/// <summary>
/// 把「模板 + 一条记录」解析成 <see cref="LabelLayout"/>。
/// <para>
/// 三条硬规矩（都写进单测）：
/// ① 未解析的占位符绝不带花括号印出去；
/// ② 一个文本要素里的变量全为空 → 整条隐藏（不留 "G.W.:  KG" 残句）；
/// ③ 任一被引用字段带 <see cref="MarkValue.NeedsReview"/> → 该文本标红，打印闸门据此拦截。
/// </para>
/// </summary>
public static class LayoutEngine
{
    public static LabelLayout Build(LabelTemplate template, MarkRecord record, LayoutContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(record);

        var items = new List<LayoutItem>();
        var unresolved = new List<string>();
        var hidden = 0;

        if (template.BorderMm > 0)
        {
            // 外框没填色＝黑＝只该落在黑版上（不分色时 InkOf 原样交回 null，与从前逐字同形）。
            items.Add(new RectItem(0, 0, template.WidthMm, template.HeightMm, template.BorderMm,
                Ink: InkOf(null, context)));
        }

        foreach (var element in template.Elements)
        {
            if (!element.Visible) continue;
            if (element.ReferenceOnly && !context.IncludeReference) continue;

            switch (element.Kind)
            {
                case ElementKind.Line:
                    items.Add(new LineItem(element.X, element.Y, element.X2, element.Y2, element.ThicknessMm,
                        Ink: InkOf(element.InkColor, context),
                        // 分不分色都画同一条弧，只是每一版上的墨量不同（Ink 那份已经按版折好了）。
                        Arc: Templates.CurveGeometry.IsCurved(element) ? Templates.CurveGeometry.Segments(element) : null));
                    break;

                case ElementKind.Rect:
                    var radii = element.CornerRadii();
                    items.Add(new RectItem(element.X, element.Y, element.Width, element.Height, element.ThicknessMm,
                        Ink: InkOf(element.InkColor, context),
                        Fill: FillOf(element.FillColor, context),
                        Stroked: element.ShowsStroke,
                        RadiusTopLeftMm: radii.TopLeft,
                        RadiusTopRightMm: radii.TopRight,
                        RadiusBottomRightMm: radii.BottomRight,
                        RadiusBottomLeftMm: radii.BottomLeft));
                    break;

                case ElementKind.Ellipse:
                    items.Add(new EllipseItem(element.X, element.Y, element.Width, element.Height, element.ThicknessMm,
                        Ink: InkOf(element.InkColor, context),
                        Fill: FillOf(element.FillColor, context),
                        Stroked: element.ShowsStroke));
                    break;

                case ElementKind.Polygon:
                    items.Add(new PolygonItem(element.X, element.Y, element.Width, element.Height, element.ThicknessMm,
                        Templates.ShapeGeometry.PolygonPoints(element),
                        Ink: InkOf(element.InkColor, context),
                        Fill: FillOf(element.FillColor, context),
                        Stroked: element.ShowsStroke));
                    break;

                case ElementKind.Vector:
                    var vectorPath = ResolveAsset(template, element);
                    if (vectorPath is not null) items.Add(new VectorItem(vectorPath, element.X, element.Y, element.Width, element.Height, element.ReferenceOnly, element.RotationDeg));
                    else hidden++;
                    break;

                case ElementKind.Image:
                    var path = ResolveAsset(template, element);
                    if (path is not null) items.Add(new ImageItem(path, element.X, element.Y, element.Width, element.Height, element.ReferenceOnly, element.RotationDeg));
                    else hidden++;
                    break;

                case ElementKind.Barcode:
                {
                    // 数据走与文本完全同一条占位符解析路：这样 {{col:条码列}}、待核标记、大小写口径都不需要第二套代码。
                    var data = ResolveText(element.Text, template, record, context, unresolved, out var barFlag);
                    if (string.IsNullOrWhiteSpace(data))
                    {
                        // 那一列本行没值 → 不画（与文本同口径），缺值提醒会说「表里没有这一列/这一格空的」。
                        hidden++;
                        break;
                    }

                    var encoding = BarcodeEncoder.Encode(data, element.Symbology);
                    // 纵向分两段：上边距（框高的 1/81，照 CDR）+ 条区 + 可读数字带。
                    // 数字带有 2.5 mm 的可读下限——CDR 的「5/81」只在框高等比时成立，
                    // 用户画的框常常又宽又扁，5/81 压出来的 0.86 mm 连字都放不下（2026-09-11 的截图教训）。
                    // 不开可读数字时两段都归零，条吃满整格——与「不印数字就不留空」的老口径一致。
                    var topMargin = element.ShowBarcodeText
                        ? element.Height * BarcodeBars.TopMarginUnits / BarcodeBars.HeightUnits
                        : 0;
                    var textBand = element.ShowBarcodeText
                        ? Math.Max(BarcodeBars.MinTextBandMm,
                            element.Height * BarcodeBars.TextBandUnits / BarcodeBars.HeightUnits)
                        : 0;
                    if (topMargin + textBand > element.Height * 0.6)
                        textBand = Math.Max(0, element.Height * 0.6 - topMargin);   // 带再大也不能把条吃了
                    // 这一格给的是「条区总高」：有保护条的 UPC/EAN 一族由保护条吃满，数据条自己矮一截（见 BarcodeBars）。
                    var barsZoneHeight = Math.Max(1, element.Height - topMargin - textBand);
                    var geometry = BarcodeBars.Build(encoding, element.X, element.Y, element.Width,
                        element.Y + topMargin, barsZoneHeight);
                    // 可读数字的逐字格子：只有 UPC/EAN 一族有。别的制式留空，渲染端按老规矩整串居中。
                    var hri = element.ShowBarcodeText && encoding.Ok
                        ? BarcodeBars.BuildHri(encoding, element.X, element.Width, geometry)
                        : Array.Empty<HriGlyph>();
                    // 字号用元素自己的（默认 8pt），装不下由 TextFit 兜底缩——
                    // 「字号 = 7.215 个模块」那条 CDR 规则在扁框里会炸出 27pt 的巨字，同一天里栽过一次，不再犯。
                    var fontSizePt = element.FontSizePt;
                    items.Add(new BarcodeItem(
                        geometry.Bars, element.X, element.Y, element.Width, element.Height,
                        geometry.BarsY, geometry.BarsHeight, geometry.ModuleMm,
                        encoding.Data, element.Symbology.ShortName(), element.ShowBarcodeText,
                        string.IsNullOrWhiteSpace(element.FontFamily) ? TemplateElement.DefaultFont : element.FontFamily,
                        fontSizePt,
                        Flagged: ShowFlag(context, barFlag is not null || !encoding.Ok),
                        FlagReason: barFlag ?? (encoding.Ok ? null : encoding.Error),
                        Warning: encoding.Ok ? geometry.Warning : null,
                        Error: encoding.Ok ? null : encoding.Error,
                        GuardBarsHeight: geometry.GuardBarsHeight,
                        Hri: hri,
                        Ink: InkOf(element.InkColor, context)));
                    break;
                }

                case ElementKind.Text:
                default:
                    var text = ApplyTextCase(
                        ResolveText(element.Text, template, record, context, unresolved, out var flagReason),
                        context.TextCase);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        // 变量全空 或 本来就是空文本 → 隐藏；纯静态文本为空也算隐藏
                        hidden++;
                        break;
                    }
                    items.Add(new TextItem(
                        text,
                        element.X, element.Y, element.Width, element.Height,
                        string.IsNullOrWhiteSpace(element.FontFamily) ? TemplateElement.DefaultFont : element.FontFamily,
                        element.FontSizePt,
                        element.Bold,
                        element.Align,
                        element.ShrinkToFit,
                        element.MaxLines,
                        Flagged: ShowFlag(context, flagReason is not null),
                        FlagReason: flagReason,
                        RotationDeg: element.RotationDeg,
                        TextScaleX: element.TextScaleX,
                        TextScaleY: element.TextScaleY,
                        WrapWidthMm: element.WrapWidthMm,
                        Ink: InkOf(element.InkColor, context))
                    { Source = element });
                    break;
            }
        }

        return new LabelLayout
        {
            Template = template,
            Items = items,
            UnresolvedTokens = unresolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            HiddenElementCount = hidden,
            RecordRowIndex = context.RowIndex,
        };
    }

    /// <summary>
    /// 这一版上该拿什么色画这块墨。<strong>不分色时原样交回</strong>（含 null=黑），
    /// 分色时折成"这一版上多少墨"的那块灰，见 <see cref="InkPlates.ForPlate"/>。
    /// </summary>
    private static Colors.LabelColor? InkOf(Colors.LabelColor? ink, LayoutContext context)
        => context.Plate == Colors.InkPlate.None ? ink : Colors.InkPlates.ForPlate(ink, context.Plate);

    /// <summary>
    /// 填充色：<strong>null 必须保持 null</strong>（"没填"和"填了黑"是两件事，笔色那边才可以，因为它本来就有默认黑）。
    /// 分色时同笔色一样按版折算成灰。
    /// </summary>
    private static Colors.LabelColor? FillOf(Colors.LabelColor? fill, LayoutContext context)
        => fill is null ? null
            : context.Plate == Colors.InkPlate.None ? fill
            : Colors.InkPlates.ForPlate(fill, context.Plate);

    /// <summary>
    /// 分色版上没有「警示红」这种墨——那是给人眼和打印闸门看的记号，不是配墨的一部分。
    /// <para>这一条不是洁癖：警示红 (255,0,0) 画到灰版上会被当成"这一版此处无墨"，
    /// 那一行字就<strong>从成品里安静地消失</strong>了。所以分色时按元素自己那支墨画，记号不上版。</para>
    /// </summary>
    private static bool ShowFlag(LayoutContext context, bool flagged)
        => flagged && context.Plate == Colors.InkPlate.None;

    /// <summary>
    /// 用一份样例记录渲染模板（还没导数据时的"示意预览"）。
    /// <para><paramref name="includeReference"/> 只有预览与编辑器画布该传 true：参考底图是给人对齐用的，不能上纸。</para>
    /// </summary>
    public static LabelLayout BuildSample(LabelTemplate template, bool includeReference = false,
        Colors.InkPlate plate = Colors.InkPlate.None)
        => Build(template, SampleRecords.StandardSample(),
            new LayoutContext(1, 1, "样例数据.xlsx", includeReference, Plate: plate));

    private static string? ResolveAsset(LabelTemplate template, TemplateElement element)
    {
        if (string.IsNullOrWhiteSpace(element.ImagePath)) return null;
        var candidate = Path.IsPathRooted(element.ImagePath)
            ? element.ImagePath
            : Path.Combine(TemplateStore.Directory, element.ImagePath);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 大小写口径只在这一个点生效。<see cref="MarkTextCase.AsSource"/> 直接原样返回（不产生新字符串），
    /// 所以默认档下连引用相等行为都没变。
    /// </summary>
    private static string? ApplyTextCase(string? text, MarkTextCase mode) => mode switch
    {
        MarkTextCase.Upper => text?.ToUpperInvariant(),
        MarkTextCase.Lower => text?.ToLowerInvariant(),
        _ => text,
    };

    private static string ResolveText(
        string? source,
        LabelTemplate template,
        MarkRecord record,
        LayoutContext context,
        List<string> unresolved,
        out string? flagReason)
    {
        flagReason = null;
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        var anyTokenEmittedValue = false;
        var textHadTokens = TemplateTokenizer.EnumerateTokens(source).Any();
        string? flaggedHere = null;

        var result = TemplateTokenizer.Replace(source, token =>
        {
            var value = ResolveToken(token, template, record, context, unresolved, out var found);
            if (found && !string.IsNullOrWhiteSpace(value))
            {
                anyTokenEmittedValue = true;
            }
            // 标红只跟着「这一格真的吃了哪个字段」走：上一版按整条记录判，
            // 一张里任一字段待核就把全片文字都标红，用户反而看不出要看哪一格。
            flaggedHere ??= ReviewFlagOfToken(token, record);
            return value;
        });

        // 含变量但所有变量都空 → 整条隐藏（不印 "G.W.:  KG" 这种残句）
        if (textHadTokens && !anyTokenEmittedValue) return string.Empty;

        flagReason = flaggedHere;
        return NormalizeSpaces(result);
    }

    private static string NormalizeSpaces(string text)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
        return collapsed.Trim();
    }

    /// <summary>这个 token 吐掉的那个字段是不是还挂着「待人工核对」；不是就返回 null。</summary>
    private static string? ReviewFlagOfToken(string token, MarkRecord record)
    {
        if (TemplateTokenizer.IsBuiltInToken(token))
        {
            return token.ToLowerInvariant() switch
            {
                "noxofy" or "nox" => PendingReason(record, MarkFieldKey.CartonNo),
                "noy" => PendingReason(record, MarkFieldKey.CartonTotal),
                _ => null,
            };
        }

        if (token.StartsWith("col:", StringComparison.OrdinalIgnoreCase))
        {
            var key = token[4..].Trim();
            var custom = record.GetCustom("col:" + key);
            return custom is { NeedsReview: true } ? $"{key}：{custom.Warning ?? "需人工核对"}" : null;
        }

        return MarkFieldCatalog.TryParseKey(token, out var field) ? PendingReason(record, field) : null;
    }

    private static string? PendingReason(MarkRecord record, MarkFieldKey key)
    {
        var value = record.Get(key);
        if (value is not { NeedsReview: true }) return null;
        var def = MarkFieldCatalog.TryGet(key, out var d) ? d.ChineseName : key.ToString();
        return $"{def}：{value.Warning ?? "需人工核对"}";
    }

    private static string? ResolveToken(
        string token,
        LabelTemplate template,
        MarkRecord record,
        LayoutContext context,
        List<string> unresolved,
        out bool found)
    {
        found = false;

        if (TemplateTokenizer.IsBuiltInToken(token))
        {
            found = true;
            return token.ToLowerInvariant() switch
            {
                "noxofy" => FormatCarton(record, context),
                "nox" => CartonValue(record, MarkFieldKey.CartonNo, context.RowIndex),
                "noy" => CartonValue(record, MarkFieldKey.CartonTotal, context.RecordCount),
                "rowindex" => context.RowIndex.ToString(),
                "recordcount" => context.RecordCount.ToString(),
                "templatename" => template.Name,
                "sourcefile" => string.IsNullOrEmpty(context.SourceFile) ? string.Empty : Path.GetFileName(context.SourceFile),
                _ => string.Empty,
            };
        }

        if (token.StartsWith("col:", StringComparison.OrdinalIgnoreCase))
        {
            var key = "col:" + token[4..].Trim();
            var custom = record.GetCustom(key);
            if (custom is null)
            {
                unresolved.Add(token);
                return null;
            }
            found = true;
            return custom.Text;
        }

        if (MarkFieldCatalog.TryParseKey(token, out var field))
        {
            var value = record.Get(field);
            if (value is null)
            {
                // 字段合法但本条无值：不算错误，按空处理
                found = false;
                return null;
            }
            found = true;
            return value.Text;
        }

        unresolved.Add(token);
        return null;
    }

    /// <summary>
    /// 件号 "x / y"。缺 y 时只印 x，都没有就用行号。
    /// <para>不印分数的两种情形：y 说总共就一箱，或分子分母是同一个数（"1 / 1" 印上去只是浪费墨）。
    /// 上一版这里写的是 <c>y == "1" || y == RecordCount &amp;&amp; RecordCount &lt;= 1</c>，被 <c>&amp;&amp;</c> 的优先级
    /// 顶成了一个永远轮不到说话的死条件（整批只有一条时 y 本来也是 "1"，前一项已经覆盖），
    /// 所以「总件数=1 不印 x/y」其实从来没生效过。</para>
    /// </summary>
    public static string FormatCarton(MarkRecord record, LayoutContext context)
    {
        var x = CartonValue(record, MarkFieldKey.CartonNo, context.RowIndex);
        var y = record.GetText(MarkFieldKey.CartonTotal).Trim();
        if (string.IsNullOrWhiteSpace(y) || y == "1" || string.Equals(x, y, StringComparison.Ordinal)) return x;
        return $"{x} / {y}";
    }

    private static string CartonValue(MarkRecord record, MarkFieldKey key, int fallback)
    {
        var text = record.GetText(key);
        if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        return fallback.ToString();
    }
}

/// <summary>
/// 样例记录：用于模板选择界面的"示意预览"和单元测试。
/// 值刻意取得有辨识度（长短混合、中英混合），方便一眼看出溢出与对齐问题。
/// </summary>
public static class SampleRecords
{
    public static MarkRecord StandardSample() => MarkRecord.Builder()
        .SetRow(1, "样例数据")
        .Set(MarkFieldKey.Consignee, "WALMART INC. / 深圳顺达贸易")
        .Set(MarkFieldKey.ClientCode, "WM-2026")
        .Set(MarkFieldKey.ContractNo, "SD-2026-0831")
        .Set(MarkFieldKey.PoNumber, "PO#45821099")
        .Set(MarkFieldKey.ItemNo, "YOGA-PANT-SS")
        .Set(MarkFieldKey.DestinationPort, "LOS ANGELES, USA")
        .Set(MarkFieldKey.DestinationCountry, "USA")
        .Set(MarkFieldKey.CartonNo, "3")
        .Set(MarkFieldKey.CartonTotal, "120")
        .Set(MarkFieldKey.Quantity, "24")
        .Set(MarkFieldKey.GrossWeight, "18.50")
        .Set(MarkFieldKey.NetWeight, "16.20")
        .Set(MarkFieldKey.Measurement, "0.068")
        .Set(MarkFieldKey.BoxSize, "60×40×25")
        .Set(MarkFieldKey.BatchNo, "B2608")
        .Set(MarkFieldKey.ShipDate, "2026/9/12")
        .Set(MarkFieldKey.Origin, "MADE IN CHINA")
        .Set(MarkFieldKey.Remarks, "THIS SIDE UP / 防潮")
        .SetCustom("col:托盘号", "PLT-0007")
        .Build();
}
