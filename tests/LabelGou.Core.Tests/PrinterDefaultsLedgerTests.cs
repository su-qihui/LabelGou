using System.IO;
using LabelGou.Core.Printing;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 84 棒：退出时要把打印机默认还回原样，前提是<strong>这本账记得对</strong>。
/// <para>用户口径：「打印默认设置……以后对系统进行了更改，导致在其他软件调用打印机也用了软件的配置
/// → 在退出 labelgou 软件时自动恢复打印原本配置」。</para>
/// <para>账落在磁盘上（不只在内存里）：进程被强杀或断电时 <c>OnExit</c> 不会跑，那台机器会一直留着
/// LabelGou 的配置——比没做这个功能更糟，因为它悄悄留着。所以这里测的是"跨进程也还在"。</para>
/// </summary>
public class PrinterDefaultsLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "labelgou-b84-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    private PrinterDefaultsLedger Ledger() => new(_dir);

    [Fact]
    public void OnlyTheFirstOriginalIsKept()
    {
        var ledger = Ledger();
        var original = new byte[] { 1, 2, 3, 4 };
        var ours = new byte[] { 9, 9, 9, 9 };

        Assert.True(ledger.NoteOriginal("RICOH 6001 黑白", original));
        // 第二次写之前那份已经是被我们改过的值——拿它当"原样"就白恢复了
        Assert.False(ledger.NoteOriginal("RICOH 6001 黑白", ours));

        var kept = Assert.Single(ledger.Pending());
        Assert.Equal(original, kept.Original);
    }

    [Fact]
    public void APrinterThatHadNoValueRestoresToDeleteIt()
    {
        var ledger = Ledger();

        Assert.True(ledger.NoteOriginal("Canon G3010", null));

        var kept = Assert.Single(ledger.Pending());
        Assert.Null(kept.Original);      // 原本没有这条值 → 恢复 = 删掉，不是写一份假的进去
    }

    [Fact]
    public void TheBlobAndThePrinterNameComeBackIntact()
    {
        var ledger = Ledger();
        var blob = new byte[520];
        new Random(7).NextBytes(blob);
        const string name = "Ricoh MP 6001 黑白 (PCL 6)";

        Assert.True(ledger.NoteOriginal(name, blob));
        var kept = Assert.Single(Ledger().Pending());          // 换一个实例：走的是文件，不是内存

        Assert.Equal(name, kept.PrinterName);
        Assert.Equal(blob, kept.Original);
    }

    [Fact]
    public void RestoredPrintersComeOffTheBook()
    {
        var ledger = Ledger();
        ledger.NoteOriginal("A", new byte[] { 1 });
        ledger.NoteOriginal("B", new byte[] { 2 });

        ledger.Clear("A");

        Assert.Equal(new[] { "B" }, ledger.Pending().Select(p => p.PrinterName));
    }

    /// <summary>坏文件不能让那台打印机悄悄漏掉——它恢复不了，必须数得出来。</summary>
    [Fact]
    public void ABrokenLedgerFileIsReportedNotSwallowed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, PrinterDefaultsLedger.MakeFileName("坏掉的那台") + ".json"), "{ 不是 json");
        Ledger().NoteOriginal("好的那台", new byte[] { 5 });

        var pending = Ledger().Pending();
        Assert.Equal(new[] { "好的那台" }, pending.Select(p => p.PrinterName));
        var bad = Assert.Single(Ledger().Unreadable());
        Assert.Contains("坏掉的那台", bad, StringComparison.Ordinal);
    }

    [Fact]
    public void PrinterNamesAreMadeFileSystemSafe()
    {
        foreach (var name in new[] { "A/B\\C:D*E?F\"G<H>I|J", "  前后空格  ", "" })
        {
            var made = PrinterDefaultsLedger.MakeFileName(name);
            Assert.NotEmpty(made);
            Assert.All(Path.GetInvalidFileNameChars(), c => Assert.DoesNotContain(c, made));
        }
    }
}
