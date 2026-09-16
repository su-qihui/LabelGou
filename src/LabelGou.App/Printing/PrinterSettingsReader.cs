using System.Printing;
using System.Runtime.InteropServices;
using System.Text;
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
    /// 返回 true 表示用户按了确定；调用方应当随后再 <see cref="Read"/> 一次刷新显示。
    /// </summary>
    public static bool OpenDriverPreferences(string? printerName, IntPtr owner)
    {
        var name = ResolveName(printerName);
        if (name is null) return false;
        IntPtr hPrinter = IntPtr.Zero;
        try
        {
            // 实测：带 PRINTER_DEFAULTSW(0x8=PRINTER_ACCESS_USE) 一律回拒绝访问，传 NULL 才打得开
            if (!OpenPrinterW(name, out hPrinter, IntPtr.Zero))
            {
                AppLog.Info($"打开打印机句柄失败（{name}）：{Marshal.GetLastWin32Error()}");
                return false;
            }
            int size = DocumentPropertiesW(owner, hPrinter, name, IntPtr.Zero, IntPtr.Zero, DM_OUT_BUFFER);
            if (size <= 0) return false;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (DocumentPropertiesW(owner, hPrinter, name, buffer, IntPtr.Zero, DM_OUT_BUFFER) <= 0)
                    return false;
                // DM_IN_PROMPT：驱动弹它自己那一页；确定返回 >0，取消返回 <=0
                return DocumentPropertiesW(owner, hPrinter, name, buffer, buffer, DM_IN_PROMPT | DM_OUT_BUFFER) > 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex)
        {
            AppLog.Info($"弹驱动首选项失败（{name}）：{Describe(ex)}");
            return false;
        }
        finally
        {
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }
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

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentPropertiesW(
        IntPtr hWnd, IntPtr hPrinter, string pDeviceName, IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);
}
