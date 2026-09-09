namespace LabelGou.Core.Barcodes;

/// <summary>一根黑条（毫米，绝对坐标，Y 与高由外层那条 <c>BarcodeItem</c> 统一给）。</summary>
/// <param name="X">条的左边（毫米）。</param>
/// <param name="Width">条的宽（毫米）。</param>
public sealed record BarStrip(double X, double Width);

/// <summary>
/// 条码在标签上的<strong>落位结果</strong>：一组毫米矩形 + 一根模块多宽。
/// <para>几何为什么放在 Core 而不是渲染端：本项目的铁律是「骨架算毫米、渲染不重算坐标」（§七），
/// 五出口（预览 / 打印 / PDF / 图片 / SVG）只要各自把这些矩形画出来就行，
/// 不可能出现「预览能扫、印出来扫不出」。</para>
/// </summary>
/// <param name="Bars">黑条们。</param>
/// <param name="ModuleMm">一根模块（最窄那条线）多宽（毫米）——扫码枪的识读能力就看它。</param>
/// <param name="BarsY">条的顶（毫米）。</param>
/// <param name="BarsHeight">条的高（毫米）。</param>
/// <param name="QuietZoneMm">左右两侧各留了多少毫米（含在里面才扫得出）。</param>
/// <param name="Warning">模块太窄这类「能画但可能扫不出」的提醒。不静默降格是红线。</param>
public sealed record BarcodeGeometry(
    IReadOnlyList<BarStrip> Bars,
    double ModuleMm,
    double BarsY,
    double BarsHeight,
    double QuietZoneMm,
    string? Warning);

/// <summary>
/// 把模块序列铺成毫米矩形。
/// </summary>
public static class BarcodeBars
{
    /// <summary>两侧静区的默认模块数（Code 128 / Code 39 / ITF-14 的规范下限；留不够，扫码枪会把边缘那两根条当成噪声）。
    /// EAN-13 按 GS1 是 11,由 <see cref="BarcodeEncoding.QuietZoneModules"/> 按制式声明。</summary>
    public const int QuietZoneModules = 10;

    /// <summary>
    /// 模块宽度下限（毫米）。0.20 mm ≈ 8 mil，是常见手持枪能稳读的底线；
    /// 比这还窄就<strong>照样画但报一句警告</strong>，不偷偷改成能扫的宽度（那会把条码推出元素框外）。
    /// </summary>
    public const double MinModuleMm = 0.20;

    /// <summary>模块宽度取整到的粒度（毫米）：不让浮点误差在 250 根条上累积成半根条。</summary>
    private const double ModuleStepMm = 0.01;

    /// <summary>
    /// 按给定的框（含静区）铺条。
    /// </summary>
    /// <param name="encoding">编码结果（必须 Ok）。</param>
    /// <param name="x">元素框左（毫米）。</param>
    /// <param name="y">元素框顶（毫米）。</param>
    /// <param name="width">元素框宽（毫米，静区算在里面）。</param>
    /// <param name="barsY">条的顶（毫米，可读文字在下面时这里就是文字上方的那条线）。</param>
    /// <param name="barsHeight">条的高（毫米）。</param>
    public static BarcodeGeometry Build(BarcodeEncoding encoding, double x, double y, double width,
        double barsY, double barsHeight)
    {
        if (encoding is not { Ok: true })
            return new BarcodeGeometry(Array.Empty<BarStrip>(), 0, barsY, barsHeight, 0, encoding?.Error ?? "条码没编出来。");
        if (width <= 0 || barsHeight <= 0)
            return new BarcodeGeometry(Array.Empty<BarStrip>(), 0, barsY, barsHeight, 0, "条码框的宽或高是 0，画不出来。");

        // 静区模块数由制式自己声明(BarcodeEncoding.QuietZoneModules):EAN-13 是 11,其余 10(第 23 棒)
        var quietModules = encoding.QuietZoneModules;
        var totalModules = encoding.Modules + quietModules * 2;
        // 往下取整到 0.01mm：宁可留一点白，也不要超出框（超出就裁掉了，裁掉的可能是最后一根条）。
        var module = Math.Floor(width / totalModules / ModuleStepMm) * ModuleStepMm;
        string? warning = null;
        if (module <= 0)
        {
            return new BarcodeGeometry(Array.Empty<BarStrip>(), 0, barsY, barsHeight, 0,
                $"这个框（{width:0.#} mm 宽）装不下 {encoding.Modules} 个模块的码：请把条码拉宽，或换更短的列/更密的制式。");
        }
        if (module < MinModuleMm)
        {
            warning = $"模块只有 {module:0.00} mm（低于常用底线 {MinModuleMm:0.00} mm）：这么密的码手持枪很可能扫不出来，" +
                      $"建议把框拉宽到 {totalModules * MinModuleMm:0} mm 以上，或改选更短的列。";
        }

        var quiet = quietModules * module;
        var bars = new List<BarStrip>();
        var cursor = x + quiet;
        var run = 0;
        for (var i = 0; i < encoding.Bits.Length; i++)
        {
            if (encoding.Bits[i] == '1')
            {
                run++;
                continue;
            }
            if (run > 0)
            {
                bars.Add(new BarStrip(cursor, run * module));
                cursor += run * module;
                run = 0;
            }
            cursor += module;
        }
        if (run > 0) bars.Add(new BarStrip(cursor, run * module));

        return new BarcodeGeometry(bars, module, barsY, barsHeight, quiet, warning);
    }
}
