using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using LabelGou.App.Services;
using LabelGou.Core.Interop.Svg;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>底图里一条已折算成设备单位的几何（毫米 × <c>96/25.4</c>）。</summary>
public sealed record PlanPath(Geometry Geometry, Brush? Fill, Pen? Stroke);

/// <summary>底图里一段文字（留在底图、没被提升成可编辑元素的那些）。</summary>
public sealed record PlanText(string Content, double LeftMm, double BaselineYmm, double SizePt, Typeface Face, Brush Fill);

/// <summary>底图里一张内嵌位图（<c>image</c> + data URI）。</summary>
public sealed record PlanImage(byte[] Bytes, string MimeType, double XMm, double YMm, double WidthMm, double HeightMm)
{
    private BitmapSource? _decoded;
    private readonly object _gate = new();

    /// <summary>惰性解码并缓存：一张底稿里的图不该每帧重解一次。位图一旦冻结即可跨线程使用。</summary>
    public BitmapSource? Decoded
    {
        get
        {
            if (_decoded is not null) return _decoded;
            lock (_gate)
            {
                if (_decoded is not null) return _decoded;
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = new MemoryStream(Bytes);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _decoded = bitmap;
                }
                catch (Exception ex)
                {
                    AppLog.Warning($"底稿内嵌位图解不开（{ex.Message}），该处留白。");
                    _decoded = null;
                }
                return _decoded;
            }
        }
    }
}

/// <summary>
/// 一份 SVG 底稿的<strong>可重放渲染计划</strong>：几何、画笔、字体都算好了，画的时候只叠一层变换。
/// <para>
/// 之所以要"计划 + 每次现画"而不是缓存一个成品 <see cref="DrawingGroup"/>：成品组里含字形
/// （<c>GlyphRun</c> 有线程亲和），一冻结就绑死在创建它的那个线程上，而导出跑在 STA 工作线程、
/// 预览跑在 UI 线程。缓存纯数据、每次现组装，才不会出现"后台线程画不了"这类只在真打印时才炸的坑。
/// </para>
/// </summary>
public sealed class SvgRenderPlan
{
    public required double WidthMm { get; init; }

    public required double HeightMm { get; init; }

    public List<PlanPath> Paths { get; } = new();

    public List<PlanText> Texts { get; } = new();

    public List<PlanImage> Images { get; } = new();

    /// <summary>解析时的降级/告警（渐变、clipPath、不支持的命令等），导入窗口与状态栏要说清。</summary>
    public List<string> Issues { get; } = new();

    /// <summary>空计划（文件读不了 / 里没有可画的东西），渲染端按"缺资源"处理。</summary>
    public bool IsEmpty => Paths.Count == 0 && Texts.Count == 0 && Images.Count == 0;
}

/// <summary>
/// <see cref="SvgDocument"/> → WPF 可画对象（M5 定案 D4 三件套的第三件，App 层）。
/// <para>
/// Core 不认识 UI，所以"底图怎么上屏"只能落在这一层。三条约束：
/// ① 毫米→设备单位只有 <see cref="_unitPerMm"/> 一处；
/// ② 曲线不展平（贝塞尔原样落到 <see cref="PathGeometry"/>，见 <see cref="SvgGeometryConverter"/>）；
/// ③ 同一份底稿只解析一次，按"最后写入时间 + 长度"失效，编辑器里改存即重读。
/// </para>
/// </summary>
public static class SvgDrawableBuilder
{
    /// <summary>毫米 → 设备单位（96DPI），与 <see cref="Mm.ToDiu"/> 同一个数。</summary>
    private static readonly double UnitPerMm = 96.0 / 25.4;

    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>最多缓存多少份底稿（一份几百对象的模板也就几百 KB，24 份够用又不至于无限涨）。</summary>
    public const int MaxCachedDocuments = 24;

    /// <summary>
    /// 相当于 265 米的单行宽度，用在“不许折行”的底稿文字上。
    /// <para>WPF 既不收正无穷，也不收超过 3579139.4（= int.MaxValue/600）的段落宽，所以给一个有限但天大的数。</para>
    /// </summary>
    private const double UnboundedTextWidthDiu = 1_000_000;

    /// <summary>
    /// 一份底稿的缓存项：解好的文档（毫米，给矢量出口）+ 已折算成设备单位的计划（给上屏）。
    /// 两者共用一份解析结果，才不会“预览看到的”与“导出去的”底图不是同一份东西。
    /// </summary>
    private sealed record Entry(DateTime Written, long Length, SvgDocument Document, SvgRenderPlan Plan);

    /// <summary>读一份 SVG 底稿并出渲染计划；读不了返回 null（原因已写进日志）。</summary>
    public static SvgRenderPlan? Load(string path) => LoadEntry(path)?.Plan;

    /// <summary>同一份底稿的毫米坐标文档（SVG 导出内联底图时用）。</summary>
    public static SvgDocument? LoadDocument(string path) => LoadEntry(path)?.Document;

    private static Entry? LoadEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        DateTime written;
        long length;
        try
        {
            var info = new FileInfo(path);
            written = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"底稿文件信息取不到：{path}（{ex.Message}）");
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var hit) && hit.Written == written && hit.Length == length)
                return hit;
        }

        SvgParseResult parsed;
        try
        {
            parsed = SvgParser.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or NotSupportedException)
        {
            AppLog.Warning($"底稿解不开：{path}（{ex.Message}）");
            return null;
        }

        var plan = Build(parsed.Document, parsed.Issues);
        if (parsed.HasError)
        {
            AppLog.Warning($"底稿有读不了的部分：{path} → {string.Join(" / ", parsed.ErrorMessages)}");
        }
        var entry = new Entry(written, length, parsed.Document, plan);

        lock (Gate)
        {
            if (Cache.Count >= MaxCachedDocuments && !Cache.ContainsKey(path))
            {
                // 打印店机器上不会同时开几十个模板，先清一遍最省事的：全部丢掉
                Cache.Clear();
            }
            Cache[path] = entry;
        }
        return entry;
    }

    /// <summary>把解析结果折算成设备单位的计划。</summary>
    public static SvgRenderPlan Build(SvgDocument document, IReadOnlyList<TemplateIssue>? issues = null)
    {
        var plan = new SvgRenderPlan
        {
            WidthMm = document.WidthMm > 0 ? document.WidthMm : Math.Max(1, document.ContentBounds.WidthMm),
            HeightMm = document.HeightMm > 0 ? document.HeightMm : Math.Max(1, document.ContentBounds.HeightMm),
        };

        if (issues is not null)
        {
            foreach (var issue in issues.Where(i => i.Severity != IssueLevel.Info))
            {
                plan.Issues.Add(issue.Message);
            }
        }

        foreach (var path in document.Paths)
        {
            var geometry = SvgGeometryConverter.BuildGeometry(path.Commands, path.EvenOdd, UnitPerMm);
            plan.Paths.Add(new PlanPath(geometry, BrushOf(path.Fill), PenOf(path.Stroke)));
        }

        foreach (var text in document.Texts)
        {
            if (string.IsNullOrWhiteSpace(text.Content)) continue;
            plan.Texts.Add(PlanTextOf(text));
        }

        foreach (var image in document.Images)
        {
            if (image.Bytes.Length == 0) continue;
            plan.Images.Add(new PlanImage(image.Bytes, image.MimeType, image.Bounds.XMm, image.Bounds.YMm,
                image.Bounds.WidthMm, image.Bounds.HeightMm));
        }

        return plan;
    }

    /// <summary>底稿文字 → 计划项（字体回退只在这一处，预览与 SVG 出口拿到的字体必定同一个）。</summary>
    public static PlanText PlanTextOf(SvgText text)
    {
        var family = RenderRules.SafeFontFamily(text.FontFamily);
        var typeface = new Typeface(family, FontStyles.Normal, text.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        return new PlanText(text.Content, text.XMm, text.BaselineYmm, text.SizePt, typeface, BrushOf(text.Fill) ?? RenderRules.Ink);
    }

    /// <summary>已定位的底稿文字：排好的 <see cref="FormattedText"/> 与它的左上角落点（设备单位）。</summary>
    public sealed record DrawableText(FormattedText Formatted, Point OriginDiu);

    /// <summary>
    /// 底稿里一段文字的可画形式。
    /// <para><strong>预览与 SVG 矢量出口共用它</strong>：上屏时直接 <c>DrawText</c>，
    /// 转曲时拿 <see cref="FormattedText.BuildGeometry"/>。两边各自 <c>new FormattedText</c> 的话，
    /// 字重/字体回退/基线取法很容易在某一侧漂掉。</para>
    /// </summary>
    public static DrawableText? BuildText(PlanText text, double pixelsPerDip)
    {
        var emSize = TextFit.RequestedEmSizeDiu(text.SizePt);
        if (emSize <= 0) return null;
        var formatted = new FormattedText(
            text.Content,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            text.Face,
            emSize,
            text.Fill,
            pixelsPerDip)
        {
            // 底稿里的文字已经是一个视觉一行（tspan 在解析阶段就拆成多段了），这里不许再折行。
            // 注意：段落宽不能给正无穷，也不能给天文学数字，WPF 两头都拒（见 UnboundedTextWidthDiu）。
            MaxTextWidth = UnboundedTextWidthDiu,
            Trimming = TextTrimming.None,
        };
        return new DrawableText(formatted, new Point(text.LeftMm * UnitPerMm, text.BaselineYmm * UnitPerMm - formatted.Baseline));
    }

    /// <summary>
    /// 铺进一个元素框（毫米）后的可画对象。<paramref name="opacity"/> 用于参考底图的淡显。
    /// <para>底稿画布与元素框比例不一致时按框拉伸一次（与位图出口同一处理），底图本身不重排。</para>
    /// </summary>
    public static Drawing? BuildDrawing(SvgRenderPlan? plan, double widthMm, double heightMm, double pixelsPerDip, double opacity = 1)
    {
        if (plan is null || plan.IsEmpty || widthMm <= 0 || heightMm <= 0) return null;

        var group = new DrawingGroup
        {
            Transform = new ScaleTransform(
                widthMm / Math.Max(1e-6, plan.WidthMm),
                heightMm / Math.Max(1e-6, plan.HeightMm)),
            Opacity = Math.Clamp(opacity, 0.05, 1),
        };

        using var dc = group.Open();
        foreach (var item in plan.Paths)
        {
            dc.DrawGeometry(item.Fill, item.Stroke, item.Geometry);
        }

        foreach (var image in plan.Images)
        {
            var bitmap = image.Decoded;
            if (bitmap is null) continue;
            dc.DrawImage(bitmap, new Rect(
                image.XMm * UnitPerMm, image.YMm * UnitPerMm,
                Math.Max(0, image.WidthMm) * UnitPerMm, Math.Max(0, image.HeightMm) * UnitPerMm));
        }

        foreach (var text in plan.Texts)
        {
            var drawable = BuildText(text, pixelsPerDip);
            if (drawable is null) continue;
            dc.DrawText(drawable.Formatted, drawable.OriginDiu);
        }

        return group;
    }

    /// <summary>模板换底稿、或校验前需要强制重读时用它。</summary>
    public static void ClearCache()
    {
        lock (Gate) Cache.Clear();
    }

    /// <summary>缓存里现在有几份底稿（单测用来确认"第二次没重读文件"）。</summary>
    public static int CachedCount
    {
        get { lock (Gate) return Cache.Count; }
    }

    private static Brush? BrushOf(SvgPaint? paint)
    {
        if (paint is null) return null;
        var color = ColorOf(paint.Color);
        if (color is null) return null;
        var a = (byte)Math.Round(Math.Clamp(paint.Opacity, 0, 1) * 255);
        var c = color.Value;
        var brush = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    private static Pen? PenOf(SvgPaint? paint)
    {
        if (paint is null) return null;
        var brush = BrushOf(paint);
        if (brush is null) return null;
        var thicknessMm = paint.WidthMm > 0 ? paint.WidthMm : 0.35;
        var pen = new Pen(brush, thicknessMm * UnitPerMm);
        if (paint.DashMm is { Length: > 0 } dashes)
        {
            var collection = new DoubleCollection(dashes.Select(d => Math.Max(0, d) * UnitPerMm).ToArray());
            collection.Freeze();
            pen.DashStyle = new DashStyle(collection, 0);
            pen.DashCap = PenLineCap.Flat;   // 虚线两端不延长，否则短虚线会连成实线
        }
        pen.Freeze();
        return pen;
    }

    /// <summary><c>#rrggbb</c> / <c>#rgb</c> / <c>rgb(r,g,b)</c> → 颜色；解析不了给 null（解析器已保证多数情况是 #rrggbb）。</summary>
    private static Color? ColorOf(string text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return null;
        if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            if (s[0] == '#')
            {
                if (s.Length == 4) s = "#" + s[1] + s[1] + s[2] + s[2] + s[3] + s[3];
                return (Color)ColorConverter.ConvertFromString(s);
            }
            if (s.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
            {
                var parts = s[4..].TrimEnd(')').Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3)
                {
                    return Color.FromRgb(ToByte(parts[0]), ToByte(parts[1]), ToByte(parts[2]));
                }
            }
            return (Color)ColorConverter.ConvertFromString(s);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte ToByte(string part)
    {
        var p = part.TrimEnd('%');
        if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        return (byte)Math.Round(Math.Clamp(v, 0, 255));
    }
}
