using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelGou.Core.Colors;

/// <summary>这一支颜色当初是<em>怎么录进来</em>的。它决定 JSON 里写哪种写法、面板打开时先给哪一档。</summary>
public enum ColorEntrySpace
{
    /// <summary>按 sRGB（屏幕/十六进制）录的。外部 SVG 里的 <c>#rrggbb</c>、<c>rgb()</c>、颜色名都归这一档。</summary>
    Srgb = 0,

    /// <summary>按 CMYK 百分数录的（印刷口径）。<c>device-cmyk()</c> 与面板 CMYK 档归这一档。</summary>
    Cmyk = 1,
}

/// <summary>
/// 一支墨的颜色：<strong>两端分量都存原值，谁都不是谁的影子</strong>。
/// <para>
/// 为什么不用一个枚举值代表色空间、另一端现算：屏幕要 8 位精确的 RGB，印刷要整数百分数的 CMYK，
/// 而 <see cref="CmykMath"/> 那对公式<em>折回来不是同一套配方</em>（CMYK(37,63,11,5) 的屏幕色再反算
/// 回去是 (29,58,0,15)——四格全变）。
/// 存两半，等于把"用户亲手填的那个数"锁住：他填 C=30，出口就该看见 C=30，
/// 哪怕屏幕上那个颜色只是按 C=30 近似画出来的。
/// </para>
/// <para>
/// <strong>本棒（第 47 棒）只把它接到显示与预览上</strong>：模板元素可以有自己的颜色，
/// 五个出口读同一支笔；出片端要真 CMYK 数值是第 48 棒（见《LabelGou-AI对接文档》§三-阶段 47）。
/// </para>
/// </summary>
[JsonConverter(typeof(LabelColorJsonConverter))]
public sealed record LabelColor
{
    private LabelColor(ColorEntrySpace entry, byte r, byte g, byte b, byte c, byte m, byte y, byte k)
    {
        Entry = entry;
        R = r; G = g; B = b;
        C = c; M = m; Y = y; K = k;
    }

    /// <summary>用户录入时那一档是哪边（见 <see cref="ColorEntrySpace"/>）。</summary>
    public ColorEntrySpace Entry { get; init; }

    /// <summary>屏幕口径：红 0~255。<strong>两种来源都有值</strong>（CMYK 录的由 <see cref="CmykMath"/> 近似出来）。</summary>
    public byte R { get; init; }

    public byte G { get; init; }

    public byte B { get; init; }

    /// <summary>印刷口径：青 0~100 百分数。<strong>两种来源都有值</strong>（RGB 录的是近似反算的）。</summary>
    public byte C { get; init; }

    public byte M { get; init; }

    public byte Y { get; init; }

    /// <summary>黑版百分数。</summary>
    public byte K { get; init; }

    /// <summary>默认墨色：sRGB 全零。与"没填颜色"同义，所以元素上的 <c>InkColor</c> 为 null 时就当它。</summary>
    public static LabelColor Black { get; } = FromSrgb(0, 0, 0);

    /// <summary>按 sRGB 录：CMYK 那半由 naive 公式近似，只给面板显示与后续出口当参考。</summary>
    public static LabelColor FromSrgb(int r, int g, int b)
    {
        var (cr, cg, cb) = (Clamp255(r), Clamp255(g), Clamp255(b));
        var (c, m, y, k) = CmykMath.SrgbToCmyk(cr, cg, cb);
        return new LabelColor(ColorEntrySpace.Srgb, (byte)cr, (byte)cg, (byte)cb, (byte)c, (byte)m, (byte)y, (byte)k);
    }

    /// <summary>按 CMYK 百分数录：sRGB 那半是<strong>屏幕近似</strong>，不许当成出片依据（这就是 <see cref="CmykMath"/> 那段声明）。</summary>
    public static LabelColor FromCmyk(int c, int m, int y, int k)
    {
        var (cc, mm, yy, kk) = (Clamp100(c), Clamp100(m), Clamp100(y), Clamp100(k));
        var (r, g, b) = CmykMath.CmykToSrgb(cc, mm, yy, kk);
        return new LabelColor(ColorEntrySpace.Cmyk, r, g, b, (byte)cc, (byte)mm, (byte)yy, (byte)kk);
    }

    /// <summary>换一档重录（面板上从 RGB 切到 CMYK 时走这里）：两端都按新值重算，<paramref name="asCmyk"/> 决定谁是原值。</summary>
    public static LabelColor ReEntered(bool asCmyk, int a, int b, int c, int? d = null)
        => asCmyk ? FromCmyk(a, b, c, d ?? 0) : FromSrgb(a, b, c);

    /// <summary>十六进制写法（<c>#rrggbb</c>）。SVG 的 <c>fill</c> 与 CorelDRAW 那条通道只认这个。</summary>
    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>
    /// 落 JSON 与写给我们自己看的紧凑写法：<c>#rrggbb</c> 或 <c>cmyk(C M Y K)</c>，百分数是整数。
    /// <para>刻意<em>不是</em> SVG 的 <c>device-cmyk()</c>——那一式的分量按 CSS 规定是 0~1 小数，
    /// 我们的存储式是印刷口径的整数百分数，两者混在一个字符串里迟早有人当 bug 修。</para>
    /// </summary>
    public string ToStorageString() => Entry == ColorEntrySpace.Cmyk
        ? $"cmyk({C} {M} {Y} {K})"
        : ToHex();

    public override string ToString() => ToStorageString();

    /// <summary>面板上给人看的一句话（CMYK 优先——那是他跟印刷店说话用的口径）。</summary>
    public string ToPrintText() => $"C{C} M{M} Y{Y} K{K}";

    /// <summary>
    /// 认这几种写法：<c>#rgb</c>、<c>#rrggbb</c>、<c>rgb(r,g,b)</c>、<c>cmyk(C M Y K)</c>（整数百分数，我们的存储式）、
    /// <c>device-cmyk(c m y k)</c>（CSS/SVG 式，0~1 小数或带 <c>%</c>）。认不出来返回 false。
    /// <para>分隔符按宽容处理：空格、逗号、斜杠都当分隔，所以 CSS4 的 <c>device-cmyk(0 .9 .9 0 / 1 #007f7f)</c>
    /// 至少能取到前四个分量；<strong>后面的 alpha 与兼容 sRGB 兜底色本棒刻意不接</strong>
    /// （唛头没有半透明墨，接了反而要让五个出口都学会处理透明）。</para>
    /// </summary>
    public static bool TryParse(string? raw, out LabelColor? color)
    {
        color = null;
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return false;

        if (s[0] == '#')
        {
            var hex = s[1..];
            if (hex.Length == 3) hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            if (hex.Length < 6 || !IsHex(hex)) return false;
            color = FromSrgb(
                Convert.ToInt32(hex[..2], 16),
                Convert.ToInt32(hex.Substring(2, 2), 16),
                Convert.ToInt32(hex.Substring(4, 2), 16));
            return true;
        }

        var open = s.IndexOf('(');
        if (open <= 0 || !s.EndsWith(')')) return false;
        var fn = s[..open].Trim().ToLowerInvariant();
        var parts = s[(open + 1)..^1].Split(new[] { ' ', ',', '/', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        switch (fn)
        {
            case "rgb":
            {
                if (parts.Length < 3 || !TryByte(parts[0], out var r) || !TryByte(parts[1], out var g) || !TryByte(parts[2], out var b)) return false;
                color = FromSrgb(r, g, b);
                return true;
            }
            case "cmyk":
            {
                if (parts.Length < 4 || !TryPercent(parts[0], out var c) || !TryPercent(parts[1], out var m)
                    || !TryPercent(parts[2], out var y) || !TryPercent(parts[3], out var k)) return false;
                color = FromCmyk(c, m, y, k);
                return true;
            }
            case "device-cmyk":
            {
                // CSS 规定这一式的数字分量是 0~1 小数；带 % 的按百分数读。
                if (parts.Length < 4 || !TryCss(parts[0], out var c) || !TryCss(parts[1], out var m)
                    || !TryCss(parts[2], out var y) || !TryCss(parts[3], out var k)) return false;
                color = FromCmyk(c, m, y, k);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryCss(string token, out int percent)
    {
        percent = 0;
        if (token.EndsWith('%')) return TryPercent(token[..^1], out percent);
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
        percent = v <= 1
            ? (int)Math.Round(v * CmykMath.MaxPercent, MidpointRounding.AwayFromZero)
            : (int)Math.Round(v, MidpointRounding.AwayFromZero);
        percent = Clamp100(percent);
        return true;
    }

    private static bool TryPercent(string token, out int percent)
    {
        percent = 0;
        var t = token.EndsWith('%') ? token[..^1] : token;
        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return false;
        percent = Clamp100(v);
        return true;
    }

    private static bool TryByte(string token, out int value)
    {
        value = 0;
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return false;
        value = Clamp255(v);
        return true;
    }

    private static bool IsHex(string s)
    {
        foreach (var ch in s)
        {
            if (!(ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        }
        return s.Length > 0;
    }

    private static int Clamp255(int v) => Math.Clamp(v, 0, 255);

    private static int Clamp100(int v) => Math.Clamp(v, 0, CmykMath.MaxPercent);
}

/// <summary>
/// 颜色在模板 JSON 里写成<strong>一个字符串</strong>（<c>"#c62828"</c> / <c>"cmyk(0 91 90 0)"</c>），不铺八个数字。
/// <para>理由：模板文件是要给人拷到另一台机器、也要被 AI 生成的，一个字符串字段比八个字段好读也好校验；
/// 而且缺省（null）时 <c>TemplateStore</c> 的 <c>WhenWritingNull</c> 会整条省掉，
/// <strong>现有模板文件逐字节不变</strong>。</para>
/// </summary>
public sealed class LabelColorJsonConverter : JsonConverter<LabelColor?>
{
    /// <summary>认不出来的写法一律当"没填"，不抛——读模板时一个坏颜色不该让整份模板打不开。</summary>
    public override LabelColor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) return null;
        return LabelColor.TryParse(reader.GetString(), out var parsed) ? parsed : null;
    }

    public override void Write(Utf8JsonWriter writer, LabelColor? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.ToStorageString());
    }
}
