using System.Printing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using LabelGou.App.Services;
using LabelGou.Core.Printing;

namespace LabelGou.App.Printing;

/// <summary>⑤ 步那一小块「这台打印机的出纸设置」的读数结果。</summary>
public sealed record PrinterSettingsReport(
    string PrinterName,
    IReadOnlyList<PrinterSettingRow> Rows,
    string? Error)
{
    public static PrinterSettingsReport Fail(string printerName, string error)
        => new(printerName, Array.Empty<PrinterSettingRow>(), error);
}

/// <summary>
/// 读打印机的出纸设置（颜色 / 纸张来源 / 纸张类型），并打开驱动自己的首选项页。
/// <para>
/// 为什么只能读不能写：实测 .NET 8 的 <c>PrintQueue.UserPrintTicket</c> 赋值在这台机器上是空操作
/// （三台打印机一致，连「横向」这种驱动必定支持的值都写不进去），而 <c>PrintVisual</c> 没有别的
/// 带 ticket 的入口。所以这三格照实显示，改值交给驱动那一页。
/// </para>
/// </summary>
public static class PrinterSettingsReader
{
    /// <summary>读一次。printerName 为空时用系统默认打印机。</summary>
    public static PrinterSettingsReport Read(string? printerName)
    {
        LocalPrintServer? server = null;
        PrintQueue? queue = null;
        try
        {
            server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
            queue = string.IsNullOrWhiteSpace(printerName)
                ? server.DefaultPrintQueue ?? throw new InvalidOperationException("系统里没有可用的打印机。")
                : new PrintQueue(server, printerName);
            queue.Refresh();
            var name = queue.Name;

            string? xml = null;
            try
            {
                using var ms = queue.GetPrintCapabilitiesAsXml();
                xml = Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                AppLog.Info($"读不到 {name} 的 capabilities XML：{ex.Message}");
            }

            var settings = PrinterCapabilities.Parse(xml);
            PrintTicket? ticket = null;
            try { ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket; }
            catch (Exception ex) { AppLog.Info($"读不到 {name} 的当前 ticket：{ex.Message}"); }

            return new PrinterSettingsReport(
                name,
                PrinterCapabilities.BuildRows(
                    settings,
                    ticket?.OutputColor?.ToString(),
                    ticket?.PageMediaType?.ToString()),
                null);
        }
        catch (Exception ex)
        {
            var text = Describe(ex);
            AppLog.Info($"读打印机出纸设置失败（{printerName ?? "默认"}）：{text}");
            return PrinterSettingsReport.Fail(printerName ?? "默认打印机", text);
        }
        finally
        {
            queue?.Dispose();
            server?.Dispose();
        }
    }

    /// <summary>
    /// 弹驱动自己的首选项页（就是 CorelDRAW 里那颗「属性(P)…」打开的同一页）。
    /// <para>
    /// 关键在返回之后：`DM_IN_PROMPT | DM_OUT_BUFFER` 只把用户的选择**交回给调用方**，驱动自己不落盘
    /// （实测过：以前我们把它丢掉，所以重开那页看着像记住了、作业却照旧走默认）。这里做两件事——
    /// ① 与原值逐字节比差并写日志（搞清「手送台 / 标签纸」住在哪几个字节）；
    /// ② 写回 `HKCU\Printers\DevModePerUser`，这才是本用户默认值的存放处（实测：改这份 blob 后
    /// <c>DocumentProperties</c> 立刻读到新值）。
    /// </para>
    /// </summary>
    /// <returns>用户按了确定时返回差异说明；取消返回 null 且 <paramref name="error"/> 为空；
    /// 弹不开返回 null 并给出一句能看的原因（「点了没反应」必须能分清取消与失败）。</returns>
    public static string? OpenDriverPreferences(string? printerName, IntPtr owner, out string? error)
    {
        error = null;
        var name = ResolveName(printerName);
        if (name is null)
        {
            error = "没找到这台打印机（列表可能过期了），点「刷新」再试。";
            return null;
        }
        IntPtr hPrinter = IntPtr.Zero;
        try
        {
            // 实测：带 PRINTER_DEFAULTSW(0x8=PRINTER_ACCESS_USE) 一律回拒绝访问，传 NULL 才打得开
            if (!OpenPrinterW(name, out hPrinter, IntPtr.Zero))
            {
                var win32 = Marshal.GetLastWin32Error();
                AppLog.Info($"打开打印机句柄失败（{name}）：{win32}");
                error = $"打不开这台打印机的设置（系统返回错误 {win32}）。先确认它在 Windows 里能正常打印。";
                return null;
            }
            int size = DocumentPropertiesW(owner, hPrinter, name, IntPtr.Zero, IntPtr.Zero, DM_OUT_BUFFER);
            if (size <= 0)
            {
                error = $"读不到「{name}」的当前设置（驱动没响应或这台机器离线）。";
                return null;
            }
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (DocumentPropertiesW(owner, hPrinter, name, buffer, IntPtr.Zero, DM_OUT_BUFFER) <= 0)
                {
                    error = $"读不到「{name}」的当前设置（驱动没响应或这台机器离线）。";
                    return null;
                }
                var oldBytes = ReadBytes(buffer, size);
                // 进与出用同一块缓冲：驱动页在它上面就地改（旧代码就这么调，实测弹得开）
                if (DocumentPropertiesW(owner, hPrinter, name, buffer, buffer, DM_IN_PROMPT | DM_OUT_BUFFER) <= 0)
                    return null;            // 用户按了取消，静默

                var newBytes = ReadBytes(buffer, size);
                var diff = DescribeDiff(oldBytes, newBytes);
                var persisted = WriteUserDevMode(name, newBytes);
                AppLog.Info($"驱动首选项页返回差异：{diff}；写回本用户默认（DevModePerUser）{(persisted ? "成功" : "失败")}");
                return diff + (persisted ? string.Empty : "（写回注册表失败，这次改动不会生效）");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            // 这里别用 Describe：它会把「P/Invoke 名字写错」这类自伤抹成"问不到设置"，线索就没了
            AppLog.Info($"弹驱动首选项失败（{name}）：{ex.GetType().Name} {ex.Message}");
            error = $"弹不开这台打印机的设置页：{ex.GetType().Name} {ex.Message}";
            return null;
        }
        finally
        {
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }
    }

    private static byte[] ReadBytes(IntPtr ptr, int count)
    {
        var bytes = new byte[count];
        Marshal.Copy(ptr, bytes, 0, count);
        return bytes;
    }

    /// <summary>公开字段里我们看得懂的那几个（偏移按 DEVMODEW 布局，与 Canon 那份对过）。</summary>
    private static readonly (string Name, int Offset)[] KnownFields =
    {
        ("dmPaperSize", 78), ("dmCopies", 86), ("dmDefaultSource", 88), ("dmDuplex", 94),
        ("dmMediaType", 196),
    };

    private static string DescribeDiff(byte[] oldBytes, byte[] newBytes)
    {
        var fields = new List<string>();
        foreach (var (fieldName, offset) in KnownFields)
        {
            var a = ToUInt16(oldBytes, offset);
            var b = ToUInt16(newBytes, offset);
            if (a != b) fields.Add($"{fieldName} {a}→{b}");
        }
        var ranges = new List<string>();
        int start = -1;
        for (var i = 0; i < Math.Min(oldBytes.Length, newBytes.Length); i++)
        {
            if (oldBytes[i] == newBytes[i])
            {
                if (start >= 0) { ranges.Add($"{start}-{i - 1}"); start = -1; }
            }
            else if (start < 0) start = i;
        }
        if (start >= 0) ranges.Add($"{start}-{Math.Min(oldBytes.Length, newBytes.Length) - 1}");
        return $"公开字段：{(fields.Count == 0 ? "没变" : string.Join("、", fields))}；"
             + $"字节差异区间：{(ranges.Count == 0 ? "无" : string.Join("、", ranges))}";
    }

    private static ushort ToUInt16(byte[] bytes, int offset) =>
        offset + 1 < bytes.Length ? (ushort)(bytes[offset] | (bytes[offset + 1] << 8)) : (ushort)0;

    /// <summary>
    /// 写 HKCU\Printers\DevModePerUser\&lt;打印机名&gt;——本用户默认 DEVMODE 的存放处。
    /// 写完**立刻回读逐字节核对**：这台机器上实测过，"调用没报错"不等于写进去了。
    /// </summary>
    private static bool WriteUserDevMode(string printerName, byte[] blob)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserDefaultsKey, writable: true);
            if (key is null) return false;
            key.SetValue(printerName, blob, RegistryValueKind.Binary);
        }
        catch (Exception ex)
        {
            AppLog.Info($"写回本用户默认 DEVMODE 抛：{ex.GetType().Name} {ex.Message}");
            return false;
        }

        try
        {
            using var check = Registry.CurrentUser.OpenSubKey(UserDefaultsKey);
            var back = check?.GetValue(printerName) as byte[];
            return back is not null && back.AsSpan().SequenceEqual(blob);
        }
        catch (Exception ex)
        {
            AppLog.Info($"回读本用户默认 DEVMODE 失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 读这台打印机**当前的本用户默认 DEVMODE**（就是驱动页里设好的那一份，整份、含私有块）。
    /// 存「默认方案」靠它——介质类型住在私有块里，只抄公开字段会丢东西。
    /// </summary>
    public static byte[]? CaptureCurrentDevMode(string? printerName, out string? error)
    {
        error = null;
        var name = ResolveName(printerName);
        if (name is null)
        {
            error = "没找到这台打印机，点「刷新」再试。";
            return null;
        }
        if (!OpenPrinterW(name, out var hPrinter, IntPtr.Zero))
        {
            error = $"打不开这台打印机的设置（系统返回错误 {Marshal.GetLastWin32Error()}）。";
            return null;
        }
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = DocumentPropertiesW(IntPtr.Zero, hPrinter, name, IntPtr.Zero, IntPtr.Zero, DM_OUT_BUFFER);
            if (size <= 0)
            {
                error = $"读不到「{name}」的当前设置（驱动没响应或这台机器离线）。";
                return null;
            }
            buffer = Marshal.AllocHGlobal(size);
            if (DocumentPropertiesW(IntPtr.Zero, hPrinter, name, buffer, IntPtr.Zero, DM_OUT_BUFFER) <= 0)
            {
                error = $"读不到「{name}」的当前设置（驱动没响应或这台机器离线）。";
                return null;
            }
            return ReadBytes(buffer, size);
        }
        catch (Exception ex)
        {
            error = $"读不到「{name}」的当前设置：{ex.GetType().Name} {ex.Message}";
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            ClosePrinter(hPrinter);
        }
    }

    /// <summary>
    /// 把一份 DEVMODE 写回本用户默认。成功返回 null；失败返回一句能看的原因。
    /// <para>三道核对：长度对得上（驱动版本换了 blob 长度也会变，硬塞不如拒）→ 写 → 回读逐字节比 →
    /// 再问一次驱动，确认它读到的公开字段变了。少一道都可能"写了个寂寞"（§五-161）。</para>
    /// </summary>
    public static string? ApplyDevMode(string? printerName, byte[] devMode, double? widthMm = null, double? heightMm = null)
    {
        var name = ResolveName(printerName);
        if (name is null)
        {
            AppLog.Info("套用默认方案失败：没找到这台打印机");
            return "没找到这台打印机，点「刷新」再试。";
        }
        // 纸规给了宽高就以纸规为准（单位换算在 Core 里：mm → 0.1mm）；没给就沿用方案里存的那份
        var target = widthMm is > 0 && heightMm is > 0
            ? DevModeFacts.WithPaperSize(devMode, widthMm.Value, heightMm.Value)
            : devMode;
        var want = DevModeFacts.Read(target);
        AppLog.Info($"套用默认方案到「{name}」：目标 {want.Describe()}");

        var current = CaptureCurrentDevMode(name, out var readError);
        if (current is null)
        {
            AppLog.Info("套用默认方案失败：读不到当前设置 —— " + readError);
            return readError ?? "读不到这台打印机的当前设置。";
        }
        if (current.Length != target.Length)
        {
            AppLog.Info($"套用默认方案失败：blob 长度不符 {target.Length} vs {current.Length}");
            return $"这份方案与「{name}」现在的驱动不匹配（{target.Length} 字节对 {current.Length} 字节）。"
                 + "多半是驱动装过新版本——请在驱动页里重设一次，再重新存方案。";
        }
        if (!WriteUserDevMode(name, target))
        {
            AppLog.Info("套用默认方案失败：写注册表被拒或回读对不上");
            return "写不进这台打印机在本机的默认设置（注册表被拒或回读核对不上）。";
        }
        var after = CaptureCurrentDevMode(name, out _);
        if (after is null)
        {
            AppLog.Info("套用默认方案：写完了但问不到驱动，无法确认");
            return "已写入，但问不到驱动确认——请打开「打印首选项…」看一眼尺寸对不对。";
        }
        // 只比公开字段：驱动会按自己的算法重写私有块，逐字节比会把成功误报成失败
        if (!DevModeFacts.MatchesWrittenSettings(target, after))
        {
            AppLog.Info($"套用默认方案：驱动读到的与目标不一致 —— 目标 {want.Describe()}，读到 {DevModeFacts.Read(after).Describe()}");
            return "写进去了，但驱动读回来的公开字段对不上（目标 " + want.Describe()
                 + "，驱动读到 " + DevModeFacts.Read(after).Describe() + "）。请把这句发我。";
        }
        AppLog.Info($"已套用默认方案到「{name}」：{want.Describe()}（驱动已读到同一份）");
        return null;
    }

    private static string? ResolveName(string? printerName)
    {
        if (!string.IsNullOrWhiteSpace(printerName)) return printerName;
        LocalPrintServer? server = null;
        try
        {
            server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
            return server.DefaultPrintQueue?.Name;
        }
        catch { return null; }
        finally { server?.Dispose(); }
    }

    private static string Describe(Exception ex) => ex switch
    {
        PrintQueueException _ => "问不到这台打印机（离线、权限或驱动抽风）。先确认它在 Windows 里能打印，再点刷新。",
        InvalidOperationException _ => ex.Message,
        _ => "问不到这台打印机的设置：" + ex.Message,
    };

    private const int DM_OUT_BUFFER = 0x0002;
    private const int DM_IN_PROMPT = 0x0004;

    /// <summary>本用户默认 DEVMODE 的存放处（实测：改它，DocumentProperties 立刻读到新值）。</summary>
    private const string UserDefaultsKey = @"Printers\DevModePerUser";

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentPropertiesW(
        IntPtr hWnd, IntPtr hPrinter, string pDeviceName, IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);
}
