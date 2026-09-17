using System.Threading;
using LabelGou.App.Services;
using LabelGou.Core.Printing;

namespace LabelGou.App.Printing;

/// <summary>
/// <strong>把「这台打印机在本机的用户默认」还成驱动自己的默认</strong>（第 84 棒补正）。
/// <para>用户口径：「打印默认设置……对系统进行了更改，导致在其他软件调用打印机也用了软件的配置」→
/// 「在红框位置设置开关——完成打印后恢复打印机默认设置，若打勾后续打印/退出软件自动恢复默认设置」。</para>
/// <para>为什么以前非要写这份注册表：托管打印票 <c>PrintQueue.UserPrintTicket</c> 在 .NET 8 上是空操作、
/// <c>AddJob</c> 带票那条会卡住不返回（§五-155），<c>HKCU\Printers\DevModePerUser</c> 才是作业取值处
/// （§五-159）。也就是说"只对本次打印生效"这条路本机走不通——那就改成<strong>用完还回去</strong>：
/// 每次打印完还一次、退出再兜一次、被强杀则下次启动补还。</para>
/// <para><strong>还的目标是驱动自己的默认</strong>（<c>DM_OUT_DEFAULT</c>，即驱动页那颗「恢复默认设置」），
/// 不是"我们进来前那份快照"：第一版拿快照当原样，而快照只能回到我们手够得着的最早那一刻——那之前若已被
/// 上一版写脏，恢复就是把脏值再放回去一遍（用户实测"没成功"：日志显示 20:54 退出时确实"已恢复 3848 字节"，
/// 而那份里就是自定义 280×200 + 私有纸盘号）。</para>
/// <para>账落在磁盘上（<see cref="PrinterDefaultsLedger"/>）：进程被强杀、断电、蓝屏时 <c>OnExit</c> 不会跑，
/// 店里那台机器会一直留着 LabelGou 的配置——那比没做这个功能更糟（它悄悄留着）。所以启动时先补还一次。</para>
/// </summary>
public static class PrinterDefaultsGuard
{
    private static readonly PrinterDefaultsLedger Ledger = new();

    /// <summary>正在恢复本身也要写这份值，不能再记一次账（否则会把"恢复后的样子"当成原样）。</summary>
    private static int _restoring;

    /// <summary>⑤ 步那颗「打印后恢复打印机默认」：关掉就什么都不还（那时改动是用户自己要留着的）。</summary>
    public static bool RestoreAtExit { get; set; } = true;

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
        var text = "上次 LabelGou 没正常退出（进程被结束或断电），已经把这台打印机的默认设置还成驱动自己的默认："
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

    /// <summary>
    /// 退出时调用：把账上的每台都还回驱动默认。返回一句给人看的话（没账或那颗勾没打就返回 null）。
    /// <para>⑤ 步那颗勾关掉（<see cref="RestoreAtExit"/> = false）就什么都不做——那时改动是用户自己要留着的。</para>
    /// </summary>
    public static string? RestoreAllOnExit()
    {
        if (!RestoreAtExit) return null;
        return RestorePending("退出");
    }

    /// <summary>
    /// 一次打印任务成功后调用（⑤ 步「打印后恢复打印机默认」勾上时）：不用等到退出，别的软件紧接着打也不会吃到我们的设置。
    /// </summary>
    public static void RestoreAfterPrint() => RestorePending("打印后");

    /// <summary>把账上挂着的每台都还回去。<paramref name="when"/> 只用于日志里说清是哪一步触发的。</summary>
    private static string? RestorePending(string when)
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
        AppLog.Info($"{when}恢复打印机本用户默认：{done.Count} 台已还回" +
                    (failed.Count > 0 ? $"，失败 {failed.Count} 台：{string.Join("、", failed)}" : ""));
        foreach (var bad in Ledger.Unreadable()) AppLog.Info("账本里有读不回来的文件（那台没被恢复）：" + bad);
        return failed.Count == 0 ? null
            : $"有 {failed.Count} 台打印机的默认设置没能还回原样（{string.Join("、", failed)}）——" +
              "下次启动会再试一次；急用就在驱动的「打印首选项」里自己看一眼。";
    }

    /// <summary>
    /// 恢复目标怎么选：<strong>驱动默认 &gt; 我们进来前那份快照 &gt; 删掉这条值</strong>。
    /// <para>纯函数，判据能直接问——"拿快照当原样"正是第一版失败的地方（快照可能本身就被上一版写脏过）。</para>
    /// </summary>
    internal static (byte[]? Target, string How, bool FellBack) ChooseRestoreTarget(byte[]? machineDefault, byte[]? snapshot)
        => machineDefault is not null ? (machineDefault, "驱动默认", false)
         : snapshot is not null ? (snapshot, "退回快照", true)
         : (null, "删掉这条值", true);

    /// <summary>
    /// 恢复一台。<strong>目标是驱动自己的默认</strong>（驱动页那颗「恢复默认设置」回到的那一套），
    /// 不是"我们进来前那份快照"——快照只能回到我们手够得着的最早那一刻，那之前若已被上一版写脏，
    /// 恢复就是把脏值再放回去一遍（第 84 棒第一版正是这样，用户实测"没成功"）。
    /// <para>读不到驱动默认时才退回：有快照写快照、原本没值就删值。恢复<strong>成功才撤账</strong>——
    /// 没恢复成却把账删了，等于把这台永远留在被改过的状态。</para>
    /// </summary>
    private static bool RestoreOne(string printerName, byte[]? snapshot)
    {
        var machineDefault = PrinterSettingsReader.MachineDefaultDevMode(printerName, out var why);
        var (target, how, fellBack) = ChooseRestoreTarget(machineDefault, snapshot);
        if (fellBack) AppLog.Info($"读不到「{printerName}」的驱动默认（{why}），改用{how}");
        Interlocked.Exchange(ref _restoring, 1);
        try
        {
            if (!PrinterSettingsReader.RestoreUserDevMode(printerName, target)) return false;
            Ledger.Clear(printerName);
            AppLog.Info($"已把「{printerName}」的本用户默认还成{how}" +
                        (target is null ? "（这台原本就没有这条值）" : $"（{target.Length} 字节）"));
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _restoring, 0);
        }
    }
}
