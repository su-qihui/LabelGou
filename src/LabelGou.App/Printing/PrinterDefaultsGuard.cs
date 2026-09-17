using System.Threading;
using LabelGou.App.Services;
using LabelGou.Core.Printing;

namespace LabelGou.App.Printing;

/// <summary>
/// <strong>退出 LabelGou 时，把「这台打印机在本机的用户默认」恢复回我们动它之前的样子</strong>（第 84 棒）。
/// <para>用户口径：「打印默认设置大致就是图一图二这方面设置以后对系统进行了更改，导致在其他软件调用打印机
/// 也用了软件的配置 → 在退出 labelgou 软件时自动恢复打印原本配置」。</para>
/// <para>为什么以前非要写这份注册表：托管打印票 <c>PrintQueue.UserPrintTicket</c> 在 .NET 8 上是空操作、
/// <c>AddJob</c> 带票那条会卡住不返回（§五-155），<c>HKCU\Printers\DevModePerUser</c> 才是作业取值处
/// （§五-159）。也就是说"只对本次打印生效"这条路本机走不通——那就改成<strong>用完还回去</strong>。</para>
/// <para>账落在磁盘上（<see cref="PrinterDefaultsLedger"/>）：进程被强杀、断电、蓝屏时 <c>OnExit</c> 不会跑，
/// 店里那台机器会一直留着 LabelGou 的配置——那比没做这个功能更糟（它悄悄留着）。所以启动时先补还一次。</para>
/// </summary>
public static class PrinterDefaultsGuard
{
    private static readonly PrinterDefaultsLedger Ledger = new();

    /// <summary>正在恢复本身也要写这份值，不能再记一次账（否则会把"恢复后的样子"当成原样）。</summary>
    private static int _restoring;

    /// <summary>启动时补还上次没还回去的（崩溃/被强杀/断电）；还回去后要说一句话，不许静默改系统设置。</summary>
    public static string? RecoverLeftoversAtStartup()
    {
        var pending = Ledger.Pending();
        if (pending.Count == 0) return null;

        var done = new List<string>();
        var failed = new List<string>();
        foreach (var backup in pending)
        {
            if (RestoreOne(backup.PrinterName, backup.Original)) done.Add(backup.PrinterName);
            else failed.Add(backup.PrinterName);
        }
        AppLog.Info($"启动时补还上次没恢复的打印机默认：还回 {done.Count} 台" +
                    (failed.Count > 0 ? $"，失败：{string.Join("、", failed)}" : ""));
        var text = "上次 LabelGou 没正常退出（进程被结束或断电），已经把这台打印机的默认设置还回原来的样子："
                   + string.Join("、", done);
        return failed.Count > 0
            ? text + $"。还有 {failed.Count} 台没能还回去（{string.Join("、", failed)}），请在驱动「打印首选项」里自己确认一下。"
            : (done.Count > 0 ? text : null);
    }

    /// <summary>写这份值之前记一笔原样（同一台只记第一次——第二次那份已经是被我们改过的）。</summary>
    public static void RememberBeforeChange(string printerName)
    {
        if (Volatile.Read(ref _restoring) != 0) return;
        try
        {
            var original = PrinterSettingsReader.ReadUserDevMode(printerName);
            if (Ledger.NoteOriginal(printerName, original))
                AppLog.Info($"已记下「{printerName}」原本的本用户默认" +
                            (original is null ? "（原本没有这条值，退出时会把它删掉）" : $"（{original.Length} 字节）"));
        }
        catch (Exception ex)
        {
            AppLog.Info($"记打印机默认原样失败（不影响这次打印，但退出时可能恢复不了）：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>退出时调用：把账上的每台都恢复回原样并撤账。返回一句给人看的话（没账就返回 null）。</summary>
    public static string? RestoreAllOnExit()
    {
        var pending = Ledger.Pending();
        if (pending.Count == 0) return null;

        var done = new List<string>();
        var failed = new List<string>();
        foreach (var backup in pending)
        {
            if (RestoreOne(backup.PrinterName, backup.Original)) done.Add(backup.PrinterName);
            else failed.Add(backup.PrinterName);
        }
        AppLog.Info($"退出时恢复打印机本用户默认：{done.Count} 台已还回原样" +
                    (failed.Count > 0 ? $"，失败 {failed.Count} 台：{string.Join("、", failed)}" : ""));
        foreach (var bad in Ledger.Unreadable()) AppLog.Info("账本里有读不回来的文件（那台没被恢复）：" + bad);
        return failed.Count == 0 ? null
            : $"有 {failed.Count} 台打印机的默认设置没能还回原样（{string.Join("、", failed)}）——" +
              "下次启动会再试一次；急用就在驱动的「打印首选项」里自己看一眼。";
    }

    /// <summary>恢复一台。成功才撤账——没恢复成却把账删了，等于把这台永远留在被改过的状态。</summary>
    private static bool RestoreOne(string printerName, byte[]? original)
    {
        Interlocked.Exchange(ref _restoring, 1);
        try
        {
            if (!PrinterSettingsReader.RestoreUserDevMode(printerName, original)) return false;
            Ledger.Clear(printerName);
            AppLog.Info($"已恢复「{printerName}」的本用户默认" + (original is null ? "（删掉了我们写进去的那条值）" : $"（{original.Length} 字节）"));
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _restoring, 0);
        }
    }
}
