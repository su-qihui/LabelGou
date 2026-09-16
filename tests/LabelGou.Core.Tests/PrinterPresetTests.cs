using System.IO;
using LabelGou.Core.Printing;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 77 棒：「打印机默认方案」的解码与库。
/// 夹具里那份 DEVMODE 头 220 字节是**店里那台 RICOH 6001 被用户设成手送台 + 标签纸 + 280×200 之后**
/// 从注册表里抠出来的真数据（不是手编的），所以解码断言钉的是真值。
/// </summary>
public class PrinterPresetTests : IDisposable
{
    /// <summary>真 blob 的公开部分（hex）：dmPaperSize=256 自定义、宽 2800、长 2000（0.1mm）、dmDefaultSource=4。</summary>
    private const string RealDevModeHead =
        "5200490043004f004800200036003000300031002000d19e7d7600006c002000440072006900760065007200000000000000" +
        "000000000000000000000000000001040001dc002c0e43ff800301000001d007f00a64000100040058020100010058020300" +
        "0000410034000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000100000000000000010000000200000001000000" +
        "0100000000000000000000000000000000000000";

    private static byte[] RealHead() => Convert.FromHexString(RealDevModeHead);

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "labelgou-preset-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public PrinterPresetTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { /* 临时目录留着无害 */ } }

    [Fact]
    public void RealDriverDevModeDecodesToTheShopSettings()
    {
        var facts = DevModeFacts.Read(RealHead());

        Assert.Equal(256, facts.PaperSizeId);
        Assert.Equal(280, facts.WidthMm);
        Assert.Equal(200, facts.LengthMm);
        Assert.Equal(4, facts.DefaultSource);
        Assert.Equal("自定义 280×200mm · 纸盘编号 4", facts.Describe());
    }

    [Fact]
    public void ShortOrEmptyBlobDecodesToNothingInsteadOfThrowing()
    {
        Assert.Equal("没指定 · 没指定（编号 0）", DevModeFacts.Read(new byte[10]).Describe());
        Assert.Equal(0, DevModeFacts.Read(Array.Empty<byte>()).PaperSizeId);
    }

    [Fact]
    public void SaveThenListRoundTripsTheWholeDevMode()
    {
        var store = new PrinterPresetStore(_dir);
        var blob = RealHead();

        var saved = store.Save("默认", "RICOH 6001 黑白", blob);

        var back = Assert.Single(store.List());
        Assert.Equal("默认", back.Name);
        Assert.Equal("RICOH 6001 黑白", back.PrinterName);
        Assert.Equal("自定义 280×200mm · 纸盘编号 4", back.Summary);
        Assert.Equal(blob, back.DevMode);          // 整份带回来，一项都不许丢
        Assert.Equal(saved.Name, store.GetByName("默认")?.Name);
        Assert.Null(store.GetByName("没这个方案"));
    }

    [Fact]
    public void ChineseNamesBecomeLegalFileNames()
    {
        Assert.Equal("默认", PrinterPresetStore.MakeFileName(" 默认 "));
        Assert.Equal("A_B_C", PrinterPresetStore.MakeFileName("A/B\\C"));
        Assert.Equal("未命名", PrinterPresetStore.MakeFileName("   "));
    }

    [Fact]
    public void BrokenPresetFilesAreReportedInsteadOfVanishingSilently()
    {
        // 与纸规库同一口径（§五-64）：坏文件要说出「哪份、为什么」，不能静默少一个方案
        File.WriteAllText(Path.Combine(_dir, "坏.json"), "{ 这不是 JSON");
        File.WriteAllText(Path.Combine(_dir, "短.json"),
            """{"Name":"短","PrinterName":"P","Summary":"","SavedAt":"","devmode":"YWJj"}""");

        var report = new PrinterPresetStore(_dir).ListWithReport();

        Assert.Empty(report.Presets);
        Assert.Equal(2, report.Skipped.Count);
        Assert.Contains(report.Skipped, s => s.Contains("JSON 语法不对", StringComparison.Ordinal));
        Assert.Contains(report.Skipped, s => s.Contains("不像一份完整设置", StringComparison.Ordinal));
    }

    [Fact]
    public void DeletingAPresetBacksItUpFirst()
    {
        var store = new PrinterPresetStore(_dir);
        store.Save("默认", "RICOH 6001 黑白", RealHead());
        var path = Path.Combine(_dir, "默认.json");

        Assert.True(store.Delete("默认", out _));
        Assert.False(File.Exists(path));
        Assert.Contains(Directory.GetFiles(_dir), f => f.Contains("默认.json.bak-", StringComparison.Ordinal));
        Assert.False(store.Delete("本来就没有", out var reason));
        Assert.Contains("没有叫", reason);
    }

    [Fact]
    public void RefusesToStoreSomethingThatIsNotADevMode()
    {
        var store = new PrinterPresetStore(_dir);
        Assert.Throws<ArgumentException>(() => store.Save("默认", "RICOH 6001 黑白", new byte[100]));
        Assert.Empty(store.List());
    }
}
