using System.Globalization;
using System.Text;

namespace LabelGou.Core.Interop.Svg;

/// <summary>SVG/CSS 长度单位。注意 <see cref="SvgLengthUnit.User"/> 与 <see cref="SvgLengthUnit.Px"/> 都按 1/96 英寸算，与 WPF 的 DIU 同口径。</summary>
public enum SvgLengthUnit
{
    /// <summary>无单位：SVG 的用户单位，默认等价于 px。</summary>
    User = 0,
    Px = 1,
    Mm = 2,
    Cm = 3,
    In = 4,
    Pt = 5,
    Pc = 6,
    /// <summary>百分比。没有稳定参照物，一律按无效处理。</summary>
    Percent = 7,
}

/// <summary>
/// 一个带单位的长度。<strong>全项目只有这一处懂 SVG 的单位体系</strong>，别处一律拿毫米。
/// </summary>
public readonly record struct SvgLength(double Value, SvgLengthUnit Unit)
{
    public const double MmPerInch = 25.4;

    /// <summary>1px = 1/96 英寸 = 0.264583mm，与 <c>Mm.FromDiu</c> 完全同口径（这是本项目唯一一处 px→mm 定义）。</summary>
    public const double MmPerPixel = MmPerInch / 96.0;

    /// <summary>
    /// 折算成毫米。<paramref name="userUnitMm"/> 是"一个用户单位等于多少毫米"，
    /// 由画布尺寸与 viewBox 的比例决定（见 <c>SvgParser</c>）。
    /// <para>百分比返回 <see cref="double.NaN"/>——宁可让上层报"这一处没换算"，也不猜参照物。</para>
    /// </summary>
    public double ToMillimetres(double userUnitMm = MmPerPixel) => Unit switch
    {
        SvgLengthUnit.User or SvgLengthUnit.Px => Value * userUnitMm,
        SvgLengthUnit.Mm => Value,
        SvgLengthUnit.Cm => Value * 10.0,
        SvgLengthUnit.In => Value * MmPerInch,
        SvgLengthUnit.Pt => Value * MmPerInch / 72.0,
        SvgLengthUnit.Pc => Value * MmPerInch / 6.0, // 1pc = 12pt
        _ => double.NaN,
    };

    /// <summary>解析 "12.5"、"100mm"、"3.94in"、"12pt"、"50%"；解析不了返回 null。</summary>
    public static SvgLength? Parse(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return null;

        var i = 0;
        if (s[i] is '+' or '-') i++;
        var digitStart = i;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
        if (i == digitStart) return null;
        if (!double.TryParse(s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;

        var unit = s[i..].Trim().ToLowerInvariant();
        if (unit.Length == 0) return new SvgLength(v, SvgLengthUnit.User);
        return unit switch
        {
            "px" or "pix" => new SvgLength(v, SvgLengthUnit.Px),
            "mm" => new SvgLength(v, SvgLengthUnit.Mm),
            "cm" => new SvgLength(v, SvgLengthUnit.Cm),
            "in" => new SvgLength(v, SvgLengthUnit.In),
            "pt" => new SvgLength(v, SvgLengthUnit.Pt),
            "pc" => new SvgLength(v, SvgLengthUnit.Pc),
            "%" => new SvgLength(v, SvgLengthUnit.Percent),
            _ => null,
        };
    }

    /// <summary>输出用：去掉浮点噪声， invariant culture，最多三位小数。</summary>
    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>输出用：带毫米单位的长度串（导出的 SVG 一律以毫米为用户单位）。</summary>
    public static string FormatMm(double valueMm) => Format(valueMm) + "mm";
}

/// <summary>
/// 二维仿射矩阵（行向量约定：<c>(x, y) * (a b; c d) + (e f)</c>，与 SVG 的 <c>matrix(a b c d e f)</c> 同序）。
/// <para>
/// CDR 导出的 SVG 里 <c>transform</c> 常见 translate/scale/rotate/skewX/matrix 的组合，
/// 全部折算成一个矩阵再作用到点上，比"看到什么关键字处理什么"更不容易漏。
/// </para>
/// </summary>
public readonly record struct SvgMatrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly SvgMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity =>
        Math.Abs(A - 1) < 1e-12 && Math.Abs(D - 1) < 1e-12 &&
        Math.Abs(B) < 1e-12 && Math.Abs(C) < 1e-12 && Math.Abs(E) < 1e-12 && Math.Abs(F) < 1e-12;

    /// <summary>先应用本矩阵（作用在点上靠前），再应用 <paramref name="other"/>。</summary>
    public SvgMatrix Then(SvgMatrix other) => new(
        A * other.A + B * other.C,
        A * other.B + B * other.D,
        C * other.A + D * other.C,
        C * other.B + D * other.D,
        E * other.A + F * other.C + other.E,
        E * other.B + F * other.D + other.F);

    public double ScaleX => Math.Max(Math.Abs(A), Math.Abs(B));
    public double ScaleY => Math.Max(Math.Abs(C), Math.Abs(D));

    /// <summary>各向缩放均值，用于把 <c>stroke-width</c> 折算到毫米（非等比缩放时只能是近似）。</summary>
    public double StrokeScale => (ScaleX + ScaleY) / 2.0;

    public bool HasFlip => A * D - B * C < 0;

    public (double X, double Y) Map(double x, double y) => (x * A + y * C + E, x * B + y * D + F);

    public static SvgMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);
    public static SvgMatrix Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static SvgMatrix Rotate(double degrees)
    {
        var rad = degrees / 180.0 * Math.PI;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        return new SvgMatrix(c, s, -s, c, 0, 0);
    }

    public static SvgMatrix Rotate(double degrees, double cx, double cy)
        => Translate(-cx, -cy).Then(Rotate(degrees)).Then(Translate(cx, cy));

    public static SvgMatrix SkewX(double degrees)
    {
        var rad = degrees / 180.0 * Math.PI;
        return new SvgMatrix(1, 0, Math.Tan(rad), 1, 0, 0);
    }

    public static SvgMatrix SkewY(double degrees)
    {
        var rad = degrees / 180.0 * Math.PI;
        return new SvgMatrix(1, Math.Tan(rad), 0, 1, 0, 0);
    }

    /// <summary>
    /// 解析整个 <c>transform</c> 属性（多个函数按书写顺序复合）。
    /// 返回 null 表示完全解析不了；部分解析得了的函数会累加，解析不了的由调用方告警。
    /// </summary>
    public static SvgMatrix? Parse(string? text, out string? unparsed)
    {
        unparsed = null;
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return null;

        var m = Identity;
        var i = 0;
        var sawAny = false;
        while (i < s.Length)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
            var nameStart = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            var name = s[nameStart..i];
            if (name.Length == 0)
            {
                i++; // 非字母字符直接跳过，避免死循环
                continue;
            }
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length || s[i] != '(')
            {
                unparsed = AppendUnparsed(unparsed, s[nameStart..]);
                break;
            }
            var close = s.IndexOf(')', i);
            if (close < 0)
            {
                unparsed = AppendUnparsed(unparsed, s[nameStart..]);
                break;
            }
            var args = ParseNumberList(s.AsSpan(i + 1, close - i - 1));
            i = close + 1;
            sawAny = true;

            var step = name.ToLowerInvariant() switch
            {
                "translate" => args.Count switch
                {
                    0 => (SvgMatrix?)null,
                    1 => Translate(args[0], 0),
                    _ => Translate(args[0], args[1]),
                },
                "scale" => args.Count switch
                {
                    0 => (SvgMatrix?)null,
                    1 => Scale(args[0], args[0]),
                    _ => Scale(args[0], args[1]),
                },
                "rotate" => args.Count switch
                {
                    0 => (SvgMatrix?)null,
                    1 => Rotate(args[0]),
                    _ => Rotate(args[0], args[1], args[2]),
                },
                "skewx" when args.Count >= 1 => SkewX(args[0]),
                "skewy" when args.Count >= 1 => SkewY(args[0]),
                "matrix" when args.Count >= 6 => new SvgMatrix(args[0], args[1], args[2], args[3], args[4], args[5]),
                _ => null,
            };
            if (step is null)
            {
                unparsed = AppendUnparsed(unparsed, name + "(" + string.Join(",", args.Select(SvgLength.Format)) + ")");
                continue;
            }
            // SVG 的书写顺序是"右边的先作用"：p' = p·(最右)·…·(最左)，所以每读到一个函数要往左插
            m = step.Value.Then(m);
        }
        return sawAny ? m : null;
    }

    private static string AppendUnparsed(string? existing, string fragment) =>
        string.IsNullOrEmpty(existing) ? fragment.Trim() : existing + " " + fragment.Trim();

    /// <summary>列表参数（"1, 2 -3.5 4"）。</summary>
    public static List<double> ParseNumberList(string? text)
        => string.IsNullOrEmpty(text) ? new List<double>() : ParseNumberList(text.AsSpan());

    /// <summary>
    /// SVG 数字列表解析：支持 <c>1e-5</c>、<c>-.5</c>、以及「10-5」这种负号即分隔的写法（CDR 导出极常见）。
    /// <para>非数字字符（路径命令字母等）逐个跳过，因此同一个分词器也能喂 <c>path@d</c>。</para>
    /// </summary>
    public static List<double> ParseNumberList(ReadOnlySpan<char> text)
    {
        var list = new List<double>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            if (i >= text.Length) break;

            var start = i;
            if (text[i] is '+' or '-') i++;
            var seenDigit = false;
            var seenDot = false;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsAsciiDigit(c)) { seenDigit = true; i++; continue; }
                if (c == '.' && !seenDot) { seenDot = true; i++; continue; }
                break;
            }
            if (!seenDigit)
            {
                i = Math.Max(i, start + 1); // 不是数（命令字母之类）→ 跳一个字符继续找
                continue;
            }

            if (i < text.Length && (text[i] is 'e' or 'E'))
            {
                var save = i;
                i++;
                if (i < text.Length && text[i] is '+' or '-') i++;
                var expStart = i;
                while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                if (i == expStart) i = save; // e 后面没数字 → 那个 e 不属于这个数
            }

            if (double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) list.Add(value);
            else i = Math.Max(i, start + 1);
        }
        return list;
    }
}

/// <summary><c>path@d</c> 的一个 token：命令字母或数字。</summary>
public readonly record struct SvgPathToken(bool IsCommand, char Command, double Number)
{
    public bool IsNumber => !IsCommand;

    public override string ToString() => IsCommand ? Command.ToString() : SvgLength.Format(Number);
}

/// <summary>
/// <c>path@d</c> 属性分词器。命令字母单独成 token，数字部分复用 <see cref="SvgMatrix.ParseNumberList(ReadOnlySpan{char})"/>
/// 的扫描规则（负号可以当分隔符，所以 "M10-10" 是合法的）。
/// </summary>
public static class SvgPathTokenizer
{
    public static List<SvgPathToken> Tokenize(string? data)
    {
        var tokens = new List<SvgPathToken>();
        var s = data ?? string.Empty;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                tokens.Add(new SvgPathToken(true, c, 0));
                i++;
                continue;
            }
            var start = i;
            if (c is '+' or '-') i++;
            var seenDigit = false;
            var seenDot = false;
            while (i < s.Length)
            {
                var d = s[i];
                if (char.IsAsciiDigit(d)) { seenDigit = true; i++; continue; }
                if (d == '.' && !seenDot) { seenDot = true; i++; continue; }
                break;
            }
            if (!seenDigit) { i = Math.Max(i, start + 1); continue; }
            if (i < s.Length && (s[i] is 'e' or 'E'))
            {
                var save = i;
                i++;
                if (i < s.Length && s[i] is '+' or '-') i++;
                var expStart = i;
                while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                if (i == expStart) i = save;
            }
            if (double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                tokens.Add(new SvgPathToken(false, '\0', v));
            else i = Math.Max(i, start + 1);
        }
        return tokens;
    }
}

/// <summary>路径的一个命令。<see cref="Command"/> 一律是<strong>大写</strong>（绝对）。</summary>
/// <param name="Command">M / L / C / Q / Z 之一（解析阶段已把 H/V/S/T/A 全部归一，见 <c>SvgParser</c> 的规范命令集约定）。</param>
/// <param name="Args">绝对坐标，单位毫米。</param>
public sealed record SvgPathCommand(char Command, IReadOnlyList<double> Args)
{
    public override string ToString()
    {
        var sb = new StringBuilder(Command.ToString());
        foreach (var arg in Args)
        {
            sb.Append(' ');
            sb.Append(SvgLength.Format(arg));
        }
        return sb.ToString();
    }
}

/// <summary>文字水平对齐（来自 <c>text-anchor</c>）。</summary>
public enum SvgTextAnchor
{
    Start = 0,
    Middle = 1,
    End = 2,
}

/// <summary>
/// 画笔（填充或描边）。<see cref="Color"/> 已归一成 <c>#rrggbb</c>；
/// 渐变/图样这类没法在零依赖下保真的，由解析器降级成"取第一处色"并挂告警，最终仍是一个纯色。
/// </summary>
public sealed record SvgPaint
{
    public string Color { get; init; } = "#000000";
    public double Opacity { get; init; } = 1;

    /// <summary>线宽（毫米）。填充时忽略。</summary>
    public double WidthMm { get; init; } = 0.35;

    /// <summary>虚线段长（毫米），null 表示实线。</summary>
    public double[]? DashMm { get; init; }

    /// <summary>降级原因（"渐变未按矢量保留，取第一停靠色"之类），null 表示没降级。</summary>
    public string? Degraded { get; init; }
}

/// <summary>
/// 矢量路径元素。<strong>rect/circle/ellipse/line/polyline/polygon/path 全部归一到这一种类型</strong>，
/// 命令一律是绝对坐标的 M/L/C/Z，坐标单位一律毫米（累加 transform 已在解析时做完）。
/// <para>
/// 这样归一的好处：渲染只需一个分支、导出只需一个分支、Core 里不存在"矩形 vs 路径"两套几何。
/// </para>
/// </summary>
public sealed class SvgPath
{
    /// <summary>来源标签名（rect/circle/path…），告警文案要指名道姓。</summary>
    public string SourceTag { get; init; } = "path";

    /// <summary>源文件里的 <c>id</c>（可空），<c>use</c> 解析与图层名要用。</summary>
    public string? Id { get; init; }

    public IReadOnlyList<SvgPathCommand> Commands { get; init; } = Array.Empty<SvgPathCommand>();

    /// <summary>null 表示 <c>fill:none</c> 或没给。</summary>
    public SvgPaint? Fill { get; init; }

    /// <summary>null 表示 <c>stroke:none</c> 或没给。</summary>
    public SvgPaint? Stroke { get; init; }

    /// <summary><c>fill-rule: evenodd</c>。</summary>
    public bool EvenOdd { get; init; }

    /// <summary>解析阶段算好的包围盒（毫米，含控制点，贝塞尔按控制包络略偏大）。</summary>
    public SvgBounds Bounds { get; init; }
}

/// <summary>文字元素。</summary>
public sealed class SvgText
{
    public string Content { get; init; } = string.Empty;

    /// <summary>左端 X（毫米，已按 <see cref="Anchor"/> 折算好：<see cref="SvgTextAnchor.Start"/> 时就是左端）。</summary>
    public double XMm { get; init; }

    /// <summary>基线 Y（毫米）。SVG 的 <c>y</c> 是基线，不是顶边——转成模板元素时要减去 ascent。</summary>
    public double BaselineYmm { get; init; }

    /// <summary>字号（磅）。</summary>
    public double SizePt { get; init; } = 8;

    public string FontFamily { get; init; } = "sans-serif";
    public bool Bold { get; init; }
    public SvgTextAnchor Anchor { get; init; } = SvgTextAnchor.Start;
    public SvgPaint? Fill { get; init; }

    /// <summary>源文件里的 <c>id</c>（可空）。导入时按它从底图里剔除被提升的文字。</summary>
    public string? Id { get; init; }

    /// <summary>
    /// 合并成本行之前那些段的 <c>id</c>（可空 = 没合并过）。
    /// <para>一行被拆成十几个单字时（CorelDRAW 导出的常态），提升成可编辑元素后<strong>这些 id 都得从底图剔掉</strong>，
    /// 只剔第一个会把剩下的单字再印一遍。</para>
    /// </summary>
    public IReadOnlyList<string>? MergedSourceIds { get; init; }

    /// <summary>本行由几个源段合并而成（1 = 未合并）。导入窗口拿它告诉用户“这行原来被拆成了几块”。</summary>
    public int MergedFromCount { get; init; } = 1;

    /// <summary>宽度估算（毫米）。<strong>只用于给导入的元素一个初始框，不参与任何印刷尺寸决定</strong>。</summary>
    public double WidthEstimateMm { get; init; }

    /// <summary>元素整体包围盒（毫米，按 0.8em 近似 ascent 与行高算）。</summary>
    public SvgBounds Bounds { get; init; }

    /// <summary>源标签是 <c>text</c> 还是 <c>tspan</c>（报告里要说清一个视觉行被拆成了几段）。</summary>
    public string SourceTag { get; init; } = "text";
}

/// <summary>内嵌位图元素。</summary>
public sealed class SvgImage
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string MimeType { get; init; } = "image/png";
    public SvgBounds Bounds { get; init; }
    public string? Id { get; init; }
}

/// <summary>毫米坐标下的包围盒（Y 向下，与 SVG/WPF/模板三方同向）。</summary>
public readonly record struct SvgBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public static readonly SvgBounds Empty = new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    public double WidthMm => IsEmpty ? 0 : MaxX - MinX;
    public double HeightMm => IsEmpty ? 0 : MaxY - MinY;
    public double XMm => IsEmpty ? 0 : MinX;
    public double YMm => IsEmpty ? 0 : MinY;

    public SvgBounds Add(double x, double y) => new(
        Math.Min(MinX, x), Math.Min(MinY, y), Math.Max(MaxX, x), Math.Max(MaxY, y));

    public SvgBounds Union(SvgBounds other) => new(
        Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
        Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
}

/// <summary>
/// 一份解析完的 SVG 底稿：<strong>坐标一律毫米、字号一律磅</strong>，
/// 也就是已经完成了"用户单位→毫米 + 累加 transform"两步，上层拿到的就是能直接进模板的东西。
/// </summary>
public sealed class SvgDocument
{
    /// <summary>画布宽（毫米）。取 <c>width</c> 属性，缺则按 viewBox 或包围盒推。</summary>
    public double WidthMm { get; set; }

    public double HeightMm { get; set; }

    /// <summary>一个用户单位等于多少毫米（写报告时要说清底稿的原始口径）。</summary>
    public double UserUnitMm { get; set; } = SvgLength.MmPerPixel;

    public List<SvgPath> Paths { get; } = new();

    public List<SvgText> Texts { get; } = new();

    public List<SvgImage> Images { get; } = new();

    /// <summary>源文件声明的图层/分组名（顶层 <c>g</c> 的 id 或 <c>inkscape:label</c>），导出时可复用。</summary>
    public List<string> LayerNames { get; } = new();

    /// <summary>内容包围盒（毫米），底稿画布比内容大很多时用来提示用户裁边。</summary>
    public SvgBounds ContentBounds { get; private set; } = SvgBounds.Empty;

    public int NodeCount { get; set; }

    public void Track(SvgBounds bounds)
    {
        if (bounds.IsEmpty) return;
        ContentBounds = ContentBounds.IsEmpty ? bounds : ContentBounds.Union(bounds);
    }

    public void Add(SvgPath path) { Paths.Add(path); Track(path.Bounds); }

    public void Add(SvgText text) { Texts.Add(text); Track(text.Bounds); }

    public void Add(SvgImage image) { Images.Add(image); Track(image.Bounds); }

    /// <summary>全部元素数（判断"这底稿复杂到什么程度"）。</summary>
    public int ElementCount => Paths.Count + Texts.Count + Images.Count;
}
