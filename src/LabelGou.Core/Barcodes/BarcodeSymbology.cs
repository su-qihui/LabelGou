namespace LabelGou.Core.Barcodes;

/// <summary>
/// 支持的条码制式（一维码）。
/// <para><strong>为什么只有这四种</strong>：用户 2026-09-09 要「加入条码栏目」，而店里唛头上真会出现的
/// 就是这几类——<see cref="Code128"/> 是通用物流码（字母数字都行），<see cref="Ean13"/> 是零售商品码
/// （TOP 那张样张右上角就是一个 EAN-13），<see cref="Itf14"/> 是箱级 GTIN-14（储运），
/// <see cref="Code39"/> 是最老也最好认的字母数字码（很多老系统在用）。
/// 其余制式（UPC-A / Codabar / POSTNET / MSI …）是别行业的码，加进来只会让下拉更长、错选更多；
/// 真需要时按 <see cref="BarcodeEncoder"/> 的形状补一张表就行。</para>
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
}

/// <summary>制式的人话名字与「这栏该填什么」的提示：界面与报错共用一份，不在两边各写一遍。</summary>
public static class BarcodeSymbologyExtensions
{
    public static string DisplayName(this BarcodeSymbology value) => value switch
    {
        BarcodeSymbology.Code128 => "Code 128（通用，字母数字都行）",
        BarcodeSymbology.Code39 => "Code 39（老系统常用，字母数字 + - . $ / + %）",
        BarcodeSymbology.Ean13 => "EAN-13（商品条码，13 位数字）",
        BarcodeSymbology.Itf14 => "ITF-14（储运箱码 GTIN-14，14 位数字）",
        _ => value.ToString(),
    };

    /// <summary>短名，给下拉与模板元素列表用。</summary>
    public static string ShortName(this BarcodeSymbology value) => value switch
    {
        BarcodeSymbology.Code128 => "Code 128",
        BarcodeSymbology.Code39 => "Code 39",
        BarcodeSymbology.Ean13 => "EAN-13",
        BarcodeSymbology.Itf14 => "ITF-14",
        _ => value.ToString(),
    };

    /// <summary>这一栏要填什么：直接写在界面上，别让人去猜（错选制式 = 印一张扫不出或扫成别的数的码）。</summary>
    public static string DataHint(this BarcodeSymbology value) => value switch
    {
        BarcodeSymbology.Code128 => "可含 0-9、A-Z、a-z、空格与常见标点（ASCII 32~126）。中文编不进去，会挡下来。",
        BarcodeSymbology.Code39 => "只能用 0-9、A-Z、- . 空格 $ / + %（小写会自动转大写并告诉你）。",
        BarcodeSymbology.Ean13 => "12 或 13 位纯数字。填 12 位时自动补第 13 位校验码；填 13 位但校验位不对会挡下来。",
        BarcodeSymbology.Itf14 => "13 或 14 位纯数字。填 13 位时自动补末位校验码；位数是奇数又超过 13 位会挡下来。",
        _ => string.Empty,
    };
}
