using LabelGou.Core.Barcodes;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 条码编码器与条码版面的测试（第 17 棒，用户 2026-09-09：「加入条码栏目—条码一般是不同的表格里会有数字要对应填入调成正确的」）。
/// <para><strong>这些期望位串不是手算的</strong>：全部来自 <c>labelgou-other\_probe\b17-barcode\oracle</c> 那次对拍——
/// 拿 ZXing.Net 的 writer 逐位比（Code 128-C / Code 39 / EAN-13 / ITF 共 260+ 项零差异），
/// 再拿 ZXing 的 reader 把我们编的每一串都读回一遍（0 项读不回）。
/// 对拍日志存在 <c>labelgou-other\artifacts\m7-b17\barcode-oracle.txt</c>。</para>
/// <para>这一批测试里最关键的是 <see cref="Code128_校验位口径_锚定与ZXing一致"/>：
/// 第一版按「数据权重从 2 起」写，逐位看一切正常，但 ZXing 的 reader 把所有码都判成校验不过——
/// 也就是印出去全是废码。锚死位串就是为了让这种错第二次发生当场变红。</para>
/// </summary>
public class BarcodeEncoderTests
{
    // ---------- Code 128 ----------

    [Fact]
    public void Code128_校验位口径_锚定与ZXing一致()
    {
        var abc = BarcodeEncoder.Encode("ABC", BarcodeSymbology.Code128);
        Assert.True(abc.Ok, abc.Error);
        Assert.Equal(
            "11010010000101000110001000101100010001000110110011011001100011101011",
            abc.Bits);

        var wiki = BarcodeEncoder.Encode("WIKIPEDIA", BarcodeSymbology.Code128);
        Assert.Equal(
            "11010010000111010001101100010001010110001110110001000101110111011010001101000101100010001100010001010100011000110010111001100011101011",
            wiki.Bits);

        // 每个符号 11 模块、Stop 13 模块：起始+9 数据+校验 = 11 个符号 → 11×11+13
        Assert.Equal(134, wiki.Modules);
    }

    [Fact]
    public void Code128_纯数字偶数位走C档_长度省一半()
    {
        // 6 位数字走 C 档 = 3 个符号；走 B 档要 6 个。箱号动辄十几位，省的是真金白银的印刷宽度。
        Assert.Equal(68, BarcodeEncoder.Encode("123456", BarcodeSymbology.Code128).Modules);
        // 奇数位没法两位一组，退回 B 档：5 个数据符 → 11 + 5×11 + 11 + 13 = 90
        Assert.Equal(90, BarcodeEncoder.Encode("12345", BarcodeSymbology.Code128).Modules);
        // 含字母 → B 档，3 个字符 3 个符号
        Assert.Equal(68, BarcodeEncoder.Encode("ABC", BarcodeSymbology.Code128).Modules);
    }

    [Fact]
    public void Code128_装不下的字符挡下来不静默删()
    {
        var r = BarcodeEncoder.Encode("唛头123", BarcodeSymbology.Code128);
        Assert.False(r.Ok);
        Assert.Contains("装不下字符", r.Error);
        // 不能替用户猜该删哪几个字：删出来的码是一个不相干的码
        Assert.DoesNotContain("已", r.Error);
    }

    [Fact]
    public void 首尾空白去掉时必须说出来()
    {
        var r = BarcodeEncoder.Encode("  123456  ", BarcodeSymbology.Code128);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("123456", r.Data);
        Assert.Contains("空白已去掉", r.Note);
    }

    // ---------- Code 39 ----------

    [Fact]
    public void Code39_小写转大写并留话()
    {
        var r = BarcodeEncoder.Encode("abc", BarcodeSymbology.Code39);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("ABC", r.Data);
        Assert.Contains("大写", r.Note);
        // 5 个符号（* A B C *）× 12 模块 + 4 条字符间窄空
        Assert.Equal(64, r.Modules);
        Assert.StartsWith("1001011011010", r.Bits);       // 起始符 * 的形状
    }

    [Fact]
    public void Code39_字符集外挡下来()
    {
        var r = BarcodeEncoder.Encode("A_B", BarcodeSymbology.Code39);
        Assert.False(r.Ok);
        Assert.Contains("装不下字符", r.Error);
    }

    // ---------- EAN-13 ----------

    [Theory]
    [InlineData("400638133393", 1)]     // GS1 文档里的经典样例
    [InlineData("590123412345", 7)]
    [InlineData("000000000000", 0)]
    public void GTIN模10校验位_对得上公开样例(string twelve, int expected)
        => Assert.Equal(expected, BarcodeEncoder.GtinCheckDigit(twelve));

    [Fact]
    public void GTIN校验位_非数字返回null不猜()
        => Assert.Null(BarcodeEncoder.GtinCheckDigit("400638I3339"));

    [Fact]
    public void EAN13_给12位就补第13位并写进说明()
    {
        var r = BarcodeEncoder.Encode("400638133393", BarcodeSymbology.Ean13);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("4006381333931", r.Data);
        Assert.Contains("补上第 13 位校验码", r.Note);
        Assert.Equal(95, r.Modules);                                  // EAN-13 整码固定 95 模块
        Assert.StartsWith("101", r.Bits);                             // 起始保护条
        Assert.EndsWith("101", r.Bits);                               // 结束保护条
        Assert.Equal("01010", r.Bits.Substring(45, 5));               // 中间保护条在 3+42 之后
    }

    [Fact]
    public void EAN13_校验位本来就错时不许悄悄改掉()
    {
        var r = BarcodeEncoder.Encode("4006381333930", BarcodeSymbology.Ean13);
        Assert.False(r.Ok);
        Assert.Contains("校验码不对", r.Error);
        // 这种时候改一下就是一张对得上别的商品的码，必须让人先去核对原单据
        Assert.Contains("核对", r.Error);
    }

    [Fact]
    public void EAN13_位数不对报错_含非数字也报错()
    {
        Assert.Contains("12 或 13", BarcodeEncoder.Encode("12345678901", BarcodeSymbology.Ean13).Error);
        Assert.Contains("只能编数字", BarcodeEncoder.Encode("40063813339A1", BarcodeSymbology.Ean13).Error);
    }

    // ---------- ITF-14 ----------

    [Fact]
    public void ITF14_给13位补第14位_形状对得上()
    {
        var r = BarcodeEncoder.Encode("1234567890123", BarcodeSymbology.Itf14);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(14, r.Data.Length);
        Assert.Contains("补上第 14 位校验码", r.Note);
        // 起始 4 + 7 组×18 + 结束 5
        Assert.Equal(135, r.Modules);
        Assert.StartsWith("1010", r.Bits);
        Assert.EndsWith("11101", r.Bits);
    }

    [Fact]
    public void ITF14_奇数长度又不到13位时挡下来()
    {
        var r = BarcodeEncoder.Encode("1234567", BarcodeSymbology.Itf14);
        Assert.False(r.Ok);
        Assert.Contains("偶数", r.Error);
    }

    // ---------- 几何（毫米） ----------

    [Fact]
    public void 铺条时静区算在框内且模块向下取整()
    {
        var encoding = BarcodeEncoder.Encode("4006381333931", BarcodeSymbology.Ean13);
        var g = BarcodeBars.Build(encoding, 0, 0, 100, 0, 10);
        Assert.Null(g.Warning);
        // 115 个模块（95 + 两侧各 10）铺进 100 mm → 往下取整到 0.01
        Assert.Equal(0.86, g.ModuleMm, 3);
        Assert.Equal(8.6, g.QuietZoneMm, 3);
        var last = g.Bars[^1];
        Assert.True(last.X + last.Width <= 100 + 1e-9, "最后一根条不许推出框外");
        Assert.True(g.Bars.Count > 0);
    }

    [Fact]
    public void 模块窄到扫不出时照样画但必须警告()
    {
        var encoding = BarcodeEncoder.Encode("4006381333931", BarcodeSymbology.Ean13);
        var g = BarcodeBars.Build(encoding, 0, 0, 20, 0, 10);
        Assert.InRange(g.ModuleMm, 0.01, BarcodeBars.MinModuleMm);
        Assert.Contains("0.17", g.Warning);           // 20/(95+20) = 0.1739 → 0.17
        Assert.Contains("扫", g.Warning);
    }

    [Fact]
    public void 框太小装不下时不返回半根条()
    {
        var encoding = BarcodeEncoder.Encode("10654321000015", BarcodeSymbology.Itf14);
        var g = BarcodeBars.Build(encoding, 0, 0, 1.5, 0, 10);      // 155 个模块铺进 1.5 mm → 一根模块不到 0.01
        Assert.Empty(g.Bars);
        Assert.Contains("装不下", g.Warning);
    }

    // ---------- 版面与闸门 ----------

    private static LabelTemplate BarcodeOnly(string text, BarcodeSymbology symbology, bool showText = true)
    {
        var template = new LabelTemplate { Name = "只有一条码", WidthMm = 140, HeightMm = 100, BorderMm = 0 };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode,
            Text = text,
            Symbology = symbology,
            ShowBarcodeText = showText,
            FontSizePt = 8,
            X = 4,
            Y = 80,
            Width = 132,
            Height = 16,
        });
        return template;
    }

    private static MarkRecord RecordWithBarcode(string value) => MarkRecord.Builder()
        .SetRow(1, "样例.xlsx")
        .Set(MarkFieldKey.Consignee, "WALMART")
        .SetCustom("col:条码", value)
        .Build();

    [Fact]
    public void 条码列有好值时不点亮待核闸门()
    {
        var layout = LayoutEngine.Build(
            BarcodeOnly("{{col:条码}}", BarcodeSymbology.Ean13), RecordWithBarcode("4006381333931"), new LayoutContext(1, 1));
        var bar = Assert.Single(layout.Items.OfType<BarcodeItem>());
        Assert.Equal("4006381333931", bar.Data);
        Assert.Null(bar.Error);
        Assert.False(bar.Flagged);
        Assert.False(layout.HasUnconfirmed);
        Assert.True(bar.Bars.Count > 0);
    }

    [Fact]
    public void 条码编不出来时和待核字段同级_拦住打印闸门()
    {
        var layout = LayoutEngine.Build(
            BarcodeOnly("{{col:条码}}", BarcodeSymbology.Ean13), RecordWithBarcode("6901234567890"), new LayoutContext(1, 1));
        var bar = Assert.Single(layout.Items.OfType<BarcodeItem>());
        Assert.NotNull(bar.Error);
        Assert.True(bar.Flagged);
        Assert.True(layout.HasUnconfirmed, "错码必须被打印闸门拦下——宁可不出纸也不出一张错码");
        Assert.Empty(bar.Bars);
    }

    [Fact]
    public void 那一格空着时条码整条隐藏而不是画个框()
    {
        var layout = LayoutEngine.Build(
            BarcodeOnly("{{col:条码}}", BarcodeSymbology.Code128), RecordWithBarcode(""), new LayoutContext(1, 1));
        Assert.Empty(layout.Items.OfType<BarcodeItem>());
        Assert.Equal(1, layout.HiddenElementCount);
    }

    [Fact]
    public void 可读数字那一刀只在版面里算一次()
    {
        var withText = LayoutEngine.Build(
            BarcodeOnly("{{col:条码}}", BarcodeSymbology.Code128, showText: true),
            RecordWithBarcode("BOX-000123"), new LayoutContext(1, 1)).Items.OfType<BarcodeItem>().Single();
        var withoutText = LayoutEngine.Build(
            BarcodeOnly("{{col:条码}}", BarcodeSymbology.Code128, showText: false),
            RecordWithBarcode("BOX-000123"), new LayoutContext(1, 1)).Items.OfType<BarcodeItem>().Single();

        Assert.True(withText.ShowText);
        Assert.InRange(withText.BarsHeight, 1, withText.Height);
        Assert.True(withText.BarsHeight < withText.Height, "留了印数字的一条");
        // 不印数字时条吃满整格高（渲染端不许再自己减一次，否则五出口会漂）
        Assert.Equal(withoutText.Height, withoutText.BarsHeight, 3);
    }

    [Fact]
    public void 模板校验_条码没绑数据是错误_绑了才过()
    {
        var empty = TemplateValidator.Validate(BarcodeOnly("  ", BarcodeSymbology.Code128));
        Assert.True(empty.HasError());
        Assert.Contains(empty.ErrorMessages(), m => m.Contains("没绑数据", StringComparison.Ordinal));

        var ok = TemplateValidator.Validate(BarcodeOnly("{{col:条码}}", BarcodeSymbology.Code128));
        Assert.False(ok.HasError(), string.Join("；", ok.ErrorMessages()));
    }

    [Fact]
    public void 制式叫法与数据提示一份不多写两遍()
    {
        // 下拉、模板元素列表、报错文案都从这三处取名字；两边各写一遍迟早漂
        Assert.Equal("Code 128", BarcodeSymbology.Code128.ShortName());
        Assert.Contains("13 位", BarcodeSymbology.Ean13.DataHint());
        Assert.Contains("Code 128", BarcodeSymbology.Code128.DisplayName());
    }
}
