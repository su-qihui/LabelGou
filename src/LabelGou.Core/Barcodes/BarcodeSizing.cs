namespace LabelGou.Core.Barcodes;

/// <summary>
/// 条码的<strong>尺寸模型——照 CorelDRAW X4 条码向导（BarCode.exe）那四格搬过来</strong>。
/// <para><strong>为什么要有这个类（第 58 棒，用户 2026-09-14：「条码还是扁的……做不好把人家框架抄过来」）</strong>：
/// 从前我们只有一个"框"，位宽 = 框宽 ÷ 模块数——<strong>框画成 92 × 12 mm，条就被撑成 0.6 mm 粗、12 mm 高</strong>，
/// 这就是他看到的那只扁码。CDR 不是这个账：它的尺寸是<strong>算出来的</strong>，
/// 一个窄元素多宽（X 尺寸）由「打印机分辨率 × 缩放比例」定，再由窄元素数乘出符号宽度。
/// 抄过来之后有一条白捡的好处：<strong>X 不再来自框宽，所以把框怎么拉都不会把条拉肥</strong>——
/// 不需要封顶、不需要锁比例、不需要居中补丁。</para>
/// <para><strong>取证的两个独立锚点</strong>（<c>labelgou-other\_probe\b58-barcode-exe\cdr-wizard-measured.md</c>）：
/// ① 用户截图 EAN-13、300 dpi、100 % → 符号宽度 <c>32.173447 mm</c> = 95 个窄元素 × 4 像素；
/// ② 我开向导实测 CodaBar、12 位、300 dpi、100 %、宽窄比 2.5:1 → <c>2.079999 英寸</c> = 624 像素
/// = 156 个窄元素 × 4 像素。两处互算出同一个 <strong>X = 4 px @300 dpi = 0.338667 mm</strong>。</para>
/// </summary>
/// <param name="Dpi">打印机分辨率——向导默认 300，X 要向上取整到这台机器的<strong>整像素</strong>（半像素会让条边糊掉，扫不稳）。</param>
/// <param name="ScalePercent">缩放比例（%）：向导里 100 % 就是标称 X；要一条 272 mm 宽的通栏码就把它拉大，
/// 那时<strong>高也跟着一起长</strong>——这正是 CDR 的规矩，也是"又宽又不那么高"该用 <paramref name="HeightFactor"/> 调、而不是压扁的原因。</param>
/// <param name="HeightFactor">条形码高度倍数：向导那格「条形码高度(H)」默认 1.0。它只乘在<strong>高</strong>上，
/// 一维都不碰 X——所以想要宽而不高，是加大缩放＋调小这个倍数，不是把框压扁。</param>
/// <param name="WidthReductionPx">条形码宽度减少值（像素）：打印时每根条两侧各让出半个像素，补偿喷墨/热转印的网点增益。
/// 向导默认 1 px。<strong>它不参与符号宽度读数</strong>（两个锚点都没扣它），只在实际出纸时把条画窄一点。</param>
public sealed record BarcodeSizing(
    int Dpi = 300,
    double ScalePercent = 100.0,
    double HeightFactor = 1.0,
    double WidthReductionPx = 1.0)
{
    // 向导的四格默认值（参数默认值只能写字面量——引用下面这几个常量会编译不过，两处必须一致）。
    public const int DefaultDpi = 300;
    public const double DefaultScalePercent = 100.0;
    public const double DefaultHeightFactor = 1.0;
    public const double DefaultWidthReductionPx = 1.0;

    /// <summary>100 % 缩放时一个窄元素的<strong>标称</strong>宽（英寸）——GS1 那套 13 mil。
    /// @300 dpi 是 3.9 像素，向上取整到整像素就是实测到的 4 px（0.338667 mm）。</summary>
    public const double BaseXInches = 0.013;

    private const double MmPerInch = 25.4;

    /// <summary>
    /// X 尺寸：一个窄元素（最窄那根条或那段空）多宽，毫米。
    /// <para>算法照 CDR：<strong>标称 X × 缩放比例，向上取整到打印机的整像素</strong>。
    /// 向上而不是向下：宁可条略宽（扫码枪更吃得开），也不要挤在半像素上——半像素在热转印机上会糊边。</para>
    /// </summary>
    public double ModuleMm => Dpi <= 0 || ScalePercent <= 0
        ? 0
        : Math.Ceiling(BaseXInches * ScalePercent / 100.0 * Dpi - 1e-9) / Dpi * MmPerInch;

    /// <summary>
    /// 符号宽度（向导那一格只读数）＝<strong>窄元素数 × X</strong>，不含静区。
    /// <para>窄元素数 = 我们的 <c>Modules</c>（位）÷ <c>NarrowBits</c>（一个窄元素几位）：
    /// EAN-13 是 95 ÷ 1 = 95，CodaBar 12 位是 312 ÷ 2 = 156——两个锚点都是这么对上的。</para>
    /// </summary>
    public double SymbolWidthMm(BarcodeEncoding encoding) => encoding is { Ok: true } ? NarrowElements(encoding) * ModuleMm : 0;

    /// <summary>这只码一共多少个窄元素（向导算符号宽度用的就是它）。</summary>
    public static int NarrowElements(BarcodeEncoding encoding) =>
        encoding is { Ok: true } && encoding.NarrowBits > 0 ? encoding.Modules / encoding.NarrowBits : 0;

    /// <summary>
    /// 按 CDR 的账，这只码在 <paramref name="showText"/> 下该占多大一只框（宽含静区、高含顶距与数字带，毫米）。
    /// <para>纵向份数照我们量 CDR 导出样本得到的那一份（<see cref="BarcodeBars.HeightUnits"/> 一族常量）再乘高度倍数：
    /// 印数字时整框 81 份（1 顶距 + 69 数据条 + 6 保护延长 + 5 数字带，实测 81.16），不印时 75 份。</para>
    /// <para><strong>这就是"默认条码"与「加到当前模板」那一下给的尺寸</strong>——不再"通栏宽 + 写死 14 mm 高"。</para>
    /// </summary>
    public (double Width, double Height) BoxOf(BarcodeEncoding encoding, bool showText = true)
    {
        if (encoding is not { Ok: true } || ModuleMm <= 0) return (0, 0);
        var quiet = encoding.QuietZoneMm > 0
            ? encoding.QuietZoneMm
            : encoding.QuietZoneModules * encoding.NarrowBits * ModuleMm;
        var width = SymbolWidthMm(encoding) + quiet * 2;
        var units = showText ? BarcodeBars.HeightUnits : BarcodeBars.HeightUnits - BarcodeBars.TopMarginUnits - BarcodeBars.TextBandUnits;
        return (width, units * ModuleMm * HeightFactor);
    }

    /// <summary>
    /// 把这只码塞进「宽最多 <paramref name="maxWidthMm"/>、高最多 <paramref name="maxHeightMm"/>」里该用的缩放参数。
    /// <para><strong>只往下调 X（一格一个整像素），绝不反过来把条压扁</strong>——那是"扁条码"的老路。
    /// 已经细到一根像素还装不下就停下（宁可超框也不把码画没）。条码面板与模板编辑器共用这一份（第 58 棒）。</para>
    /// </summary>
    public BarcodeSizing FittedTo(BarcodeEncoding encoding, double maxWidthMm, double maxHeightMm, bool showText = true)
    {
        var sizing = this;
        for (var guard = 0; guard < 64; guard++)
        {
            var (w, h) = sizing.BoxOf(encoding, showText);
            if (w <= 0 || h <= 0 || (w <= maxWidthMm + 1e-6 && h <= maxHeightMm + 1e-6)) break;

            // X 是整像素：按比例乘系数常常连一个像素都降不动（第一版就在这里原地打转，被测试当场抓住），
            // 所以直接把像素数减一再反推回缩放比例。
            var px = Math.Ceiling(BaseXInches * sizing.ScalePercent / 100.0 * sizing.Dpi - 1e-9);
            if (px <= 1) break;      // 已经是最细的一根线，再降就把码画没了——宁可超框
            sizing = sizing with { ScalePercent = (px - 1) / sizing.Dpi / BaseXInches * 100.0 };
        }
        return sizing;
    }

    /// <summary>这一族有没有保护条（UPC/EAN 才有）——纵向分份与条高都跟着分两族，见 <see cref="BarcodeBars"/>。</summary>
    public static bool HasGuardBars(BarcodeEncoding encoding) => encoding.GuardRanges is { Count: > 0 };
}
