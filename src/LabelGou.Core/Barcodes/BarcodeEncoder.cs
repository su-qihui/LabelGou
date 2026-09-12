using System.Text;

namespace LabelGou.Core.Barcodes;

/// <summary>
/// 保护条（guard bar）在模块序列里占据的一段。
/// <para>UPC/EAN 一族的码，两端与正中有几根条按规范要<strong>比数据条高出一截</strong>——
/// 扫码枪靠它们找条码的边界和中点。用户导出的 CDR 样本（<c>labelgou-CL\条码\图形1.svg</c>）
/// 里这段差是 6 个模块，本文件把实测值写进 <see cref="BarcodeBars.GuardExtensionModules"/>。</para>
/// </summary>
/// <param name="StartModule">这段从码的第几个模块开始（0 = 码的第一根条）。</param>
/// <param name="Length">这段有几个模块。</param>
public readonly record struct GuardRange(int StartModule, int Length);

/// <summary>
/// 这一段可读数字骑在哪块地方。
/// <para>UPC/EAN 的首位（和 UPC 的校验位）是印在静区那块白上的，不压条；
/// 它不能按模块坐标算——静区是固定毫米（<see cref="BarcodeEncoding.QuietZoneMm"/>），
/// 不是固定模块数，所以得单独说清骑在哪边。</para>
/// </summary>
public enum HriAnchor
{
    /// <summary>压在条上：位置 = 条区左 + <see cref="HriSegment.StartModule"/> × 模块宽。</summary>
    Bars = 0,

    /// <summary>骑在左静区里（EAN-13 / UPC 的首位、UPC-E 的数字系统位）。</summary>
    LeftQuiet = 1,

    /// <summary>骑在右静区里（UPC-A / UPC-E 的校验位）。</summary>
    RightQuiet = 2,
}

/// <summary>
/// 可读数字（HRI）里<strong>一段</strong>的排版要求。
/// <para>UPC/EAN 的数字不是整串居中，而是按规范分成几段：首位骑在左静区、
/// 中间两段各自铺满自己的 7 模块格（每 7 个模块一个字）、校验位骑在右静区。
/// 用户 2026-09-11 点名要的「和 BARCODE WIZARD 一模一样」，看的就是这个分段。</para>
/// </summary>
/// <param name="Text">这一段要印的字符。</param>
/// <param name="StartModule">这一段压在条上时的起点（相对条区第一根条的第几个模块）。
/// 骑静区的那两段（<paramref name="Anchor"/> 不是 <see cref="HriAnchor.Bars"/>）忽略它。</param>
/// <param name="ModuleSpan">压在条上时占几个模块宽；骑静区的段忽略它（宽度由静区本身给）。</param>
/// <param name="PerCharModules">每个字符独占几个模块（0 = 整段居中，不按位分格）；
/// UPC/EAN 的数据段一律是 7——一个数字正好压在自己的 7 模块上。</param>
/// <param name="Anchor">骑在条上还是骑在某一侧的静区里。</param>
public sealed record HriSegment(
    string Text,
    int StartModule,
    int ModuleSpan,
    int PerCharModules = 0,
    HriAnchor Anchor = HriAnchor.Bars);

/// <summary>
/// 一次条码编码的结果（纯数据，不碰像素也不碰毫米）。
/// </summary>
/// <param name="Ok">能不能编。</param>
/// <param name="Bits">条与空的模块序列：<c>'1'</c> = 黑条、<c>'0'</c> = 白空，<strong>不含静区</strong>（静区由几何层加）。</param>
/// <param name="Data">真正编进去的那一串：可能与传进来的不同（自动补了校验位、Code 39 把小写洗成大写），
/// <see cref="Note"/> 会说清差在哪。</param>
/// <param name="Note">自动替用户做的那件事（补校验位 / 转大写）。不静默改印刷数据是本项目红线，改了必须说。</param>
/// <param name="Error">编不出来的原因 + 怎么改。界面拿它直接显示，不做二次加工。</param>
/// <param name="QuietZoneModules">按<strong>模块数</strong>算的左右静区（GS1 的规范下限）：EAN-13 是 11、其余 10。
/// 只在 <paramref name="QuietZoneMm"/> 为 0 时生效。</param>
/// <param name="GuardRanges">保护条占的模块区间（只有 UPC/EAN 一族有）。空 = 这一族没有保护条。</param>
/// <param name="HriSegments">可读数字的分段排版（只有 UPC/EAN 一族有）。空 = 整串居中即可。</param>
/// <param name="QuietZoneMm">按<strong>毫米</strong>算的左右静区；&gt; 0 时优先于 <paramref name="QuietZoneModules"/>。
/// <para>这是 Corel BARCODE WIZARD 的口径：它不按模块数留静区，而是左右各固定 3.0 mm
/// （从用户导出的 <c>labelgou-CL\条码\图形1.svg</c> 反解：38.1 mm 外框里条区 32.1056 mm，
/// 两侧各留约 2.99 mm）。用户 2026-09-11 要「和 CDR 一模一样」，所以 UPC/EAN 一族走这个值。</para>
/// <para><strong>代价说清楚</strong>：X = 0.338 mm 时 3.0 mm 只有约 8.9 个模块，低于 GS1 对 EAN-13 的
/// 11 模块下限。要回到合规口径，把这个字段置 0 即可（<see cref="QuietZoneModules"/> 就接上了）。</para></param>
/// <param name="NarrowUnits"><strong>最窄那根线</strong>在 <see cref="Bits"/> 里占几个位。
/// <para>EAN/UPC/Code 128 一族「一位就是一个模块」，这里是 1。宽窄比制的码
/// （Code 39 / Codabar / ITF / 25 码）要表达 CDR 实测的 <strong>2.5 : 1</strong>，
/// 而位串只能是整数，所以统一按<strong>窄 = 2 位、宽 = 5 位</strong>铺（2.5 : 1 = 5 : 2），这里是 2。</para>
/// <para><strong>为什么必须带这个数</strong>：几何层拿位宽判「窄到扫不出」，也拿它把静区的
/// 「模块数」换成毫米。不知道一位等于几个窄元素，这两个判断都会差一倍——
/// 警告会在还扫得动的时候就报，静区会只留一半。</para></param>
public sealed record BarcodeEncoding(
    bool Ok,
    string Bits,
    string Data,
    string? Note,
    string? Error,
    int QuietZoneModules = BarcodeBars.QuietZoneModules,
    IReadOnlyList<GuardRange>? GuardRanges = null,
    IReadOnlyList<HriSegment>? HriSegments = null,
    double QuietZoneMm = 0,
    int NarrowUnits = 1)
{
    /// <summary>模块数（= 位数）。几何层按它决定一根模块多宽。</summary>
    public int Modules => Bits.Length;

    /// <summary>最窄那根线有几个位宽。「扫不扫得出」要按它判，不能按一位判。</summary>
    public int NarrowBits => NarrowUnits < 1 ? 1 : NarrowUnits;
}

/// <summary>
/// 一维码编码器：把一串数据编成「条/空模块序列」。<strong>零依赖</strong>（本项目连 PDF 都是自写的，§七）。
/// <para>
/// <strong>码表来源与取证口径</strong>：Code 128 的 107 条宽度表、Code 39 的 43 条、EAN 的 L/G 与
/// 首位奇偶表、ITF 的宽窄表，全部逐字从本机已克隆的 <c>labelgou-other\Github\08-core-libs\ZXing.Net\Source\lib\oned\</c>
/// （Apache-2.0）里抽出来，<strong>只抄表不引依赖</strong>；抽取脚本 <c>_probe\b17-barcode\extract_tables.py</c>。
/// 抽完还对了公开规范里那几个锚点值：Code 128 的 Start A/B/C = 211412 / 211214 / 211232、Stop = 2331112、
/// 每个符号 11 模块、EAN-13 整码 95 模块。条码错一位就是印出一张扫不出、或扫出来是别的数的码，
/// 那是本项目红线里最重的一类（看着正常但印错货），所以凭记忆写表这条路直接封死。
/// </para>
/// <para>
/// <strong>第 41 棒（2026-09-11）补的三个 UPC/EAN 成员</strong>（EAN-8 / UPC-A / UPC-E）走的是同一条路：
/// 结构照 <c>EAN8Writer.cs</c> / <c>UPCAWriter.cs</c> / <c>UPCEWriter.cs</c>，
/// L/G 复用 EAN-13 已有的那张（<c>UPCEANReader.L_PATTERNS</c> 与这里的 <see cref="EanLeft"/> 逐项相同），
/// UPC-E 的奇偶表与压缩规则照 <c>UPCEReader.NUMSYS_AND_CHECK_DIGIT_PATTERNS</c> 与
/// <c>convertUPCEtoUPCA</c>。<strong>没有引 ZXing 的依赖，只是把表抄过来</strong>。
/// </para>
/// <para>
/// <strong>编不出来就报错，不静默改数据</strong>：中文、奇数位、校验位不对——一律返回 <see cref="BarcodeEncoding.Error"/>，
/// 让界面挡住它。唯一会替用户动数据的情形是「补一个规范要求的校验位」与「Code 39 把小写转大写」，
/// 两者都会写进 <see cref="BarcodeEncoding.Note"/> 让界面显示出来。
/// </para>
/// </summary>
public static class BarcodeEncoder
{
    /// <summary>单条数据允许的最长字符数（Code 39/ITF 的常规上限，也是防呆：一栏粘进整段备注就该被看见）。</summary>
    public const int MaxDataLength = 80;

    /// <summary>EAN-13 / UPC-A 的静区模块数（GS1 对 13 位码的要求）。</summary>
    public const int Gtin13QuietZoneModules = 11;

    /// <summary>EAN-8 的静区模块数（GS1 对 8 位码的要求，比 13 位码窄）。</summary>
    public const int Gtin8QuietZoneModules = 7;

    /// <summary>UPC-A / UPC-E 的静区模块数（GS1 对北美码的要求）。</summary>
    public const int UpcQuietZoneModules = 9;

    /// <summary>
    /// CDR 版式的固定静区（毫米）：Corel BARCODE WIZARD 对 UPC/EAN 一族一律左右各留这么多，
    /// 不按模块数。见 <see cref="BarcodeEncoding.QuietZoneMm"/> 的取证与代价说明。
    /// </summary>
    public const double EanUpcQuietZoneMm = 3.0;

    /// <summary>
    /// 宽窄比制（Code 39 / Codabar / ITF / 25 码）里<strong>宽元素</strong>占几个位。
    /// <para><strong>这个 5 是量出来的，不是选出来的</strong>：拿用户那份 Corel BARCODE WIZARD 样张
    /// <c>labelgou-CL\条码\1234567891231.svg</c> 逐框测，四个宽窄比制的码窄元素 0.3387 mm、
    /// 宽元素 0.8467 mm，比值 <strong>2.500</strong>（最小二乘拟合 rms 0.008 mm，等于 CDR 自己
    /// 导出时 1/100 英寸的坐标量化噪声）。2.5 : 1 写不成整数位串，能表达它的最小整数对就是 5 : 2。</para>
    /// <para>原来用的是 2 : 1（Code 39 / Codabar）与 3 : 1（ITF），都在规范允许的 2:1~3:1 区间里、
    /// 都扫得出，但和 CDR 排出来的<strong>条宽分布不一样</strong>——并排放一眼就看得出宽窄不同。
    /// 用户要的是「和 CDR 效果几乎完全适配」，所以统一到它实测的那一档。</para>
    /// </summary>
    public const int WideRatioUnits = 5;

    /// <summary>宽窄比制里<strong>窄元素</strong>占几个位。与 <see cref="WideRatioUnits"/> 成 2.5 : 1。</summary>
    public const int NarrowRatioUnits = 2;

    /// <summary>
    /// CDR 版式对<strong>除储运箱码外所有制式</strong>一律左右各留这么多毫米的静区（Corel BARCODE WIZARD 实测）。
    /// <para>逐框量 <c>labelgou-CL\条码\1234567891231.svg</c>：EAN-13 / CodaBar / code25 / code39 /
    /// code128 / EAN-8 / ITF 七框的左静区都是 2.946 mm、右 3.047 mm（合计 ≈ 6 mm，取整每侧 3.0）。</para>
    /// <para>与 <see cref="EanUpcQuietZoneMm"/> 数值相同但语义分开：那个是「UPC/EAN 一族」的历史口径，
    /// 这个是「其余全部制式」——分开写，改一处不会误伤另一族的取证注释。</para>
    /// <para><strong>代价（与 UPC/EAN 同）</strong>：3.0 mm 不一定够 GS1 的「10 个窄元素」下限，
    /// 尤其框画得宽、模块被撑大时。要回到 GS1 合规口径，把制式返回里的 <c>QuietZoneMm</c> 置 0，
    /// 就退回按 <see cref="BarcodeEncoding.QuietZoneModules"/> × 窄元素数算。</para>
    /// </summary>
    public const double CdrQuietZoneMm = 3.0;

    /// <summary>
    /// 储运箱码（ITF-14）的静区——<strong>比一般制式宽得多</strong>，别和 <see cref="CdrQuietZoneMm"/> 混用。
    /// <para>同那份样张里 ITF-14 那框实测左 7.180 / 右 7.217 mm（其余框是 2.9 mm）。
    /// ITF-14 是仓库/托盘上远距离扫的箱码，GS1 也要求它比零售码留更宽的空白区，两边对得上。</para>
    /// </summary>
    public const double BoxQuietZoneMm = 7.2;

    /// <summary>EAN/UPC/GTIN 通用的模 10 校验位算法（对不含校验位的那一串数字求）。</summary>
    /// <returns>校验位 0~9；传进来的串含非数字时返回 null。</returns>
    public static int? GtinCheckDigit(string digitsWithoutCheck)
    {
        if (string.IsNullOrEmpty(digitsWithoutCheck)) return null;
        var sum = 0;
        // 从右往左：奇数位（第 1、3、5…个）×3，偶数位 ×1。与 GS1 文档里「乘 3 的位置」口径一致。
        for (var i = digitsWithoutCheck.Length - 1; i >= 0; i -= 2)
        {
            if (!IsDigit(digitsWithoutCheck[i], out var d)) return null;
            sum += d;
        }
        sum *= 3;
        for (var i = digitsWithoutCheck.Length - 2; i >= 0; i -= 2)
        {
            if (!IsDigit(digitsWithoutCheck[i], out var d)) return null;
            sum += d;
        }
        // (10 - 余数) % 10:求和超 1000 时 C# 的 % 会吐负数,校验位必须是 0~9(公共 API,第 23 棒)
        return (10 - sum % 10) % 10;
    }

    private static bool IsDigit(char c, out int value)
    {
        value = c - '0';
        return value >= 0 && value <= 9;
    }

    private static bool AllDigits(string text, out char bad)
    {
        foreach (var c in text)
        {
            if (!IsDigit(c, out _))
            {
                bad = c;
                return false;
            }
        }
        bad = '\0';
        return true;
    }

    /// <summary>把一串数据按指定制式编成模块序列。</summary>
    public static BarcodeEncoding Encode(string? raw, BarcodeSymbology symbology)
    {
        var text = (raw ?? string.Empty).Trim();
        // 首尾空白是表格单元格里最常见的残留，带着它编出来的码与不带是两个码；去掉比留着好，但不能删了不说。
        var trimNote = text.Length > 0 && raw != text
            ? "数据首尾的空白已去掉（表格里常见的单元格残留）"
            : null;
        if (text.Length == 0)
            return Fail(string.Empty, "条码数据是空的：这一格没绑列，或那一列这一行没值。回到 ② 步把条码要的那一列连上。");
        if (text.Length > MaxDataLength)
            return Fail(text, $"条码数据 {text.Length} 个字符，超过这一制式常用的 {MaxDataLength} 上限：多半是那一列粘了整段备注而不是一串编号。");

        var result = symbology switch
        {
            BarcodeSymbology.Code128 => EncodeCode128(text),
            BarcodeSymbology.Code39 => EncodeCode39(text),
            BarcodeSymbology.Ean13 => EncodeEan13(text),
            BarcodeSymbology.Ean8 => EncodeEan8(text),
            BarcodeSymbology.UpcA => EncodeUpcA(text),
            BarcodeSymbology.UpcE => EncodeUpcE(text),
            BarcodeSymbology.Codabar => EncodeCodabar(text),
            BarcodeSymbology.Msi => EncodeMsi(text),
            BarcodeSymbology.Itf => EncodeItf(text),
            BarcodeSymbology.Jan8 => EncodeEan8(text),
            BarcodeSymbology.Jan13 => EncodeEan13(text),
            BarcodeSymbology.Isbn => EncodePrefixedEan(text, "978", "ISBN"),
            BarcodeSymbology.Issn => EncodePrefixedEan(text, "977", "ISSN"),
            BarcodeSymbology.Itf14 => EncodeItf14(text),
            BarcodeSymbology.Code25 => EncodeCode25(text),
            _ => Fail(text, $"不认识的条码制式：{symbology}。"),
        };
        if (trimNote is not null && result.Ok)
            result = result with { Note = JoinNote(result.Note, trimNote) };
        return result;
    }

    private static string JoinNote(string? first, string second)
        => first is { Length: > 0 } ? first + "；" + second : second;

    // ---------- Code 128 ----------

    /// <summary>
    /// Code 128：每个符号 6 个元素（3 条 3 空）共 11 模块，末位 Stop 是 7 个元素 13 模块。
    /// <para><strong>档位选择照 ZXing 的前瞻式（lookahead）走，逐位换档</strong>——
    /// 不是「整串要么全 C、要么全 B」。<see cref="FindDigitRun"/> 看当前位置能凑几个数字，
    /// <see cref="ChooseCodeSet"/> 据此定档：开头连续两个数字就起 C 档；已在 B 档且后面还有
    /// 足够数字才值得切 C；<strong>落单的末位数字切 B 档编</strong>（C 档必须两位一组）。</para>
    /// <para><strong>为什么非要改这一处</strong>（第 41 棒的原口径是「奇数位整串退 B 档，不做中途换档」）：
    /// 用户拿 Corel BARCODE WIZARD 导出的样张 <c>labelgou-CL\条码\1234567891231.svg</c> 实测——
    /// 那串 13 位数字（奇数位）在 CDR 里是 <c>StartC + 12,34,56,78,91,23 + CodeB + '1'</c>；
    /// 旧口径整串 B 档要 <strong>178 模块</strong>，新口径 <strong>123 模块</strong>，旧的宽出 <strong>45%</strong>。
    /// 同一串数据在两个软件里条数都不一样，并排一比就穿帮。
    /// 而 ZXing 的 <c>Code128Writer.chooseCode</c> 对「落单末位」正是切 B 档、其余压 C 档，
    /// 与 CDR <strong>完全一致</strong>——所以「对齐 ZXing」和「对齐 CDR」在这里是同一件事，不是取舍。</para>
    /// <para><strong>CDR 那多出来的 11 模块故意不抄</strong>：它的符号序列里第二个是 FNC1（值 102），
    /// 那是 GS1-128 的应用标识符前缀，扫码枪会把它读成「这是 GS1 数据」，
    /// 解出来的串和表里那串就不是一回事了。校验位也跟着变（带 FNC1 算是 32，不带是 34）——
    /// 两边都自洽，但只有不带 FNC1 的那份扫出来才是用户表里的原始数字。</para>
    /// </summary>
    private static BarcodeEncoding EncodeCode128(string text)
    {
        var symbols = new List<int>();      // 符号值序列，第一个是起始符
        var codeSet = 0;                    // 0 = 还没定档；其余见 CodeA/CodeB/CodeC
        var pos = 0;

        while (pos < text.Length)
        {
            var newCodeSet = ChooseCodeSet(text, pos, codeSet);

            if (newCodeSet != codeSet)
            {
                // 要换档（或第一次定档）：本轮只写起始符 / 切换符，不吃数据
                symbols.Add(codeSet == 0
                    ? newCodeSet switch { CodeA => StartA, CodeB => StartB, _ => StartC }
                    : newCodeSet);
                codeSet = newCodeSet;
                continue;
            }

            if (codeSet == CodeC)
            {
                // C 档一次吃两位。走到这里 ChooseCodeSet 已保证还剩两位数字，
                // 万一没有就是本方法自己的档位判断出了错——报出来，不静默编半截。
                if (pos + 1 >= text.Length || !IsDigit(text[pos], out var hi) || !IsDigit(text[pos + 1], out var lo))
                    return Fail(text, "Code 128 的 C 档要两位一组，末尾却只剩一个数字。这是编码器内部没对齐，请把这一串数据反馈回来。");
                symbols.Add(hi * 10 + lo);
                pos += 2;
            }
            else
            {
                var c = text[pos];
                if (c < 32 || c > 126)
                    return Fail(text, $"Code 128 装不下字符「{c}」（U+{(int)c:X4}）：Code B 只能编 ASCII 32~126。" +
                                      "把这一栏换成表里的英文货号/编号列，或先在那一列里把中文去掉——软件不替你猜该删哪几个字。");
                symbols.Add(c - 32);
                pos++;
            }
        }

        if (symbols.Count == 0)
            return Fail(text, "Code 128 数据是空的，一个符号都编不出来。");

        // 校验符 = (起始符×1 + 第 i 个符号×i) mod 103，也就是起始符与第一个数据符同权重 1、之后 2、3…
        // <para><strong>这个口径不是凭记忆写的</strong>：早先按「起始 1、数据从 2 起」算（网上两种说法都有），
        // 拿 ZXing 的 reader 当裁判一验：那种算法下所有码都被判成校验不过（只有数据全 0 的能过），
        // 而 ZXing 自己按 1、1、2、3… 算的码全部能读回。见 <c>_probe\b17-barcode\probe128</c>。
        // 校验位错一档 = 印出去的箱唛扫不上，这属本项目最重的红线。
        // 换档以后 symbols[0] 仍是起始符（切换符排在它后面），所以这条权重式一个字都不用改。</para>
        var sum = 0;
        for (var i = 0; i < symbols.Count; i++) sum += symbols[i] * Math.Max(1, i);
        var check = sum % 103;

        var bits = new StringBuilder();
        foreach (var s in symbols) AppendWidths(bits, Code128Patterns[s]);
        AppendWidths(bits, Code128Patterns[check]);
        AppendWidths(bits, Code128Patterns[Stop]);
        // Code 128 不属宽窄比族：窄线 = 1 位，NarrowUnits 用默认 1。静区照 CDR 那框实测（左 2.946 / 右 3.047）。
        return new BarcodeEncoding(true, bits.ToString(), text, null, null,
            QuietZoneMm: CdrQuietZoneMm);
    }

    /// <summary>Code 128 的<strong>档位切换符</strong>（数值与起始符不同，别搞混：Start B = 104、切到 B = 100）。</summary>
    private const int CodeA = 101;
    private const int CodeB = 100;
    private const int CodeC = 99;

    /// <summary><see cref="FindDigitRun"/> 的结果：从这个位置起能凑几个连续数字。</summary>
    private enum DigitRun { Uncodable, OneDigit, TwoDigits }

    /// <summary>照 ZXing <c>Code128Writer.findCType</c>：这个位置起能凑出几个连续数字。</summary>
    private static DigitRun FindDigitRun(string value, int start)
    {
        if (start >= value.Length) return DigitRun.Uncodable;
        if (!IsDigit(value[start], out _)) return DigitRun.Uncodable;
        if (start + 1 >= value.Length) return DigitRun.OneDigit;
        return IsDigit(value[start + 1], out _) ? DigitRun.TwoDigits : DigitRun.OneDigit;
    }

    /// <summary>
    /// 照 ZXing <c>Code128Writer.chooseCode</c> 逐条搬过来（去掉本项目用不到的 FNC1 分支：
    /// 软件不编 GS1-128，数据里也不会出现那个转义字符）。
    /// <para>这段是「逐位换档」的大脑：决定当前位置该用 A / B / C 哪一档。
    /// 照抄它的理由——它同时是 ZXing 的权威行为、也是 CDR 样张实测出来的行为，两边对得上。</para>
    /// </summary>
    private static int ChooseCodeSet(string value, int start, int oldCode)
    {
        var lookahead = FindDigitRun(value, start);

        if (lookahead == DigitRun.OneDigit)
            return oldCode == CodeA ? CodeA : CodeB;        // 落单一位，C 档编不了

        if (lookahead == DigitRun.Uncodable)
        {
            // 控制字符（ASCII < 32）只有 A 档装得下；其余一律 B
            if (start < value.Length && value[start] < ' ') return CodeA;
            return CodeB;
        }

        if (oldCode == CodeC) return CodeC;                 // 已在 C 档就继续，别白换

        if (oldCode == CodeB)
        {
            // 已在 B 档又看到两个连续数字：值不值得切 C？后面还得有数字才划算
            lookahead = FindDigitRun(value, start + 2);
            if (lookahead == DigitRun.Uncodable || lookahead == DigitRun.OneDigit)
                return CodeB;                               // 后面不够，切了反而多一个符号
            // 到这里至少还有 4 个连续数字：往后数到断点，看总个数是奇是偶
            var index = start + 4;
            while (FindDigitRun(value, index) == DigitRun.TwoDigits) index += 2;
            // 奇数个数字 → 晚点再切（先把落单那位用 B 编掉），偶数个 → 现在就切
            return FindDigitRun(value, index) == DigitRun.OneDigit ? CodeB : CodeC;
        }

        // oldCode == 0：正在选起始档
        return lookahead == DigitRun.TwoDigits ? CodeC : CodeB;
    }

    // ---------- Code 39 ----------

    /// <summary>
    /// Code 39：每个字符 9 个元素（5 条 4 空，其中 3 个宽），字符之间插一条窄空；起止符都是 <c>*</c>。
    /// </summary>
    private static BarcodeEncoding EncodeCode39(string text)
    {
        string? note = null;
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLower(c))
            {
                sb.Append(char.ToUpperInvariant(c));
                if (note is null) note = "Code 39 没有小写，已按大写编（印出来的数字母也一律大写）";
            }
            else
            {
                sb.Append(c);
            }
        }
        var data = sb.ToString();

        foreach (var c in data)
        {
            if (Code39Alphabet.IndexOf(c) < 0)
            {
                return Fail(data, $"Code 39 装不下字符「{c}」（U+{(int)c:X4}）。它能用的字符只有：0-9、A-Z、减号、点、空格、$ / + %。");
            }
        }

        var bits = new StringBuilder();
        AppendCode39Char(bits, Code39StarEncoding);
        foreach (var c in data) AppendCode39Char(bits, Code39Encodings[Code39Alphabet.IndexOf(c)]);
        AppendCode39Char(bits, Code39StarEncoding, trailingSpace: false);
        return new BarcodeEncoding(true, bits.ToString(), data, note, null,
            QuietZoneMm: CdrQuietZoneMm,
            NarrowUnits: NarrowRatioUnits);
    }

    /// <summary>
    /// 一个 Code 39 字符（9 个元素，宽 = 5 位、窄 = 2 位）+ 后面那条窄空。
    /// <para>宽窄比 <strong>2.5 : 1</strong>（= 5 : 2），照 Corel BARCODE WIZARD 样张实测定的，
    /// 取证见 <see cref="WideRatioUnits"/>。原来是 2 : 1——同样在规范允许的区间里、同样扫得出，
    /// 但条宽分布与 CDR 排出来的不一样，并排一比就看得出宽窄不同。</para>
    /// </summary>
    private static void AppendCode39Char(StringBuilder bits, int encoding, bool trailingSpace = true)
    {
        // 高位起对应第 1 个元素：1 = 宽。
        var widths = new int[9];
        for (var i = 0; i < 9; i++)
            widths[i] = (encoding & (1 << (8 - i))) != 0 ? WideRatioUnits : NarrowRatioUnits;
        AppendWidths(bits, widths);
        // 字符之间那一条窄空（窄 = 2 位，与本制式其余窄元素同宽）。
        // startsWithBar=false 别丢：本制式每个字符以条收尾，分隔再按条铺就会把两根条粘成一根超宽条。
        if (trailingSpace) AppendWidths(bits, new[] { NarrowRatioUnits }, startsWithBar: false);
    }

    // ---------- UPC / EAN 一族的公共件 ----------

    /// <summary>
    /// UPC/EAN 一族的「补或验校验位」：给的是不含校验位那位 → 按规范补上并在 <c>Note</c> 里说明；
    /// 给的是全长 → 验一遍，对不上就挡下来。
    /// <para><strong>为什么全长也要验而不是直接编</strong>（第 23 棒定的口径）：表里 13 位抄错一位，
    /// 直接编出来就是一张「看着正常、扫出来是别的商品」的码。校验位错 = 抄错，必须先让人回去核对原单据。</para>
    /// </summary>
    /// <param name="text">用户填的那一串。</param>
    /// <param name="fullLength">含校验位的全长（EAN-13 → 13、EAN-8 → 8、UPC-A → 12、UPC-E → 8）。</param>
    /// <param name="name">报错文案里的制式名。</param>
    /// <param name="checksumInput">把「不含校验位的那一串」映射成真正喂给模 10 算法的数字串
    /// （只有 UPC-E 需要先转成 UPC-A，其余恒等）。</param>
    private static (string Text, string? Note, string? Error) ResolveCheckDigit(
        string text, int fullLength, string name, Func<string, string> checksumInput)
    {
        if (text.Length == fullLength - 1)
        {
            var check = GtinCheckDigit(checksumInput(text));
            if (check is null)
                return (text, null, $"{name} 只能编数字，这一栏里有非数字字符。请改选真正放商品条码的那一列。");
            return (text + check, $"{name} 表里给的是 {fullLength - 1} 位，已按规范补上第 {fullLength} 位校验码 {check}", null);
        }

        if (text.Length != fullLength)
            return (text, null, $"{name} 要 {fullLength - 1} 或 {fullLength} 位数字，这一栏是 {text.Length} 位。位数不对的码扫出来就是别的数，不能印。");

        var expected = GtinCheckDigit(checksumInput(text[..(fullLength - 1)]));
        if (expected is null)
            return (text, null, $"{name} 只能编数字，这一栏里有非数字字符。请改选真正放商品条码的那一列。");

        if (expected != text[^1] - '0')
            return (text, null, $"{name} 第 {fullLength} 位校验码不对：表里是 {text[^1]}，按前 {fullLength - 1} 位算出来应是 {expected}。" +
                              "要么表里少了一位、要么抄错了一位——请先核对原单据，软件不悄悄替你改掉（改了扫出来可能对上别的商品）。");

        return (text, null, null);
    }

    /// <summary>骑在某一侧静区上的那一位（EAN-13 的首位、UPC 的数字系统位与校验位）。
    /// 静区是固定毫米，所以位置不能按模块算，只标个「骑哪边」。</summary>
    private static HriSegment QuietDigit(char digit, HriAnchor anchor) => new(digit.ToString(), 0, 0, 0, anchor);

    // ---------- EAN-13 ----------

    private static BarcodeEncoding EncodeEan13(string text)
    {
        if (!AllDigits(text, out var bad))
            return Fail(text, $"EAN-13 只能编数字，这一栏里有「{bad}」。请改选真正放商品条码（13 位数字）的那一列。");

        var (data, note, error) = ResolveCheckDigit(text, 13, "EAN-13", s => s);
        if (error is not null) return Fail(text, error);

        var parities = Ean13FirstDigitEncodings[data[0] - '0'];
        var bits = new StringBuilder();
        AppendWidths(bits, EanGuard);                       // 起始保护条：条-空-条
        for (var i = 1; i <= 6; i++)
        {
            var digit = data[i] - '0';
            if (((parities >> (6 - i)) & 1) == 1) digit += 10;   // 左半用 G 码（= L 码反序），奇偶组合就是首位
            AppendWidths(bits, EanLeft[digit], startsWithBar: false);
        }
        AppendWidths(bits, EanMiddle, startsWithBar: false);      // 中间保护条：空-条-空-条-空
        for (var i = 7; i <= 12; i++)
            AppendWidths(bits, EanLeft[data[i] - '0'], startsWithBar: true);
        AppendWidths(bits, EanGuard);                              // 结束保护条
        // GS1 对 EAN-13 的左右静区要求是 11 模块,比其他制式的 10 多一个(第 23 棒)
        return new BarcodeEncoding(true, bits.ToString(), data, note, null,
            QuietZoneModules: Gtin13QuietZoneModules,
            GuardRanges: Ean13GuardRanges,
            HriSegments: new[]
            {
                QuietDigit(data[0], HriAnchor.LeftQuiet),         // 首位骑在左静区
                new HriSegment(data[1..7], 3, 42, 7),             // 左半 6 位，每位 7 模块
                new HriSegment(data[7..13], 50, 42, 7),           // 右半 6 位
            },
            QuietZoneMm: EanUpcQuietZoneMm);
    }

    // ---------- EAN-8 ----------

    /// <summary>
    /// EAN-8：3（起始保护条）+ 4×7（左半）+ 5（中间保护条）+ 4×7（右半）+ 3（结束保护条）= 67 模块。
    /// <para>结构照 ZXing 的 <c>EAN8Writer.cs</c>：左半用 L 码、右半同一张表但从条开始（等价于 R 码），
    /// 与 EAN-13 是同一条路，只是没有「首位数字决定奇偶」这一层——EAN-8 左右两半固定纯 L / 纯 R。</para>
    /// </summary>
    private static BarcodeEncoding EncodeEan8(string text)
    {
        if (!AllDigits(text, out var bad))
            return Fail(text, $"EAN-8 只能编数字，这一栏里有「{bad}」。请改选真正放商品条码（8 位数字）的那一列。");

        var (data, note, error) = ResolveCheckDigit(text, 8, "EAN-8", s => s);
        if (error is not null) return Fail(text, error);

        var bits = new StringBuilder();
        AppendWidths(bits, EanGuard);
        for (var i = 0; i <= 3; i++)
            AppendWidths(bits, EanLeft[data[i] - '0'], startsWithBar: false);
        AppendWidths(bits, EanMiddle, startsWithBar: false);
        for (var i = 4; i <= 7; i++)
            AppendWidths(bits, EanLeft[data[i] - '0'], startsWithBar: true);
        AppendWidths(bits, EanGuard);

        return new BarcodeEncoding(true, bits.ToString(), data, note, null,
            QuietZoneModules: Gtin8QuietZoneModules,
            GuardRanges: Ean8GuardRanges,
            HriSegments: new[]
            {
                new HriSegment(data[..4], 3, 28, 7),
                new HriSegment(data[4..8], 36, 28, 7),
            },
            QuietZoneMm: EanUpcQuietZoneMm);
    }

    // ---------- UPC-A ----------

    /// <summary>
    /// UPC-A：与 EAN-13 同为 95 模块，等同「首位固定 0 的 EAN-13」——所以左半全部走 L 码
    /// （奇偶表 0x00 = LLLLLL），不必再查首位表。
    /// <para>数字是 12 位，可读行按北美规矩排成四段：数字系统位（左静区）+ 左 5 位 + 右 5 位 + 校验位（右静区）。</para>
    /// </summary>
    private static BarcodeEncoding EncodeUpcA(string text)
    {
        if (!AllDigits(text, out var bad))
            return Fail(text, $"UPC-A 只能编数字，这一栏里有「{bad}」。请改选真正放商品条码（12 位数字）的那一列。");

        var (data, note, error) = ResolveCheckDigit(text, 12, "UPC-A", s => s);
        if (error is not null) return Fail(text, error);

        var bits = new StringBuilder();
        AppendWidths(bits, EanGuard);
        for (var i = 0; i <= 5; i++)
            AppendWidths(bits, EanLeft[data[i] - '0'], startsWithBar: false);
        AppendWidths(bits, EanMiddle, startsWithBar: false);
        for (var i = 6; i <= 11; i++)
            AppendWidths(bits, EanLeft[data[i] - '0'], startsWithBar: true);
        AppendWidths(bits, EanGuard);

        return new BarcodeEncoding(true, bits.ToString(), data, note, null,
            QuietZoneModules: UpcQuietZoneModules,
            GuardRanges: Ean13GuardRanges,          // 95 模块，保护条位置与 EAN-13 完全相同
            HriSegments: new[]
            {
                QuietDigit(data[0], HriAnchor.LeftQuiet),
                new HriSegment(data[1..6], 3, 35, 7),
                new HriSegment(data[6..11], 50, 35, 7),
                new HriSegment(data[11..12], 0, 0, 0, HriAnchor.RightQuiet),
            },
            QuietZoneMm: EanUpcQuietZoneMm);
    }

    // ---------- UPC-E ----------

    /// <summary>
    /// UPC-E：3（起始保护条）+ 6×7（六位数据）+ 6（结束保护条）= 51 模块。
    /// <para>六位数据用 L 还是 G，取决于「数字系统位 + 校验位」这一对——这就是那张
    /// <see cref="UpcENumSysAndCheckPatterns"/> 表的用处（照 ZXing 的 <c>UPCEReader</c> 抄）。
    /// 校验位本身要先把 UPC-E 还原成 11 位的 UPC-A 再按模 10 算，用的是 ZXing 的
    /// <c>convertUPCEtoUPCA</c> 同一套压缩规则（见 <see cref="ConvertUpcEToUpcA"/>）。</para>
    /// </summary>
    private static BarcodeEncoding EncodeUpcE(string text)
    {
        if (!AllDigits(text, out var bad))
            return Fail(text, $"UPC-E 只能编数字，这一栏里有「{bad}」。请改选真正放商品条码（8 位数字）的那一列。");

        var (data, note, error) = ResolveCheckDigit(text, 8, "UPC-E", ConvertUpcEToUpcA);
        if (error is not null) return Fail(text, error);

        var numberSystem = data[0] - '0';
        if (numberSystem is not (0 or 1))
            return Fail(text, $"UPC-E 的首位（数字系统位）只能是 0 或 1，这一栏是 {numberSystem}。这多半是选错了制式——北美压缩码才会用到它。");

        var checkDigit = data[7] - '0';
        var parities = UpcENumSysAndCheckPatterns[numberSystem][checkDigit];

        var bits = new StringBuilder();
        AppendWidths(bits, EanGuard);
        for (var i = 1; i <= 6; i++)
        {
            var digit = data[i] - '0';
            if (((parities >> (6 - i)) & 1) == 1) digit += 10;   // 1 = 用 G 码
            AppendWidths(bits, EanLeft[digit], startsWithBar: false);
        }
        AppendWidths(bits, EanEndGuard, startsWithBar: false);

        return new BarcodeEncoding(true, bits.ToString(), data, note, null,
            QuietZoneModules: UpcQuietZoneModules,
            GuardRanges: UpcEGuardRanges,
            HriSegments: new[]
            {
                QuietDigit(data[0], HriAnchor.LeftQuiet),
                new HriSegment(data[1..7], 3, 42, 7),
                new HriSegment(data[7..8], 0, 0, 0, HriAnchor.RightQuiet),
            },
            QuietZoneMm: EanUpcQuietZoneMm);
    }

    /// <summary>
    /// 把 7/8 位的 UPC-E 还原成不含校验位的 11 位 UPC-A 串（校验位算法要喂它）。
    /// <para>逐字照 ZXing <c>UPCEReader.convertUPCEToUPCA</c>：看第 6 位是几，决定中间要补几个 0、
    /// 厂商码与商品码怎么切。这个压缩规则没有任何自由发挥的余地，写错一位就换算出别的校验位。</para>
    /// </summary>
    internal static string ConvertUpcEToUpcA(string upcE)
    {
        if (upcE.Length < 7) return upcE;
        var body = upcE.Substring(1, 6);
        var sb = new StringBuilder(11);
        sb.Append(upcE[0]);
        var lastChar = body[5];
        switch (lastChar)
        {
            case '0':
            case '1':
            case '2':
                sb.Append(body, 0, 2);
                sb.Append(lastChar);
                sb.Append("0000");
                sb.Append(body, 2, 3);
                break;
            case '3':
                sb.Append(body, 0, 3);
                sb.Append("00000");
                sb.Append(body, 3, 2);
                break;
            case '4':
                sb.Append(body, 0, 4);
                sb.Append("00000");
                sb.Append(body[4]);
                break;
            default:
                sb.Append(body, 0, 5);
                sb.Append("0000");
                sb.Append(lastChar);
                break;
        }
        return sb.ToString();
    }

    // ---------- Codabar ----------

    /// <summary>
    /// Codabar：每字符 7 个元素（条空交替，2 或 3 个是宽元素，宽 = 2 模块、窄 = 1 模块），
    /// 字符之间插一条窄空；首尾必须有起止符 A/B/C/D。
    /// <para>结构逐字照 ZXing 的 <c>CodaBarWriter.cs</c> 与 <c>CodaBarReader.CHARACTER_ENCODINGS</c>：
    /// 一个字符哪几个元素是宽的，由那 7 位编码高位起决定。</para>
    /// </summary>
    private static BarcodeEncoding EncodeCodabar(string text)
    {
        var data = text.ToUpperInvariant();       // Codabar 没有小写
        string? note = data != text ? "Codabar 没有小写，已按大写编" : null;

        foreach (var c in data)
        {
            if (CodabarAlphabet.IndexOf(c) < 0)
                return Fail(data, $"Codabar 装不下字符「{c}」（U+{(int)c:X4}）。它能用的只有：0-9、- $ : / . +，以及起止符 A B C D。");
        }

        // 起止符：给了就得首尾都给；都没给才自动配。一头有一头没有是抄错了，挡下来让人看。
        var startsGuard = CodabarStartEnd.IndexOf(data[0]) >= 0;
        var endsGuard = CodabarStartEnd.IndexOf(data[^1]) >= 0;
        if (startsGuard != endsGuard)
            return Fail(data, "Codabar 的起止符要么首尾都给 A/B/C/D，要么都不给（不给就自动配 A）。现在只有一头带着。");

        // 起止符是「帧」不是数据——扫码枪读回来的串里没有它（ZXing 的 reader 也会剥掉），
        // 所以下面印的那串数字同样不带（用户导出的 CDR 样本就是这么排的）。
        var display = data;
        if (!startsGuard)
        {
            data = "A" + data + "A";
            note = JoinNote(note, "首尾没给起止符，已按最常用的 A 配上（起止符是帧，下面印的数字里不带它）");
        }

        var bits = new StringBuilder();
        for (var i = 0; i < data.Length; i++)
        {
            var code = CodabarEncodings[CodabarAlphabet.IndexOf(data[i])];
            var widths = new int[7];
            for (var bit = 0; bit < 7; bit++)
                // 宽窄比 2.5 : 1（= 5 : 2），照 CDR 样张实测，取证见 WideRatioUnits
                widths[bit] = ((code >> (6 - bit)) & 1) != 0 ? WideRatioUnits : NarrowRatioUnits;
            AppendWidths(bits, widths);
            // 字符之间那一条窄空，与本制式其余窄元素同宽。
            // startsWithBar=false 别丢：每个字符以条收尾，分隔再按条铺就会把两根条粘成一根超宽条。
            if (i < data.Length - 1) AppendWidths(bits, new[] { NarrowRatioUnits }, startsWithBar: false);
        }
        return new BarcodeEncoding(true, bits.ToString(), display, note, null,
            QuietZoneMm: CdrQuietZoneMm,
            NarrowUnits: NarrowRatioUnits);
    }

    // ---------- 25 码（非交错 Industrial 2 of 5） ----------

    /// <summary>
    /// 25 码：非交错的「二五码」，与交错的 <see cref="EncodeItf"/> 是两种码。
    /// 每个数字 5 根<strong>条</strong>（恰 2 根宽），条之间一律一条恒窄空——
    /// 空只当分隔、不携带信息，所以条数 = 3（起始）+ 5 × 位数 + 3（结束），元素总数恒为奇数。
    /// <para>数字表复用 <see cref="ItfPatterns"/>：2 of 5 全家族共享同一张「5 取 2」权重表
    /// （那份表逐字对过 ZXing，又与用户 CDR 样张逐位反推交叉验证一致，取证见
    /// <c>labelgou-word\LabelGou-条码层交付说明.md</c> §4.3）。</para>
    /// <para><strong>不照抄 CDR 的一处缺陷</strong>：样张里数字 2 只画了 1 根宽条（<c>nwnnn</c>，
    /// 应为 <c>nwnnw</c>），全码宽条 28 而非 30，违反「每数字恰 2 宽」的自校验定义、扫不出。
    /// 这里按标准表编。</para>
    /// </summary>
    private static BarcodeEncoding EncodeCode25(string text)
    {
        if (!AllDigits(text, out var bad))
            return Fail(text, $"25 码只能编数字，这一栏里有「{bad}」。这种老式码就是一串纯数字。");

        // 先按规范记法（N=1 / W=3）攒齐每一根条的宽度，再用恒窄空逐根隔开，出口统一过 RatioScale。
        var bars = new List<int>(text.Length * 5 + 6);
        bars.AddRange(Code25Start);
        foreach (var c in text)
            bars.AddRange(ItfPatterns[c - '0']);
        bars.AddRange(Code25Stop);

        var widths = new int[bars.Count * 2 - 1];
        for (var i = 0; i < bars.Count; i++)
        {
            if (i > 0) widths[i * 2 - 1] = 1;      // 恒窄空（规范记法 1）
            widths[i * 2] = bars[i];
        }

        var bits = new StringBuilder();
        AppendWidths(bits, RatioScale(widths));
        return new BarcodeEncoding(true, bits.ToString(), text, null, null,
            QuietZoneMm: CdrQuietZoneMm,
            NarrowUnits: NarrowRatioUnits);
    }

    // ---------- MSI / Plessey ----------

    /// <summary>
    /// MSI/Plessey：起始 条2-空1 + 每位数字 4 条 4 空（12 个模块）+ 结束 条1-空2-条1。
    /// <para>结构逐字照 ZXing 的 <c>MSIWriter.cs</c>。这一版<strong>不编校验位</strong>——
    /// MSI 的校验位（Luhn / Mod10 / Mod11）各家用法不一，表里给什么就编什么；真有那需求再加。</para>
    /// </summary>
    private static BarcodeEncoding EncodeMsi(string text)
    {
        if (!AllDigits(text, out var bad))
            return Fail(text, $"MSI 只能编数字，这一栏里有「{bad}」。这种老式仓储码就是一串纯数字。");

        var bits = new StringBuilder();
        AppendWidths(bits, MsiStartWidths);
        foreach (var c in text)
            AppendWidths(bits, MsiNumberWidths[c - '0']);
        AppendWidths(bits, MsiEndWidths);
        return new BarcodeEncoding(true, bits.ToString(), text, null, null);
    }

    // ---------- 别名（JAN / ISBN / ISSN） ----------

    /// <summary>JAN-8 / JAN-13 就是 EAN-8 / EAN-13 的日本叫法，编码完全相同（CDR 的下拉里也是这么并列的）。</summary>
    private static BarcodeEncoding EncodeJan8(string text) => EncodeEan8(text);

    private static BarcodeEncoding EncodeJan13(string text) => EncodeEan13(text);

    /// <summary>ISBN（978 开头）/ ISSN（977 开头）：就是带固定前缀的 EAN-13。前缀不对挡下来——
    /// 图书条码印成期刊号，扫出来对得上号却对不上书，比编不出来更糟。</summary>
    private static BarcodeEncoding EncodePrefixedEan(string text, string prefix, string name)
    {
        var probe = text.Trim();
        // 12 位要补完校验位才能看全前缀，所以先把校验位补上再核对
        if (probe.Length == 12 && GtinCheckDigit(probe) is { } check) probe += check.ToString();
        if (probe.Length < prefix.Length || !probe.StartsWith(prefix, StringComparison.Ordinal))
            return Fail(text, $"{name} 的条码必须 {prefix} 开头，这一栏是「{(text.Length > 3 ? text[..3] + "…" : text)}」。" +
                              $"要么选错了制式（一般选 EAN-13 就行），要么这一列不是{name}号。");
        return EncodeEan13(text);
    }

    // ---------- ITF（通用交错 2 of 5） ----------

    /// <summary>
    /// ITF / 交错 2 of 5：起始 4 个元素 + 每两位数字 10 个元素（5 条 5 空交错）+ 结束 3 个元素。
    /// <para>与 <see cref="EncodeItf14"/> 的区别：这边<strong>不补也不验 GTIN 校验位</strong>，
    /// 只要求长度是偶数（奇数就在前面补一个 0——交错码必须两位一组，补哪一头是规范里定死的）。
    /// 码表与那边共用 <see cref="ItfPatterns"/>。</para>
    /// </summary>
    private static BarcodeEncoding EncodeItf(string text)
    {
        if (!AllDigits(text, out var bad))
            return Fail(text, $"ITF 只能编数字，这一栏里有「{bad}」。交错 2 of 5 就是给箱号/货号用的。");

        string? note = null;
        if (text.Length % 2 != 0)
        {
            text = "0" + text;
            note = "交错 2 of 5 必须两位一组，这一栏是奇数位，已按规范在前面补了一个 0";
        }

        var bits = new StringBuilder();
        AppendItfBody(bits, text);
        return new BarcodeEncoding(true, bits.ToString(), text, note, null,
            QuietZoneMm: CdrQuietZoneMm,
            NarrowUnits: NarrowRatioUnits);
    }

    // ---------- ITF-14 ----------

    private static BarcodeEncoding EncodeItf14(string text)
    {
        foreach (var c in text)
        {
            if (!IsDigit(c, out _))
                return Fail(text, $"ITF-14 只能编数字，这一栏里有「{c}」。储运箱码（GTIN-14）就是一串纯数字。");
        }

        string? note = null;
        if (text.Length == 13)
        {
            var check = GtinCheckDigit(text) ?? 0;
            text += check.ToString();
            note = $"ITF-14 表里给的是 13 位，已按 GTIN 规范补上第 14 位校验码 {check}";
        }
        else if (text.Length == 14)
        {
            // 与 EAN-13 同一口径(第 23 棒):14 位不再放行,校验位错=抄错一位,扫出来是别的箱
            var expected = GtinCheckDigit(text[..13]) ?? 0;
            if (expected != text[13] - '0')
            {
                return Fail(text, $"ITF-14 第 14 位校验码不对：表里是 {text[13]}，按前 13 位算出来应是 {expected}。" +
                                  "要么表里少了一位、要么抄错了一位——请先核对原单据，软件不悄悄替你改掉。");
            }
        }
        else if (text.Length % 2 != 0)
        {
            return Fail(text, $"ITF 是把两位数字交错排的，长度必须是偶数，这一栏是 {text.Length} 位。" +
                              "要么前面补一个 0、要么确认这一列真的放的是箱码。");
        }

        var bits = new StringBuilder();
        AppendItfBody(bits, text);
        // 储运箱码的静区比一般制式宽得多（样张实测左 7.180 / 右 7.217 mm），别套用 3 mm。
        return new BarcodeEncoding(true, bits.ToString(), text, note, null,
            QuietZoneMm: BoxQuietZoneMm,
            NarrowUnits: NarrowRatioUnits);
    }

    /// <summary>
    /// 交错 2 of 5 的共同铺法：起始 4 元素 + 每两位数字 10 元素（5 条 5 空交错）+ 结束 3 元素。
    /// <para>ITF 与 ITF-14 只有「补不补 GTIN 校验位」这一处不同，铺码的部分是同一段——
    /// 抽出来是为了宽窄比只改一处（原来两边各写一遍，改的时候漏一边就变成两种条宽）。</para>
    /// </summary>
    private static void AppendItfBody(StringBuilder bits, string text)
    {
        AppendWidths(bits, RatioScale(ItfStart));
        for (var i = 0; i < text.Length; i += 2)
        {
            var bars = ItfPatterns[text[i] - '0'];
            var spaces = ItfPatterns[text[i + 1] - '0'];
            var interleaved = new int[10];
            for (var j = 0; j < 5; j++)
            {
                interleaved[j * 2] = bars[j];
                interleaved[j * 2 + 1] = spaces[j];
            }
            AppendWidths(bits, RatioScale(interleaved));
        }
        AppendWidths(bits, RatioScale(ItfEnd));
    }

    /// <summary>
    /// 把「规范记法的宽窄表」换成实际要铺的位数：窄（1）→ <see cref="NarrowRatioUnits"/>、
    /// 宽（3）→ <see cref="WideRatioUnits"/>。
    /// <para><strong>为什么不直接改码表</strong>：<see cref="ItfPatterns"/> 里的 1 / 3 是交错 2 of 5
    /// 规范自己的记法（N = 窄、W = 宽），也是 ZXing 源码里的写法。码表照原样留着、
    /// 只在出口处换算比例，改宽窄比时只动一处，不必把十行码表逐字重写——
    /// 重写正是最容易抄错一位、然后印出一张扫不出的码的地方。</para>
    /// </summary>
    private static int[] RatioScale(int[] widths)
    {
        var scaled = new int[widths.Length];
        for (var i = 0; i < widths.Length; i++)
            scaled[i] = widths[i] <= 1 ? NarrowRatioUnits : WideRatioUnits;
        return scaled;
    }

    private static BarcodeEncoding Fail(string data, string error)
        => new(false, string.Empty, data, null, error);

    /// <summary>
    /// 把一段「元素宽度」按 条-空-条… 交替追加成模块位串。
    /// <para><paramref name="startsWithBar"/> = false 用于 EAN 左半那几组（它们前面已经有一根条了，
    /// 这一段是从空开始的）。这一句是整个编码里最容易写错的地方，所以只留这一个开关，其余都从条开始。</para>
    /// </summary>
    private static void AppendWidths(StringBuilder bits, int[] widths, bool startsWithBar = true)
    {
        var bar = startsWithBar;
        foreach (var w in widths)
        {
            if (w <= 0) continue;
            bits.Append(bar ? '1' : '0');
            for (var i = 1; i < w; i++) bits.Append(bar ? '1' : '0');
            bar = !bar;
        }
    }

    private static int[] ParseWidths(string pattern)
    {
        var widths = new int[pattern.Length];
        for (var i = 0; i < pattern.Length; i++) widths[i] = pattern[i] - '0';
        return widths;
    }

    // ==================== 码表（逐字取自 ZXing.Net，见类注释） ====================

    private const int StartA = 103;
    private const int StartB = 104;
    private const int StartC = 105;
    private const int Stop = 106;

    /// <summary>Code 128 的 107 条宽度表：每条 6 个数字（和 = 11 模块），最后一条 Stop 是 7 个（13 模块）。</summary>
    private static readonly string[] Code128PatternStrings =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312",
        "132212", "221213", "221312", "231212", "112232", "122132", "122231", "113222",
        "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131",
        "311222", "321122", "321221", "312212", "322112", "322211", "212123", "212321",
        "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121",
        "313121", "211331", "231131", "213113", "213311", "213131", "311123", "311321",
        "331121", "312113", "312311", "332111", "314111", "221411", "431111", "111224",
        "111422", "121124", "121421", "141122", "141221", "112214", "112412", "122114",
        "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112",
        "421211", "212141", "214121", "412121", "111143", "111341", "131141", "114113",
        "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412",
        "211214", "211232", "2331112",
    };

    private static readonly int[][] Code128Patterns =
        Code128PatternStrings.Select(ParseWidths).ToArray();

    private const string Code39Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    /// <summary>Codabar 的字符表（与 <see cref="CodabarEncodings"/> 一一对齐）。</summary>
    private const string CodabarAlphabet = "0123456789-$:/.+ABCD";

    /// <summary>Codabar 的起止符——首尾必须是这四个里的一个（或都不给，自动配 A）。</summary>
    private const string CodabarStartEnd = "ABCD";

    /// <summary>Codabar 的 20 条：7 位二进制，1 = 宽元素（2 模块）、0 = 窄元素（1 模块），
    /// 高位起对应第 1 个元素。逐字取自 ZXing 的 <c>CodaBarReader.CHARACTER_ENCODINGS</c>。</summary>
    private static readonly int[] CodabarEncodings =
    {
        0x003, 0x006, 0x009, 0x060, 0x012, 0x042, 0x021, 0x024, 0x030, 0x048,   // 0-9
        0x00c, 0x018, 0x045, 0x051, 0x054, 0x015, 0x01A, 0x029, 0x00B, 0x00E,   // - $ : / . + A B C D
    };

    private const int Code39StarEncoding = 0x094;

    /// <summary>Code 39 的 43 条：9 位二进制，1 = 宽元素、0 = 窄元素（高位起对应第 1 个元素）。</summary>
    private static readonly int[] Code39Encodings =
    {
        0x034, 0x121, 0x061, 0x160, 0x031, 0x130, 0x070, 0x025, 0x124, 0x064,   // 0-9
        0x109, 0x049, 0x148, 0x019, 0x118, 0x058, 0x00D, 0x10C, 0x04C, 0x01C,   // A-J
        0x103, 0x043, 0x142, 0x013, 0x112, 0x052, 0x007, 0x106, 0x046, 0x016,   // K-T
        0x181, 0x0C1, 0x1C0, 0x091, 0x190, 0x0D0, 0x085, 0x184, 0x0C4, 0x0A8,   // U-$
        0x0A2, 0x08A, 0x02A,                                                    // /-%
    };

    private static readonly int[] EanGuard = { 1, 1, 1 };
    private static readonly int[] EanMiddle = { 1, 1, 1, 1, 1 };

    /// <summary>UPC-E 的结束保护条：6 个模块（比 EAN 的 <see cref="EanGuard"/> 长一倍，这是 UPC-E 的特征）。</summary>
    private static readonly int[] EanEndGuard = { 1, 1, 1, 1, 1, 1 };

    /// <summary>EAN 的 L 码（和 = 7 模块）；G 码 = 同一条反序，所以这里只存一份，取用时按 +10 索引反转。
    /// 这张表与 ZXing 的 <c>UPCEANReader.L_PATTERNS</c> 逐项相同——EAN-8 / UPC-A / UPC-E 全部复用它。</summary>
    private static readonly int[][] EanLeft = BuildEanLeftRight();

    /// <summary>首位数字 → 左半 6 位的奇偶排布（1 = 用 G 码）。0x00=LLLLLL、0x0B=LLGLGG…</summary>
    private static readonly int[] Ean13FirstDigitEncodings = { 0x00, 0x0B, 0x0D, 0x0E, 0x13, 0x19, 0x1C, 0x15, 0x16, 0x1A };

    /// <summary>UPC-E：数字系统位（0/1）× 校验位 → 六位数据的 L/G 排布（1 = 用 G 码）。
    /// 照 ZXing <c>UPCEReader.NUMSYS_AND_CHECK_DIGIT_PATTERNS</c> 抄，没有任何自由发挥的余地。</summary>
    private static readonly int[][] UpcENumSysAndCheckPatterns =
    {
        new[] { 0x38, 0x34, 0x32, 0x31, 0x2C, 0x26, 0x23, 0x2A, 0x29, 0x25 },   // 数字系统位 0
        new[] { 0x07, 0x0B, 0x0D, 0x0E, 0x13, 0x19, 0x1C, 0x15, 0x16, 0x1A },   // 数字系统位 1
    };

    /// <summary>EAN-13 与 UPC-A 的保护条：起始 3 模块、正中 5 模块（从第 45 个模块起）、结束 3 模块。</summary>
    private static readonly GuardRange[] Ean13GuardRanges =
    {
        new(0, 3), new(45, 5), new(92, 3),
    };

    /// <summary>EAN-8 的保护条：起始 3、正中 5（从第 31 个模块起）、结束 3（第 64 个模块起）。</summary>
    private static readonly GuardRange[] Ean8GuardRanges =
    {
        new(0, 3), new(31, 5), new(64, 3),
    };

    /// <summary>UPC-E 的保护条：起始 3 模块、结束 6 模块（从第 45 个模块起）。</summary>
    private static readonly GuardRange[] UpcEGuardRanges =
    {
        new(0, 3), new(45, 6),
    };

    private static readonly int[] ItfStart = { 1, 1, 1, 1 };
    private static readonly int[] ItfEnd = { 3, 1, 1 };

    /// <summary>25 码（非交错）起止符：起始 宽-宽-窄、结束 宽-窄-宽。沿用 N=1 / W=3 记法，铺码前过 <see cref="RatioScale"/>。
    /// <para>取自用户 CDR 样张（<c>labelgou-CL\条码\1234567891231.svg</c> 的 "code25" 框）逐条实测：
    /// 前 3 根条 0.847/0.847/0.339 = <c>WWN</c>，末 3 根 0.847/0.339/0.847 = <c>WNW</c>。
    /// 两者各含 2 根宽条，与 2 of 5 「每个符号 5 元素 2 宽」的自校验惯例一致。</para></summary>
    private static readonly int[] Code25Start = { 3, 3, 1 };
    private static readonly int[] Code25Stop = { 3, 1, 3 };

    /// <summary>MSI 的起止符：起始 条2-空1、结束 条1-空2-条1（照 ZXing 的 <c>MSIWriter</c>）。</summary>
    private static readonly int[] MsiStartWidths = { 2, 1 };
    private static readonly int[] MsiEndWidths = { 1, 2, 1 };

    /// <summary>MSI 每位数字的 4 条 4 空（元素宽 1/2，共 12 个模块），逐字取自 ZXing 的 <c>MSIWriter.numberWidths</c>。</summary>
    private static readonly int[][] MsiNumberWidths =
    {
        new[] { 1, 2, 1, 2, 1, 2, 1, 2 },
        new[] { 1, 2, 1, 2, 1, 2, 2, 1 },
        new[] { 1, 2, 1, 2, 2, 1, 1, 2 },
        new[] { 1, 2, 1, 2, 2, 1, 2, 1 },
        new[] { 1, 2, 2, 1, 1, 2, 1, 2 },
        new[] { 1, 2, 2, 1, 1, 2, 2, 1 },
        new[] { 1, 2, 2, 1, 2, 1, 1, 2 },
        new[] { 1, 2, 2, 1, 2, 1, 2, 1 },
        new[] { 2, 1, 1, 2, 1, 2, 1, 2 },
        new[] { 2, 1, 1, 2, 1, 2, 2, 1 },
    };

    /// <summary>ITF 的 0~9：5 个元素，N=1 窄、W=3 宽（条与空各占一组，交错排）。</summary>
    private static readonly int[][] ItfPatterns =
    {
        new[] { 1, 1, 3, 3, 1 },   // 0
        new[] { 3, 1, 1, 1, 3 },   // 1
        new[] { 1, 3, 1, 1, 3 },   // 2
        new[] { 3, 3, 1, 1, 1 },   // 3
        new[] { 1, 1, 3, 1, 3 },   // 4
        new[] { 3, 1, 3, 1, 1 },   // 5
        new[] { 1, 3, 3, 1, 1 },   // 6
        new[] { 1, 1, 1, 3, 3 },   // 7
        new[] { 3, 1, 1, 3, 1 },   // 8
        new[] { 1, 3, 1, 3, 1 },   // 9
    };

    private static int[][] BuildEanLeftRight()
    {
        var l = new[]
        {
            new[] { 3, 2, 1, 1 }, new[] { 2, 2, 2, 1 }, new[] { 2, 1, 2, 2 }, new[] { 1, 4, 1, 1 }, new[] { 1, 1, 3, 2 },
            new[] { 1, 2, 3, 1 }, new[] { 1, 1, 1, 4 }, new[] { 1, 3, 1, 2 }, new[] { 1, 2, 1, 3 }, new[] { 3, 1, 1, 2 },
        };
        var both = new int[20][];
        Array.Copy(l, 0, both, 0, 10);
        for (var i = 0; i < 10; i++)
        {
            // G 码 = L 码的元素顺序反过来（宽度值本身不变），这正是 EAN 的「奇/偶」两套编码关系。
            both[10 + i] = l[i].Reverse().ToArray();
        }
        return both;
    }
}
