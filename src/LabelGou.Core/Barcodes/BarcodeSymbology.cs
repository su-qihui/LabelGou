namespace LabelGou.Core.Barcodes;

/// <summary>
/// 支持的条码制式（一维码）。
/// <para><strong>为什么是这七种</strong>：用户 2026-09-09 要「加入条码栏目」，店里唛头上真会出现的
/// 就是这几类——<see cref="Code128"/> 是通用物流码（字母数字都行），<see cref="Ean13"/> 是零售商品码
/// （TOP 那张样张右上角就是一个 EAN-13），<see cref="Itf14"/> 是箱级 GTIN-14（储运），
/// <see cref="Code39"/> 是最老也最好认的字母数字码（很多老系统在用）。</para>
/// <para><strong>2026-09-11（第 41 棒）按用户点名的「和 Corel BARCODE WIZARD 一样」又补了三个</strong>：
/// <see cref="Ean8"/>（短商品码）、<see cref="UpcA"/>、<see cref="UpcE"/>（北美零售码）。
/// 这三个与 EAN-13 同属 UPC/EAN 一族，<strong>保护条</strong>（每条码两端与正中那几根加长的条）
/// 与<strong>可读数字的分段排版</strong>都按同一套规矩走——用户导出的 CDR 样本
/// <c>labelgou-CL\条码\图形1.svg</c> 就是把这三个当参照物的。</para>
/// <para><strong>枚举值是持久化的</strong>（模板存盘按名字也按值），所以新制式一律<strong>往后追加</strong>，
/// 不许插在中间改动已有成员的值——那会让老模板里的 ITF-14 读成别的码。</para>
/// </summary>
public enum BarcodeSymbology
{
    /// <summary>Code 128（自动在 B/C 之间选档：纯数字且偶数位走 C，长度省一半）。</summary>
    Code128 = 0,

    /// <summary>Code 39（43 个字符 + 起止符 *，宽窄 3 元素为一组）。</summary>
    Code39 = 1,

    /// <summary>EAN-13（13 位数字；给 12 位时自动补校验位）。</summary>
    Ean13 = 2,

    /// <summary>ITF-14 / Interleaved 2 of 5（数字、偶数长度；给 13 位时自动补 GTIN 校验位）。</summary>
    Itf14 = 3,

    /// <summary>EAN-8（8 位数字短商品码；给 7 位时自动补校验位）。67 模块。</summary>
    Ean8 = 4,

    /// <summary>UPC-A（12 位北美零售码；给 11 位时自动补校验位）。95 模块，等同于前面补 0 的 EAN-13。</summary>
    UpcA = 5,

    /// <summary>UPC-E（8 位压缩零售码，首位 0 或 1；给 7 位时自动补校验位）。51 模块。</summary>
    UpcE = 6,

    /// <summary>
    /// Codabar（老式数字码，血库/图书馆/快递单在用；0-9 与 <c>- $ : / . +</c>，
    /// 首尾各一个起止符 A/B/C/D）。每字符 7 个元素，无保护条。
    /// <para>第 41 棒第三轮按用户从 Corel DRAW BARCODE WIZARD 导出的对照样本补的。</para>
    /// </summary>
    Codabar = 7,

    /// <summary>
    /// ITF / 交错 2 of 5（<strong>通用</strong>：数字、偶数长度，不强制 14 位、也不补 GTIN 校验位）。
    /// <para>与 <see cref="Itf14"/> 的区别：ITF-14 是「储运箱码」，13 位自动补第 14 位校验码、
    /// 14 位还要验校验位；这个通用 ITF 只按交错 2 of 5 编，奇数位前面补一个 0。</para>
    /// </summary>
    Itf = 8,

    /// <summary>MSI/Plessey（数字码，老式仓储/货架码；每位 4 条 4 空，无保护条，也没有校验位）。</summary>
    Msi = 9,

    /// <summary>JAN-8——就是 EAN-8 的日本叫法，编码完全相同（CDR 的下拉里也是这么个并列项）。</summary>
    Jan8 = 10,

    /// <summary>JAN-13——就是 EAN-13 的日本叫法，编码完全相同。</summary>
    Jan13 = 11,

    /// <summary>ISBN——图书条码，即 978 开头的 EAN-13；不是 978 开头会挡下来。</summary>
    Isbn = 12,

    /// <summary>ISSN——期刊条码，即 977 开头的 EAN-13；不是 977 开头会挡下来。</summary>
    Issn = 13,

    /// <summary>
    /// 25 码 / Code 2 of 5（<strong>非交错</strong>的矩阵制，也叫 Industrial 2 of 5）。
    /// <para>与 <see cref="Itf"/>（交错制）是<strong>两种不同的码</strong>，别当成一回事：
    /// 交错制把两个数字的宽窄分别塞进「条」和「空」里，一对数字只占 5 条 5 空；
    /// 25 码是<strong>每个数字自己占 5 根条</strong>（其中恰好 2 根宽），
    /// 条与条之间的空<strong>一律是窄的、不携带任何信息</strong>。
    /// 所以同样 13 位数字，25 码要 71 根条、交错制只要 39 根——25 码宽得多。</para>
    /// <para>用户那份 Corel BARCODE WIZARD 样张（<c>labelgou-CL\条码\1234567891231.svg</c>）
    /// 下拉框里列的 <c>code25</c> 就是这一种：实测 71 根条、起始 宽宽窄、结束 宽窄宽、空恒窄。
    /// 原来软件把 <c>code25</c> 当 <see cref="Itf"/> 编，条数都对不上（71 vs 39），所以单独补一个制式。</para>
    /// <para><strong>样张里有一处不照抄</strong>：CDR 把数字 <c>2</c> 编成「窄宽窄窄窄」（只有 1 根宽条），
    /// 整码宽条数 28 根而不是标准要求的 30 根。2 of 5 的定义就是每数字恰 2 根宽条，
    /// 少一根扫不出来——那是 CDR 自己的实现缺陷，按标准码表编。</para>
    /// </summary>
    Code25 = 14,
}

/// <summary>制式的人话名字与「这栏该填什么」的提示：界面与报错共用一份，不在两边各写一遍。</summary>
public static class BarcodeSymbologyExtensions
{
    public static string DisplayName(this BarcodeSymbology value) => value switch
    {
        BarcodeSymbology.Code128 => "Code 128（通用，字母数字都行）",
        BarcodeSymbology.Code39 => "Code 39（老系统常用，字母数字 + - . $ / + %）",
        BarcodeSymbology.Ean13 => "EAN-13（商品条码，13 位数字）",
        BarcodeSymbology.Ean8 => "EAN-8（小程序码，8 位数字）",
        BarcodeSymbology.UpcA => "UPC-A（北美零售码，12 位数字）",
        BarcodeSymbology.UpcE => "UPC-E（北美压缩码，8 位数字）",
        BarcodeSymbology.Codabar => "Codabar（老式数字码，首尾要带起止符 A/B/C/D）",
        BarcodeSymbology.Msi => "MSI/Plessey（老式仓储数字码，没有校验位）",
        BarcodeSymbology.Jan8 => "JAN-8（日本 EAN-8，8 位数字）",
        BarcodeSymbology.Jan13 => "JAN-13（日本 EAN-13，13 位数字）",
        BarcodeSymbology.Isbn => "ISBN（图书条码，978 开头）",
        BarcodeSymbology.Issn => "ISSN（期刊条码，977 开头）",
        BarcodeSymbology.Itf => "ITF（交错 2 of 5，数字、偶数长度）",
        BarcodeSymbology.Itf14 => "ITF-14（储运箱码 GTIN-14，14 位数字）",
        BarcodeSymbology.Code25 => "25 码（非交错 2 of 5，数字；CDR 里的 code25）",
        _ => value.ToString(),
    };

    /// <summary>短名，给下拉与模板元素列表用。</summary>
    public static string ShortName(this BarcodeSymbology value) => value switch
    {
        BarcodeSymbology.Code128 => "Code 128",
        BarcodeSymbology.Code39 => "Code 39",
        BarcodeSymbology.Ean13 => "EAN-13",
        BarcodeSymbology.Ean8 => "EAN-8",
        BarcodeSymbology.UpcA => "UPC-A",
        BarcodeSymbology.UpcE => "UPC-E",
        BarcodeSymbology.Codabar => "Codabar",
        BarcodeSymbology.Msi => "MSI",
        BarcodeSymbology.Jan8 => "JAN-8",
        BarcodeSymbology.Jan13 => "JAN-13",
        BarcodeSymbology.Isbn => "ISBN",
        BarcodeSymbology.Issn => "ISSN",
        BarcodeSymbology.Itf => "ITF",
        BarcodeSymbology.Itf14 => "ITF-14",
        BarcodeSymbology.Code25 => "25 码",
        _ => value.ToString(),
    };

    /// <summary>这一栏要填什么：直接写在界面上，别让人去猜（错选制式 = 印一张扫不出或扫成别的数的码）。</summary>
    public static string DataHint(this BarcodeSymbology value) => value switch
    {
        BarcodeSymbology.Code128 => "可含 0-9、A-Z、a-z、空格与常见标点（ASCII 32~126）。中文编不进去，会挡下来。",
        BarcodeSymbology.Code39 => "只能用 0-9、A-Z、- . 空格 $ / + %（小写会自动转大写并告诉你）。",
        BarcodeSymbology.Ean13 => "12 或 13 位纯数字。填 12 位时自动补第 13 位校验码；填 13 位但校验位不对会挡下来。",
        BarcodeSymbology.Ean8 => "7 或 8 位纯数字。填 7 位时自动补第 8 位校验码；填 8 位但校验位不对会挡下来。",
        BarcodeSymbology.UpcA => "11 或 12 位纯数字。填 11 位时自动补第 12 位校验码；填 12 位但校验位不对会挡下来。",
        BarcodeSymbology.UpcE => "7 或 8 位纯数字，首位必须是 0 或 1。填 7 位时自动补第 8 位校验码。",
        BarcodeSymbology.Codabar => "只能用 0-9 与 - $ : / . +。首尾要么都给起止符 A/B/C/D，要么都不给（不给就自动配 A）。",
        BarcodeSymbology.Msi => "纯数字（这一版不编校验位，表里给什么就编什么）。",
        BarcodeSymbology.Jan8 => "与 EAN-8 相同：7 或 8 位纯数字。",
        BarcodeSymbology.Jan13 => "与 EAN-13 相同：12 或 13 位纯数字。",
        BarcodeSymbology.Isbn => "13 位纯数字，必须 978 开头（图书的 GTIN）。填 12 位自动补校验码。",
        BarcodeSymbology.Issn => "13 位纯数字，必须 977 开头（期刊的 GTIN）。填 12 位自动补校验码。",
        BarcodeSymbology.Itf => "纯数字。奇数位会自动在前面补一个 0（交错 2 of 5 必须两位一组）。",
        BarcodeSymbology.Itf14 => "13 或 14 位纯数字。填 13 位时自动补末位校验码；位数是奇数又超过 13 位会挡下来。",
        BarcodeSymbology.Code25 => "纯数字，不用凑偶数位、也没有校验位（表里给几位就编几位）。" +
                                   "这一种比 ITF 宽得多——同样 13 位数字要 71 根条、ITF 只要 39 根，框不够宽就别选它。",
        _ => string.Empty,
    };

    /// <summary>
    /// 这一族的可读数字是否要按 UPC/EAN 的规矩<strong>分段</strong>排在条码下方
    /// （首位骑在左静区、末位骑在右静区、中间两段各占自己的 7 模块格），而不是整串居中。
    /// <para>用户要的「和 BARCODE WIZARD 一样」主要就是这一条加上保护条——见第 41 棒。</para>
    /// </summary>
    public static bool HasUpcEanHri(this BarcodeSymbology value) => value
        is BarcodeSymbology.Ean13 or BarcodeSymbology.Ean8 or BarcodeSymbology.UpcA or BarcodeSymbology.UpcE;

    /// <summary>这一族有没有「保护条」——两端与正中那几根按规范要高出数据条一截的条。</summary>
    public static bool HasGuardBars(this BarcodeSymbology value) => value.HasUpcEanHri();
}
