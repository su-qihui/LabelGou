using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace LabelGou.Core.Interop.Svg;

/// <summary>
/// 解析期间沿元素树继承的一组样式（只留唛头用得上的几项）。
/// <para>
/// 字号与线宽统一存成<strong>用户单位</strong>：SVG 里 <c>font-size:12</c> 与 <c>stroke-width:0.5</c>
/// 都是用户单位，几何坐标也是用户单位，同一口径才不用在继承链上反复换算；出树时再一次性折成毫米/磅。
/// </para>
/// </summary>
public sealed record SvgStyle
{
    public double FontSizeUser { get; init; } = 12;
    public string FontFamily { get; init; } = "sans-serif";
    public bool Bold { get; init; }
    public double StrokeWidthUser { get; init; } = 1;
    public SvgTextAnchor Anchor { get; init; } = SvgTextAnchor.Start;

    /// <summary>颜色原样存字符串（<c>#rrggbb</c> / <c>none</c> / <c>url(#grad)</c>），出树时才归一。</summary>
    public string Fill { get; init; } = "#000000";

    public string Stroke { get; init; } = "none";
    public double FillOpacity { get; init; } = 1;
    public double StrokeOpacity { get; init; } = 1;
    public string? Dash { get; init; }
    public bool EvenOdd { get; init; }

    /// <summary><c>display:none</c>（整棵子树不画）。</summary>
    public bool Displayed { get; init; } = true;

    /// <summary><c>visibility:hidden</c> 或 <c>opacity:0</c>（画不了但不算出错）。</summary>
    public bool Visible { get; init; } = true;

    public static readonly SvgStyle Default = new();

    /// <summary>
    /// 叠加当前元素上的样式，优先级 <c>style="…"</c> 内联 &gt; CSS 规则 &gt; 表现属性 &gt; 父级继承
    /// （SVG 规范里表现属性优先级低于 CSS，内联最高——按这个来，才不会导入后字号跟底稿对不上）。
    /// <para>
    /// <paramref name="userUnitMm"/> 是<strong>本文档</strong>“一个用户单位 = 多少毫米”，由画布与 viewBox 的比例定。
    /// 带单位的字号/线宽（<c>12pt</c>、<c>0.5mm</c>）必须先按它反化成用户单位，
    /// 否则出树时再乘一次矩阵系数就会把绝对长度误当 px，算出四倍大的字。
    /// </para>
    /// </summary>
    public SvgStyle Overlay(XElement element, SvgStyleSheet sheet, double userUnitMm = SvgLength.MmPerPixel)
    {
        var next = this;
        foreach (var (key, value) in PresentationAttributes(element)) next = next.With(key, value, userUnitMm);
        foreach (var (key, value) in sheet.For(element)) next = next.With(key, value, userUnitMm);
        foreach (var (key, value) in SvgStyle.ParseDeclarations((string?)element.Attribute("style"))) next = next.With(key, value, userUnitMm);
        return next;
    }

    private SvgStyle With(string key, string value, double userUnitMm)
    {
        switch (key)
        {
            case "font-size":
                // SVG 里 "12px" 与 "12" 同义：一律按用户单位收，出树时再乘系数
                if (TryUserLength(value, userUnitMm, out var size) && size > 0) return this with { FontSizeUser = size };
                if (value.Contains('%') && SvgLength.Parse(value) is { } pct)
                    return this with { FontSizeUser = FontSizeUser * pct.Value / 100.0 };
                return this;
            case "font-family":
                return this with { FontFamily = CleanFontList(value) };
            case "font-weight":
                return this with { Bold = value is "bold" or "bolder" || (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w >= 600) };
            case "stroke-width":
                return TryUserLength(value, userUnitMm, out var sw) && sw > 0 ? this with { StrokeWidthUser = sw } : this;
            case "text-anchor":
                return value switch
                {
                    "middle" => this with { Anchor = SvgTextAnchor.Middle },
                    "end" => this with { Anchor = SvgTextAnchor.End },
                    _ => this,
                };
            case "fill":
                return this with { Fill = value.Trim() };
            case "stroke":
                return this with { Stroke = value.Trim() };
            case "fill-opacity":
            case "opacity":
                return this with { FillOpacity = Clamp01(value, FillOpacity) };
            case "stroke-opacity":
                return this with { StrokeOpacity = Clamp01(value, StrokeOpacity) };
            case "stroke-dasharray":
                return this with { Dash = value == "none" ? null : value };
            case "fill-rule":
                return this with { EvenOdd = value == "evenodd" };
            case "display":
                return value is "none" or "hidden" ? this with { Displayed = false } : this;
            case "visibility":
                return value is "hidden" or "collapse" ? this with { Visible = false } : this;
            default:
                return this;
        }
    }

    private static double Clamp01(string value, double fallback)
    {
        if (SvgLength.Parse(value) is not { } len) return fallback;
        if (len.Unit == SvgLengthUnit.Percent) return Math.Clamp(len.Value / 100.0, 0, 1);
        return Math.Clamp(len.Value, 0, 1);
    }

    private static bool TryUserLength(string value, double userUnitMm, out double userUnits)
    {
        userUnits = 0;
        if (SvgLength.Parse(value) is not { } len) return false;
        if (len.Unit is SvgLengthUnit.Percent) return false;
        var safeUnit = userUnitMm > 1e-9 ? userUnitMm : SvgLength.MmPerPixel;
        // 无单位/px 本身就是用户单位；mm/in/pt/pc 是绝对长度，要先反化成“本文档的多少个用户单位”，
        // 否则出树时再乘一次矩阵系数会把绝对长度误当 px，算出四倍大的字
        userUnits = len.Unit == SvgLengthUnit.User || len.Unit == SvgLengthUnit.Px
            ? len.Value
            : len.ToMillimetres(safeUnit) / safeUnit;
        return !double.IsNaN(userUnits);
    }

    /// <summary>"Arial, 黑体" → 取第一个族名并去引号（WPF 与 CDR 都只认单族，多族兜底在渲染端做）。</summary>
    private static string CleanFontList(string value)
    {
        var first = value.Split(',')[0].Trim().Trim('\'', '"');
        return first.Length == 0 ? "sans-serif" : first;
    }

    private static IEnumerable<(string Key, string Value)> PresentationAttributes(XElement element)
    {
        foreach (var name in new[]
                 {
                     "font-size", "font-family", "font-weight", "stroke-width", "text-anchor",
                     "fill", "stroke", "fill-opacity", "stroke-opacity", "opacity",
                     "stroke-dasharray", "fill-rule", "display", "visibility",
                 })
        {
            var v = (string?)element.Attribute(name);
            if (!string.IsNullOrWhiteSpace(v)) yield return (name, v);
        }
    }

    /// <summary>解析 <c>style="a:b; c:d"</c>；同名键后写的覆盖先写的。</summary>
    public static Dictionary<string, string> ParseDeclarations(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;
        foreach (var chunk in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = chunk.IndexOf(':');
            if (colon <= 0) continue;
            var key = chunk[..colon].Trim();
            var value = chunk[(colon + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0) map[key] = value;
        }
        return map;
    }
}

/// <summary>
/// 极简 CSS 规则表：CDR/Illustrator 导出的 SVG 常把字号与字体写进 <c>&lt;style&gt;</c> 的类规则里，
/// 不认的话整份底稿的文字就会全部退回 12px——那是最难发现的一种"看着不对但说不出哪错"。
/// <para>只支持 <c>.class</c> / <c>#id</c> / <c>tag</c> 三种选择器（含逗号分组），不支持后代与伪类。</para>
/// </summary>
public sealed class SvgStyleSheet
{
    private readonly Dictionary<string, Dictionary<string, string>> _byClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _byTag = new(StringComparer.OrdinalIgnoreCase);

    public int RuleCount { get; private set; }

    /// <summary>供 <see cref="SvgStyle.Overlay"/> 用的空表（无 CSS 时）。</summary>
    public static readonly SvgStyleSheet Empty = new();

    /// <summary>扫一遍整棵树，收所有 <c>&lt;style&gt;</c> 里的规则。</summary>
    public static SvgStyleSheet Collect(XElement root)
    {
        var sheet = new SvgStyleSheet();
        foreach (var node in root.DescendantsAndSelf())
        {
            if (node.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                sheet.Ingest(node.Value);
        }
        return sheet;
    }

    /// <summary>从 <c>&lt;style&gt;</c> 文本里收规则；注释 <c>/* … */</c> 先剥掉。</summary>
    public void Ingest(string css)
    {
        if (string.IsNullOrWhiteSpace(css)) return;
        var stripped = StripComments(css);

        var cursor = 0;
        while (cursor < stripped.Length)
        {
            var open = stripped.IndexOf('{', cursor);
            if (open < 0) break;
            var close = stripped.IndexOf('}', open + 1);
            if (close < 0) break;

            var selector = stripped[cursor..open].Trim();
            var body = stripped[(open + 1)..close];
            cursor = close + 1;

            if (selector.Length > 0 && selector[0] == '@') continue; // @media/@font-face 不支持（会连带丢样式，交给上层告警）
            var declarations = SvgStyle.ParseDeclarations(body);
            if (declarations.Count == 0) continue;
            if (selector.Length == 0) continue;

            foreach (var part in selector.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var key = part.Trim();
                if (key.Length == 0 || key == "*") continue;
                // 只取选择器最后一段（".a .b" 退化成 ".b"）：近似但比完全不认强
                var space = key.LastIndexOfAny([' ', '>', '+', '~']);
                if (space >= 0) key = key[(space + 1)..].Trim();
                if (key.Length == 0) continue;

                var name = key[0] is '.' or '#' ? key[1..] : key;
                if (name.Length == 0) continue;

                var target = key[0] switch
                {
                    '.' => _byClass,
                    '#' => _byId,
                    _ => _byTag,
                };
                if (target.TryGetValue(name, out var existing)) target[name] = Merge(existing, declarations);
                else target[name] = new Dictionary<string, string>(declarations, StringComparer.OrdinalIgnoreCase);
                RuleCount++;
            }
        }
    }

    private static Dictionary<string, string> Merge(Dictionary<string, string> lower, Dictionary<string, string> higher)
    {
        var merged = new Dictionary<string, string>(lower, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in higher) merged[k] = v;
        return merged;
    }

    /// <summary>按 tag &lt; class &lt; id 的顺序合并出该元素适用的声明（后写的覆盖先写的）。</summary>
    public IReadOnlyDictionary<string, string> For(XElement element)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Absorb(IReadOnlyDictionary<string, string>? decls)
        {
            if (decls is null) return;
            foreach (var (k, v) in decls) map[k] = v;
        }

        Absorb(_byTag.TryGetValue(element.Name.LocalName, out var byTag) ? byTag : null);

        var classes = ((string?)element.Attribute("class"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (classes is not null)
        {
            foreach (var c in classes)
                if (_byClass.TryGetValue(c, out var byClass)) Absorb(byClass);
        }

        var id = (string?)element.Attribute("id");
        if (!string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out var byId)) Absorb(byId);

        return map;
    }

    /// <summary>当前表里某元素是否声明了这些键（用于"CSS 里给了字号但没生效"的诊断）。</summary>
    public bool DeclaresFontSizeFor(XElement element) =>
        For(element).Any(kv => kv.Key.Equals("font-size", StringComparison.OrdinalIgnoreCase));

    private static string StripComments(string css)
    {
        var sb = new StringBuilder(css.Length);
        var i = 0;
        while (i < css.Length)
        {
            if (i + 1 < css.Length && css[i] == '/' && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 2;
                continue;
            }
            sb.Append(css[i]);
            i++;
        }
        return sb.ToString();
    }
}

/// <summary>颜色归一：SVG 认颜色名、<c>#rgb</c>、<c>#rrggbb</c>、<c>rgb()</c>，而我们的出口只写 <c>#rrggbb</c>。</summary>
public static class SvgColor
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000", ["white"] = "#ffffff", ["red"] = "#ff0000", ["green"] = "#008000",
        ["blue"] = "#0000ff", ["yellow"] = "#ffff00", ["gray"] = "#808080", ["grey"] = "#808080",
        ["cyan"] = "#00ffff", ["magenta"] = "#ff00ff", ["orange"] = "#ffa500", ["purple"] = "#800080",
        ["brown"] = "#a52a2a", ["lime"] = "#00ff00", ["navy"] = "#000080", ["silver"] = "#c0c0c0",
        ["maroon"] = "#800000", ["currentcolor"] = "#000000",
    };

    /// <summary>
    /// 归一成 <c>#rrggbb</c>。<paramref name="warning"/> 非空表示发生了降级（渐变、CMYK、不认识的写法）。
    /// </summary>
    /// <returns><c>none</c> 或空串返回 null，表示"这一笔不画"。</returns>
    public static string? Normalize(string? raw, out string? warning)
    {
        warning = null;
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

        if (s.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            warning = "渐变/图样填充（url(...)）没有按矢量保留，已退回黑色实心。";
            return "#000000";
        }
        if (s.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
        {
            var inner = s[4..].TrimEnd(')');
            var parts = inner.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3
                && int.TryParse(parts[0], out var r) && int.TryParse(parts[1], out var g) && int.TryParse(parts[2], out var b))
                return $"#{Clamp(r):X2}{Clamp(g):X2}{Clamp(b):X2}";
            warning = "rgb() 颜色写法没认全，已退回黑色。";
            return "#000000";
        }
        if (s.StartsWith("cmyk(", StringComparison.OrdinalIgnoreCase))
        {
            warning = "CMYK 颜色已按 sRGB 近似为黑色（唛头是单色活，不影响出片）。";
            return "#000000";
        }
        if (s[0] == '#')
        {
            var hex = s[1..];
            if (hex.Length == 3) return $"#{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}".ToLowerInvariant();
            if (hex.Length >= 6 && IsHex(hex)) return ("#" + hex[..6]).ToLowerInvariant();
            warning = $"颜色「{s}」不是可认的十六进制写法，已退回黑色。";
            return "#000000";
        }
        if (Named.TryGetValue(s, out var named)) return named;

        warning = $"颜色「{s}」不认（可能是自定义色板名），已退回黑色。";
        return "#000000";
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 255);
}
