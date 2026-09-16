using LabelGou.App.Printing;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 74 棒：⑤ 步那三格的读数。
/// 只钉「形状 + 失败要说人话」——不钉某台机器具体报了什么值，
/// 否则全绿就取决于这台电脑上装过什么驱动（§五-95 那一族）。
/// </summary>
public class PrinterSettingsPanelTests
{
    [Fact]
    public void ReaderAlwaysHandsBackThreeRowsOrSaysWhyNot()
    {
        var report = PrinterSettingsReader.Read(null);

        Assert.True(report.Error is null, $"读默认打印机失败：{report.Error}");
        Assert.Equal(3, report.Rows.Count);
        Assert.False(string.IsNullOrWhiteSpace(report.PrinterName));
        Assert.All(report.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Label)));
        Assert.All(report.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.CurrentText)));
    }

    [Fact]
    public void UnknownPrinterNameDegradesToHumanWordsInsteadOfThrowing()
    {
        var report = PrinterSettingsReader.Read("这台打印机根本不存在-B74");

        Assert.Empty(report.Rows);
        Assert.False(string.IsNullOrWhiteSpace(report.Error));
        Assert.DoesNotContain("PrintQueueException", report.Error);   // 类名甩给用户看就是没说清
        Assert.Contains("打印机", report.Error);
    }
}
