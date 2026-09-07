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

    /// <summary>是否有需要人工核对的字段（M6 消费；M3 打印前闸门读它）。</summary>
    public bool HasUnconfirmed => Items.OfType<TextItem>().Any(t => t.Flagged);
}

/// <summary>排版上下文（跨记录的公共信息）。</summary>
/// <param name="RowIndex">当前记录序号（1 起）。</param>
/// <param name="RecordCount">本次任务记录总数。</param>
/// <param name="SourceFile">数据源文件名，可为 null。</param>
/// <param name="IncludeReference">
/// 是否把 <see cref="TemplateElement.ReferenceOnly"/> 的元素也算进版面。
/// <para><strong>默认 false</strong>：打印/PDF/图片/SVG 导出一律不含参考图（那是给人对齐用的，不能上纸）。
/// 只有单标签预览、整版预览与模板编辑器画布会传 true。</para>
/// </param>
public sealed record LayoutContext(int RowIndex, int RecordCount, string? SourceFile = null, bool IncludeReference = false);

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

                case ElementKind.Text:
                default:
                    var text = ResolveText(element.Text, template, record, context, unresolved, out var flagReason);
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

        var result = TemplateTokenizer.Replace(source, token =>
        {
            var value = ResolveToken(token, template, record, context, unresolved, out var found);
            if (found && !string.IsNullOrWhiteSpace(value))
            {
                anyTokenEmittedValue = true;
            }
            return value;
        });

        // 含变量但所有变量都空 → 整条隐藏（不印 "G.W.:  KG" 这种残句）
        if (textHadTokens && !anyTokenEmittedValue) return string.Empty;

        flagReason = FindReviewFlag(record);
        return NormalizeSpaces(result);
    }

    private static string NormalizeSpaces(string text)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
        return collapsed.Trim();
    }

    private static string? FindReviewFlag(MarkRecord record)
    {
        foreach (var kv in record.PendingReview())
        {
            var def = MarkFieldCatalog.TryGet(kv.Key, out var d) ? d.ChineseName : kv.Key.ToString();
            return $"{def}：{kv.Value.Warning ?? "需人工核对"}";
        }
        return null;
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

    /// <summary>件号 "x / y"。缺 y 时只印 x，都没有就用行号。</summary>
    public static string FormatCarton(MarkRecord record, LayoutContext context)
    {
        var x = CartonValue(record, MarkFieldKey.CartonNo, context.RowIndex);
        var y = record.GetText(MarkFieldKey.CartonTotal);
        return string.IsNullOrWhiteSpace(y) || y == "1" || y == context.RecordCount.ToString() && context.RecordCount <= 1
            ? x
            : $"{x} / {y.Trim()}";
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
