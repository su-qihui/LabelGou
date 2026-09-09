using System.Text;

namespace LabelGou.Core.Barcodes;

/// <summary>
/// 一次条码编码的结果（纯数据，不碰像素也不碰毫米）。
/// </summary>
/// <param name="Ok">能不能编。</param>
/// <param name="Bits">条与空的模块序列：<c>'1'</c> = 黑条、<c>'0'</c> = 白空，<strong>不含静区</strong>（静区由几何层加）。</param>
/// <param name="Data">真正编进去的那一串：可能与传进来的不同（自动补了校验位、Code 39 把小写洗成大写），
/// <see cref="Note"/> 会说清差在哪。</param>
/// <param name="Note">自动替用户做的那件事（补校验位 / 转大写）。不静默改印刷数据是本项目红线，改了必须说。</param>
/// <param name="Error">编不出来的原因 + 怎么改。界面拿它直接显示，不做二次加工。</param>
/// <param name="QuietZoneModules">这个制式规范的左右静区模块数：EAN-13 是 11，其余 10（第 23 棒按制式区分）。</param>
public sealed record BarcodeEncoding(
    bool Ok,
    string Bits,
    string Data,
    string? Note,
    string? Error,
    int QuietZoneModules = BarcodeBars.QuietZoneModules)
{
    /// <summary>模块数（= 位数）。几何层按它决定一根模块多宽。</summary>
    public int Modules => Bits.Length;
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
/// <strong>编不出来就报错，不静默改数据</strong>：中文、奇数位、校验位不对——一律返回 <see cref="BarcodeEncoding.Error"/>，
/// 让界面挡住它。唯一会替用户动数据的情形是「补一个规范要求的校验位」与「Code 39 把小写转大写」，
/// 两者都会写进 <see cref="BarcodeEncoding.Note"/> 让界面显示出来。
/// </para>
/// </summary>
public static class BarcodeEncoder
{
    /// <summary>单条数据允许的最长字符数（Code 39/ITF 的常规上限，也是防呆：一栏粘进整段备注就该被看见）。</summary>
    public const int MaxDataLength = 80;

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
            BarcodeSymbology.Itf14 => EncodeItf14(text),
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
    /// <para>档位选择按最省的那条规则来：整串都是数字、长度是偶数且不短于 4 → Code C（两位数字一个符号）；
    /// 其余走 Code B（ASCII 32~126，值 = 字符码 - 32）。中途换档不做——那需要动态规划，而店里的数据
    /// 要么整串是数字（货号/箱号）要么含字母，用不着混排。</para>
    /// </summary>
    private static BarcodeEncoding EncodeCode128(string text)
    {
        var allDigits = true;
        foreach (var c in text)
        {
            if (!IsDigit(c, out _)) { allDigits = false; break; }
        }
        var useC = allDigits && text.Length % 2 == 0 && text.Length >= 4;

        var codes = new List<int>();
        int start;
        if (useC)
        {
            start = StartC;
            for (var i = 0; i < text.Length; i += 2)
                codes.Add((text[i] - '0') * 10 + (text[i + 1] - '0'));
        }
        else
        {
            start = StartB;
            foreach (var c in text)
            {
                if (c < 32 || c > 126)
                {
                    return Fail(text, $"Code 128 装不下字符「{c}」（U+{(int)c:X4}）：Code B 只能编 ASCII 32~126。" +
                                      "把这一栏换成表里的英文货号/编号列，或先在那一列里把中文去掉——软件不替你猜该删哪几个字。");
                }
                codes.Add(c - 32);
            }
        }

        // 校验符 = (起始符×1 + 第 i 个数据符×(i+1)) mod 103，也就是数据权重从 1 起。
        // <para><strong>这个口径不是凭记忆写的</strong>：上一版按「起始 1、数据从 2 起」算（网上两种说法都有），
        // 拿 ZXing 的 reader 当裁判一验：那种算法下所有码都被判成校验不过（只有数据全 0 的能过），
        // 而 ZXing 自己按 1、2、3… 算的码全部能读回。见 <c>_probe\b17-barcode\probe128</c>。
        // 校验位错一档 = 印出去的箱唛扫不上，这属本项目最重的红线。</para>
        var sum = start;
        for (var i = 0; i < codes.Count; i++) sum += codes[i] * (i + 1);
        var check = sum % 103;

        var bits = new StringBuilder();
        AppendWidths(bits, Code128Patterns[start]);
        foreach (var code in codes) AppendWidths(bits, Code128Patterns[code]);
        AppendWidths(bits, Code128Patterns[check]);
        AppendWidths(bits, Code128Patterns[Stop]);
        return new BarcodeEncoding(true, bits.ToString(), text, null, null);
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
        return new BarcodeEncoding(true, bits.ToString(), data, note, null);
    }

    /// <summary>一个 Code 39 字符（9 个元素，宽 = 2 模块、窄 = 1 模块）+ 后面那条窄空。</summary>
    private static void AppendCode39Char(StringBuilder bits, int encoding, bool trailingSpace = true)
    {
        // 高位起对应第 1 个元素：1 = 宽。宽窄比 2:1 与 ZXing 同档（同一串数据在两个软件里排出来的条宽才会一样）。
        var widths = new int[9];
        for (var i = 0; i < 9; i++)
            widths[i] = (encoding & (1 << (8 - i))) != 0 ? 2 : 1;
        AppendWidths(bits, widths);
        if (trailingSpace) bits.Append('0');      // 字符之间那一条窄空
    }

    // ---------- EAN-13 ----------

    private static BarcodeEncoding EncodeEan13(string text)
    {
        foreach (var c in text)
        {
            if (!IsDigit(c, out _))
                return Fail(text, $"EAN-13 只能编数字，这一栏里有「{c}」。请改选真正放商品条码（13 位数字）的那一列。");
        }

        string? note = null;
        if (text.Length == 12)
        {
            var check = GtinCheckDigit(text) ?? 0;
            text += check.ToString();
            note = $"EAN-13 表里给的是 12 位，已按规范补上第 13 位校验码 {check}";
        }
        else if (text.Length != 13)
        {
            return Fail(text, $"EAN-13 要 12 或 13 位数字，这一栏是 {text.Length} 位。位数不对的码扫出来就是别的数，不能印。");
        }
        else
        {
            var expected = GtinCheckDigit(text[..12]) ?? 0;
            if (expected != text[12] - '0')
            {
                return Fail(text, $"EAN-13 第 13 位校验码不对：表里是 {text[12]}，按前 12 位算出来应是 {expected}。" +
                                  "要么表里少了一位、要么抄错了一位——请先核对原单据，软件不悄悄替你改掉（改了扫出来可能对上别的商品）。");
            }
        }

        var parities = Ean13FirstDigitEncodings[text[0] - '0'];
        var bits = new StringBuilder();
        AppendWidths(bits, EanGuard);                       // 起始保护条：条-空-条
        for (var i = 1; i <= 6; i++)
        {
            var digit = text[i] - '0';
            if (((parities >> (6 - i)) & 1) == 1) digit += 10;   // 左半用 G 码（= L 码反序），奇偶组合就是首位
            AppendWidths(bits, EanLeft[digit], startsWithBar: false);
        }
        AppendWidths(bits, EanMiddle, startsWithBar: false);      // 中间保护条：空-条-空-条-空
        for (var i = 7; i <= 12; i++)
            AppendWidths(bits, EanLeft[text[i] - '0'], startsWithBar: true);
        AppendWidths(bits, EanGuard);                              // 结束保护条
        // GS1 对 EAN-13 的左右静区要求是 11 模块,比其他制式的 10 多一个(第 23 棒)
        return new BarcodeEncoding(true, bits.ToString(), text, note, null, QuietZoneModules: 11);
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
        AppendWidths(bits, ItfStart);
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
            AppendWidths(bits, interleaved);
        }
        AppendWidths(bits, ItfEnd);
        return new BarcodeEncoding(true, bits.ToString(), text, note, null);
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

    /// <summary>EAN 的 L 码（和 = 7 模块）；G 码 = 同一条反序，所以这里只存一份，取用时按 +10 索引反转。</summary>
    private static readonly int[][] EanLeft = BuildEanLeftRight();

    /// <summary>首位数字 → 左半 6 位的奇偶排布（1 = 用 G 码）。0x00=LLLLLL、0x0B=LLGLGG…</summary>
    private static readonly int[] Ean13FirstDigitEncodings = { 0x00, 0x0B, 0x0D, 0x0E, 0x13, 0x19, 0x1C, 0x15, 0x16, 0x1A };

    private static readonly int[] ItfStart = { 1, 1, 1, 1 };
    private static readonly int[] ItfEnd = { 3, 1, 1 };

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
