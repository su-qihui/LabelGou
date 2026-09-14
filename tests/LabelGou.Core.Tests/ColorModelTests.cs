using System.IO;
using LabelGou.Core.Colors;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 47 棒的地基：一支墨的颜色怎么存、怎么换算、怎么进出模板文件。
/// <para>
/// 三件事各自要钉住：① <strong>分量不许漂</strong>——用户填 C=30，出口就该看见 C=30；
/// ② <strong>没填颜色 = 黑 = 逐字旧行为</strong>——存盘连这个字段都不该出现，
/// 现有那些模板文件（金沐/邱总与 11 份 AI 版式）一个字节都不用更新；
/// ③ <strong>换算只是近似</strong>——naive 减色不是色彩管理，测试要把这个边界本身钉住，
/// 免得后来的人把屏幕上那个颜色当成出片依据。
/// </para>
/// </summary>
public class ColorModelTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-color-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>把一份模板按库内口径写成文件再读回来（序列化与反序列化都要过，才叫"存住了"）。</summary>
    private static LabelTemplate WriteThenRead(LabelTemplate template, string folder)
    {
        var store = new TemplateStore(folder);
        var path = Path.Combine(folder, "case.json");
        File.WriteAllText(path, TemplateStore.ToJson(template));
        var (read, issues) = store.ReadFile(path);
        Assert.NotNull(read);
        Assert.False(issues.HasError(), string.Join(" | ", issues.ErrorMessages()));
        return read!;
    }

    // ---------- ① 换算本身 ----------

    [Fact]
    public void CmykCornersMapWhereSubtractiveMathSays()
    {
        var paper = CmykMath.CmykToSrgb(0, 0, 0, 0);
        Assert.Equal(255, paper.R);                                     // 无墨 = 纸白
        Assert.Equal(255, paper.G);
        Assert.Equal(255, paper.B);

        var black = CmykMath.CmykToSrgb(0, 0, 0, 100);
        Assert.Equal(0, black.R);                                       // 满版黑
        Assert.Equal(0, black.G);
        Assert.Equal(0, black.B);

        var cyan = CmykMath.CmykToSrgb(100, 0, 0, 0);
        Assert.Equal(0, cyan.R);                                        // 满青吸掉红，剩蓝绿
        Assert.Equal(255, cyan.G);
        Assert.Equal(255, cyan.B);

        var red = CmykMath.CmykToSrgb(0, 100, 100, 0);
        Assert.Equal(255, red.R);                                       // 印刷那支红 = 无青 + 满品满黄
        Assert.Equal(0, red.G);
        Assert.Equal(0, red.B);
    }

    [Fact]
    public void OutOfRangePercentagesAreClampedNotThrown()
    {
        // 分量来自用户手填与外部 SVG：一个越界数字不该让整张标签画不出来。
        var over = CmykMath.CmykToSrgb(250, -40, 0, 0);
        Assert.Equal(0, over.R);
        Assert.Equal(255, over.G);

        Assert.Equal((100, 0, 0, 0), CmykMath.SrgbToCmyk(-30, 900, 300));
    }

    [Fact]
    public void NeutralGreyLandsOnTheBlackPlateOnly()
    {
        // 印刷店说"灰字别掺彩"，靠的就是这条：黑版取三色里最少墨的那一档，中性灰于是只落 K。
        var (c, m, y, k) = CmykMath.SrgbToCmyk(128, 128, 128);
        Assert.Equal(0, c);
        Assert.Equal(0, m);
        Assert.Equal(0, y);
        Assert.Equal(50, k);
    }

    [Fact]
    public void TypedPlateSplitSurvivesEvenThoughRgbCannotCarryIt()
    {
        // 存两半的理由，用一条会变的数钉住：同一支墨的 sRGB 再反算回 CMYK，得到的是**另一套配墨**
        // （屏幕上分不出，版上四格全不同）。哪天有人"简化"成只存一端，这条会红——
        // 那时要想起出口给印刷店的配方，不再是用户亲手填的那一套。
        var ink = LabelColor.FromCmyk(37, 63, 11, 5);
        Assert.Equal((37, 63, 11, 5), (ink.C, ink.M, ink.Y, ink.K));
        Assert.Equal((29, 58, 0, 15), CmykMath.SrgbToCmyk(ink.R, ink.G, ink.B));
    }

    // ---------- ② 写法与解析 ----------

    [Fact]
    public void DeviceCmykFractionsAndPercentsBothParse()
    {
        // SVG/CSS 规定 device-cmyk 的分量是 0~1 小数，各家工具又常写百分数——两副面孔都要认。
        Assert.True(LabelColor.TryParse("device-cmyk(0 0.91 0.90 0)", out var byFraction));
        Assert.Equal((0, 91, 90, 0), (byFraction!.C, byFraction.M, byFraction.Y, byFraction.K));

        Assert.True(LabelColor.TryParse("device-cmyk(0%, 91%, 90%, 0%)", out var byPercent));
        Assert.Equal(byFraction, byPercent);
        Assert.Equal(ColorEntrySpace.Cmyk, byPercent!.Entry);

        // 分量是契约，精确比；屏幕那三个通道只是近似（0.1×255 落在 25 还是 26 取决于浮点，不该钉死）。
        Assert.Equal(255, (int)byPercent.R);
        Assert.InRange(byPercent.G, (byte)22, (byte)24);
        Assert.InRange(byPercent.B, (byte)24, (byte)26);
    }

    [Fact]
    public void CssAlphaAndCompatColourDoNotBreakParsing()
    {
        Assert.True(LabelColor.TryParse("device-cmyk(0 .9 .9 0 / 1 #ff0000)", out var ink));
        Assert.Equal(90, ink!.M);
    }

    [Fact]
    public void OurStorageSpellingIsPrintFirst()
    {
        Assert.Equal("cmyk(0 91 90 0)", LabelColor.FromCmyk(0, 91, 90, 0).ToStorageString());
        Assert.Equal("#ff0000", LabelColor.FromSrgb(255, 0, 0).ToStorageString());
        Assert.Equal("C0 M91 Y90 K0", LabelColor.FromCmyk(0, 91, 90, 0).ToPrintText());
    }

    [Theory]
    [InlineData("BrandBlue-900")]                                        // 自定义色板名不是颜色
    [InlineData("cmyk(a b c d)")]
    [InlineData("cmyk(0 91 90)")]                                        // 少一格不猜
    [InlineData("#gg0000")]
    [InlineData("")]
    [InlineData(null)]
    public void UnparseableThingsAreNotColours(string? raw)
    {
        Assert.False(LabelColor.TryParse(raw, out var ink));
        Assert.Null(ink);
    }

    // ---------- ③ 进出模板文件 ----------

    [Fact]
    public void TemplateWithoutAColourWritesNoInkFieldAtAll()
    {
        // 「模板文件暂时不用更新」的技术含义：没填颜色时连字段都不出现，
        // 于是老文件与新文件在这一格上逐字同形，diff 与对比都不会凭空多出改动。
        var json = TemplateStore.ToJson(OneTextTemplate(null));
        Assert.DoesNotContain("inkColor", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InkColourRoundTripsThroughATemplateFile()
    {
        var template = OneTextTemplate(LabelColor.FromCmyk(0, 91, 90, 0));
        var read = WriteThenRead(template, MakeTempDir());

        var element = Assert.Single(read.Elements);
        Assert.Equal(LabelColor.FromCmyk(0, 91, 90, 0), element.InkColor);
        Assert.Contains("\"inkColor\": \"cmyk(0 91 90 0)\"", TemplateStore.ToJson(read), StringComparison.Ordinal);
    }

    [Fact]
    public void HexEnteredColourKeepsItsOwnSpellingAndStillCarriesCmyk()
    {
        var read = WriteThenRead(OneTextTemplate(LabelColor.FromSrgb(255, 0, 0)), MakeTempDir());

        var ink = Assert.Single(read.Elements).InkColor;
        Assert.Equal(ColorEntrySpace.Srgb, ink!.Entry);
        Assert.Equal("#ff0000", ink.ToHex());
        Assert.Equal((0, 100, 100, 0), (ink.C, ink.M, ink.Y, ink.K));
    }

    [Fact]
    public void TemplateFileWithoutTheFieldReadsBackAsNoColour()
    {
        // 老文件（v6 之前根本没这一格）读进来必须是 null = 黑，而不是"白"，也不许抛。
        var json = TemplateStore.ToJson(OneTextTemplate(LabelColor.FromCmyk(0, 100, 100, 0)))
            .Replace("\"inkColor\": \"cmyk(0 100 100 0)\",", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("inkColor", json, StringComparison.OrdinalIgnoreCase);

        var read = WriteThenReadJson(json);
        Assert.Null(Assert.Single(read.Elements).InkColor);
    }

    [Fact]
    public void BogusColourSpellingDoesNotKillTheTemplate()
    {
        // 看不懂的墨色写法不该让整份模板打不开——那是"印不出东西"级别的后果。
        // 与它对应的处理是当没填（退回黑），而不是把整份文件判死。
        var json = TemplateStore.ToJson(OneTextTemplate(LabelColor.Black))
            .Replace("\"#000000\"", "\"BrandRed-9\"", StringComparison.Ordinal);
        Assert.Contains("BrandRed-9", json, StringComparison.Ordinal);

        var read = WriteThenReadJson(json);
        Assert.Null(Assert.Single(read.Elements).InkColor);
    }

    /// <summary>
    /// 版本号跟着"最近一次加字段"走。递增时在这儿钉一下新值，逐条"缺字段该当什么"写在
    /// <see cref="LabelTemplate.CurrentSchemaVersion"/> 的注释里（一份事实只留一处）。
    /// </summary>
    [Fact]
    public void SchemaVersionTracksTheLatestFieldAddition() => Assert.Equal(12, LabelTemplate.CurrentSchemaVersion);

    private static LabelTemplate WriteThenReadJson(string json)
    {
        var dir = MakeTempDir();
        var path = Path.Combine(dir, "case.json");
        File.WriteAllText(path, json);
        var (read, issues) = new TemplateStore(dir).ReadFile(path);
        Assert.NotNull(read);
        Assert.False(issues.HasError(), string.Join(" | ", issues.ErrorMessages()));
        return read!;
    }

    private static LabelTemplate OneTextTemplate(LabelColor? ink)
    {
        var template = new LabelTemplate { Id = "user.color", Name = "颜色测试", WidthMm = 100, HeightMm = 80, BuiltIn = false };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = "{{ContractNo}}",
            X = 5,
            Y = 5,
            Width = 60,
            Height = 8,
            FontSizePt = 10,
            InkColor = ink,
        });
        return template;
    }
}
