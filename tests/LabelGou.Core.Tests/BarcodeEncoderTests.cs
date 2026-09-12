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
    public void Code128_数字成对压C档_落单末位才切B()
    {
        // 6 位数字全压 C 档 = StartC + 12,34,56 + check = 5 个 11 模块符号 + Stop 13 = 68。
        Assert.Equal(68, BarcodeEncoder.Encode("123456", BarcodeSymbology.Code128).Modules);
        // 第 42 棒改前瞻式换档（照 ZXing chooseCode + CDR 样张实测）：奇数位不再整串退 B 档，
        // 而是「成对的压 C、落单的末位切 B」："12345" → StartC + 12 + 34 + CodeB + '5' + check
        // = 6 个 11 模块符号 + Stop 13 = 79（旧口径整串 B 档要 90，多一个符号的印刷宽度）。
        Assert.Equal(79, BarcodeEncoder.Encode("12345", BarcodeSymbology.Code128).Modules);
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
        // 第 42 棒：宽窄比改 2.5 : 1（窄 = 2 位、宽 = 5 位），照 CDR 样张实测。
        // 5 个符号（* A B C *）×（3 宽 × 5 + 6 窄 × 2 = 27 位）+ 4 条字符间窄空 × 2 = 143。
        Assert.Equal(143, r.Modules);
        // 起始符 *（0x094）：窄宽窄窄宽窄宽窄窄 → 条2 空5 条2 空2 条5 空2 条5 空2 条2
        Assert.StartsWith("11000001100111110011111001100", r.Bits);
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
        // 第 42 棒：宽窄比 2.5 : 1（窄 = 2 位、宽 = 5 位）。
        // 起始（窄窄窄窄 = 4×2）+ 7 组×（2宽+3窄 条 & 空 = 每组 18 位×... 实际每组 26 位）+ 结束（宽窄窄 = 5+2+2）。
        // 元素总数不变、每个窄翻成 2 位、宽翻成 5 位：旧 135 位 → 新 241 位。
        Assert.Equal(241, r.Modules);
        Assert.StartsWith("11001100", r.Bits);           // 起始：条2 空2 条2 空2
        Assert.EndsWith("111110011", r.Bits);            // 结束：条5 空2 条2（宽窄窄）
    }

    [Fact]
    public void ITF14_奇数长度又不到13位时挡下来()
    {
        var r = BarcodeEncoder.Encode("1234567", BarcodeSymbology.Itf14);
        Assert.False(r.Ok);
        Assert.Contains("偶数", r.Error);
    }

    [Fact]
    public void ITF14_14位校验码不符要拒_与EAN13同一口径()
    {
        // 旧口径 14 位直接编:GTIN 抄错一位的表会被静默印成扫不回原数的箱码(第 23 棒)。
        // "10654321000015" 的第 14 位按前 13 位算应是 9。
        var r = BarcodeEncoder.Encode("10654321000015", BarcodeSymbology.Itf14);
        Assert.False(r.Ok);
        Assert.Contains("校验码", r.Error);
        Assert.Contains("9", r.Error);
    }

    [Fact]
    public void ITF14_14位校验码正确就照编()
    {
        var r = BarcodeEncoder.Encode("10654321000019", BarcodeSymbology.Itf14);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(14, r.Data.Length);
        Assert.Null(r.Note);
    }

    // ---------- 第 41 棒（2026-09-11）：保护条 + UPC/EAN 的分段数字 ----------
    //
    // 用户点名「做出来效果要和在 Corel BARCODE WIZARD 一样」，参照物是他自己从 CorelDRAW 导出的
    // labelgou-CL\条码\图形1.svg。下面那些数字——保护条比数据条高 6 个模块、每个数字占 7 模块、
    // 首位骑在左静区——全部是从那份 CDR 矢量样本里逐元素反解出来的，不是照记忆写的：
    // 样本里 59 个元素、95 个模块的结构与标准 EAN-13 编码逐位吻合（首位 4 决定奇偶 LGL LGG，
    // 左半 518518、右半 121156），数据条高 23.366 mm、保护条高 25.398 mm，差 6.01 个模块。
    //
    // 编码本身（EAN-8 / UPC-A / UPC-E 的码表、校验位、压缩规则）另有一轮 ZXing 对拍背书，
    // 见 _probe\b17-barcode\oracle：EAN-8 13 项、UPC-A 13 项、UPC-E 204 项，位串逐位一致、
    // reader 读回 0 失败。这三个是第 41 棒按用户勾选的制式范围新加的。

    /// <summary>CDR 样本里那串数据（图形1.svg 解出来的：EAN-13、95 模块、6 根保护条）。</summary>
    private const string CdrSample = "4518518121156";

    [Fact]
    public void 从CDR样本解出来的那串数据本身是合法的EAN13()
    {
        var r = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(95, r.Modules);
        Assert.Null(r.Note);          // 样本里本来就带正确校验位，不该再补
    }

    [Fact]
    public void EAN13的保护条是三段共六根_且比数据条高六个模块()
    {
        var encoding = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        Assert.Equal(new[] { (0, 3), (45, 5), (92, 3) },
            encoding.GuardRanges!.Select(g => (g.StartModule, g.Length)).ToArray());

        var g = BarcodeBars.Build(encoding, 0, 0, 60, 0, 20);
        Assert.Equal(6, g.Bars.Count(b => b.IsGuard));      // 起 2 + 中 2 + 止 2
        Assert.Equal(20, g.GuardBarsHeight, 3);             // 保护条吃满条区
        // 延长量 = min(6 个模块, 条区高的 6/75)——后者是扁框的防呆上限（第五轮：
        // 用户画 132×14 的框时 6 个模块能到 8 mm，数据条会被压剩一半）
        var expected = Math.Min(BarcodeBars.GuardExtensionModules * g.ModuleMm, 20 * 6.0 / 75);
        Assert.Equal(expected, g.GuardBarsHeight - g.BarsHeight, 3);
        Assert.True(g.BarsHeight > g.GuardBarsHeight * 0.85, "数据条不许被保护条压掉一成以上");
    }

    [Fact]
    public void EAN13的可读数字分成三段_每位压在自己那七个模块上()
    {
        var encoding = BarcodeEncoder.Encode(CdrSample, BarcodeSymbology.Ean13);
        var geometry = BarcodeBars.Build(encoding, 0, 0, 60, 0, 20);
        var module = geometry.ModuleMm;
        var hri = BarcodeBars.BuildHri(encoding, 0, 60, geometry);

        Assert.Equal(13, hri.Count);
        Assert.Equal(CdrSample, string.Concat(hri.Select(h => h.Ch)));

        // 首位骑在左静区那把白上——静区是 CDR 的固定 3 mm，不是 11 个模块（第 41 棒改口径）
        Assert.Equal(3.0, geometry.QuietZoneMm, 3);
        Assert.Equal(0, hri[0].X, 3);
        Assert.Equal(3.0, hri[0].Width, 3);

        // 左半第一格从条区第 3 个模块起（让开起始保护条的 3 个模块），往后一格 7 模块
        Assert.Equal(3 + 3 * module, hri[1].X, 3);
        Assert.Equal(7 * module, hri[1].Width, 3);
        Assert.Equal(7 * module, hri[2].X - hri[1].X, 3);

        // 右半第一格从条区第 50 个模块起（3 + 42 + 5）
        Assert.Equal(3 + 50 * module, hri[7].X, 3);
        Assert.Equal('1', hri[7].Ch);
    }

    [Fact]
    public void 只有UPC和EAN一族才有保护条与分段数字()
    {
        var code128 = BarcodeEncoder.Encode("BOX-000123", BarcodeSymbology.Code128);
        Assert.Empty(code128.GuardRanges ?? Array.Empty<GuardRange>());
        Assert.Empty(code128.HriSegments ?? Array.Empty<HriSegment>());

        var g = BarcodeBars.Build(code128, 0, 0, 60, 0, 20);
        Assert.DoesNotContain(g.Bars, b => b.IsGuard);
        Assert.Equal(g.BarsHeight, g.GuardBarsHeight, 3);         // 没有保护条时两者相等
        Assert.Empty(BarcodeBars.BuildHri(code128, 0, 60, g));
    }

    [Fact]
    public void EAN8_给7位补第8位_整码67模块()
    {
        var r = BarcodeEncoder.Encode("9638507", BarcodeSymbology.Ean8);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("96385074", r.Data);
        Assert.Contains("补上第 8 位校验码 4", r.Note);
        Assert.Equal(67, r.Modules);
        Assert.StartsWith("101", r.Bits);
        Assert.EndsWith("101", r.Bits);
        Assert.Equal("01010", r.Bits.Substring(31, 5));            // 中间保护条在 3+28 之后
        Assert.Equal(7, r.QuietZoneModules);
        Assert.Equal(new[] { (0, 3), (31, 5), (64, 3) },
            r.GuardRanges!.Select(g => (g.StartModule, g.Length)).ToArray());
        Assert.Equal(2, r.HriSegments!.Count);                     // 4 位 + 4 位，没有骑静区的首位
    }

    [Fact]
    public void UPCA_给11位补第12位_整码95模块_数字分四段()
    {
        var r = BarcodeEncoder.Encode("03600029145", BarcodeSymbology.UpcA);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("036000291452", r.Data);
        Assert.Equal(95, r.Modules);
        Assert.Equal(9, r.QuietZoneModules);
        Assert.Equal(4, r.HriSegments!.Count);                     // 数字系统位 + 5 + 5 + 校验位
        Assert.Equal("0", r.HriSegments[0].Text);
        Assert.Equal("36000", r.HriSegments[1].Text);
        Assert.Equal("29145", r.HriSegments[2].Text);
        Assert.Equal("2", r.HriSegments[3].Text);
    }

    [Fact]
    public void UPCE_给7位补第8位_整码51模块_首位只能是0或1()
    {
        var r = BarcodeEncoder.Encode("0123456", BarcodeSymbology.UpcE);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(8, r.Data.Length);
        Assert.Equal(51, r.Modules);
        Assert.Equal(new[] { (0, 3), (45, 6) },                    // 结束保护条是 6 个模块，UPC-E 的特征
            r.GuardRanges!.Select(g => (g.StartModule, g.Length)).ToArray());

        var bad = BarcodeEncoder.Encode("2123456", BarcodeSymbology.UpcE);
        Assert.False(bad.Ok);
        Assert.Contains("0 或 1", bad.Error);
    }

    [Fact]
    public void UPCE_校验位要先压回UPCA再算()
    {
        // "0000001" 压回 11 位 UPC-A 是 "00010000000"，模 10 得 9。
        // 校验位最容易错的一步就在这个压缩规则上：写错一位就换算出别的校验位，
        // 印出来是一张扫不上、或扫成别的数的码。
        var r = BarcodeEncoder.Encode("0000001", BarcodeSymbology.UpcE);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("00000019", r.Data);

        // 全长给错校验位要拒——与 EAN-13 同一口径，不悄悄改掉
        var wrong = BarcodeEncoder.Encode("00000017", BarcodeSymbology.UpcE);
        Assert.False(wrong.Ok);
        Assert.Contains("校验码不对", wrong.Error);
    }

    [Fact]
    public void 照CDR样本那张框排_纵向每一段都对得上()
    {
        // 用户导出那条 EAN-13 的对象框是 38.1 × 27.43 mm。
        // 纵向的规则在第五轮改成了「自适应」：数字带有 2.5 mm 的可读下限、
        // 保护条延长最多占条区的 6/75——因为 CDR 那套 1:69:6:5 只在框高等比时成立，
        // 用户自己画的扁框（132 × 14）按那个比例会把数据条压剩一半、字炸成 27pt。
        // 所以这个锚点现在验证的是「接近 CDR」（容差 1 mm），不再是逐毫米相等：
        //   CDR 实测：上边距 0.331 / 数据条 23.366 / 保护条 25.398 / 数字带 1.701
        //   本实现：  0.339      / 22.62        / 24.59        / 2.50
        // 差的这不到 1 mm 换来的是：任何用户自画的框比例下都不会再出现叠字压条。
        const double frameW = 38.1, frameH = 27.43;
        var template = new LabelTemplate { Name = "CDR 那张", WidthMm = 60, HeightMm = 60, BorderMm = 0 };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode,
            Text = CdrSample,
            Symbology = BarcodeSymbology.Ean13,
            ShowBarcodeText = true,
            FontSizePt = 8,
            X = 0, Y = 0, Width = frameW, Height = frameH,
        });

        var bar = LayoutEngine.Build(template, RecordWithBarcode(CdrSample), new LayoutContext(1, 1))
            .Items.OfType<BarcodeItem>().Single();

        Assert.InRange(bar.ModuleMm, 0.337, 0.339);                          // 样本 0.33795（取整到 0.0001）
        Assert.Equal(3.0, bar.Bars[0].X, 3);                                 // 左静区固定 3 mm
        Assert.InRange(bar.BarsY, 0.339 - 0.02, 0.339 + 0.02);               // 上边距 = 框高 1/81
        Assert.InRange(bar.BarsHeight, 22.62 - 0.1, 22.62 + 0.1);            // 数据条（CDR 23.366）
        Assert.InRange(bar.EffectiveGuardBarsHeight, 24.59 - 0.1, 24.59 + 0.1);   // 保护条（CDR 25.398）
        Assert.InRange(frameH - (bar.BarsY + bar.EffectiveGuardBarsHeight), 2.5, 2.6);  // 数字带 = 下限 2.5
        // 扁框防呆：132 × 14 的框（用户实拍出事的那张）里，数据条不许再被保护条压剩一半
        var flat = new LabelTemplate { Name = "扁框", WidthMm = 140, HeightMm = 40, BorderMm = 0 };
        flat.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode,
            Text = CdrSample,
            Symbology = BarcodeSymbology.Ean13,
            ShowBarcodeText = true,
            FontSizePt = 8,
            X = 4, Y = 4, Width = 132, Height = 14,
        });
        var flatBar = LayoutEngine.Build(flat, RecordWithBarcode(CdrSample), new LayoutContext(1, 1))
            .Items.OfType<BarcodeItem>().Single();
        Assert.InRange(flatBar.BarsHeight, 9.5, 11.5);                       // 数据条稳稳过半，不再被压扁
        Assert.True(flatBar.EffectiveGuardBarsHeight - flatBar.BarsHeight <= 1.0,
            "保护条的延长在扁框里不许超过 1 mm");
    }

    [Fact]
    public void Codabar_起止符是帧_不印进下面那串数字()
    {
        // 用户从 CDR 导出的对照图里，Codabar 下面印的是「1234567891231」——起止符没印出来。
        // 理由：起止符是帧不是数据，扫码枪读回来的串里也没有它（ZXing 的 reader 会剥掉）。
        var r = BarcodeEncoder.Encode("1234567891231", BarcodeSymbology.Codabar);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("1234567891231", r.Data);
        Assert.Contains("起止符", r.Note);
        Assert.Equal(334, r.Modules);                         // 2.5:1：2 个起止符各 23 位 + 13 数字各 20 位 + 14 条窄空各 2 位
        // 起始符 A（0x01A = 0011010，第 4、3、1 位是宽元素）：条2 空2 条5 空5 条2 空5 条2
        Assert.StartsWith("11001111100000110000011", r.Bits);

        // 用户自己写了起止符就照他的来（数据里也不带）
        var explicitGuard = BarcodeEncoder.Encode("A40156B", BarcodeSymbology.Codabar);
        Assert.True(explicitGuard.Ok, explicitGuard.Error);
        Assert.Equal("A40156B", explicitGuard.Data);
        Assert.Null(explicitGuard.Note);

        // 首尾只有一头带着起止符 = 抄错了，挡下来
        var halfGuard = BarcodeEncoder.Encode("A40156", BarcodeSymbology.Codabar);
        Assert.False(halfGuard.Ok);
        Assert.Contains("起止符", halfGuard.Error);
    }

    [Fact]
    public void Codabar_字符集外挡下来()
    {
        var r = BarcodeEncoder.Encode("A12X34A", BarcodeSymbology.Codabar);
        Assert.False(r.Ok);
        Assert.Contains("装不下字符", r.Error);
    }

    [Fact]
    public void 通用ITF_奇数位前面补0_不补也不验校验位()
    {
        var r = BarcodeEncoder.Encode("1234567891231", BarcodeSymbology.Itf);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("01234567891231", r.Data);             // 交错码必须两位一组，前面补一个 0
        Assert.Contains("补了一个 0", r.Note);
        Assert.Equal(241, r.Modules);                       // 宽窄比 2.5:1：起始+7组+结束，每窄翻2位每宽翻5位

        // 同一串 13 位喂给 ITF-14：那边补的是第 14 位校验码，是另一回事。
        // 这个值正好和用户导出的 CDR 对照图里印的那串「12345678912312」一致。
        var itf14 = BarcodeEncoder.Encode("1234567891231", BarcodeSymbology.Itf14);
        Assert.Equal("12345678912312", itf14.Data);
        Assert.Contains("校验码", itf14.Note);
        Assert.DoesNotContain("校验码", r.Note);
    }

    [Fact]
    public void 通用ITF_非数字挡下来()
    {
        var r = BarcodeEncoder.Encode("1234A678", BarcodeSymbology.Itf);
        Assert.False(r.Ok);
        Assert.Contains("只能编数字", r.Error);
    }

    // ---------- 25 码（非交错，第 42 棒按 CDR 样张补的制式） ----------

    [Fact]
    public void 二五码_每位数字五根条_不补零也不加校验位()
    {
        // 用户点名「code25 要和 CDR 一样」。CDR 那框实测 71 根条（= 起始 3 + 13 位×5 + 结束 3），
        // 而交错制同一串只有 39 根——两种码，不能拿 ITF 顶替。
        var r = BarcodeEncoder.Encode("1234567891231", BarcodeSymbology.Code25);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("1234567891231", r.Data);              // 奇数位照编，不学交错码那样补 0
        Assert.Null(r.Note);                                 // 非交错 2 of 5 没有校验位可补
        // 位数手算：71 根条（30 宽 × 5 位 + 41 窄 × 2 位 = 232）+ 70 条恒窄分隔 × 2 位 = 140 → 372
        Assert.Equal(372, r.Modules);
        Assert.Equal(2, r.NarrowUnits);                      // 宽窄比 2.5:1 → 窄元素占 2 位
        // 起始「宽宽窄」：条5 空2 条5 空2 条2
        Assert.StartsWith("1111100111110011", r.Bits);
        // 结束「宽窄宽」：…空2 条5 空2 条2 空2 条5
        Assert.EndsWith("100110011111", r.Bits);
    }

    [Fact]
    public void 二五码_每数字恰两根宽条_不照抄CDR少一根的那个缺陷()
    {
        // CDR 样张把数字 2 画成「窄宽窄窄窄」——只有 1 根宽条，整码宽条 28 根。
        // 「每数字 5 条取 2 宽」是 2 of 5 的自校验定义，少一根扫码枪就认不出，所以按标准表编。
        var r = BarcodeEncoder.Encode("1234567891231", BarcodeSymbology.Code25);
        var barRuns = new List<int>();
        for (var i = 0; i < r.Bits.Length;)
        {
            if (r.Bits[i] != '1') { i++; continue; }
            var j = i;
            while (j < r.Bits.Length && r.Bits[j] == '1') j++;
            barRuns.Add(j - i);
            i = j;
        }
        Assert.Equal(71, barRuns.Count);                                   // 71 根条
        Assert.Equal(30, barRuns.Count(w => w == 5));                      // 宽条：起始2 + 13×2 + 结束2
        Assert.Equal(41, barRuns.Count(w => w == 2));                      // 窄条：其余全部
        Assert.All(barRuns, w => Assert.Contains(w, new[] { 2, 5 }));      // 只有窄(2)与宽(5)两档
    }

    [Fact]
    public void 二五码静区走CDR的三毫米_储运箱码另按七毫米二()
    {
        // 样张实测：code25 那框左 2.946 / 右 3.047 mm；ITF-14 那框却是左 7.180 / 右 7.217 mm。
        // 箱码要在仓库远距离扫，静区比一般码宽一倍——两套值不能混用一个常量。
        var code25 = BarcodeEncoder.Encode("1234567891231", BarcodeSymbology.Code25);
        Assert.Equal(3.0, code25.QuietZoneMm, 3);
        var itf14 = BarcodeEncoder.Encode("12345678912312", BarcodeSymbology.Itf14);
        Assert.Equal(7.2, itf14.QuietZoneMm, 3);
        Assert.NotEqual(itf14.QuietZoneMm, code25.QuietZoneMm);
    }

    [Fact]
    public void 二五码按CDR那框铺_窄线与条数都对得上()
    {
        // CDR 的 code25 框宽 67.89 mm，实测窄条 0.3386 mm、71 根条。
        var encoding = BarcodeEncoder.Encode("1234567891231", BarcodeSymbology.Code25);
        var g = BarcodeBars.Build(encoding, 0, 0, 67.89, 0, 20);
        Assert.Null(g.Warning);
        Assert.Equal(71, g.Bars.Count);
        Assert.InRange(g.ModuleMm, 0.330, 0.340);           // CDR 实测 0.3386
        Assert.Equal(3.0, g.QuietZoneMm, 3);
        var last = g.Bars[^1];
        Assert.True(last.X + last.Width <= 67.89 + 1e-9, "最后一根条不许推出框外");
    }

    [Fact]
    public void 二五码非数字挡下来()
    {
        var r = BarcodeEncoder.Encode("12A45", BarcodeSymbology.Code25);
        Assert.False(r.Ok);
        Assert.Contains("只能编数字", r.Error);
    }

    // ---------- 几何（毫米） ----------

    [Fact]
    public void 铺条时静区算在框内且模块向下取整()
    {
        var encoding = BarcodeEncoder.Encode("4006381333931", BarcodeSymbology.Ean13);
        Assert.Equal(11, encoding.QuietZoneModules);                // GS1 的静区下限还记着(第 23 棒)
        Assert.Equal(3.0, encoding.QuietZoneMm, 3);                 // 但铺条时走 CDR 的固定 3 mm(第 41 棒)
        var g = BarcodeBars.Build(encoding, 0, 0, 100, 0, 10);
        Assert.Null(g.Warning);
        // 100 mm 扣掉两侧各 3 mm 静区 → 94 mm 铺给 95 个模块 → 往下取整到 0.0001
        Assert.Equal(0.9894, g.ModuleMm, 4);
        Assert.Equal(3.0, g.QuietZoneMm, 3);
        var last = g.Bars[^1];
        Assert.True(last.X + last.Width <= 100 + 1e-9, "最后一根条不许推出框外");
        Assert.True(g.Bars.Count > 0);
    }

    [Fact]
    public void 模块窄到扫不出时照样画但必须警告()
    {
        var encoding = BarcodeEncoder.Encode("4006381333931", BarcodeSymbology.Ean13);
        var g = BarcodeBars.Build(encoding, 0, 0, 20, 0, 10);
        Assert.InRange(g.ModuleMm, 0.001, BarcodeBars.MinModuleMm);
        Assert.Contains("0.15", g.Warning);           // (20-6)/95 = 0.1474 → 取整 0.147 → 显示 0.15
        Assert.Contains("扫", g.Warning);
    }

    [Fact]
    public void 框太小装不下时不返回半根条()
    {
        var encoding = BarcodeEncoder.Encode("10654321000019", BarcodeSymbology.Itf14);
        var g = BarcodeBars.Build(encoding, 0, 0, 0.01, 0, 10);     // 155 个模块铺进 0.01 mm → 一根模块连最小粒度都不到
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
    public void 小尺寸条码的文字带不溢出元素底边()
    {
        // 高 2mm 的小元素:旧口径文字带固定 3mm,条顶在 Y、文字带压出元素底边(第 23 棒审计)。
        var template = BarcodeOnly("{{col:条码}}", BarcodeSymbology.Code128);
        template.Elements[0].Height = 2;

        var bar = LayoutEngine.Build(template, RecordWithBarcode("BOX-000123"), new LayoutContext(1, 1))
            .Items.OfType<BarcodeItem>().Single();

        Assert.True(bar.BarsHeight >= bar.Height * 0.5 - 1e-9,
            $"条高 {bar.BarsHeight} 应至少占元素高 {bar.Height} 的一半");
        Assert.True(bar.BarsY >= bar.Y - 1e-9,
            $"条顶 {bar.BarsY} 不该跑到元素框上方 {bar.Y}");
        Assert.True(bar.BarsY + bar.EffectiveGuardBarsHeight <= bar.Y + bar.Height + 1e-9,
            "条压出元素底边");
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
