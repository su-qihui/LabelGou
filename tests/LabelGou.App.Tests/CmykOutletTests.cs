using System.IO;
using System.Text;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;

using LabelGou.Core.Colors;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 48 棒：CMYK 四版从版面一路走到 PDF / TIFF 文件。
/// <para>
/// 这里钉的是<strong>接得上、分量不落空</strong>：用户在「墨色」里填的那四个百分数，
/// 到了分色版上还是那四个数（不是从屏幕 RGB 现算回来的），而且两条出口各自挑对了写出器。
/// 渲染与写文件的逐字节口径分别由 <c>CmykPlateTests</c>（Core）与探针脚本管，这里不重复。
/// </para>
/// </summary>
public sealed class CmykOutletTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _deleteWhenDone;

    public CmykOutletTests()
    {
        // 设上这个环境变量就把产物留在指定目录（外部阅读器复核 CMYK 靠它，与 M3 复核 PDF 同构）
        var keep = Environment.GetEnvironmentVariable("LABELGOU_TEST_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(keep))
        {
            _dir = keep;
            Directory.CreateDirectory(_dir);
        }
        else
        {
            _dir = Path.Combine(Path.GetTempPath(), "labelgou-cmyk-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(_dir);
            _deleteWhenDone = true;
        }
    }

    public void Dispose()
    {
        if (!_deleteWhenDone) return;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* 临时目录留给人看一次就够了 */ }
    }

    private const double LabelW = 60;
    private const double LabelH = 30;

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static SheetSpec Spec() => new()
    {
        Id = "test.cmyk", Name = "分色小样",
        PaperWidthMm = 80, PaperHeightMm = 40,
        MarginLeftMm = 4, MarginTopMm = 4, MarginRightMm = 4, MarginBottomMm = 4,
        GutterXMm = 2, GutterYMm = 2, CropMarkThicknessMm = 0.15,
        RepeatSameLabelPerPage = false,
    };

    private static SheetExportRequest Request(LabelTemplate template, bool cmyk, int dpi = 150)
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1);
        return new SheetExportRequest
        {
            Plan = plan,
            Source = new PageContentSource(template, new List<MarkRecord> { SampleRecords.StandardSample() }, "样例.xlsx"),
            PageIndexes = new List<int> { 0 },
            BaseName = "分色件",
            Dpi = dpi,
            CmykPlates = cmyk,
        };
    }

    private static LabelTemplate TemplateWith(TemplateElement element)
    {
        var template = new LabelTemplate
        {
            Id = "user.cmyk", Name = "分色出口", WidthMm = LabelW, HeightMm = LabelH, PaddingMm = 2, BorderMm = 0,
        };
        template.Elements.Add(element);
        return template;
    }

    private static TemplateElement BigText(LabelColor? ink) => new()
    {
        Kind = ElementKind.Text, Text = "9999", X = 4, Y = 8, Width = 52, Height = 14,
        FontSizePt = 40, Bold = true, InkColor = ink,
    };

    /// <summary>找整页上墨最多的那一格——它一定在字身内部，四版在那里都是满覆盖。</summary>
    private static (int Index, int Total) Darkest(PageRasterizer.CmykPage page)
    {
        var best = 0;
        var bestIndex = 0;
        for (var i = 0; i < page.Width * page.Height; i++)
        {
            var total = page.Pixels[i * 4] + page.Pixels[i * 4 + 1] + page.Pixels[i * 4 + 2] + page.Pixels[i * 4 + 3];
            if (total > best) { best = total; bestIndex = i; }
        }
        return (bestIndex, best);
    }

    /// <summary>一版上总共多少墨（long：一张 A4@300DPI 的满版能到 20 亿，int 会翻）。</summary>
    private static long Total(byte[] plane) => plane.Sum(v => (long)v);

    [Fact]
    public void ThePlatesCarryTheRecipeTheUserTypedNotAReDerivation() => OnSta(() =>
    {
        // 47 棒那组实测数：屏幕色反算回来是 29/58/0/15，四格全变。分色版上必须还是 37/63/11/5。
        var request = Request(TemplateWith(BigText(LabelColor.FromCmyk(37, 63, 11, 5))), true);
        var page = PageRasterizer.RenderCmykPage(request.Plan, 1, request.Dpi,
            request.Source.AsPlateProvider(), false);

        var (index, total) = Darkest(page);
        Assert.True(total > 200, $"页上找不到有墨的格子（最重一格 {total}）——字没画出来，后面都白测");
        var actual = new[] { page.Pixels[index * 4], page.Pixels[index * 4 + 1], page.Pixels[index * 4 + 2], page.Pixels[index * 4 + 3] };
        var wanted = new[] { 37, 63, 11, 5 }.Select(p => (int)Math.Round(p * 255.0 / 100, MidpointRounding.AwayFromZero)).ToArray();

        // ±3 是量具的分辨率，不是放宽标准：WPF 的文本渲染在字身内部给每版带来 ±2/255（≈0.8% 墨）的
        // 舍入串色（实测青版峰值 2）。而"从屏幕色现算"那套会差到 65/103/28，谁都看得出来。
        for (var i = 0; i < 4; i++)
        {
            Assert.True(Math.Abs(actual[i] - wanted[i]) <= 3,
                $"第 {i} 版墨量 {actual[i]}，用户填的配方要的是 {wanted[i]}±3（实测 C{actual[0]} M{actual[1]} Y{actual[2]} K{actual[3]}）");
        }
        return true;
    });

    [Fact]
    public void UntouchedInkPrintsOnTheKeyPlateOnly() => OnSta(() =>
    {
        // 没填颜色＝黑＝只落 K 版。青品黄三版该只剩渲染舍入的那点边（≤4/255），黑版该有整支字。
        var request = Request(TemplateWith(BigText(null)), true);
        var page = PageRasterizer.RenderCmykPage(request.Plan, 1, request.Dpi, request.Source.AsPlateProvider(), false);
        var planes = page.ToPlanes();
        Assert.True(planes[0].Max() <= 4 && planes[1].Max() <= 4 && planes[2].Max() <= 4,
            $"彩版上出现了不该有的墨：峰值 C{planes[0].Max()} M{planes[1].Max()} Y{planes[2].Max()}");
        Assert.True(Total(planes[3]) > 0, "黑版上没墨，等于把唛头印空了");
        return true;
    });

    [Fact]
    public void CropMarksGoToTheKeyPlateWhileRegistrationMarksStayOnAllFour() => OnSta(() =>
    {
        // 版面里不放任何内容，页上的墨只剩辅助线：黑版该比彩版多（角线只落黑版），
        // 而彩版不是空的（套准十字四版都得有，不然四版对不齐）。
        var empty = new LabelTemplate
        {
            Id = "user.cmyk.marks", Name = "只有标线", WidthMm = LabelW, HeightMm = LabelH, PaddingMm = 2, BorderMm = 0,
        };
        var request = Request(empty, true);
        var withMarks = PageRasterizer.RenderCmykPage(request.Plan, 1, request.Dpi, request.Source.AsPlateProvider(), true);
        var without = PageRasterizer.RenderCmykPage(request.Plan, 1, request.Dpi, request.Source.AsPlateProvider(), false);

        var plates = withMarks.ToPlanes();
        var bare = without.ToPlanes();
        var cyan = Total(plates[0]);
        var key = Total(plates[3]);
        Assert.Equal(0, bare.Sum(p => Total(p)));                      // 关掉标线就该一张空版
        Assert.True(cyan > 0, "套准十字在青版上没了——四版对不齐就是废版");
        Assert.True(key > cyan, $"角线该只落黑版，结果黑版 {key} 并不比青版 {cyan} 多");
        return true;
    });

    [Fact]
    public void TiffExportPicksTheWriterThatStatesItsOwnConvention() => OnSta(() =>
    {
        var request = Request(TemplateWith(BigText(LabelColor.FromCmyk(0, 100, 100, 0))), true);
        var path = Path.Combine(_dir, "cmyk.tif");
        var outcome = SheetExportService.ExportTiff(request, path, null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("CMYK 四版", outcome.Summary, StringComparison.Ordinal);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x49, 0x49 }, bytes[..2]);           // 我们那个自写头（WPF 编码器不写 InkSet/DotRange）
        Assert.Contains("0 = no ink", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        return true;
    });

    [Fact]
    public void PdfExportEmbedsTheFourChannelPageImage() => OnSta(() =>
    {
        var request = Request(TemplateWith(BigText(LabelColor.FromCmyk(100, 0, 100, 0))), true);
        var path = Path.Combine(_dir, "cmyk.pdf");
        var outcome = SheetExportService.ExportPdf(request, path, null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("CMYK 四版", outcome.Summary, StringComparison.Ordinal);
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(path));
        Assert.Contains("/ColorSpace /DeviceCMYK", text, StringComparison.Ordinal);
        Assert.Contains("/Decode [0 1 0 1 0 1 0 1]", text, StringComparison.Ordinal);
        Assert.Contains("/Colors 4", text, StringComparison.Ordinal);
        return true;
    });

    [Fact]
    public void PngSaysOutLoudThatItCannotHoldFourPlates() => OnSta(() =>
    {
        // PNG 装不下 CMYK（47 棒实测 WPF 会悄悄转成 Bgr24）。开了四版还出 PNG，必须把这句话写在摘要里。
        var request = Request(TemplateWith(BigText(null)), true);
        var outcome = SheetExportService.ExportPngPages(request, _dir, null, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("PNG 装不下 CMYK 四版", outcome.Summary, StringComparison.Ordinal);
        return true;
    });

    [Fact]
    public void TheRgbOutletsAreUntouchedWhenNobodyAsksForPlates() => OnSta(() =>
    {
        // 默认不开四版：PDF 还是 JPEG/RGB，TIFF 还是 WPF 那份，摘要里不该冒出 CMYK 那句话。
        var request = Request(TemplateWith(BigText(null)), false);
        var pdf = Path.Combine(_dir, "rgb.pdf");
        var outcome = SheetExportService.ExportPdf(request, pdf, null, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("JPEG", outcome.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("CMYK", outcome.Summary, StringComparison.Ordinal);
        return true;
    });

    [Fact]
    public void PlatesRefuseInsteadOfManglingArtworkThatCannotBeSeparated() => OnSta(() =>
    {
        // 图片与矢量底图带着自己的颜色，画到灰版上只有红通道会被当成墨——那不是"颜色差一点"，是整版错。
        // 量具要钉的是：它必须停下，而不是安静地出一份看着像 CMYK 的废件。
        var withLogo = TemplateWith(new TemplateElement
        {
            Kind = ElementKind.Image, ImagePath = Path.Combine(_dir, "logo.png"), X = 4, Y = 4, Width = 20, Height = 10,
        });
        var request = Request(withLogo, true);
        var outcome = SheetExportService.ExportTiff(request, Path.Combine(_dir, "nope.tif"), null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("分不开图片与矢量底图", outcome.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "nope.tif")), "拦下了还留个半成品文件，等于换个方式骗人");
        return true;
    });

    [Fact]
    public void SvgSaysOutLoudThatThisChannelCannotCarryCmyk() => OnSta(() =>
    {
        // 取证：CDR 的 SVG 通道装不下 CMYK。摘要里必须点破，不能等店里拿到近似色再来问。
        var request = Request(TemplateWith(BigText(LabelColor.FromCmyk(37, 63, 11, 5))), false);
        var outcome = SheetExportService.ExportSvg(request, _dir, new SvgExportOptions
        {
            Mode = SvgExportMode.PerSheet,
        }, null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("带不动 CMYK", outcome.Summary, StringComparison.Ordinal);

        // 没填 CMYK 墨色的模板不该被唠叨这件事。
        var plain = Request(TemplateWith(BigText(null)), false);
        var plainOutcome = SheetExportService.ExportSvg(plain, _dir, new SvgExportOptions { Mode = SvgExportMode.PerSheet },
            null, CancellationToken.None);
        Assert.DoesNotContain("带不动 CMYK", plainOutcome.Summary, StringComparison.Ordinal);
        return true;
    });

    [Fact]
    public void TheEditorShowsWhatOnlyTheExportUsedToSay() => OnSta(() =>
    {
        // 底稿降级从前只在导出摘要里说一次：画布上看着正常、渐变被抹成纯色要等到出片才看得见。
        var art = Path.Combine(_dir, "bg.svg");
        File.WriteAllText(art, """
<svg xmlns="http://www.w3.org/2000/svg" width="50mm" height="40mm" viewBox="0 0 50 40">
  <rect x="5" y="5" width="30" height="20" fill="url(#fade)"/>
</svg>
""");
        var template = TemplateWith(new TemplateElement
        {
            Kind = ElementKind.Vector, ImagePath = art, X = 4, Y = 4, Width = 40, Height = 24,
        });
        var vm = new TemplateEditorViewModel(template, new TemplateStore(Path.Combine(_dir, "lib")));

        Assert.Contains(vm.Issues, i => i.Contains("底稿", StringComparison.Ordinal) && i.Contains("渐变", StringComparison.Ordinal));
        return true;
    });
}
