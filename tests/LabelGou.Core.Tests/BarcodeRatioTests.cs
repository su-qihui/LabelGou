using LabelGou.Core.Barcodes;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 条码的<strong>比例</strong>钉子（第 56 棒，用户 2026-09-14：「编辑的条码和外面这层显示都不正常，
/// 需显示为正确比例，把这个高固定删了」）。
/// <para>病根：位宽只按框宽算，框多扁条就多粗。用户那只 92 × 12 mm 的框里，9 位 Code 128 的窄线被撑到
/// 0.6 mm 粗、高只有 12 mm —— 数据条高:条宽 ≈ 20:1，而条码的标准是 <strong>69:1</strong>
/// （CDR 样本 <c>labelgou-CL\条码\图形1.svg</c> 实测：数据条 23.366 mm ÷ 模块 0.33795 mm = 69.14）。
/// 这一批测试钉三件事：扁框必须被高度拉回标准比例、等比框逐毫米不许动（那是第 41 棒对过 CDR 的成果）、
/// 以及拉回比例后条组在框里居中且可读数字跟着条走。</para>
/// <para><strong>为什么不改面板那侧就完事</strong>：面板给框的比例管不到用户自己在编辑器里把框拉扁，
/// 所以判据落在 <see cref="BarcodeBars.Build"/> 这一层——五个出口共用它，一处修好五处都好。</para>
/// </summary>
public class BarcodeRatioTests
{
    private const string CdrSample = "4518518121156";      // 用户从 CDR 导出的那张 EAN-13

    /// <summary>CDR 样本的对象框：38.1 × 27.43 mm（等比，一位 0.33795 mm × 81 份 ≈ 27.37）。</summary>
    private const double CdrFrameW = 38.1, CdrFrameH = 27.43;

    [Fact]
    public void 扁框里的条被高度拉回标准比例_不再是一根根粗杠()
    {
        // 用户截图那只：92 mm 宽、12 mm 高的 Code 128（占位串 123456789）。
        // 从前位宽 = (92 − 静区) ÷ 模块数 = 0.58 mm → 条高才 12 mm，比例 20:1；
        // 现在条区高把位宽压到 条区高 ÷ 69。
        var bar = Build(BarcodeSymbology.Code128, "123456789", width: 92, height: 12);
        var modules = bar.BarsHeight / bar.ModuleMm;

        Assert.True(modules >= 60, $"数据条高只有条宽的 {modules:0.#} 倍：条又被拉肥了（标准是 69 倍）");
        Assert.True(modules <= 69.5, $"数据条高:条宽 = {modules:0.#}，比标准 69 还高——比例反过来了");
        // 12 mm 高的框里只能容 0.13 mm 的窄线：比手持枪底线还窄，必须报话且说对症（拉高/拉窄，不是拉宽）
        Assert.NotNull(bar.Warning);
        Assert.Contains("拉高", bar.Warning);
    }

    [Fact]
    public void 等比框逐毫米照旧_封顶只在被高卡住时生效()
    {
        // 第 41 棒拿 CDR 那张样本对过毫米：这一条是防「修比例把对的那半也改了」。
        var bar = Build(BarcodeSymbology.Ean13, CdrSample, CdrFrameW, CdrFrameH);

        Assert.Equal(0.3378, bar.ModuleMm, 4);                     // 样本 0.33795，向下取整到 0.0001
        Assert.Equal(3.0, bar.Bars[0].X, 3);                       // 左静区仍是 3 mm：没居中、没挪位
        Assert.Null(bar.Warning);
        var last = bar.Bars[^1];
        Assert.InRange(last.X + last.Width, CdrFrameW - 3.0 - 0.05, CdrFrameW - 3.0 + 0.05);
    }

    [Fact]
    public void 拉回比例后条组居中_左右留白相等()
    {
        var encoding = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        var g = BarcodeBars.Build(encoding, 4, 4, 132, 4, 11.33);  // 132 × 14 的扁框（第五轮出事那只）扣掉数字带
        Assert.True(g.CenterOffsetMm > 1, "扁框里条没居中：右边空一大截、左边贴死，看着像渲染坏了");

        var left = g.Bars[0].X - 4;
        var lastBar = g.Bars[^1];
        var right = 4 + 132 - (lastBar.X + lastBar.Width);
        Assert.Equal(left, right, 2);
        Assert.True(left > 40, $"留白 {left:0.#} mm：条组没被摆到框正中");
    }

    [Fact]
    public void 居中时骑静区的首末数字跟着条走_不落在留白正中()
    {
        var encoding = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        var g = BarcodeBars.Build(encoding, 4, 4, 132, 4, 11.33);
        var hri = BarcodeBars.BuildHri(encoding, 4, 132, g);
        var barsLeft = g.Bars[0].X;
        var lastBar = g.Bars[^1];
        var barsRight = lastBar.X + lastBar.Width;

        // EAN-13 的首位骑左静区：居中之后它仍要从「条的左边界 − 静区」起，不许跑到留白正中。
        Assert.Equal(barsLeft - 3.0, hri[0].X, 3);
        // 整串可读数字都得留在条的两侧各一个静区之内——居中偏移只能挪基准，不能把数字甩进空白。
        Assert.All(hri, h => Assert.InRange(h.X + h.Width / 2, barsLeft - 3.0, barsRight + 3.0));
    }

    [Fact]
    public void 标准比例高把CDR那只框原样还原()
    {
        var encoding = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        Assert.InRange(BarcodeBars.NaturalHeightMm(encoding, CdrFrameW), 27.2, 27.5);   // CDR 实测 27.43

        // 不印下面那串数字时，顶距与数字带都不留：81 份变 75 份
        Assert.Equal(BarcodeBars.NaturalHeightMm(encoding, CdrFrameW) * 75 / 81,
            BarcodeBars.NaturalHeightMm(encoding, CdrFrameW, showText: false), 3);
    }

    [Fact]
    public void 反算宽度不自相矛盾_等比框里数据条恰是69根窄线()
    {
        // 280 × 200 的唛头底部通栏（内容 272 宽）按标准比例铺满要 143 mm 高 → 面板只许它占内容高的三成。
        // ProportionalBox 改成「按高定宽」后，这只框必须自己对自己诚实：放进去就是 69:1，且条铺满宽。
        var encoding = BarcodeEncoder.Encode("123456789", BarcodeSymbology.Code128);
        var (w, h) = BarcodeBars.ProportionalBox(encoding, 272, 57.6);
        Assert.True(w < 272 && h > 50, $"框 {w:0.#} × {h:0.#}：没被高卡住或卡过了头");

        var bar = Build(BarcodeSymbology.Code128, "123456789", w, h);
        Assert.InRange(bar.BarsHeight / bar.ModuleMm, 68.5, 69.5);
        Assert.Null(bar.Warning);                                    // 0.7 mm 的窄线，扫得妥妥的
        var last = bar.Bars[^1];
        Assert.True(last.X + last.Width > w * 0.9, $"条只铺到 {last.X + last.Width:0.#} / {w:0.#} mm：等比框里不该留这么大空白");
    }

    [Fact]
    public void 反算宽度也护得住固定毫米静区()
    {
        // UPC/EAN 一族的静区是固定 3 mm、不随框宽缩放：拿比例反除会把它一起缩掉，
        // 于是宽算小了、高给多了，画出来顶到框外。这里量的是「按高定宽」之后条正好铺到右静区左边。
        var encoding = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        var (w, h) = BarcodeBars.ProportionalBox(encoding, 272, 40);
        var g = BarcodeBars.Build(encoding, 0, 0, w, 0, h - h / BarcodeBars.HeightUnits - h * BarcodeBars.TextBandUnits / BarcodeBars.HeightUnits);

        Assert.True(g.CenterOffsetMm < 0.5, $"等比框里还居中偏移 {g.CenterOffsetMm:0.##} mm：宽给小了");
        var last = g.Bars[^1];
        Assert.True(last.X + last.Width <= w + 1e-6, "最后一根条推出框外了");
        Assert.True(last.X + last.Width > w - 3.0 - 0.2, "右边空过头：宽给大了");
    }

    // ---------- 帮助 ----------

    /// <summary>把一只条码按给定毫米框排一次版，拿回版面里那一条（生产那条 <see cref="LayoutEngine"/> 路）。</summary>
    private static BarcodeItem Build(BarcodeSymbology symbology, string value, double width, double height)
    {
        var template = new LabelTemplate { Name = "比例看样", WidthMm = 280, HeightMm = 200, BorderMm = 0 };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode,
            Text = "{{col:条码}}",
            Symbology = symbology,
            ShowBarcodeText = true,
            FontSizePt = 8,
            X = 0,
            Y = 0,
            Width = width,
            Height = height,
        });
        var record = MarkRecord.Builder()
            .SetRow(1, "比例.xlsx")
            .Set(MarkFieldKey.Consignee, "WALMART")
            .SetCustom("col:条码", value)
            .Build();
        return LayoutEngine.Build(template, record, new LayoutContext(1, 1))
            .Items.OfType<BarcodeItem>().Single();
    }
}
