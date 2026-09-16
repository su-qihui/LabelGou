using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.App.Mvvm;
using LabelGou.App.Services;
using LabelGou.App.Services.Recognition;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;

namespace LabelGou.App.ViewModels;

/// <summary>
/// 一份文档在核对窗口左侧列表里的一项。
/// <para>计数每次都要重算，所以字段行改动时会回调 <see cref="Invalidate"/>。</para>
/// </summary>
public sealed class BatchItem : ObservableObject
{
    private bool _imported;

    public BatchItem(RecognizedBatch batch) => Batch = batch;

    public RecognizedBatch Batch { get; }

    public string SourceName => Batch.SourceName;

    public string PendingText => Batch.PendingCount == 0
        ? "全部已核对"
        : $"待核 {Batch.PendingCount} 项";

    public string HotText => Batch.HotCount == 0 ? string.Empty : $"机器没把握 {Batch.HotCount} 项";

    /// <summary>已经导入过的不允许再点第二次，否则会灌进重复记录。</summary>
    public bool Imported
    {
        get => _imported;
        set
        {
            if (Set(ref _imported, value)) Invalidate();
        }
    }

    public bool CanImport => Batch.ReadyToImport && !_imported;

    public string ChannelText => Batch.TextChannel switch
    {
        TextChannel.DocxText => "Word 正文",
        TextChannel.Ocr => "图片 OCR",
        _ => "文本层",
    };

    /// <summary>列表项提示：为什么还不能导入。</summary>
    public string BlockedReason => Batch.Fields.Count == 0
        ? "一个字段都没识别出来"
        : Batch.ReadyToImport ? "可以导入" : $"还有 {Batch.PendingCount} 项没人工确认";

    public void Invalidate()
    {
        Raise(nameof(PendingText));
        Raise(nameof(HotText));
        Raise(nameof(CanImport));
        Raise(nameof(BlockedReason));
    }
}

/// <summary>
/// 核对窗口中间表格里的一行 —— <see cref="ReviewedField"/> 的界面外壳。
/// <para>包装而不直接绑定的原因：<see cref="ReviewedField.Value"/> 的 setter 会把"已确认"打回未确认，
/// 这个连带变化必须让上层（计数、按钮可用性）知道，包装层才能同时通知两边。</para>
/// </summary>
public sealed class ReviewFieldRow : ObservableObject
{
    private readonly Action _changed;

    public ReviewFieldRow(ReviewedField field, Action changed)
    {
        Field = field;
        _changed = changed;
    }

    public ReviewedField Field { get; }

    public MarkFieldKey Key => Field.Field;

    public string ChineseName => Field.ChineseName;

    public string EnglishLabel => Field.EnglishLabel;

    /// <summary>印出去的值。改它会立刻把本行退回待核（Core 里定义的规矩，这里只转发）。</summary>
    public string? Value
    {
        get => Field.Value;
        set
        {
            if (string.Equals(Field.Value, value, StringComparison.Ordinal)) return;
            Field.Value = value;
            Raise(nameof(Value));
            Raise(nameof(IsPending));
            _changed();
        }
    }

    public bool Confirmed
    {
        get => Field.Confirmed;
        set
        {
            if (Field.Confirmed == value) return;
            Field.Confirmed = value;
            Raise(nameof(Confirmed));
            Raise(nameof(IsPending));
            _changed();
        }
    }

    public bool IsPending => !Field.Confirmed;

    /// <summary>两通道各读到什么，并排列出来给人比。</summary>
    public string? TextChannelValue => Field.TextValue;

    public string? ModelValue => Field.LlmValue;

    /// <summary>
    /// 「一致」只能给两路都开了口且值相同的行。
    /// <para>只有一路说话时不能写「不一致」（没人反驳），更不能写「一致」（那是骗人去看下一行）——
    /// 写「只有一路读到」，操作员才知道这一项得自己对原图（第 9 棒批次一-9）。</para>
    /// </summary>
    public string AgreementText => Field.LlmValue is null || Field.TextValue is null
        ? "只有一路读到"
        : Field.Agreed ? "一致" : "不一致";

    public string EvidenceText => Field.Evidence switch
    {
        EvidenceLevel.Exact => "原文命中",
        EvidenceLevel.Compact => "去空格命中",
        EvidenceLevel.DigitsOnly => "只有数字对得上",
        _ => "查无原文",
    };

    public string ConfidenceText => $"{Field.Confidence:P0}";

    public string NoteText => string.Join(" ", new[] { Field.Warning, Field.Note }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>标红条件：值可疑（不一致/低置信/有告警）。</summary>
    public bool IsHot => Field.IsHot;

    public int EvidenceLineIndex => Field.EvidenceLineIndex;

    public void RefreshFromField()
    {
        Raise(nameof(Value));
        Raise(nameof(Confirmed));
        Raise(nameof(IsPending));
        Raise(nameof(AgreementText));
        Raise(nameof(EvidenceText));
        Raise(nameof(ConfidenceText));
        Raise(nameof(NoteText));
        Raise(nameof(IsHot));
    }
}

/// <summary>
/// 识别结果核对窗口的大脑 —— M6 的 D13 落在这一个界面上：
/// <strong>机器给的一切字段都要人点头，才算数据</strong>；没核对干净的字段不允许导入，也就进不了打印队列。
/// <para>刻意不做的事：① 没有"全部确认"按钮，只有"批量确认机器真有把握的那些"（规则见
/// <see cref="RecognizedBatch.BulkConfirm"/>），把高风险项混进一键放行等于没有闸门；
/// ② 窗口不直接写主界面，只攒 <see cref="ResultRecords"/>，由主窗口决定换不换数据源。</para>
/// </summary>
public sealed class RecognitionReviewViewModel : ObservableObject
{
    private readonly RecognitionRun _run;
    private readonly List<ReviewFieldRow> _allRows = new();

    private BatchItem? _selectedBatch;
    private ReviewFieldRow? _selectedRow;
    private string _errorText = string.Empty;

    public RecognitionReviewViewModel(RecognitionRun run)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));

        Batches = new ObservableCollection<BatchItem>(run.Batches.Select(b => new BatchItem(b)));
        foreach (var batch in Batches)
        {
            foreach (var field in batch.Batch.Fields)
                _allRows.Add(new ReviewFieldRow(field, OnRowChanged));
        }

        BulkConfirmCommand = new RelayCommand(BulkConfirm, () => SelectedBatch is not null);
        ImportBatchCommand = new RelayCommand(ImportSelectedBatch, () => SelectedBatch?.CanImport == true);
        OpenSourceCommand = new RelayCommand(OpenSource, () => File.Exists(SelectedBatch?.Batch.SourcePath));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());

        SelectedBatch = Batches.FirstOrDefault();
    }

    public event Action? CloseRequested;

    // ---------- 命令 ----------

    public RelayCommand BulkConfirmCommand { get; }

    public RelayCommand ImportBatchCommand { get; }

    public RelayCommand OpenSourceCommand { get; }

    public RelayCommand CloseCommand { get; }

    // ---------- 绑定项 ----------

    public ObservableCollection<BatchItem> Batches { get; }

    public ObservableCollection<ReviewFieldRow> Rows { get; } = new();

    public ObservableCollection<string> EvidenceLines { get; } = new();

    public ObservableCollection<string> ChannelLines { get; } = new();

    public BatchItem? SelectedBatch
    {
        get => _selectedBatch;
        set
        {
            if (!Set(ref _selectedBatch, value)) return;
            RebuildRows();
            Raise(nameof(HasBatch));
            Raise(nameof(SourceName));
            Raise(nameof(PreviewImage));
            ImportBatchCommand.RaiseCanExecute();
            OpenSourceCommand.RaiseCanExecute();
        }
    }

    public bool HasBatch => _selectedBatch is not null;

    public ReviewFieldRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            Raise(nameof(SelectedEvidenceIndex));
            Raise(nameof(SelectedFieldHint));
        }
    }

    /// <summary>选中字段的证据行下标，右侧原文列表跟着跳。</summary>
    public int SelectedEvidenceIndex => _selectedRow?.EvidenceLineIndex ?? -1;

    public string SelectedFieldHint => _selectedRow is null
        ? "点一行看它在原文里的位置。"
        : $"{_selectedRow.ChineseName}｜{_selectedRow.AgreementText}｜证据：{_selectedRow.EvidenceText}｜置信 {_selectedRow.ConfidenceText}";

    public string SourceName => _selectedBatch?.SourceName ?? string.Empty;

    /// <summary>原图缩略（只为核对定位，不参与任何排版）。docx 或读不出来时为 null。</summary>
    public ImageSource? PreviewImage
    {
        get
        {
            var path = _selectedBatch?.Batch.SourcePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !RecognitionService.IsImage(path))
                return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;    // 立刻读完，不把文件锁住
                bitmap.DecodePixelWidth = 720;                    // 缩到够看清就行，扫描件 6000px 没必要全解码
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                // 预览失败不能拖累核对本身——原因写进告警区，让人知道是预览没出来，不是图没识别。
                AppLog.Warning($"识别预览图加载失败 {path}：{ex.Message}");
                return null;
            }
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    /// <summary>本次攒下来、准备交给主界面的记录（按导入顺序）。</summary>
    public List<MarkRecord> ResultRecords { get; } = new();

    /// <summary>导入过的文档名，主界面拿它写「数据从哪来」。</summary>
    public List<string> ImportedNames { get; } = new();

    public bool AnyImported => ResultRecords.Count > 0;

    /// <summary>整批通道说明（含降级与"数据出网"提醒），窗口顶部常驻。</summary>
    public string ChannelLine
    {
        get
        {
            var head = _run.Batches.Count == 0 ? "没有识别出任何文档" : _run.Capability.ChannelsUsed(
                _run.Batches.Any(b => b.TextChannel == TextChannel.DocxText),
                // Win7 变体图片不产 OCR 文本层、记为 Llm（模型直给）；Ocr 保留以兼容主仓语义。
                _run.Batches.Any(b => b.TextChannel is TextChannel.Ocr or TextChannel.Llm));
            var tail = _run.Warnings.Count == 0 ? string.Empty : " · " + string.Join("；", _run.Warnings);
            return $"实际走的通道：{head}{tail}";
        }
    }

    public string StatusLine
    {
        get
        {
            var pending = Batches.Sum(b => b.Batch.PendingCount);
            var hot = Batches.Sum(b => b.Batch.HotCount);
            var imported = ResultRecords.Count;
            var blocked = Batches.Count(b => !b.Imported && !b.CanImport);
            var text = $"共 {Batches.Count} 份文档 · 待核 {pending} 项（其中 {hot} 项机器没把握）· 已导入 {imported} 份";
            if (blocked > 0) text += $" · 还有 {blocked} 份没核完，导不进来";
            return text;
        }
    }

    // ---------- 行为 ----------

    private void RebuildRows()
    {
        Rows.Clear();
        EvidenceLines.Clear();
        ChannelLines.Clear();
        if (_selectedBatch is null) return;

        foreach (var row in _allRows.Where(r => _selectedBatch.Batch.Fields.Contains(r.Field)))
        {
            Rows.Add(row);
        }

        var text = _selectedBatch.Batch.Text;
        if (text is not null)
        {
            for (var i = 0; i < text.Lines.Count; i++) EvidenceLines.Add($"{i + 1} · {text.Lines[i].Text}");
        }
        else
        {
            EvidenceLines.Add("（这一份没有文本层，只有模型给的值——每一项都必须人工看原图）");
        }

        foreach (var warning in _selectedBatch.Batch.ChannelWarnings) ChannelLines.Add(warning);
        if (ChannelLines.Count == 0) ChannelLines.Add("通道过程无异常。");

        SelectedRow = null;
        Raise(nameof(SelectedRow));
        Raise(nameof(SelectedEvidenceIndex));
        Raise(nameof(SelectedFieldHint));
    }

    private void OnRowChanged()
    {
        _selectedBatch?.Invalidate();
        foreach (var batch in Batches) batch.Invalidate();
        if (_selectedRow is not null) _selectedRow.RefreshFromField();
        Raise(nameof(StatusLine));
        Raise(nameof(SelectedFieldHint));
        ImportBatchCommand.RaiseCanExecute();
    }

    private void BulkConfirm()
    {
        var batch = SelectedBatch;
        if (batch is null) return;

        var count = batch.Batch.BulkConfirm();
        foreach (var row in Rows) row.RefreshFromField();
        batch.Invalidate();
        Raise(nameof(StatusLine));
        ImportBatchCommand.RaiseCanExecute();

        ErrorText = count == 0
            ? "没有可以批量确认的项。剩下的都是两通道不一致、查无原文或有告警的，必须逐条看一眼再勾。"
            : $"已批量确认 {count} 项（两通道一致、原文命中、无告警）。剩下 {batch.Batch.PendingCount} 项请逐条核对。";
        AppLog.Info($"识别核对：{batch.SourceName} 批量确认 {count} 项，余 {batch.Batch.PendingCount} 项");
    }

    private void ImportSelectedBatch()
    {
        var batch = SelectedBatch;
        if (batch is null) return;
        if (!batch.Batch.ReadyToImport)
        {
            ErrorText = $"还有 {batch.Batch.PendingCount} 项没确认，不能导入（定案 D13：机器给的东西必须人点过头）。";
            return;
        }
        if (batch.Imported)
        {
            ErrorText = "这一份已经导入过了，不要重复导入。";
            return;
        }

        var record = batch.Batch.ToRecord(ResultRecords.Count + 1);
        ResultRecords.Add(record);
        ImportedNames.Add(batch.SourceName);
        batch.Imported = true;
        ErrorText = string.Empty;
        Raise(nameof(StatusLine));
        Raise(nameof(AnyImported));
        ImportBatchCommand.RaiseCanExecute();
        AppLog.Info($"识别核对：导入 {batch.SourceName}，{record.Values.Count} 个字段，累计 {ResultRecords.Count} 条");
    }

    private void OpenSource()
    {
        var path = SelectedBatch?.Batch.SourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            ErrorText = "打不开文件所在位置：" + ex.Message;
        }
    }

    /// <summary>关掉窗口时还有没核完的东西：给主界面一句话，让它提示用户。</summary>
    public string LeftoverSummary()
    {
        var notImported = Batches.Where(b => !b.Imported).ToList();
        if (notImported.Count == 0) return string.Empty;
        return $"{notImported.Count} 份文档没导入（{string.Join("、", notImported.Take(3).Select(b => b.SourceName))}" +
               (notImported.Count > 3 ? " 等" : string.Empty) + "）";
    }
}
