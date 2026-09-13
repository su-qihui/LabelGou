namespace LabelGou.Core.Colors;

/// <summary>
/// HSV（色相/饱和/明度）与 sRGB 的互换，<strong>只给取色调色盘当手感用</strong>。
/// <para>
/// 为什么要有它：印刷分量按 CMYK 百分数填是对的，但"我要一支比刚才再亮一点、偏品红一点的红"
/// 这种话用四格数字说不出来，得用一对方块拖。HSV 就是那对方块的坐标系。
/// </para>
/// <para>
/// <strong>HSV 不是第三种存法</strong>：它不进模板 JSON、不进渲染、不进出口。拖完调色盘，
/// 落盘的仍然是 <see cref="LabelColor"/> 那两端原值（见 <see cref="CmykMath"/> 的"两端并存"那条）。
/// 灰阶（黑、白、灰）上色相<em>没有定义</em>，<see cref="FromRgb"/> 因此返回
/// <see cref="double.NaN"/> 而不是随便给个 0——取色器要据此保留上一次选中的色相，
/// 否则拖到黑色那一角，色相条会自己弹回红色。
/// </para>
/// </summary>
public static class HsvMath
{
    /// <summary>色相周角（度）。</summary>
    public const double HueMax = 360;

    /// <summary>
    /// HSV → RGB。<paramref name="hueDeg"/> 任意实数都收（按 360 取模，负数也绕回来），
    /// 饱和度与明度按 [0,1] 夹住——它们来自鼠标位置，越界是常态不是错误，不该让取色器抛异常。
    /// </summary>
    public static (byte R, byte G, byte B) ToRgb(double hueDeg, double saturation, double value)
    {
        var h = ((hueDeg % HueMax) + HueMax) % HueMax;
        var s = Clamp01(saturation);
        var v = Clamp01(value);

        var chroma = v * s;
        var secondary = chroma * (1 - Math.Abs(h / 60 % 2 - 1));
        var match = v - chroma;          // 三通道共同的灰底

        var (r, g, b) = h switch
        {
            < 60 => (chroma, secondary, 0.0),
            < 120 => (secondary, chroma, 0.0),
            < 180 => (0.0, chroma, secondary),
            < 240 => (0.0, secondary, chroma),
            < 300 => (secondary, 0.0, chroma),
            _ => (chroma, 0.0, secondary),
        };
        return (ToByte(r + match), ToByte(g + match), ToByte(b + match));
    }

    /// <summary>
    /// RGB → HSV。色相在<strong>无彩</strong>（r=g=b，含黑与白）时为 <see cref="double.NaN"/>，
    /// 此时饱和度归 0；明度始终是最亮那一通道。
    /// </summary>
    public static (double HueDeg, double Saturation, double Value) FromRgb(int r, int g, int b)
    {
        double rf = ClampByte(r) / 255.0, gf = ClampByte(g) / 255.0, bf = ClampByte(b) / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        if (delta <= 0) return (double.NaN, 0, max);        // 无彩：色相没有定义，别编一个 0 出来

        var hue = max switch
        {
            var _ when max == rf => 60 * (((gf - bf) / delta) % 6),
            var _ when max == gf => 60 * ((bf - rf) / delta + 2),
            _ => 60 * ((rf - gf) / delta + 4),
        };
        if (hue < 0) hue += HueMax;
        return (hue, delta / max, max);
    }

    /// <summary>色相归到 [0,360)。负数按周角绕回（-90 与 270 是同一支色相）。</summary>
    public static double NormalizeHue(double hueDeg)
    {
        if (double.IsNaN(hueDeg) || double.IsInfinity(hueDeg)) return 0;
        var h = hueDeg % HueMax;
        return h < 0 ? h + HueMax : h;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static int ClampByte(int v) => Math.Clamp(v, 0, 255);

    private static byte ToByte(double zeroToOne) => (byte)Math.Clamp((int)Math.Round(zeroToOne * 255, MidpointRounding.AwayFromZero), 0, 255);
}
