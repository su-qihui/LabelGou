using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using LabelGou.App.Export;
using LabelGou.App.Mvvm;
using LabelGou.App.Printing;
using LabelGou.App.Services;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Printing;

namespace LabelGou.App.ViewModels;

/// <summary>
/// 「⑥ 输出与打印」面板的状态与动作。单独成一个 VM 是为了让 <see cref="MainViewModel"/> 不再长胖
/// （M2 的教训：它已经 600+ 行）。它只在 UI 线程上组装请求，真正的渲染/送打全部交给 STA 后台线程。
/// </summary>
public sealed class ExportViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private CancellationTokenSource? _cts;

    public ExportViewModel(MainViewModel owner)
    {
        _owner = owner;
        _owner.Sheet.PlanChanged += RefreshFromSource;
        foreach (var dpi in new[] { 150, 200, 300, 600 })
        {
            DpiOptions.Add(new ChoiceOption<int>(dpi, dpi == 300 ? $"{dpi} DPI（常规，推荐）" : $"{dpi} DPI"));
        }
        SelectedDpi = DpiOptions.FirstOrDefault(o => o.Value == 300) ?? DpiOptions[0];

        foreach (var format in new[] { PdfImageKind.Jpeg, PdfImageKind.Rgb24 })
        {
            PdfFormatOptions.Add(new ChoiceOption<PdfImageKind>(format,
                format == PdfImageKind.Jpeg ? "JPEG 压缩（文件小，常规够用）" : "无损（字口最硬，文件大）"));
        }
        SelectedPdfFormat = PdfFormatOptions[0];

        foreach (var mode in new[] { SvgExportMode.PerSheet, SvgExportMode.PerLabel })
        {
            SvgModeOptions.Add(new ChoiceOption<SvgExportMode>(mode,
                mode == SvgExportMode.PerSheet ? "整页一图（含角线）" : "一枚一图（对方自己拼版）"));
        }
        SelectedSvgMode = SvgModeOptions[0];

        _outputDirectory = DefaultOutputDirectory();

        RefreshPrintersCommand = new RelayCommand(RefreshPrinters);
        PrintCommand = new RelayCommand(RunPrint, () => CanStartJob);
        ExportPdfCommand = new RelayCommand(RunExportPdf, () => CanStartJob);
        ExportPngCommand = new RelayCommand(RunExportPng, () => CanStartJob);
        ExportTiffCommand = new RelayCommand(RunExportTiff, () => CanStartJob);
        ExportSvgCommand = new RelayCommand(RunExportSvg, () => CanStartJob);
        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        OpenPreferencesCommand = new RelayCommand(OpenPreferences, () => SelectedPrinter is not null);
    }

    /// <summary>窗口加载完后调一次：枚举打印机要问后台服务，放加载阶段做会拖慢首屏。</summary>
    public void WarmUpPrinters() => RefreshPrinters();

    // ---------- 绑定项 ----------

    public ObservableCollection<PrinterInfo> Printers { get; } = new();

    public ObservableCollection<ChoiceOption<int>> DpiOptions { get; } = new();

    public ObservableCollection<ChoiceOption<PdfImageKind>> PdfFormatOptions { get; } = new();

    public ObservableCollection<ChoiceOption<SvgExportMode>> SvgModeOptions { get; } = new();

    public RelayCommand RefreshPrintersCommand { get; }
    public RelayCommand PrintCommand { get; }
    public RelayCommand ExportPdfCommand { get; }
    public RelayCommand ExportPngCommand { get; }
    public RelayCommand ExportTiffCommand { get; }

    public RelayCommand ExportSvgCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>「这台打印机的出纸设置」三格（输出颜色 / 纸张来源 / 纸张类型），内容全取自驱动原话。</summary>
    public ObservableCollection<PrinterSettingRow> PrinterSettingRows { get; } = new();

    /// <summary>三格上方那句说明：读不到时是原因，读到时是「为什么这里只能看不能改」。</summary>
    public string PrinterSettingsNote { get; private set; } = string.Empty;

    public RelayCommand OpenPreferencesCommand { get; }

    public PrinterInfo? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (!Set(ref _selectedPrinter, value)) return;
            Raise(nameof(SelectedPrinterHint));
            ProbeFit();
            RefreshPrinterSettings();
        }
    }
    private PrinterInfo? _selectedPrinter;

    public ChoiceOption<int> SelectedDpi
    {
        get => _selectedDpi;
        set
        {
            if (!Set(ref _selectedDpi, value)) return;
            RefreshEstimate();
        }
    }
    private ChoiceOption<int> _selectedDpi = null!;

    public ChoiceOption<PdfImageKind> SelectedPdfFormat
    {
        get => _selectedPdfFormat;
        set
        {
            if (!Set(ref _selectedPdfFormat, value)) return;
            RefreshEstimate();
        }
    }
    private ChoiceOption<PdfImageKind> _selectedPdfFormat = null!;

    public ChoiceOption<SvgExportMode> SelectedSvgMode
    {
        get => _selectedSvgMode;
        set => Set(ref _selectedSvgMode, value);
    }
    private ChoiceOption<SvgExportMode> _selectedSvgMode = null!;

    /// <summary>默认转曲（定案：所见即所得 + 不怕对方缺中文字体）。取消勾选才保留 <c>&lt;text&gt;</c>。</summary>
    public bool SvgTextAsOutlines
    {
        get => _svgTextAsOutlines;
        set => Set(ref _svgTextAsOutlines, value);
    }
    private bool _svgTextAsOutlines = true;

    public bool SvgEmbedImages
    {
        get => _svgEmbedImages;
        set => Set(ref _svgEmbedImages, value);
    }
    private bool _svgEmbedImages = true;

    public bool SvgIncludeNotes
    {
        get => _svgIncludeNotes;
        set => Set(ref _svgIncludeNotes, value);
    }
    private bool _svgIncludeNotes;

    /// <summary>把面板上的开关汇成一份 Core 侧口径（⑥ 面板、导出命令与单测都走它，避免默认值写两处）。</summary>
    public SvgExportOptions BuildSvgOptions() => new()
    {
        Mode = SelectedSvgMode.Value,
        TextAsOutlines = SvgTextAsOutlines,
        EmbedRasterImages = SvgEmbedImages,
        IncludeNotes = SvgIncludeNotes,
        IncludeCropMarks = IncludeTrimMarks,
        IncludeRegistrationMarks = IncludeTrimMarks,
        Producer = SheetExportService.ProducerName,
        Title = _owner.ProfileName,
    };

    /// <summary>页范围文本，空 = 全部。真源在 Core 的 PageRange，这里只存用户敲的字。</summary>
    public string PageRangeText
    {
        get => _pageRangeText;
        set
        {
            if (!Set(ref _pageRangeText, value)) return;
            RefreshEstimate();
        }
    }
    private string _pageRangeText = string.Empty;

    public int Copies
    {
        get => _copies;
        set => Set(ref _copies, Math.Max(1, Math.Min(value, 99)));
    }
    private int _copies = 1;

    public bool IncludeTrimMarks
    {
        get => _includeTrimMarks;
        set => Set(ref _includeTrimMarks, value);
    }
    private bool _includeTrimMarks = true;

    /// <summary>
    /// <strong>按 CMYK 四版出</strong>（第 48 棒）。只管 PDF 与 TIFF 两条位图出口：
    /// PNG 装不下四版（47 棒实测 WPF 会把它悄悄转成 RGB），打印走驱动自己的色管。
    /// 开着它时 PDF 那颗「JPEG/无损」下拉不再适用，面板上一起灰掉。
    /// </summary>
    public bool CmykPlates
    {
        get => _cmykPlates;
        set
        {
            if (!Set(ref _cmykPlates, value)) return;
            Raise(nameof(PdfFormatApplies));
        }
    }
    private bool _cmykPlates;

    /// <summary>JPEG/无损那一档现在说得上话吗（CMYK 四版一律走无损 Flate，不适用就灰掉）。</summary>
    public bool PdfFormatApplies => !CmykPlates;

    public bool ScaleToFitPrintableArea
    {
        get => _scaleToFit;
        set
        {
            if (!Set(ref _scaleToFit, value)) return;
            ProbeFit();
        }
    }
    private bool _scaleToFit;

    public bool ConfirmBeforePrint
    {
        get => _confirmBeforePrint;
        set => Set(ref _confirmBeforePrint, value);
    }
    private bool _confirmBeforePrint;

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => Set(ref _outputDirectory, value);
    }
    private string _outputDirectory;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanStartJob));
        }
    }
    private bool _isBusy;

    public bool CanStartJob => !IsBusy && _owner.Sheet.HasPlan;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }
    private string _statusText = "准备好后即可打印或导出。";

    public string SelectionText
    {
        get => _selectionText;
        private set => Set(ref _selectionText, value);
    }
    private string _selectionText = string.Empty;

    public string FitText
    {
        get => _fitText;
        private set => Set(ref _fitText, value);
    }
    private string _fitText = "换打印机后会重新检查整版能否原大打进可打印区。";

    public string SelectedPrinterHint => SelectedPrinter is null
        ? "未选打印机"
        : $"{SelectedPrinter.Name}（队列中 {SelectedPrinter.PendingJobs} 个任务）";

    // ---------- 与主 VM 的接缝 ----------

    /// <summary>拼版结果或数据变了之后由主 VM 调用：重算选择与体积估算。</summary>
    public void RefreshFromSource()
    {
        RefreshEstimate();
        Raise(nameof(CanStartJob));
    }

    private void RefreshEstimate()
    {
        var plan = _owner.Sheet.Plan;
        if (plan is null || plan.PageCount == 0)
        {
            SelectionText = "尚无整版方案，先完成拼版。";
            return;
        }

        if (!PageRange.TryParse(PageRangeText, plan.PageCount, out var range, out var error))
        {
            SelectionText = error!;
            return;
        }

        var request = BuildRequest(range!, out _, runReviewGate: false);
        var estimate = request is null ? string.Empty : " · 预计 " + SheetExportService.EstimatePdfSize(request);
        SelectionText = string.Format(CultureInfo.InvariantCulture,
            "共 {0} 页，本次选 {1} 页 · {2}DPI · 页面 {3}×{4}mm{5}",
            plan.PageCount, range!.Count, SelectedDpi.Value,
            plan.PageWidthMm.ToString("0.#", CultureInfo.InvariantCulture),
            plan.PageHeightMm.ToString("0.#", CultureInfo.InvariantCulture),
            request is null ? string.Empty : estimate);
    }

    private void ProbeFit()
    {
        var plan = _owner.Sheet.Plan;
        var source = _owner.CreatePageSource();
        if (plan is null || plan.PageCount == 0 || source is null) return;

        // 屏幕上的体检不能阻塞输入，直接问一次驱动（读属性很快，不送任务）
        try
        {
            var request = new PrintRequest
            {
                Plan = plan,
                Source = source,
                PageIndexes = new[] { 0 },
                PrinterName = SelectedPrinter?.Name,
            };
            var advice = PrintService.ProbeFit(request, out var probeError);
            FitText = probeError ?? advice.Describe($"整版 {plan.PageWidthMm:0.#}×{plan.PageHeightMm:0.#}mm");
            AppLog.Info($"打印落位体检（{SelectedPrinter?.Name ?? "默认打印机"}）：{FitText}");
        }
        catch (Exception ex)
        {
            FitText = "这台打印机暂时问不到：" + ex.Message;
        }
    }

    // ---------- 动作 ----------

    private void RefreshPrinters()
    {
        var previous = SelectedPrinter?.Name;
        var list = PrintService.ListPrinters(out var error);
        Printers.Clear();
        foreach (var printer in list) Printers.Add(printer);
        SelectedPrinter = Printers.FirstOrDefault(p => p.Name == previous)
                          ?? Printers.FirstOrDefault(p => p.IsDefault)
                          ?? Printers.FirstOrDefault();
        StatusText = error ?? (list.Count == 0
            ? "没找到任何打印机。先在 Windows 设置里添加打印机，再点右侧的刷新。"
            : $"读到 {list.Count} 台打印机。");
        AppLog.Info($"打印机列表刷新：{list.Count} 台，选中「{SelectedPrinter?.Name ?? "无"}」" +
                    (error is null ? string.Empty : $"，原因：{error}"));
        ProbeFit();
        RefreshPrinterSettings();
    }

    /// <summary>
    /// 重读「这台打印机的出纸设置」三格。同步问驱动一次（与 <see cref="ProbeFit"/> 同一口径：只读属性、不送任务）。
    /// </summary>
    private void RefreshPrinterSettings()
    {
        var report = PrinterSettingsReader.Read(SelectedPrinter?.Name);
        PrinterSettingRows.Clear();
        foreach (var row in report.Rows) PrinterSettingRows.Add(row);
        PrinterSettingsNote = report.Error ?? "这三项由打印机驱动管：能读回来的读给你看；读不回来的（如纸张来源——驱动把它存在自己的私有设置块里，"
            + "公开字段不动）就进「打印首选项…」看或改。在那里改的是这台打印机在这台电脑上的默认设置，别的软件也共用。";
        Raise(nameof(PrinterSettingsNote));
    }

    /// <summary>弹驱动自己的首选项页；按了确定就把改动写回本用户默认，并把差异报给用户看。</summary>
    private void OpenPreferences()
    {
        var owner = new System.Windows.Interop.WindowInteropHelper(
            System.Windows.Application.Current.MainWindow).Handle;
        var diff = PrinterSettingsReader.OpenDriverPreferences(SelectedPrinter?.Name, owner, out var error);
        AppLog.Info($"弹驱动首选项页（{SelectedPrinter?.Name ?? "默认打印机"}）："
            + (error is not null ? "失败：" + error : diff is null ? "用户按了取消" : "用户按了确定"));
        if (error is not null)
        {
            StatusText = error;
            return;
        }
        if (diff is null) return;
        RefreshPrinterSettings();
        StatusText = "驱动页按了确定，这次改动：" + diff
            + "。已写回这台打印机在本机的默认设置——请打一张看走没走你选的盘；"
            + "没走就把上面这句原样发我（它告诉我们那一项到底存在哪几个字节）。";
    }

    private void ChooseFolder()
    {
        // 不拉 Windows Forms（那是另一个依赖包），用 WPF 自带的对话框足以选目录。
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择输出目录（随便选一个文件所在处即可，我们只用它的目录）",
            CheckFileExists = false,
            FileName = "选择此目录",
            Filter = "占位|*.placeholder",
        };
        if (Directory.Exists(OutputDirectory)) dialog.InitialDirectory = OutputDirectory;
        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = Path.GetDirectoryName(dialog.FileName) ?? OutputDirectory;
            StatusText = "输出目录已设为 " + OutputDirectory;
        }
    }

    /// <param name="runReviewGate">复核闸门只属于真正要出纸/出文件的那一次。估算那一路（页范围框每个键击
    /// 都会走这里）曾经也弹模态确认框——只要批次里有待核对字段，用户打个数字就弹一次，安全闸被训练成
    /// 「见框就点 Yes」（第 23 棒）。</param>
    private SheetExportRequest? BuildRequest(PageRange range, out string? error, bool runReviewGate = true)
    {
        error = null;
        var plan = _owner.Sheet.Plan;
        var source = _owner.CreatePageSource();
        if (plan is null || plan.PageCount == 0 || source is null)
        {
            error = "还没有可输出的整版：请先导入数据并应用映射。";
            return null;
        }
        if (runReviewGate && !PassesReviewGate(source))
        {
            error = "已按出口闸门停下：未确认的字段或未处理的纸规错误还在，不进入打印与导出。";
            return null;
        }

        return new SheetExportRequest
        {
            Plan = plan,
            Source = source,
            PageIndexes = range.SelectedIndexes(),
            BaseName = SuggestBaseName(plan),
            Dpi = SelectedDpi.Value,
            IncludeTrimMarks = IncludeTrimMarks,
            RasterKind = SelectedPdfFormat.Value,
            CmykPlates = CmykPlates,
        };
    }

    private string SuggestBaseName(SheetPlan plan)
    {
        var profile = _owner.ProfileName;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var head = string.IsNullOrWhiteSpace(profile) ? "唛头整版" : profile;
        return SheetExportService.SafeFileName($"{head}-{plan.LabelCount}枚-{stamp}");
    }

    private void RunExportPdf()
    {
        if (!TryResolveRange(out var range, out var error)) { StatusText = error!; return; }
        var request = BuildRequest(range!, out error);
        if (request is null) { StatusText = error!; return; }

        var target = AskSaveFile(request.BaseName + ".pdf", "PDF 文件|*.pdf");
        if (target is null) { StatusText = "已取消导出（未选保存位置）。"; return; }

        RunJob($"PDF 导出→{Path.GetFileName(target)}", (progress, token) =>
            SheetExportService.ExportPdf(request, target, progress, token));
    }

    private void RunExportPng()
    {
        if (!TryResolveRange(out var range, out var error)) { StatusText = error!; return; }
        var request = BuildRequest(range!, out error);
        if (request is null) { StatusText = error!; return; }

        var folder = Path.Combine(OutputDirectory, SheetExportService.SafeFileName(request.BaseName));
        RunJob($"PNG 导出→{folder}", (progress, token) =>
            SheetExportService.ExportPngPages(request, folder, progress, token));
    }

    private void RunExportTiff()
    {
        if (!TryResolveRange(out var range, out var error)) { StatusText = error!; return; }
        var request = BuildRequest(range!, out error);
        if (request is null) { StatusText = error!; return; }

        var target = AskSaveFile(request.BaseName + ".tif", "TIFF 图像|*.tif;*.tiff");
        if (target is null) { StatusText = "已取消导出（未选保存位置）。"; return; }

        RunJob($"TIFF 导出→{Path.GetFileName(target)}", (progress, token) =>
            SheetExportService.ExportTiff(request, target, progress, token));
    }

    private void RunExportSvg()
    {
        if (!TryResolveRange(out var range, out var error)) { StatusText = error!; return; }
        var request = BuildRequest(range!, out error);
        if (request is null) { StatusText = error!; return; }

        var options = BuildSvgOptions();
        var folder = Path.Combine(OutputDirectory, SheetExportService.SafeFileName(request.BaseName) + "-SVG");
        RunJob($"SVG 导出→{folder}", (progress, token) =>
            SheetExportService.ExportSvg(request, folder, options, progress, token));
    }

    private void RunPrint()
    {
        if (!TryResolveRange(out var range, out var error)) { StatusText = error!; return; }
        var plan = _owner.Sheet.Plan;
        var source = _owner.CreatePageSource();
        if (plan is null || source is null)
        {
            StatusText = "还没有可打印的整版：请先导入数据并应用映射。";
            return;
        }
        if (!PassesReviewGate(source))
        {
            StatusText = "打印已按出口闸门停下（未确认的字段或未处理的纸规错误不能上机）。";
            return;
        }

        // 整版超出可打印区：先问一句再打（第 75 棒）。真因常常是驱动里的纸张尺寸还停在 A4，
        // 而软件改不动它——以前这里直接抛异常，人只看到"换纸或减小页边"，以为是我们把版算错了。
        var printAnyway = false;
        if (!ScaleToFitPrintableArea)
        {
            var probe = new PrintRequest
            {
                Plan = plan, Source = source, PageIndexes = new[] { 0 }, PrinterName = SelectedPrinter?.Name,
            };
            var fit = PrintService.ProbeFit(probe, out var probeError);
            var ask = probeError is null
                ? fit.OverflowConfirmText(plan.PageWidthMm, plan.PageHeightMm, ScaleToFitPrintableArea)
                : null;
            if (ask is not null)
            {
                if (!(_owner.ConfirmGate?.Invoke(ask) ?? true))
                {
                    AppLog.Info($"打印前停下：整版 {plan.PageWidthMm:0.#}×{plan.PageHeightMm:0.#}mm 比可打印区大 "
                                + $"{fit.OverflowWidthMm:0.#}×{fit.OverflowHeightMm:0.#}mm，用户选了「否」");
                    StatusText = "没送出。先去改驱动里的纸张尺寸：⑤ 步「打印首选项…」→ 纸张尺寸改成 "
                                 + $"{plan.PageWidthMm:0.#}×{plan.PageHeightMm:0.#}mm（或你那张标签纸），再打。";
                    return;
                }
                printAnyway = true;
            }
        }

        var request = new PrintRequest
        {
            Plan = plan,
            Source = source,
            PageIndexes = range!.SelectedIndexes(),
            PrinterName = SelectedPrinter?.Name,
            Copies = Copies,
            IncludeTrimMarks = IncludeTrimMarks,
            ScaleToFitPrintableArea = ScaleToFitPrintableArea,
            ConfirmBeforePrint = ConfirmBeforePrint,
            PrintAnywayAtOneToOne = printAnyway,
        };

        if (SelectedPrinter is { LikelyPromptsForFile: true } && !ConfirmBeforePrint)
        {
            StatusText = $"「{SelectedPrinter.Name}」是虚拟打印机，很可能弹出保存框挡住批量任务。" +
                         "换成实体打印机，或勾选「打印前打开驱动窗口」手动确认。";
            return;
        }

        RunJob("打印", (progress, token) => PrintService.Print(request, progress, token));
    }

    /// <summary>
    /// §七-11 的硬规矩：带 `NeedsReview` 的字段不得默认上机。数一下有几张，有就请用户点头。
    /// 询问器由 MainWindow 挂上（没挂时视为不阻塞，单测环境就靠这一点）。
    /// <para>纸规本身的 Error 也走这道门：以前 <c>HasError()</c> 没一处消费 <c>plan.Issues</c>，
    /// 校验器说「这张纸不行」而五个出口一个都不拦，只看 <c>PerPage</c>。</para>
    /// </summary>
    private bool PassesReviewGate(PageContentSource source)
    {
        var inkOverflow = source.InkOverflowLabelCount;
        var text = ComposeGateMessage(source.LabelCount, source.UnconfirmedLabelCount,
            _owner.Sheet.Plan?.ErrorCount ?? 0, _owner.TemplateCautions,
            inkOverflow, source.InkScanTruncated ? PageContentSource.InkScanCap : 0);
        if (text is null) return true;

        var accepted = _owner.ConfirmGate?.Invoke(text) ?? true;
        if (!accepted)
        {
            AppLog.Info($"出口闸门拦下任务：{source.UnconfirmedLabelCount}/{source.LabelCount} 张待核对，" +
                        $"纸规错误 {_owner.Sheet.Plan?.ErrorCount ?? 0} 条，墨迹出纸 {inkOverflow} 张");
        }
        return accepted;
    }

    /// <summary>
    /// 闸门要问的那句话；返回 null 表示没东西要拦，直接放行。
    /// <para>单独抽成一个静态函数：「纸规错误也进闸门」这件事否则只能靠真开一个打印任务才能验，
    /// 而本机没实体打印机（§五-70）。</para>
    /// <para>第 24 棒（活账 A-1 的最小改法）：模板写死文字与这批货对不上号的告警也进这道门——
    /// 纯告警也能触发一次确认（只拦这一次，不自动改；与 §五-123 不冲突：它仍只在真出纸/出文件那一路跑）。</para>
    /// </summary>
    public static string? ComposeGateMessage(int labelCount, int flagged, int sheetErrors,
        IReadOnlyList<string>? templateCautions = null, int inkOverflow = 0, int inkScannedOf = 0)
    {
        var cautions = templateCautions ?? Array.Empty<string>();
        if (flagged <= 0 && sheetErrors <= 0 && cautions.Count == 0 && inkOverflow <= 0) return null;

        var lines = new List<string>();
        if (flagged > 0)
            lines.Add($"这批共 {labelCount} 张标签里，有 {flagged} 张含「需人工核对」的字段（红色标记）。");
        if (sheetErrors > 0)
            lines.Add($"整版方案上有 {sheetErrors} 条标成错误的纸规问题（列在第 ④ 步的提示里），件上可能缺线、缺角线或裁错位置。");
        if (inkOverflow > 0)
        {
            // 第 46 棒：文本改成"永不折行"之后，长值会照实排到纸外——行带那份保护由这道闸接手。
            var scanned = inkScannedOf > 0 && inkScannedOf < labelCount
                ? $"（只抽查了前 {inkScannedOf} 张，这批共 {labelCount} 张）"
                : string.Empty;
            lines.Add($"有 {inkOverflow} 张标签的文字排到了纸边外，会被刀模裁掉{scanned}。" +
                      "回第 ③ 步点「缩回纸内」，或在模板里给那一行填「折行宽度(mm)」让它折行。");
        }
        if (cautions.Count > 0)
        {
            lines.Add($"模板里有 {cautions.Count} 处写死的文字与这批货对不上号：");
            foreach (var c in cautions) lines.Add("・" + c);
        }
        lines.Add(string.Empty);
        lines.Add("唛头数字印错就是真实货损。确认这些已经人工过目了吗？");
        return string.Join("\n", lines);
    }

    private bool TryResolveRange(out PageRange? range, out string? error)
    {
        var plan = _owner.Sheet.Plan;
        range = null;
        if (plan is null || plan.PageCount == 0)
        {
            error = "尚无整版方案，无法确定页范围。";
            return false;
        }
        return PageRange.TryParse(PageRangeText, plan.PageCount, out range, out error);
    }

    private void RunJob(string jobName, Func<IProgress<string>, CancellationToken, object> job)
    {
        if (IsBusy)
        {
            // 第 23 棒：AI 面板「按这版去打印」曾绕过 CanExecute 进来，第二个 STA 任务与第一个并发出纸，
            // 收尾时还会把对方的取消源 Dispose 掉（「取消」从此失效）。命令按钮有 CanExecute 挡，这里再关一道门。
            StatusText = "已有一个输出任务在跑（看状态栏进度）；等它结束或取消，再发下一个。";
            AppLog.Info($"任务「{jobName}」被拒：已有任务在跑");
            return;
        }
        _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = jobName + " 开始…";
        var started = Stopwatch.StartNew();
        var task = StaWorker.RunAsync(job, text => StatusText = $"{jobName}：{text}", _cts.Token);
        task.ContinueWith(t =>
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            if (t.IsCanceled)
            {
                // 取消不是失败：不写 ERROR 日志，也不把「A task was canceled.」当错误弹给用户
                StatusText = jobName + " 已取消（半途而废的不算产出）。";
                AppLog.Info($"{jobName} 被用户取消");
                return;
            }
            if (t.Exception is { } aggregate)
            {
                var ex = aggregate.GetBaseException();
                StatusText = jobName + " 失败：" + ex.Message;
                AppLog.Error($"{jobName} 任务异常", ex);
                return;
            }
            Report(t.Result, started.Elapsed);
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Report(object outcome, TimeSpan elapsed)
    {
        var text = outcome switch
        {
            ExportOutcome export => export.Success
                ? $"完成：{export.Summary}（耗时 {elapsed.TotalSeconds:0.#} 秒）"
                : "失败：" + export.Error,
            PrintOutcome print => print.Success
                ? $"完成：{print.Summary}（耗时 {elapsed.TotalSeconds:0.#} 秒）"
                : "失败：" + print.Error,
            _ => $"任务结束（耗时 {elapsed.TotalSeconds:0.#} 秒）",
        };
        StatusText = text;
        if (outcome is PrintOutcome { Fit: not null } p) FitText = p.Fit.Describe("整版");
        ProbeFit();
    }

    private string? AskSaveFile(string suggestedName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存导出文件",
            Filter = filter,
            FileName = suggestedName,
            AddExtension = true,
        };
        if (Directory.Exists(OutputDirectory)) dialog.InitialDirectory = OutputDirectory;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* 任务刚好结束，忽略 */ }
    }

    private static string DefaultOutputDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents)) documents = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(documents, "LabelGou输出");
    }
}
