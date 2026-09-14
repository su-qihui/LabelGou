namespace LabelGou.Core.Barcodes;

/// <summary>
/// 可读数字（HRI）里<strong>一个字符</strong>的落位（毫米）：<paramref name="X"/> 是这一格的左、
/// <paramref name="Width"/> 是格宽，字符在这一格里居中。
/// <para>为什么拆到「一格一个字」：UPC/EAN 的数字不是整串居中，而是每位压在自己的 7 模块上
/// （首位骑左静区、末位骑右静区），逐格定位才能和 Corel BARCODE WIZARD 排出来的一模一样。
/// 拆完之后五个出口都只需照格子画，不用各自再算一遍间距。</para>
/// </summary>
/// <param name="Ch">这个格子里要印的字符。</param>
/// <param name="X">格子的左（毫米，绝对坐标）。</param>
/// <param name="Width">格子的宽（毫米）。</param>
public sealed record HriGlyph(char Ch, double X, double Width);

/// <summary>
/// 一根黑条（毫米，绝对坐标，Y 由外层那条 <c>BarcodeItem</c> 统一给）。
/// </summary>
/// <param name="X">条的左边（毫米）。</param>
/// <param name="Width">条的宽（毫米）。</param>
/// <param name="IsGuard">是不是<strong>保护条</strong>（UPC/EAN 两端与正中那几根）。
/// 保护条要画得比数据条高——高多少由 <see cref="BarcodeGeometry.GuardBarsHeight"/> 统一给。</param>
public sealed record BarStrip(double X, double Width, bool IsGuard = false);

/// <summary>
/// 条码在标签上的<strong>落位结果</strong>：一组毫米矩形 + 一根模块多宽。
/// <para>几何为什么放在 Core 而不是渲染端：本项目的铁律是「骨架算毫米、渲染不重算坐标」（§七），
/// 五出口（预览 / 打印 / PDF / 图片 / SVG）只要各自把这些矩形画出来就行，
/// 不可能出现「预览能扫、印出来扫不出」。</para>
/// </summary>
/// <param name="Bars">黑条们（每根自带 <see cref="BarStrip.IsGuard"/>）。</param>
/// <param name="ModuleMm">一根模块（最窄那条线）多宽（毫米）——扫码枪的识读能力就看它。</param>
/// <param name="BarsY">条的顶（毫米）。</param>
/// <param name="BarsHeight"><strong>数据条</strong>的高（毫米）。</param>
/// <param name="QuietZoneMm">左右两侧各留了多少毫米（含在里面才扫得出）。</param>
/// <param name="Warning">模块太窄这类「能画但可能扫不出」的提醒。不静默降格是红线。</param>
/// <param name="GuardBarsHeight"><strong>保护条</strong>的高（毫米）；这一族没有保护条时等于 <paramref name="BarsHeight"/>。
/// <para>UPC/EAN 的起止与正中那几根条按规范比数据条高出一截，扫码枪靠它们找边界与中点。
/// 用户 2026-09-11 拿 Corel BARCODE WIZARD 当参照物点名要的，差多少见 <see cref="BarcodeBars.GuardExtensionModules"/>。</para></param>
/// <param name="CenterOffsetMm">整组条往右让了多少毫米——第 58 棒按 CDR 的 X 尺寸画时，
/// 符号可能比元素框窄（框是用户拖的），多出来的两头是<strong>留白不是静区</strong>：静区仍是制式声明的数，
/// 首末那几位可读数字要贴着自己的条，所以这个偏移得单独带出去给 <see cref="BarcodeBars.BuildHri"/> 用。</param>
public sealed record BarcodeGeometry(
    IReadOnlyList<BarStrip> Bars,
    double ModuleMm,
    double BarsY,
    double BarsHeight,
    double QuietZoneMm,
    string? Warning,
    double GuardBarsHeight = 0,
    double CenterOffsetMm = 0);

/// <summary>
/// 把模块序列铺成毫米矩形。
/// </summary>
public static class BarcodeBars
{
    /// <summary>两侧静区的默认模块数（Code 128 / Code 39 / ITF-14 的规范下限；留不够，扫码枪会把边缘那两根条当成噪声）。
    /// EAN-13 按 GS1 是 11、EAN-8 是 7、UPC 是 9，由 <see cref="BarcodeEncoding.QuietZoneModules"/> 按制式声明。</summary>
    public const int QuietZoneModules = 10;

    /// <summary>
    /// 保护条比数据条高出几个模块。
    /// <para><strong>这个 6 不是拍脑袋来的</strong>：用户导出的 CDR 样本
    /// <c>labelgou-CL\条码\图形1.svg</c> 里，数据条高 23.366 mm（= 69.14 个模块）、
    /// 保护条高 25.398 mm（= 75.15 个模块），差 <strong>6.01 个模块</strong>——
    /// 也就是「数据条 69 模块 + 保护条再加 6 模块」，与 GS1 对 EAN-13 的条高/X 比（约 69）对得上。
    /// 用户要的就是「和 BARCODE WIZARD 一样」，所以这个数照实测抄，不照记忆写。</para>
    /// </summary>
    public const int GuardExtensionModules = 6;

    /// <summary>
    /// 条码框的纵向分份——照 Corel BARCODE WIZARD 实测的<strong>整数比</strong>。
    /// <para>样本（<c>labelgou-CL\条码\图形1.svg</c>，框高 27.43 mm）里：上边距 0.331 mm、
    /// 数据条 23.366 mm、保护条多出来的那截 2.032 mm、可读数字带 1.701 mm。
    /// 把框高按 81 份切，这四段正好是 <strong>1 / 69 / 6 / 5</strong> 份，一位不差。</para>
    /// <para>用户 2026-09-11 要「和 CDR 一模一样」，所以纵向就按这四份分，
    /// 不再用「可读数字带占 22%」那套经验值（那套在框高不是 81X 时会把条拉扁）。</para>
    /// </summary>
    public const int HeightUnits = 81;

    /// <summary>
    /// CDR 在「条形码高度(H) = 1.0」时，<strong>数据条高合几个窄元素宽</strong>——老模板封顶用的就是它。
    /// <para>实测自用户导出的那张样本 <c>labelgou-CL\条码\图形1.svg</c>：X = 0.33795 mm、
    /// 数据条 23.366 mm → 23.366 ÷ 0.33795 = <strong>69.14</strong>（同一张样本里保护条全长 25.398 ÷ X = 75.15、
    /// 整框 27.43 ÷ X = 81.16，三处都是量的不是凑的）。</para>
    /// <para><strong>为什么按数据条封顶、不按条区那 75.15</strong>：我们的可读数字带有 2.5 mm 的可读下限
    /// （<see cref="MinTextBandMm"/>，第五轮定的——扁框里 5/81 只剩 0.86 mm 连字都放不下），
    /// 所以条区比 CDR 的少一截；按 75.15 封顶会把 CDR 自己那只等比框（38.1 × 27.43）也误伤，
    /// 那条逐毫米钉子当场红给我看了。按 69.14 封顶则只咬"真被压扁"的框。</para>
    /// </summary>
    public const double CdrDataBarUnits = 69.14;

    /// <summary>条顶到框顶占几份（框高的 1/81）。</summary>
    public const int TopMarginUnits = 1;

    /// <summary>可读数字带占几份（框底往上那一条）。</summary>
    public const int TextBandUnits = 5;

    /// <summary>保护条比数据条多出来的那截占几份——与 <see cref="GuardExtensionModules"/> 是同一个数，
    /// 只是那边按模块算、这边按框高算（框高恰为 81X 时两者相等，正是 CDR 的情况）。</summary>
    public const int GuardExtensionUnits = 6;

    /// <summary>
    /// 可读数字的字号是几个模块大。
    /// <para>CDR 样本里字号 2.4382 mm、模块宽 0.33795 mm → <strong>7.215</strong>。
    /// 也就是说数字是跟着条码一起缩放的：条码拉大，字也变大——这跟「字号写死 8pt」是两回事，
    /// 也是「一模一样」里容易被忽略的一处。</para>
    /// </summary>
    public const double HriFontSizeModules = 7.215;

    /// <summary>
    /// 可读数字带从<strong>数据条底</strong>往下让开几个模块再起。
    /// <para>CDR 样本里数字顶距数据条底 3.175 个模块（字底落在数据条底下方 8.34 个模块处）。
    /// 不让这一段，字会紧贴条底——样本里不是那样，并排一看就分得出。</para>
    /// </summary>
    public const double HriTopOffsetModules = 3.0;

    /// <summary>
    /// 模块宽度下限（毫米）。0.20 mm ≈ 8 mil，是常见手持枪能稳读的底线；
    /// 比这还窄就<strong>照样画但报一句警告</strong>，不偷偷改成能扫的宽度（那会把条码推出元素框外）。
    /// </summary>
    public const double MinModuleMm = 0.20;

    /// <summary>
    /// 保护条最多能占掉条区高的比例。
    /// <para>第五轮从 0.4 收到 <strong>0.08 = 6/75</strong>：按 CDR 的比例，延长量是数据条的 6/69，
    /// 换算成「占条区（保护条全长）的比例」就是 6/75。之前拿 0.4 当防呆上限根本防不住——
    /// 用户画了个 132 × 14 的扁框、模块宽被撑到 1.33 mm 时，6 个模块就是 8 mm，
    /// 数据条直接被压剩一半，截图里那条「被大象踩过」的码就是这么来的。</para>
    /// </summary>
    private const double MaxGuardShareOfBars = 0.08;

    /// <summary>
    /// 可读数字带的最小高度（毫米）。
    /// <para>CDR 的「框高 5/81」只在框高等比时成立；用户自己画的框常常又宽又扁
    /// （132 × 14 mm），5/81 只剩 0.86 mm——字会被压成蚂蚁再叠到条上，同一个截图的教训。
    /// 框越扁，越要靠这条下限保住数字的可读性。</para>
    /// </summary>
    public const double MinTextBandMm = 2.5;

    /// <summary>
    /// 模块宽度取整到的粒度（毫米）。
    /// <para>第 41 棒从 0.01 一路收到 0.0001。理由：CDR 那张样本的模块宽是 0.33795 mm（不是整数倍），
    /// 取整到 0.01 会把它压成 0.33，整条条区比 CDR 窄 2.4%；取到 0.001 剩 0.09 mm 的偏差，
    /// 逐根比下来末根错开 0.17 mm——用户要「一模一样」，这一档才把偏差压到 0.02 mm 以内
    /// （已经小于 CDR 自己导出时 0.001 英寸的坐标量化噪声）。
    /// 取整本身照旧要做：浮点误差摊在 250 根条上会攒出肉眼可见的缝。</para>
    /// </summary>
    private const double ModuleStepMm = 0.0001;

    /// <summary>
    /// 按给定的框（含静区）铺条。
    /// </summary>
    /// <param name="encoding">编码结果（必须 Ok）。</param>
    /// <param name="x">元素框左（毫米）。</param>
    /// <param name="y">元素框顶（毫米）。</param>
    /// <param name="width">元素框宽（毫米，静区算在里面）。</param>
    /// <param name="barsY">条的顶（毫米，可读文字在下面时这里就是文字上方的那条线）。</param>
    /// <param name="barsHeight">条区的总高（毫米）。有保护条时这一格由保护条吃满，数据条相应矮一截。</param>
    /// <param name="sizing">CDR 口径的尺寸参数（第 58 棒）。<strong>给了它，位宽就来自 X 尺寸而不是框宽</strong>——
    /// 框再怎么拉也拉不肥条，符号比框窄时整组居中；null＝照旧按框宽算（老模板输出逐字不变）。</param>
    public static BarcodeGeometry Build(BarcodeEncoding encoding, double x, double y, double width,
        double barsY, double barsHeight, BarcodeSizing? sizing = null)
    {
        if (encoding is not { Ok: true })
            return new BarcodeGeometry(Array.Empty<BarStrip>(), 0, barsY, barsHeight, 0, encoding?.Error ?? "条码没编出来。");
        if (width <= 0 || barsHeight <= 0)
            return new BarcodeGeometry(Array.Empty<BarStrip>(), 0, barsY, barsHeight, 0, "条码框的宽或高是 0，画不出来。");

        // 静区：能用制式声明的毫米值就用它（CDR 版式：多数制式固定 3.0 mm），没有才按模块数算。
        // 按模块数算时 QuietZoneModules 说的是<strong>窄元素数</strong>——宽窄比制的码一个窄元素
        // = NarrowBits 个位（见 BarcodeEncoding.NarrowUnits）。不乘这个系数，2.5:1 的码静区会只剩一半。
        var quietIsMm = encoding.QuietZoneMm > 0;
        var quietMm = quietIsMm ? encoding.QuietZoneMm : 0;
        var quietModules = quietIsMm ? 0 : encoding.QuietZoneModules * encoding.NarrowBits;
        var barsWidth = quietIsMm ? width - quietMm * 2 : width;
        var totalModules = encoding.Modules + quietModules * 2;
        // 往下取整到 0.01mm：宁可留一点白，也不要超出框（超出就裁掉了，裁掉的可能是最后一根条）。
        // 这里算出来的是「一位」的宽；对外报的 ModuleMm 是「最窄那根线」的宽（宽窄比制里窄 = 2 位）。
        var bit = Math.Floor(barsWidth / totalModules / ModuleStepMm) * ModuleStepMm;
        var frameBit = bit;
        var centerOffset = 0.0;
        string? sizingWarning = null;
        if (sizing is { ModuleMm: > 0 } size && bit > 0)
        {
            // 第 58 棒的主修：X 尺寸照 CDR 由「打印机分辨率 × 缩放比例」算（整像素量化），**框宽不再参与条的胖瘦**。
            // 从前只按框宽算，用户把框画成 92 × 12 mm 就把 9 位码撑成 0.6 mm 粗杠——那就是他报的"扁"。
            var xBit = size.ModuleMm / encoding.NarrowBits;
            var used = encoding.Modules * xBit + (quietIsMm ? quietMm * 2 : quietModules * xBit * 2);
            if (used <= width + 1e-9)
            {
                bit = xBit;
                centerOffset = (width - used) / 2;      // 居中挪的是整组条，静区仍是制式那个数（见 CenterOffsetMm）
            }
            else
            {
                sizingWarning = $"这只框（{width:0.#} mm）比缩放比例要求的符号还窄：已按框宽把 X 从 " +
                                $"{size.ModuleMm:0.###} mm 收到 {frameBit * encoding.NarrowBits:0.###} mm。" +
                                "要照参数画就把框拉宽，或把缩放比例调小。";
            }
        }
        var module = bit * encoding.NarrowBits;
        var heightBound = false;
        if (sizing is null && module > barsHeight / CdrDataBarUnits)
        {
            // 老模板（没有尺寸参数那四格）这一路：从前只按框宽算，框多扁条就多粗——用户拿截图点名要的"别再把条码压扁"。
            // 上限照 CDR 在 H=1.0 时量出来的数据条比例（见 CdrDataBarUnits 的出处），再扁也不许比 CDR 自己画的更粗。
            bit = Math.Floor(barsHeight / CdrDataBarUnits / encoding.NarrowBits / ModuleStepMm) * ModuleStepMm;
            module = bit * encoding.NarrowBits;
            heightBound = true;
            centerOffset = Math.Max(0, (width - (encoding.Modules * bit + (quietIsMm ? quietMm * 2 : quietModules * bit * 2))) / 2);
        }
        var quiet = quietIsMm ? quietMm : quietModules * bit;
        // 向导那格「条形码宽度减少值」：出纸时每根黑条两侧各让出半个像素，补偿喷墨/热转印的网点增益。
        // 位置与空白都不动，只把条画窄——所以它必须真的参与画，不然那一格就是个骗人的旋钮。
        var reductionMm = sizing is null || sizing.Dpi <= 0 ? 0 : sizing.WidthReductionPx / sizing.Dpi * 25.4;
        if (sizing is not null && reductionMm >= module && module > 0)
        {
            sizingWarning = (sizingWarning is { Length: > 0 } sw ? sw + " " : "") +
                            $"宽度减少值 {sizing!.WidthReductionPx:0.##} 像素已经不小于一个窄元素（{module:0.###} mm）：条会被画没了，这里按不减少画。请把它调小。";
            reductionMm = 0;
        }
        string? warning = sizingWarning;
        if (bit <= 0)
        {
            return new BarcodeGeometry(Array.Empty<BarStrip>(), 0, barsY, barsHeight, 0,
                $"这个框（{width:0.#} mm 宽）装不下 {encoding.Modules} 个模块的码：请把条码拉宽，或换更短的列/更密的制式。");
        }
        if (module < MinModuleMm)
        {
            // 判「扫不扫得出」看的是窄线宽（module），不是位宽（bit）——宽窄比制一位只有半根窄线。
            var tooNarrow = heightBound
                ? $"这只框太矮，条已按 CDR 的比例（数据条高 = 窄元素宽的 {CdrDataBarUnits:0.##} 倍）缩到 {module:0.00} mm，" +
                  $"低于常用底线 {MinModuleMm:0.00} mm：手持枪很可能扫不出来。把条码框拉高或拉窄一点，或改选更短的列。"
                : $"最窄的线只有 {module:0.00} mm（低于常用底线 {MinModuleMm:0.00} mm）：这么密的码手持枪很可能扫不出来，" +
                  $"建议把框拉宽到 {(int)Math.Ceiling(encoding.Modules / (double)encoding.NarrowBits * MinModuleMm) + quiet * 2:0} mm 以上，或改选更短的列。";
            warning = warning is { Length: > 0 } ? warning + " " + tooNarrow : tooNarrow;
        }

        // 保护条从条区总高里"往下伸"：顶线与数据条齐平，底线更低——这与 CDR 样本里
        // 「两者顶都在 136.592、保护条底到 161.99、数据条底在 159.958」完全一致。
        var guards = encoding.GuardRanges;
        var hasGuard = guards is { Count: > 0 };
        var guardExtension = hasGuard
            ? Math.Min(GuardExtensionModules * module, barsHeight * MaxGuardShareOfBars)
            : 0;
        // 保护条吃满整个条区；没有保护条的制式 guardExtension 恒为 0，两个值自然相等。
        var guardBarsHeight = barsHeight;
        var dataBarsHeight = barsHeight - guardExtension;

        var bars = new List<BarStrip>();
        var cursor = x + quiet + centerOffset;
        var run = 0;
        var runStart = 0;
        for (var i = 0; i < encoding.Bits.Length; i++)
        {
            if (encoding.Bits[i] == '1')
            {
                if (run == 0) runStart = i;
                run++;
                continue;
            }
            if (run > 0)
            {
                bars.Add(new BarStrip(cursor, Math.Max(0, run * bit - reductionMm), IsGuardRun(runStart, guards)));
                cursor += run * bit;
                run = 0;
            }
            cursor += bit;
        }
        if (run > 0) bars.Add(new BarStrip(cursor, Math.Max(0, run * bit - reductionMm), IsGuardRun(runStart, guards)));

        return new BarcodeGeometry(bars, module, barsY, dataBarsHeight, quiet, warning, guardBarsHeight, centerOffset);
    }

    /// <summary>这根条的起点是否落在某个保护条区间里（条与空是交替的，落区间内的那几根就是保护条）。</summary>
    private static bool IsGuardRun(int runStartModule, IReadOnlyList<GuardRange>? guards)
    {
        if (guards is not { Count: > 0 }) return false;
        foreach (var g in guards)
        {
            if (runStartModule >= g.StartModule && runStartModule < g.StartModule + g.Length) return true;
        }
        return false;
    }

    /// <summary>
    /// 把 UPC/EAN 的可读数字按规范铺成「一格一个字」（毫米）。
    /// <para>没有 <see cref="BarcodeEncoding.HriSegments"/> 的制式（Code 128 / 39 / ITF）返回空表，
    /// 那种码的可读行就是整串居中，渲染端走原来的路。</para>
    /// </summary>
    /// <param name="encoding">编码结果。</param>
    /// <param name="x">元素框左（毫米）。</param>
    /// <param name="width">元素框宽（毫米）——右侧静区的位置要它。</param>
    /// <param name="geometry">同一份编码铺出来的落位结果（要它的模块宽与静区宽）。</param>
    public static IReadOnlyList<HriGlyph> BuildHri(BarcodeEncoding encoding, double x, double width, BarcodeGeometry geometry)
    {
        if (encoding.HriSegments is not { Count: > 0 } || geometry.ModuleMm <= 0) return Array.Empty<HriGlyph>();

        var module = geometry.ModuleMm;
        var quiet = geometry.QuietZoneMm;
        var offset = geometry.CenterOffsetMm;
        var barsOrigin = x + quiet + offset;
        var glyphs = new List<HriGlyph>();
        foreach (var seg in encoding.HriSegments)
        {
            if (seg.Text.Length == 0) continue;

            // 骑静区的那几位（EAN-13 的首位、UPC 的数字系统位与校验位）：静区是固定毫米，
            // 位置直接按框的左右边界算，不走模块坐标。整组条居中时基准跟着一起挪——
            // 不然这个数字会落到那截留白的正中，离自己的条十万八千里。
            if (seg.Anchor != HriAnchor.Bars)
            {
                var origin = seg.Anchor == HriAnchor.LeftQuiet ? x + offset : x + width - offset - quiet;
                var cell = quiet / seg.Text.Length;
                for (var i = 0; i < seg.Text.Length; i++)
                    glyphs.Add(new HriGlyph(seg.Text[i], origin + i * cell, cell));
                continue;
            }

            // 压在条上的那些段：一格一个字符。有按位分格的就照它（UPC/EAN 数据段是每位 7 模块），
            // 没有的就把整段平分。这样渲染端永远只面对「一格一个字」，不必知道分段规矩。
            var perCharModules = seg.PerCharModules > 0
                ? (double)seg.PerCharModules
                : (double)seg.ModuleSpan / seg.Text.Length;
            var segStart = barsOrigin + seg.StartModule * module;
            for (var i = 0; i < seg.Text.Length; i++)
                glyphs.Add(new HriGlyph(seg.Text[i], segStart + i * perCharModules * module, perCharModules * module));
        }
        return glyphs;
    }
}
