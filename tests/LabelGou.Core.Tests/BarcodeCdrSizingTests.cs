using LabelGou.Core.Barcodes;
using LabelGou.Core.Layout;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 条码的<strong>尺寸模型 = CorelDRAW 条码向导那四格</strong>（第 58 棒）。
/// <para>用户 2026-09-14 拿四张图说事：三张是 CorelDRAW 自己的「条码向导」（打印机分辨率 300 dpi、
/// 条形码宽度减少值 1 像素、缩放比例 100 %、条形码高度 1.0、宽度压缩率 2.0 到 1、只读的符号宽度
/// <c>32.173447 mm</c>），一张是我们编辑器里那只扁码。他的判词是
/// 「<strong>你把这套参数搬过来，别自己发明 69:1</strong>」。</para>
/// <para>所以这一批测试的期望值<strong>不是我们算给自己看的数，而是 CDR 那两格读数</strong>：
/// EAN-13 的 32.173447 mm（用户截图）与 CodaBar 12 位的 2.079999 英寸（我开向导实测，
/// 取证记在 <c>labelgou-other\_probe\b58-barcode-exe\cdr-wizard-measured.md</c>）。
/// 两处互算出同一个 X = 4 像素 @300 dpi = 0.338667 mm。</para>
/// <para>与第 56 棒那套（被退回作废，存档 <c>cfa38ac</c>）的分别要说清：那次是我拿"框太扁就缩条+居中"
/// 打补丁，比例是我推的；这次尺寸有<strong>来源</strong>——X 由打印分辨率与缩放比例定，框反而是算出来的。</para>
/// </summary>
public class BarcodeCdrSizingTests
{
    private const string CdrSample = "4518518121156";        // 用户从 CDR 导出的那张 EAN-13

    [Fact]
    public void X尺寸锚点_EAN13在300dpi满缩放时的符号宽度就是向导那一格()
    {
        var sizing = new BarcodeSizing();                      // 向导默认：300 dpi、100 %
        Assert.Equal(0.338667, sizing.ModuleMm, 5);            // 4 像素 @300dpi = 0.338667 mm

        var ean13 = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        Assert.Equal(95, BarcodeSizing.NarrowElements(ean13));
        Assert.InRange(sizing.SymbolWidthMm(ean13), 32.1733, 32.1735);   // 截图读数 32.173447
    }

    [Fact]
    public void X尺寸锚点_CodaBar十二位实测那格也落在同一个X上()
    {
        // 我开着 BarCode.exe 实测：CodaBar、123456789012、300 dpi、100 %、宽窄比 2.5:1 → 符号宽度 2.079999 英寸。
        var codabar = BarcodeEncoder.Encode("123456789012", BarcodeSymbology.Codabar);
        var mm = new BarcodeSizing().SymbolWidthMm(codabar);
        Assert.Equal(156, BarcodeSizing.NarrowElements(codabar));          // 312 位 ÷ 每窄元素 2 位
        Assert.InRange(mm, 52.831, 52.833);
        Assert.InRange(mm / 25.4, 2.0799, 2.0801);                          // 换回英寸＝向导显示的那一格
    }

    [Fact]
    public void 缩放比例翻倍X就翻倍_分辨率翻倍毫米宽不变()
    {
        Assert.Equal(new BarcodeSizing(ScalePercent: 200).ModuleMm, new BarcodeSizing().ModuleMm * 2, 6);
        // 600 dpi 时 X 是 8 个像素：物理宽度仍是 0.0133 英寸——这正是"向上取整到整像素"的意思
        Assert.Equal(new BarcodeSizing(Dpi: 600).ModuleMm, new BarcodeSizing().ModuleMm, 6);
        Assert.True(new BarcodeSizing(Dpi: 203).ModuleMm > new BarcodeSizing().ModuleMm);   // 低分辨率只能进到更大的整像素
    }

    [Fact]
    public void 扁框里有尺寸参数就不再按框宽算_条不肥且整组居中()
    {
        // 用户截图那只：92 mm 宽 × 12 mm 高。从前位宽 = (92 − 静区) ÷ 模块数 ≈ 0.58 mm → 粗杠。
        var bar = Build("123456789", BarcodeSymbology.Code128, 92, 12, new BarcodeSizing());
        Assert.Equal(new BarcodeSizing().ModuleMm, bar.ModuleMm, 4);       // X 来自参数，与框宽无关
        Assert.True(bar.Bars[0].X > 4, "符号比框窄却没居中：整组条还贴在左边");
        var last = bar.Bars[^1];
        Assert.True(last.X + last.Width < 92, "条铺出了框");
    }

    [Fact]
    public void 老模板没有尺寸参数也照CDR的比例封顶_扁框拉不肥条()
    {
        // 同一只扁框，但元素是旧文件里那种（BarcodeSize = null）：也不能再把条撑成 0.58 mm 粗杠。
        var legacy = Build("123456789", BarcodeSymbology.Code128, 92, 12, sizing: null);
        Assert.True(legacy.ModuleMm < 0.20, $"老模板这只扁框里窄线仍有 {legacy.ModuleMm:0.###} mm——封顶没生效");
        Assert.True(legacy.BarsHeight / legacy.ModuleMm > 55,
            $"数据条高只有条宽的 {legacy.BarsHeight / legacy.ModuleMm:0.#} 倍：还是被压扁的样子");
    }

    [Fact]
    public void 等比框一个像素都不许动_CDR那张样本照旧()
    {
        // 防"修扁框把对的那半也改了"：38.1 × 27.43 正是 CDR 自己的框（第 41 棒逐毫米对过）。
        var bar = Build(CdrSample, BarcodeSymbology.Ean13, 38.1, 27.43, sizing: null);
        Assert.Equal(0.3378, bar.ModuleMm, 4);                 // 样本 0.33795，向下取整到 0.0001
        Assert.Equal(3.0, bar.Bars[0].X, 3);                   // 没有居中偏移：等比框铺满
        Assert.Null(bar.Warning);
    }

    [Fact]
    public void 宽度减少值真的把条画窄而位置不动()
    {
        var encoding = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        var sizing = new BarcodeSizing(WidthReductionPx: 1);
        var reduced = BarcodeBars.Build(encoding, 0, 0, 60, 0, 43, sizing);
        var plain = BarcodeBars.Build(encoding, 0, 0, 60, 0, 43, sizing with { WidthReductionPx = 0 });

        var onePxMm = 25.4 / BarcodeSizing.DefaultDpi;
        Assert.Equal(plain.Bars[0].Width - onePxMm, reduced.Bars[0].Width, 4);
        Assert.Equal(plain.Bars[1].X, reduced.Bars[1].X, 6);   // 只画窄，不挪位置——空白才是让出来的那一截
    }

    [Fact]
    public void 塞不下时降的是缩放比例而不是把条压扁()
    {
        var ean13 = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        var fitted = new BarcodeSizing().FittedTo(ean13, 40, 20);
        Assert.True(fitted.ScalePercent < 100, "40 mm 的框塞不下 32 mm 的码？那这格白给");
        var (w, h) = fitted.BoxOf(ean13);
        Assert.True(w <= 40.0001 && h <= 20.0001, $"降完缩放仍是 {w:0.##} × {h:0.##}：没塞进去");
        Assert.True(fitted.ModuleMm < new BarcodeSizing().ModuleMm);   // X 变小（整像素往下掉），不是把条压扁
    }

    [Fact]
    public void 尺寸参数进得了模板文件_缺字段就是老模板()
    {
        var with = new LabelTemplate { Name = "带尺寸的条码", WidthMm = 100, HeightMm = 80, BorderMm = 0 };
        with.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode, Text = "123456789", Symbology = BarcodeSymbology.Code128,
            BarcodeSize = new BarcodeSizing(Dpi: 600, ScalePercent: 150, HeightFactor: 0.6),
        });
        var legacy = new LabelTemplate { Name = "老模板条码", WidthMm = 100, HeightMm = 80, BorderMm = 0 };
        legacy.Elements.Add(new TemplateElement { Kind = ElementKind.Barcode, Text = "123456789", Symbology = BarcodeSymbology.Code128 });

        Assert.Contains("barcodeSize", TemplateStore.ToJson(with), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("barcodeSize", TemplateStore.ToJson(legacy), StringComparison.OrdinalIgnoreCase);

        // 真走一遍盘：存下去再读回来，四格数值一个都不能漂
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-b58-" + Guid.NewGuid().ToString("N")[..6]);
        var store = new TemplateStore(dir);
        Assert.True(store.Save(with).Saved);
        var back = store.GetById(with.Id)!;
        var size = back.Elements.Single(e => e.Kind == ElementKind.Barcode).BarcodeSize;
        Assert.NotNull(size);
        Assert.Equal(600, size!.Dpi);
        Assert.Equal(150, size.ScalePercent, 3);
        Assert.Equal(0.6, size.HeightFactor, 3);
        Assert.Null(store.GetById(legacy.Id)?.Elements[0].BarcodeSize);
    }

    /// <summary>
    /// 各制式的默认框必须"正好包住符号"（第 62 棒审计修的两处都在这里露馅）：
    /// 符号宽度用整除截断会把奇数位宽的制式（Code 39）少算半个窄元素 → 框比符号窄 → 退回按框宽算、X 模型失效；
    /// 静区多乘一次 NarrowBits 会让框凭空宽出一截 → 条码永远居中留白。两头都不该出现。
    /// </summary>
    [Theory]
    [InlineData(BarcodeSymbology.Code128, "123456789")]
    [InlineData(BarcodeSymbology.Code39, "ABC")]              // 模块数 143＝奇数，整除会少半个窄元素
    [InlineData(BarcodeSymbology.Codabar, "1234567891231")]   // 宽窄比 2.5:1
    [InlineData(BarcodeSymbology.Code25, "1234567891231")]
    [InlineData(BarcodeSymbology.Ean13, CdrSample)]
    public void 默认框正好包住符号_两头只剩静区(BarcodeSymbology symbology, string value)
    {
        var encoding = BarcodeEncoder.Encode(value, symbology);
        Assert.True(encoding.Ok, encoding.Error);
        var sizing = new BarcodeSizing();
        var (w, _) = sizing.BoxOf(encoding);

        var g = BarcodeBars.Build(encoding, 0, 0, w, 0, 30, sizing);
        Assert.True(g.CenterOffsetMm < 0.01, $"{symbology}：默认框里还留了 {g.CenterOffsetMm:0.##} mm 居中偏移——框与符号对不上");
        Assert.True(g.Warning is null || !g.Warning.Contains("比缩放比例要求的符号还窄"), $"{symbology}：{g.Warning}");
        // 两条路必须用同一个静区数（多乘一次 NarrowBits 就是在这里露馅）：
        // BoxOf 给的框宽减去符号宽度再除二，要正好等于 Build 实际用的静区。
        Assert.Equal((w - sizing.SymbolWidthMm(encoding)) / 2, g.QuietZoneMm, 4);
    }

    // ---------- 帮助 ----------

    private static BarcodeItem Build(string value, BarcodeSymbology symbology, double width, double height, BarcodeSizing? sizing)
    {
        var template = new LabelTemplate { Name = "尺寸看样", WidthMm = 280, HeightMm = 200, BorderMm = 0 };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode,
            Text = "{{col:条码}}",
            Symbology = symbology,
            ShowBarcodeText = true,
            FontSizePt = 8,
            BarcodeSize = sizing,
            X = 0,
            Y = 0,
            Width = width,
            Height = height,
        });
        var record = MarkRecord.Builder()
            .SetRow(1, "尺寸.xlsx")
            .Set(MarkFieldKey.Consignee, "WALMART")
            .SetCustom("col:条码", value)
            .Build();
        return LayoutEngine.Build(template, record, new LayoutContext(1, 1))
            .Items.OfType<BarcodeItem>().Single();
    }
}
