using System.Text;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Colors;
using LabelGou.Core.Export;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 48 棒的地基：把一页拆成 C/M/Y/K 四张版，以及把四张版写成一个说得清自己的 TIFF。
/// <para>
/// 这一层最要紧的一条是<strong>分量不许漂</strong>：分色版上那一格的墨量必须正是用户在「墨色」里
/// 填的那四个数。拿渲染好的 RGB 反算 CMYK 会换配方（47 棒实测 37/63/11/5 → 29/58/0/15），
/// 所以这里既钉住"每版读的是哪一格"，也钉住"读的是存着的那一份、不是现算的"。
/// </para>
/// </summary>
public class CmykPlateTests
{
    private static LabelTemplate Template(params TemplateElement[] elements)
    {
        var template = new LabelTemplate
        {
            Id = "user.plate", Name = "分色测试", WidthMm = 100, HeightMm = 80, PaddingMm = 4, BorderMm = 0,
        };
        template.Elements.AddRange(elements);
        return template;
    }

    private static LabelLayout Build(LabelTemplate template, InkPlate plate)
        => LayoutEngine.Build(template, SampleRecords.StandardSample(),
            new LayoutContext(1, 1, "样例.xlsx", Plate: plate));

    // ---------- ① 每版读哪一格 ----------

    [Fact]
    public void EachPlateReadsItsOwnPercentagesNotAReDerivation()
    {
        // 47 棒那组"折回来会换配方"的实测数：屏幕色反算是 29/58/0/15，这里必须是 37/63/11/5。
        var ink = LabelColor.FromCmyk(37, 63, 11, 5);
        Assert.Equal((37, 63, 11, 5), (ink.C, ink.M, ink.Y, ink.K));
        Assert.Equal(29, InkPlates.Percent(LabelColor.FromSrgb(ink.R, ink.G, ink.B), InkPlate.Cyan));   // 现算的那一路就是这个下场

        Assert.Equal(37, InkPlates.Percent(ink, InkPlate.Cyan));
        Assert.Equal(63, InkPlates.Percent(ink, InkPlate.Magenta));
        Assert.Equal(11, InkPlates.Percent(ink, InkPlate.Yellow));
        Assert.Equal(5, InkPlates.Percent(ink, InkPlate.Black));
    }

    [Fact]
    public void UnfilledInkIsBlackSoOnlyTheKeyPlateCarriesIt()
    {
        // 「没填 = 黑」在分色里的样子：青品黄三版全空，黑版满墨。与 47 棒那条缺字段=黑同一条口径。
        Assert.Equal(0, InkPlates.Percent(null, InkPlate.Cyan));
        Assert.Equal(0, InkPlates.Percent(null, InkPlate.Magenta));
        Assert.Equal(0, InkPlates.Percent(null, InkPlate.Yellow));
        Assert.Equal(100, InkPlates.Percent(null, InkPlate.Black));
    }

    [Theory]
    [InlineData(0, 255)]       // 0% 墨 = 白（这一版此处不上墨）
    [InlineData(50, 127)]      // 墨量先四舍五入（128），灰是它的反面 255-128
    [InlineData(100, 0)]       // 100% 墨 = 黑
    public void PlateGreyIsTheInkAmountUpsideDown(int percent, byte expectedGrey)
    {
        var ink = percent == 100 ? LabelColor.Black : LabelColor.FromCmyk(0, 0, 0, percent);
        var grey = InkPlates.ForPlate(ink, InkPlate.Black).R;
        Assert.Equal(expectedGrey, grey);
        Assert.Equal(percent, (int)Math.Round((255 - grey) * 100.0 / 255, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void NotSeparatingStillHandsBackTheExactSameInk()
    {
        // 不分色时（Plate=None）LayoutEngine 交回的墨必须与从前逐字同形：null 还是 null，不是"另一个也是黑的"。
        var plain = Template(new TemplateElement { Kind = ElementKind.Text, Text = "MADE IN CHINA", X = 10, Y = 10, Width = 60, Height = 8 });
        var text = Assert.Single(Build(plain, InkPlate.None).Items.OfType<TextItem>());
        Assert.Null(text.Ink);

        var red = LabelColor.FromCmyk(0, 100, 100, 0);
        var coloured = Template(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "MADE IN CHINA", X = 10, Y = 10, Width = 60, Height = 8, InkColor = red,
        });
        Assert.Same(red, Assert.Single(Build(coloured, InkPlate.None).Items.OfType<TextItem>()).Ink);
    }

    // ---------- ② 版面项按版折算 ----------

    [Fact]
    public void EveryItemKindCarriesItsPlateInkIntoTheLayout()
    {
        var template = Template(
            new TemplateElement { Kind = ElementKind.Line, X = 10, Y = 20, X2 = 60, Y2 = 20, ThicknessMm = 0.35, InkColor = LabelColor.FromCmyk(100, 0, 0, 0) },
            new TemplateElement { Kind = ElementKind.Rect, X = 10, Y = 30, Width = 40, Height = 12, InkColor = LabelColor.FromCmyk(100, 0, 0, 0) },
            new TemplateElement { Kind = ElementKind.Text, Text = "CONTRACT NO.", X = 10, Y = 46, Width = 60, Height = 8, InkColor = LabelColor.FromCmyk(100, 0, 0, 0) },
            new TemplateElement { Kind = ElementKind.Barcode, Text = "12345678", Symbology = BarcodeSymbology.Code128, X = 10, Y = 56, Width = 80, Height = 18, InkColor = LabelColor.FromCmyk(100, 0, 0, 0) });

        // 青版：这支墨 C=100 → 满墨（画成黑）；品红版：M=0 → 这一版没它的墨（画成白）。
        var cyan = Build(template, InkPlate.Cyan).Items;
        Assert.Equal(4, cyan.OfType<LineItem>().Count() + cyan.OfType<RectItem>().Count()
            + cyan.OfType<TextItem>().Count() + cyan.OfType<BarcodeItem>().Count());
        Assert.All(cyan.OfType<LineItem>(), i => Assert.Equal(0, i.Ink!.R));
        Assert.All(cyan.OfType<RectItem>(), i => Assert.Equal(0, i.Ink!.R));
        Assert.All(cyan.OfType<TextItem>(), i => Assert.Equal(0, i.Ink!.R));
        Assert.All(cyan.OfType<BarcodeItem>(), i => Assert.Equal(0, i.Ink!.R));

        var magenta = Build(template, InkPlate.Magenta).Items;
        Assert.All(magenta.OfType<LineItem>(), i => Assert.Equal(255, i.Ink!.R));
        Assert.All(magenta.OfType<RectItem>(), i => Assert.Equal(255, i.Ink!.R));
        Assert.All(magenta.OfType<TextItem>(), i => Assert.Equal(255, i.Ink!.R));
        Assert.All(magenta.OfType<BarcodeItem>(), i => Assert.Equal(255, i.Ink!.R));
    }

    [Fact]
    public void TemplateBorderBelongsToTheKeyPlateOnly()
    {
        var template = Template();
        template.BorderMm = 0.4;

        Assert.Null(Build(template, InkPlate.None).Items.OfType<RectItem>().First().Ink);       // 不分色时还是那个"没填"
        Assert.Equal(255, Build(template, InkPlate.Cyan).Items.OfType<RectItem>().First().Ink!.R);
        Assert.Equal(0, Build(template, InkPlate.Black).Items.OfType<RectItem>().First().Ink!.R);
    }

    [Fact]
    public void WarningRedNeverReachesThePlates()
    {
        // 警示红是给人眼和闸门看的，不是配墨。画到灰版上它会被当成"无墨"，那行字就从成品里安静消失了。
        var template = Template(new TemplateElement { Kind = ElementKind.Text, Text = "{{Consignee}}", X = 10, Y = 10, Width = 60, Height = 8 });
        var record = MarkRecord.Builder()
            .SetRow(1, "样例.xlsx")
            .Set(MarkFieldKey.Consignee, new MarkValue("LOS ANGELES", ValueOrigin.AiOcr)
            { NeedsReview = true, Warning = "识别置信度低" })
            .Build();
        var normal = LayoutEngine.Build(template, record, new LayoutContext(1, 1));
        Assert.True(Assert.Single(normal.Items.OfType<TextItem>()).Flagged);

        var plate = Assert.Single(Build(template, InkPlate.Magenta).Items.OfType<TextItem>());
        Assert.False(plate.Flagged);                                     // 版上没有"红字"这种墨
        Assert.NotNull(plate.Ink);                                       // 但这一版该上多少墨照旧算得出来
    }

    // ---------- ③ 自写的 CMYK TIFF ----------

    [Fact]
    public void TiffStatesTheInkConventionInsteadOfLeavingItToGuessing()
    {
        var (one, two) = (Frame(4, 2, 30), Frame(4, 2, 200));
        var bytes = CmykTiffWriter.Write(new[] { one, two }, "LabelGou 0.7.0", 300);
        var ifds = Tiff.ReadIfds(bytes);

        Assert.Equal(2, ifds.Count);
        foreach (var ifd in ifds)
        {
            Assert.Equal(5u, ifd.Get(262));              // PhotometricInterpretation = Separated
            Assert.Equal(4u, ifd.Get(277));              // SamplesPerPixel
            Assert.False(ifd.Has(333) || ifd.Has(334) || ifd.Has(335));   // 实测这三条会让 libtiff 每次打开都报警，刻意不写
            Assert.Equal(1u, ifd.Get(284));              // 逐像素交错（chunky）
            Assert.Equal(2u, ifd.Get(296));              // 分辨率单位＝英寸
            Assert.Equal(300u, ifd.GetRational(282));    // XResolution
            Assert.Equal(300u, ifd.GetRational(283));    // YResolution
            Assert.Equal(8u, ifd.Get(259));              // Compression = Adobe Deflate
            Assert.Equal("8 8 8 8", ifd.GetArray(258));
            Assert.Equal("0 255", ifd.GetArray(336));    // DotRange：0 = 没有点，255 = 满点
            Assert.Contains("0 = no ink", ifd.GetText(270), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TiffPixelsSurviveWriteAndInflateByteForByte()
    {
        var frame = Frame(8, 3, 77);
        var bytes = CmykTiffWriter.Write(new[] { frame }, "LabelGou", 600);
        var ifd = Tiff.ReadIfds(bytes).Single();
        var raw = Deflate.Decompress(bytes[(int)ifd.Get(273)..][..(int)ifd.Get(279)]);
        Assert.Equal(frame.Pixels, raw);
    }

    [Fact]
    public void TiffChasesItsSecondPageThroughTheNextIfdLink()
    {
        var a = Frame(4, 1, 10);
        var b = Frame(6, 2, 20);
        var bytes = CmykTiffWriter.Write(new[] { a, b }, "LabelGou", 300);
        var ifds = Tiff.ReadIfds(bytes);
        Assert.Equal(4u, ifds[0].Get(256));
        Assert.Equal(6u, ifds[1].Get(256));
        Assert.Equal(b.Pixels, Deflate.Decompress(bytes[(int)ifds[1].Get(273)..][..(int)ifds[1].Get(279)]));
    }

    [Fact]
    public void BadFramesAreRefusedBeforeAnyBytesGoOut()
    {
        var issues = new List<string>();
        CmykTiffWriter.CollectIssues(new[] { new CmykTiffFrame(4, 4, new byte[4 * 4 * 3]) }, issues);
        Assert.Single(issues);
        Assert.Contains("不符", issues[0], StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => CmykTiffWriter.Write(Array.Empty<CmykTiffFrame>(), "x", 300));
        Assert.Throws<InvalidDataException>(() => CmykTiffWriter.Write(new[] { Frame(4, 1, 0) }, "x", 60));
    }

    [Fact]
    public void NonAsciiSoftwareNameBecomesQuestionMarksNotCrushedBytes()
    {
        var bytes = CmykTiffWriter.Write(new[] { Frame(2, 1, 5) }, "LabelGou 测试", 300);
        var text = Encoding.ASCII.GetString(bytes).Replace("\0", string.Empty);
        Assert.Contains("LabelGou ??", text, StringComparison.Ordinal);     // TIFF 的 ASCII 字段只放 7 位字符
    }

    private static CmykTiffFrame Frame(int w, int h, byte seed)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(seed + i * 7);
        return new CmykTiffFrame(w, h, pixels);
    }

    /// <summary>测试自带的最小 TIFF 读法：生产代码只写不读，判据不能靠写它的那套代码自证。</summary>
    private sealed class Tiff
    {
        private readonly Dictionary<ushort, uint> _tags = new();
        private readonly Dictionary<ushort, ushort[]> _arrays = new();
        private readonly Dictionary<ushort, string> _texts = new();
        private readonly byte[] _data;

        private Tiff(byte[] data) => _data = data;

        public uint Get(ushort tag) => _tags[tag];
        public string GetArray(ushort tag) => string.Join(' ', _arrays[tag]);
        public string GetText(ushort tag) => _texts[tag];

        /// <summary>RATIONAL 的值域里放的是偏移，分子/分母各一个 LONG。</summary>
        public uint GetRational(ushort tag)
        {
            var at = (int)_tags[tag];
            return BitConverter.ToUInt32(_data, at) / BitConverter.ToUInt32(_data, at + 4);
        }

        public bool Has(ushort tag) => _tags.ContainsKey(tag);

        public static List<Tiff> ReadIfds(byte[] data)
        {
            Assert.Equal(new byte[] { 0x49, 0x49 }, data[..2]);           // 小端
            Assert.Equal(42, BitConverter.ToUInt16(data, 2));
            var list = new List<Tiff>();
            var offset = (int)BitConverter.ToUInt32(data, 4);
            while (offset != 0)
            {
                var ifd = new Tiff(data);
                var count = BitConverter.ToUInt16(data.AsSpan(offset));
                var pos = offset + 2;
                for (var i = 0; i < count; i++, pos += 12)
                {
                    var tag = BitConverter.ToUInt16(data, pos);
                    var type = BitConverter.ToUInt16(data, pos + 2);
                    var n = (int)BitConverter.ToUInt32(data, pos + 4);
                    var valuePos = pos + 8;
                    ifd._tags[tag] = BitConverter.ToUInt32(data, valuePos);
                    if (type == 3)
                    {
                        var shorts = new ushort[n];
                        if (n <= 2)
                        {
                            for (var s = 0; s < n; s++) shorts[s] = BitConverter.ToUInt16(data, valuePos + s * 2);
                        }
                        else
                        {
                            var at = (int)BitConverter.ToUInt32(data, valuePos);
                            for (var s = 0; s < n; s++) shorts[s] = BitConverter.ToUInt16(data, at + s * 2);
                        }
                        ifd._arrays[tag] = shorts;
                    }
                    else if (type == 2)
                    {
                        var at = n <= 4 ? valuePos : (int)BitConverter.ToUInt32(data, valuePos);
                        ifd._texts[tag] = Encoding.ASCII.GetString(data, at, n).TrimEnd('\0');
                    }
                }
                list.Add(ifd);
                offset = (int)BitConverter.ToUInt32(data, pos);
            }
            return list;
        }
    }
}
