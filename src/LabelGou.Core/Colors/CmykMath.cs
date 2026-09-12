namespace LabelGou.Core.Colors;

/// <summary>
/// CMYK（百分数）与 sRGB（0~255）之间的换算。<strong>这是 naive 减色模型，不是色彩管理</strong>，
/// 用它的人必须知道边界在哪。
/// <para>
/// 公式与 colorPicker（MIT，Peter Dematté）里 <c>rgb2cmy</c>/<c>cmy2cmyk</c>/<c>cmyk2cmy</c>/<c>cmy2rgb</c>
/// 四条同口径（那份 JS 工程是用户 2026-09-13 指定的参考来源之一；这里是照公式重写的 C#，不搬代码）。
/// 它在源码里自己引了维基一段话，我们把它原样带进 UI 文案：
/// <em>「RGB 与 CMYK 都是设备相关色空间，两者之间没有简单而普适的换算公式；换算一般要靠色彩管理系统
/// 和描述两端的 profile 来做，而且因为两个空间的色域差别很大，换算结果不可能精确。」</em>
/// </para>
/// <para>
/// <strong>所以这一层的输出只能用来【显示】，不能用来【出片】。</strong>
/// 真要印，印刷设备吃的必须是 <see cref="LabelColor"/> 里那份<strong>用户亲手填的 CMYK 原值</strong>
/// （出口那一步在后面的棒做，见《LabelGou-AI对接文档》§三-阶段 47 的三棒划分）。
/// 正因为折回来会变，<see cref="LabelColor"/> 把两端分量<strong>都存下来</strong>，
/// 任何一侧都不许由另一侧现算回去——实测：CMYK(37,63,11,5) 经 sRGB 再折回 CMYK 得
/// <strong>(29,58,0,15)</strong>：屏幕上还是那个颜色，版上四格却全换了配方。
/// 拿"折回来的那套"去出片，印刷店收到的就不是用户填的那个配方。
/// </para>
/// </summary>
public static class CmykMath
{
    /// <summary>CMYK 分量的取值上限（百分数，整数档；印刷口述"几成墨"就是它）。</summary>
    public const int MaxPercent = 100;

    /// <summary>
    /// CMYK 百分数 → sRGB。<paramref name="cPercent"/> 等为 0~100 的百分数，越界一律夹住（不抛：
    /// 分量来自用户手填与外部 SVG，一个越界数字不该让整张标签画不出来）。
    /// </summary>
    public static (byte R, byte G, byte B) CmykToSrgb(int cPercent, int mPercent, int yPercent, int kPercent)
    {
        var c = ClampPercent(cPercent) / 100.0;
        var m = ClampPercent(mPercent) / 100.0;
        var y = ClampPercent(yPercent) / 100.0;
        var k = ClampPercent(kPercent) / 100.0;

        // 减色：某一色墨通道的透过率 (1-ink)，再整体乘黑版 (1-k)。
        var r = (1 - c) * (1 - k);
        var g = (1 - m) * (1 - k);
        var b = (1 - y) * (1 - k);
        return (ToByte(r), ToByte(g), ToByte(b));
    }

    /// <summary>
    /// sRGB → CMYK 百分数。<strong>黑版取三色里最少墨的那一支（最低档 GCR）</strong>：
    /// <c>k = min(c,m,y)</c>，剩下三格按 <c>(v - k) / (1 - k)</c> 摊出去。纯灰 (128,128,128) 因此落成
    /// (0,0,0,50)——青品黄三格全零、灰只由黑版叠出来，而不是拿三色各压一点叠出来（后者就是印刷店说的
    /// "灰字里掺彩，套不准就发花"）。
    /// </summary>
    public static (int C, int M, int Y, int K) SrgbToCmyk(int r, int g, int b)
    {
        var c = 1 - ClampByte(r) / 255.0;
        var m = 1 - ClampByte(g) / 255.0;
        var y = 1 - ClampByte(b) / 255.0;

        var k = Math.Min(c, Math.Min(m, y));
        if (k >= 1) return (0, 0, 0, MaxPercent);      // 纯黑：只落黑版，其余三格清零

        var remaining = 1 - k;                          // 上面那条 k >= 1 已经挡掉除零
        return (
            (int)Math.Round((c - k) / remaining * MaxPercent, MidpointRounding.AwayFromZero),
            (int)Math.Round((m - k) / remaining * MaxPercent, MidpointRounding.AwayFromZero),
            (int)Math.Round((y - k) / remaining * MaxPercent, MidpointRounding.AwayFromZero),
            (int)Math.Round(k * MaxPercent, MidpointRounding.AwayFromZero));
    }

    private static int ClampPercent(int v) => Math.Clamp(v, 0, MaxPercent);

    private static int ClampByte(int v) => Math.Clamp(v, 0, 255);

    private static byte ToByte(double zeroToOne) => (byte)Math.Clamp((int)Math.Round(zeroToOne * 255, MidpointRounding.AwayFromZero), 0, 255);
}
