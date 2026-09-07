using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows.Controls;
using LabelGou.App.Export;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Units;

namespace LabelGou.App.Printing;

/// <summary>打印机候选项（给 UI 下拉用）。Status 为空表示没读到异常状态。</summary>
public sealed record PrinterInfo(
    string Name,
    bool IsDefault,
    bool LikelyPromptsForFile,
    string? Status,
    int PendingJobs)
{
    public string DisplayName => LikelyPromptsForFile
        ? Name + "（虚拟打印机会弹保存框，不适合批量）"
        : IsDefault ? Name + "（默认）" : Name;
}

/// <summary>一次打印任务的全部意图。份数用"逐份重复出纸"实现，不依赖驱动支持，行为可预期。</summary>
public sealed class PrintRequest
{
    public required SheetPlan Plan { get; init; }
    public required PageContentSource Source { get; init; }

    /// <summary>要打的页（0 起始）。</summary>
    public required IReadOnlyList<int> PageIndexes { get; init; }

    public string? PrinterName { get; init; }
    public int Copies { get; init; } = 1;
    public bool IncludeTrimMarks { get; init; } = true;

    /// <summary>可打印区装不下时，是否允许自动缩小到刚好放下（默认不允许：尺寸不准的唛头比打不出来更糟）。</summary>
    public bool ScaleToFitPrintableArea { get; init; }

    /// <summary>送打前先弹系统打印对话框（选纸盒/质量靠它）。弹了就不算静默。</summary>
    public bool ConfirmBeforePrint { get; init; }

    public void CollectIssues(IList<string> issues)
    {
        if (PageIndexes is null || PageIndexes.Count == 0) issues.Add("没有选中任何一页可打印。");
        if (Copies < 1 || Copies > 99) issues.Add($"份数 {Copies} 不合理（1~99）。");
        if (Plan.PerPage <= 0) issues.Add("纸规放不下任何一枚标签，请先调整拼版设置。");
        // 页号越界：导出端一直有这一项，打印端上一版漏了（选错页就静默少打）
        Plan.CollectPageRangeIssues(PageIndexes, issues);
    }
}

public sealed record PrintOutcome(
    bool Success,
    string? Error,
    int SheetsSent,
    PrintFitAdvice? Fit,
    string? Summary)
{
    public static PrintOutcome Fail(string error, PrintFitAdvice? fit = null)
        => new(false, error, 0, fit, null);
}

/// <summary>
/// 直连 Windows 打印机出片。
/// <para>
/// 全程用 WPF 的 <see cref="PrintDialog"/> + <c>PrintVisual</c>：视觉对象是真实尺寸（毫米→DIU），
/// 驱动负责点阵化，因此我们不做第二套 DPI 换算，也不会出现"预览与纸上位置不一致"。
/// 不调 ShowDialog()，改为直接给 <see cref="PrintDialog.PrintQueue"/>，这就是"静默批量"的实现方式。
/// </para>
/// </summary>
public static class PrintService
{
    /// <summary>这些名字基本都对应有"另存为"弹窗的虚拟端口，静默打会卡在那儿等人点。</summary>
    private static readonly string[] VirtualPrinterHints =
    {
        "print to pdf", "microsoft pdf", "xps", "onenote", "kingsoft", "wps", "foxit", "adobe pdf", "virtual", "传真", "虚拟",
    };

    public const int MaxPagesPerJob = 500;

    public static IReadOnlyList<PrinterInfo> ListPrinters(out string? error)
    {
        error = null;
        var result = new List<PrinterInfo>();
        try
        {
            using var server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
            string? defaultName = null;
            try { defaultName = server.DefaultPrintQueue?.FullName; } catch { /* 没设默认打印机时这里会抛，不影响列表 */ }

            foreach (var queue in server.GetPrintQueues(new[]
                     { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }))
            {
                try
                {
                    queue.Refresh();
                    string? portName = null;
                    try { portName = queue.QueuePort?.Name; } catch { /* 端口读不到不算错，降级按名字判 */ }
                    result.Add(new PrinterInfo(
                        queue.Name,
                        string.Equals(queue.FullName, defaultName, StringComparison.OrdinalIgnoreCase),
                        LooksLikeFilePrinter(queue.Name, portName),
                        queue.IsInError ? DescribeStatus(queue) : null,
                        queue.NumberOfJobs));
                }
                catch
                {
                    // 单个队列读不动（离线/权限）不影响其他打印机
                    result.Add(new PrinterInfo(queue.Name, false, LooksLikeFilePrinter(queue.Name, null), "读不到状态", 0));
                }
            }
        }
        catch (Exception ex)
        {
            error = $"读不到打印机列表（打印后台服务 Spooler 可能没开）：{ex.Message}";
        }
        result.Sort((a, b) => (b.IsDefault, a.Name).CompareTo((a.IsDefault, b.Name)));
        return result;
    }

    /// <summary>
    /// 先看端口（PORTPROMPT: 就是“每打一次问一次存哪儿”），再按名字兜底判断。
    /// 这类驱动静默批量会被弹窗堵死，必须提前告知用户。
    /// </summary>
    public static bool LooksLikeFilePrinter(string name, string? port)
    {
        if (!string.IsNullOrEmpty(port) &&
            (port!.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase)
             || port.Contains("FILE:", StringComparison.OrdinalIgnoreCase)
             || port.Contains("SHARED", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        var lower = name.ToLowerInvariant();
        foreach (var hint in VirtualPrinterHints)
        {
            if (lower.Contains(hint, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>先问一句"这版能不能原大打进这台机器"，让用户在按打印之前就看到结论。</summary>
    public static PrintFitAdvice ProbeFit(PrintRequest request, out string? error)
    {
        error = null;
        try
        {
            using var scope = new PrintSession(request.PrinterName);
            return scope.EvaluateFit(request.Plan);
        }
        catch (Exception ex)
        {
            error = DescribePrintException(ex);
            return PrintFit.Evaluate(request.Plan.PageWidthMm, request.Plan.PageHeightMm, 0, 0);
        }
    }

    public static PrintOutcome Print(PrintRequest request, IProgress<string>? progress, CancellationToken token)
    {
        var issues = new List<string>();
        request.CollectIssues(issues);
        if (issues.Count > 0) return PrintOutcome.Fail(string.Join(" ", issues));
        if (request.PageIndexes.Count > MaxPagesPerJob)
        {
            return PrintOutcome.Fail($"一次最多送 {MaxPagesPerJob} 页（当前 {request.PageIndexes.Count} 页），请分批打印。");
        }

        try
        {
            using var scope = new PrintSession(request.PrinterName);
            if (request.ConfirmBeforePrint && !scope.ShowDriverDialog())
            {
                return new PrintOutcome(false, null, 0, null, "打印对话框里按了取消，未送出任何页。");
            }
            var fit = scope.EvaluateFit(request.Plan);
            var sheets = scope.PrintSheets(request, fit, progress, token);
            var summary = string.Format(CultureInfo.InvariantCulture,
                "已向「{0}」送出 {1} 张（{2} 页 × {3} 份）", scope.PrinterName, sheets, request.PageIndexes.Count, request.Copies);
            return new PrintOutcome(true, null, sheets, fit, summary + "。" + fit.Describe("整版"));
        }
        catch (OperationCanceledException)
        {
            return PrintOutcome.Fail("打印已取消（半途取消可能已打出部分页，请检查出纸）。");
        }
        catch (Exception ex)
        {
            return PrintOutcome.Fail(DescribePrintException(ex));
        }
    }

    private static string? DescribeStatus(PrintQueue queue)
    {
        // 不去枚举 PrintQueueStatus 那几十个位，直接拿驱动给的标志集文本（缺纸/卡纸/暂停都会落在里面）
        try
        {
            var status = queue.QueueStatus;
            return status == PrintQueueStatus.None ? "队列报告异常" : status.ToString();
        }
        catch
        {
            return "状态未知";
        }
    }

    private static string DescribePrintException(Exception ex)
    {
        var text = ex.Message ?? string.Empty;
        return ex switch
        {
            PrintQueueException _ when text.Contains("InvalidHandle", StringComparison.OrdinalIgnoreCase)
                => "系统里没有可用的打印机（或当前账户读不到）。请先在 Windows 设置里装好打印机再打。",
            PrintQueueException _ when text.Contains("拒绝访问", StringComparison.Ordinal)
                                  || text.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
                => "当前账户没有权限访问打印服务（可能被组策略限制）。改用导出 PDF 再拖到打印机上，或换一台机器。",
            PrintQueueException _ => $"打印机返回错误：{text}",
            UnauthorizedAccessException _ => $"没有权限操作打印机：{text}",
            IOException _ => $"送打过程中写不进假脱机文件：{text}",
            _ => $"打印失败：{text}",
        };
    }

    /// <summary>
    /// 一次打印会话：持有 PrintQueue + PrintDialog，用完释放。
    /// 单独成类是因为这三样都是 STA/线程亲和对象，必须在同一个线程里创建和使用。
    /// </summary>
    private sealed class PrintSession : IDisposable
    {
        private readonly LocalPrintServer? _server;
        private readonly PrintDialog _dialog;

        public PrintSession(string? printerName)
        {
            _dialog = new PrintDialog();
            if (string.IsNullOrWhiteSpace(printerName))
            {
                try { PrinterName = _dialog.PrintQueue?.Name ?? "系统默认打印机"; }
                catch { PrinterName = "系统默认打印机（读不到队列信息）"; }
                return;
            }

            // 服务器级对象只能给 EnumerateServer（实测用 UsePrinter 会被假脱机直接拒："拒绝访问"）
            _server = new LocalPrintServer(PrintSystemDesiredAccess.EnumerateServer);
            try
            {
                var queue = new PrintQueue(_server, printerName);
                queue.Refresh();
                PrinterName = queue.Name;
                _dialog.PrintQueue = queue;
                // 故意不改 PrintTicket：纸张/质量/纸盒一律用用户自己在驱动里设好的默认值，
                // 我们去回写反而会把人家机器的全局默认搞乱；尺寸对不上由下面的 PrintFit 提醒。
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"找不到打印机「{printerName}」：{ex.Message}", ex);
            }
        }

        public string PrinterName { get; }

        /// <summary>弹系统打印对话框；返回 false 表示用户按了取消。</summary>
        public bool ShowDriverDialog() => _dialog.ShowDialog() == true;

        /// <summary>驱动给的可打印区（DIU）→ 毫米，交给 Core 判档。</summary>
        public PrintFitAdvice EvaluateFit(SheetPlan plan)
        {
            double printableW, printableH;
            try
            {
                printableW = _dialog.PrintableAreaWidth;
                printableH = _dialog.PrintableAreaHeight;
            }
            catch
            {
                // 脱机或驱动抽风时读不到可视区，归到"报不出来"那档（PrintFit 会给提示而不是抛异常）
                return PrintFit.Evaluate(plan.PageWidthMm, plan.PageHeightMm, 0, 0);
            }
            if (!double.IsFinite(printableW) || !double.IsFinite(printableH) || printableW <= 0 || printableH <= 0)
            {
                return PrintFit.Evaluate(plan.PageWidthMm, plan.PageHeightMm, 0, 0);
            }
            return PrintFit.Evaluate(plan.PageWidthMm, plan.PageHeightMm, Mm.FromDiu(printableW), Mm.FromDiu(printableH));
        }

        public int PrintSheets(PrintRequest request, PrintFitAdvice fit, IProgress<string>? progress, CancellationToken token)
        {
            var scale = request.ScaleToFitPrintableArea && fit.Level is PrintFitLevel.NeedsShrink or PrintFitLevel.Rejected
                ? fit.SuggestedScale
                : 1.0;
            if (scale == 1.0 && fit.Level is PrintFitLevel.NeedsShrink or PrintFitLevel.Rejected)
            {
                throw new InvalidOperationException(
                    fit.Describe("整版") + " 想强行打出去，请勾选「允许缩放以放下」，或换纸/减小页边后重算。");
            }

            var total = request.PageIndexes.Count * request.Copies;
            var done = 0;
            for (var copy = 1; copy <= request.Copies; copy++)
            {
                foreach (var index in request.PageIndexes)
                {
                    token.ThrowIfCancellationRequested();
                    var visual = PageRasterizer.BuildPrintVisual(
                        request.Plan, index + 1, request.Source.AsProvider(), request.IncludeTrimMarks, scale);
                    var label = request.Copies > 1
                        ? $"LabelGou 整版 第 {index + 1} 页（第 {copy}/{request.Copies} 份）"
                        : $"LabelGou 整版 第 {index + 1} 页";
                    _dialog.PrintVisual(visual, label);
                    done++;
                    progress?.Report($"已送打 {done}/{total} 页…");
                }
            }
            return done;
        }

        public void Dispose()
        {
            (_dialog.PrintQueue as IDisposable)?.Dispose();
            _server?.Dispose();
        }
    }
}
