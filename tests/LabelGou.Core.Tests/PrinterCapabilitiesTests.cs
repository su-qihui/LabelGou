using LabelGou.Core.Printing;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 74 棒：⑤ 步「这台打印机的出纸设置」三格的判据。
/// <para>
/// 夹具是从店里那台 RICOH 6001 与一台 Canon G3010 的真 <c>PrintCapabilities</c> XML 里剪出来的
/// （标签与候选值原文照抄，只删掉本棒不关心的功能）——自己编一份「像样的 XML」测不出真驱动的形状。
/// </para>
/// </summary>
public class PrinterCapabilitiesTests
{
    /// <summary>RICOH 6001 黑白：只有「颜色」和「纸张来源」两项功能，压根没有介质类型。</summary>
    private const string RicohXml = """
        <psf:PrintCapabilities xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                               xmlns:xsd="http://www.w3.org/2001/XMLSchema" version="1"
                               xmlns:ns0000="http://schemas.microsoft.com/windows/printing/oemdriverpt/RICOH_Aficio_MP 6001 PCL 6/6.0.6000.16386/"
                               xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords">
          <psf:Feature name="psk:PageOutputColor">
            <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">颜色</psf:Value></psf:Property>
            <psf:Option name="psk:Monochrome">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">黑白(C)</psf:Value></psf:Property>
            </psf:Option>
          </psf:Feature>
          <psf:Feature name="psk:JobInputBin">
            <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">纸张来源(S)</psf:Value></psf:Property>
            <psf:Option name="ns0000:Auto">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">自动选择纸盘</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="ns0000:Upper">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">纸盘1</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="ns0000:Middle">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">纸盘2</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="ns0000:Lower">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">纸盘3</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="psk:Manual">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">手送台</psf:Value></psf:Property>
            </psf:Option>
          </psf:Feature>
        </psf:PrintCapabilities>
        """;

    /// <summary>Canon G3010：三项功能都有，介质类型还带驱动私有的相纸档（这里只留前四档）。</summary>
    private const string CanonXml = """
        <psf:PrintCapabilities xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                               xmlns:xsd="http://www.w3.org/2001/XMLSchema" version="1"
                               xmlns:ns0000="http://schemas.microsoft.com/windows/printing/oemdriverpt/Canon_G3010_series/10.83.1.0/"
                               xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords">
          <psf:Feature name="psk:PageOutputColor">
            <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">彩色</psf:Value></psf:Property>
            <psf:Option name="psk:Color">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">彩色</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="psk:Monochrome">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">灰度</psf:Value></psf:Property>
            </psf:Option>
          </psf:Feature>
          <psf:Feature name="psk:JobInputBin">
            <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">纸张来源</psf:Value></psf:Property>
            <psf:Option name="psk:AutoSheetFeeder">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">后端托盘</psf:Value></psf:Property>
            </psf:Option>
          </psf:Feature>
          <psf:Feature name="psk:PageMediaType">
            <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">介质类型</psf:Value></psf:Property>
            <psf:Option name="psk:Plain">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">普通纸</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="ns0000:PhotoPaperPlusGlossy2">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">高级光面照片纸 II</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="psk:EnvelopePlain">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">信封</psf:Value></psf:Property>
            </psf:Option>
            <psf:Option name="psk:HighResolution">
              <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">高分辨率纸</psf:Value></psf:Property>
            </psf:Option>
          </psf:Feature>
        </psf:PrintCapabilities>
        """;

    [Fact]
    public void DriverWordsAreCarriedThroughVerbatim()
    {
        var settings = PrinterCapabilities.Parse(RicohXml);

        var bin = Assert.Contains(PrinterCapabilities.BinFeature, settings);
        Assert.Equal("纸张来源(S)", bin.DisplayName);
        Assert.Equal(
            new[] { "自动选择纸盘", "纸盘1", "纸盘2", "纸盘3", "手送台" },
            bin.Options.Select(o => o.DisplayName));
        // 驱动私有前缀的关键字要原样留着——它是唯一能写回驱动的值
        Assert.Equal("ns0000:Upper", bin.Options[1].Keyword);
        Assert.Equal("Upper", bin.Options[1].KeywordTail);
    }

    [Fact]
    public void FeatureTheDriverNeverReportsIsNotFakedFromTicketDefault()
    {
        // 这台 RICOH 的 ticket 永远回 PageMediaType=Plain，可驱动压根没有这个功能：
        // 拿那个默认值糊出「普通纸」就是编故事——操作员会以为机器认了这个设置。
        var rows = PrinterCapabilities.BuildRows(
            PrinterCapabilities.Parse(RicohXml), "Monochrome", "Plain");

        var mediaType = rows.Single(r => r.Label == "纸张类型");
        Assert.Equal("这台机器的驱动不报这一项", mediaType.CurrentText);
        Assert.DoesNotContain("普通纸", mediaType.OptionsText);
    }

    [Fact]
    public void CurrentValueIsTranslatedWithTheDriversOwnVocabulary()
    {
        var rows = PrinterCapabilities.BuildRows(
            PrinterCapabilities.Parse(CanonXml), "Monochrome", "EnvelopePlain");

        Assert.Equal("灰度", rows.Single(r => r.Label == "输出颜色").CurrentText);
        Assert.Equal("信封", rows.Single(r => r.Label == "纸张类型").CurrentText);
        // 纸盘这一格没有可信的当前值来源（实测 ticket 回 Unknown），只报这台机器有什么；
        // 界面不许承诺「改了会生效」——用户实测过一回：驱动页选了手送台，作业照旧走纸盘1
        var bin = rows.Single(r => r.Label == "纸张来源");
        Assert.Equal("软件读不到，进「打印首选项…」看", bin.CurrentText);
        Assert.Contains("读不回当前值", bin.Note);
        Assert.DoesNotContain("生效", bin.Note);
        // 行标题是我们固定的，但驱动里的叫法必须一起说清楚：进驱动页时操作员要能对上号
        Assert.Contains("驱动里这一项叫「彩色」", rows.Single(r => r.Label == "输出颜色").Note);
    }

    [Fact]
    public void ThreeRowsAlwaysInFixedOrder()
    {
        foreach (var xml in new[] { RicohXml, CanonXml, null!, "不是 XML", "" })
        {
            var rows = PrinterCapabilities.BuildRows(PrinterCapabilities.Parse(xml), null, null);
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "输出颜色", "纸张来源", "纸张类型" }, rows.Select(r => r.Label));
        }
    }

    [Fact]
    public void UnreadableXmlSaysSoInsteadOfThrowingOrLookingFine()
    {
        var rows = PrinterCapabilities.BuildRows(PrinterCapabilities.Parse("坏掉的 <psf:Feature"), null, null);
        Assert.All(rows, r => Assert.Equal("这台机器的驱动不报这一项", r.CurrentText));
    }

    [Fact]
    public void DoctypeFromADriverIsAcceptedWithoutResolvingEntities()
    {
        // 真驱动给的 XML 会带 DOCTYPE（§五-56 那一族）：不能因此一项都不显示，也不许去读本地文件
        const string WithDoctype = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE psf:PrintCapabilities [ <!ENTITY dummy "x"> ]>
            <psf:PrintCapabilities xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                                   xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                                   xmlns:xsd="http://www.w3.org/2001/XMLSchema" version="1">
              <psf:Feature name="psk:PageOutputColor">
                <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">颜色</psf:Value></psf:Property>
                <psf:Option name="psk:Color">
                  <psf:Property name="psk:DisplayName"><psf:Value xsi:type="xsd:string">彩色</psf:Value></psf:Property>
                </psf:Option>
              </psf:Feature>
            </psf:PrintCapabilities>
            """;

        var settings = PrinterCapabilities.Parse(WithDoctype);
        Assert.Equal("彩色", settings[PrinterCapabilities.ColorFeature].Options.Single().DisplayName);
    }

    [Fact]
    public void UnknownKeywordStaysRawInsteadOfInventingAChineseName()
    {
        var settings = PrinterCapabilities.Parse(RicohXml);
        var bin = settings[PrinterCapabilities.BinFeature];
        Assert.Equal("ns0000:Weird", bin.NameOf("ns0000:Weird"));
        Assert.Null(bin.NameOf(null));
    }

    [Fact]
    public void SingleOptionFeatureSaysItHasNothingToChoose()
    {
        // 这台 6001 是黑白机：颜色那项驱动只给「黑白(C)」一个值，界面要说「只有一个可选值」而不是让人找下拉
        var rows = PrinterCapabilities.BuildRows(PrinterCapabilities.Parse(RicohXml), "Monochrome", null);
        var color = rows.Single(r => r.Label == "输出颜色");
        Assert.Equal("黑白(C)", color.CurrentText);
        Assert.Contains("只有一个可选值", color.Note);
    }
}
