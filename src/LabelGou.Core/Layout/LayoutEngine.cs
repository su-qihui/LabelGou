using LabelGou.Core.Barcodes;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Layout;

public abstract record LayoutItem;

/// <summary>已解析好的矩形边框（毫米）。</summary>
public sealed record RectItem(double X, double Y, double Width, double Height, double ThicknessMm) : LayoutItem;

/// <summary>已解析好的线段（毫米）。</summary>
public sealed record LineItem(double X1, double Y1, double X2, double Y2, double ThicknessMm) : LayoutItem;

/// <summary>
/// 已解析好的文本项。
/// </summary>
/// <param name="Content">变量替换后的最终文本（非空）。</param>
/// <param name="Flagged">含需要人工核对的字段值（M6 的 AI 结果未确认）——渲染时标红。</param>
/// <param name="FlagReason">标红原因，界面状态栏/悬浮提示用。</param>
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
    string? FlagReason = null) : LayoutItem;

/// <summary>图片项（M1 仅在 Logo 有值且文件存在时产出）。</summary>
/// <param name="ReferenceOnly">true 表示只作对齐参考，不进打印与导出（M5：从 .cdr 抠出来的缩略图）。</param>
public sealed record ImageItem(string AbsolutePath, double X, double Y, double Width, double Height, bool ReferenceOnly = false) : LayoutItem;

/// <summary>
/// 矢量底图项（M5 的 C 类模板）：一份从 CDR 导出的 SVG 底稿，坐标已是毫米。
/// <para>渲染端负责把 <paramref name="AbsolutePath"/> 指向的 SVG 画成 WPF Drawing（App 层 <c>SvgDrawableBuilder</c>），
/// 矢量出口则直接写回 SVG；Core 只负责落位，不认 SVG 内容。</para>
/// </summary>
public sealed record VectorItem(string AbsolutePath, double X, double Y, double Width, double Height, bool ReferenceOnly = false) : LayoutItem;

/// <summary>
/// 条码项（第 17 棒）。<strong>矩形已经在 Core 算成毫米</strong>，渲染端只负责画，不重算条宽——
/// 这是「预览能扫、印出来也能扫」的唯一保证。
/// </summary>
/// <param name="Bars">黑条（绝对毫米坐标）。<see cref="Error"/> 非空时为空表。</param>
/// <param name="Data">真正编进去的那一串（可能与表里的原值差一个自动补的校验位）。</param>
/// <param name="SymbologyName">制式短名，给提示与 SVG 注记用。</param>
/// <param name="Error">编不出来：数据里有这个制式装不下的字符、或校验位不对。
/// 非空时这一项被标成待核，打印闸门拦得住它——<strong>宁可不出纸也不出一张错码</strong>。</param>
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
    string? Error = null) : LayoutItem;

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
public sealed record LayoutContext(
    int RowIndex, int RecordCount, string? SourceFile = null, bool IncludeReference = false,
    MarkTextCase TextCase = MarkTextCase.AsSource);

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
            items.Add(new RectItem(0, 0, template.WidthMm, template.HeightMm, template.BorderMm));
        }

        foreach (var element in template.Elements)
        {
            if (!element.Visible) continue;
            if (element.ReferenceOnly && !context.IncludeReference) continue;

            switch (element.Kind)
            {
                case ElementKind.Line:
                    items.Add(new LineItem(element.X, element.Y, element.X2, element.Y2, element.ThicknessMm));
                    break;

                case ElementKind.Rect:
                    items.Add(new RectItem(element.X, element.Y, element.Width, element.Height, element.ThicknessMm));
                    break;

                case ElementKind.Vector:
                    var vectorPath = ResolveAsset(template, element);
                    if (vectorPath is not null) items.Add(new VectorItem(vectorPath, element.X, element.Y, element.Width, element.Height, element.ReferenceOnly));
                    else hidden++;
                    break;

                case ElementKind.Image:
                    var path = ResolveAsset(template, element);
                    if (path is not null) items.Add(new ImageItem(path, element.X, element.Y, element.Width, element.Height, element.ReferenceOnly));
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
                    // 可读数字那一条占底部 22%（最少 3 mm、最多一半高）：只在这里算一次。
                    // 上限是第 23 棒补的——高 2mm 的小元素曾给 3mm 文字带,条顶在 Y、文字带压出元素底边。
                    var textBand = element.ShowBarcodeText
                        ? Math.Min(Math.Max(3, element.Height * 0.22), element.Height * 0.5)
                        : 0;
                    var barsHeight = Math.Max(1, element.Height - textBand);
                    var geometry = BarcodeBars.Build(encoding, element.X, element.Y, element.Width, element.Y, barsHeight);
                    items.Add(new BarcodeItem(
                        geometry.Bars, element.X, element.Y, element.Width, element.Height,
                        geometry.BarsY, geometry.BarsHeight, geometry.ModuleMm,
                        encoding.Data, element.Symbology.ShortName(), element.ShowBarcodeText,
                        string.IsNullOrWhiteSpace(element.FontFamily) ? TemplateElement.DefaultFont : element.FontFamily,
                        element.FontSizePt,
                        Flagged: barFlag is not null || !encoding.Ok,
                        FlagReason: barFlag ?? (encoding.Ok ? null : encoding.Error),
                        Warning: encoding.Ok ? geometry.Warning : null,
                        Error: encoding.Ok ? null : encoding.Error));
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
                        Flagged: flagReason is not null,
                        FlagReason: flagReason));
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
    /// 用一份样例记录渲染模板（还没导数据时的"示意预览"）。
    /// <para><paramref name="includeReference"/> 只有预览与编辑器画布该传 true：参考底图是给人对齐用的，不能上纸。</para>
    /// </summary>
    public static LabelLayout BuildSample(LabelTemplate template, bool includeReference = false)
        => Build(template, SampleRecords.StandardSample(),
            new LayoutContext(1, 1, "样例数据.xlsx", includeReference));

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
