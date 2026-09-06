using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Windows;
using LabelGou.App.Mvvm;
using LabelGou.Core.Data;
using LabelGou.Core.Layout;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.App.ViewModels;

/// <summary>映射界面里"一列"的下拉选项。</summary>
public sealed class ColumnOption
{
    public ColumnOption(int index, string label)
    {
        Index = index;
        Label = label;
    }

    public int Index { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>模板下拉项。</summary>
public sealed class TemplateOption
{
    public TemplateOption(LabelTemplate template)
    {
        Template = template;
    }

    public LabelTemplate Template { get; }

    public string Id => Template.Id;

    public string Name => Template.Name;

    public string SizeText => $"{Template.WidthMm:0.#} × {Template.HeightMm:0.#} mm";

    public string DisplayName => Template.BuiltIn ? $"{Template.Name}（{SizeText}）[内置]" : $"{Template.Name}（{SizeText}）";
}

/// <summary>已保存的映射方案下拉项。</summary>
public sealed class ProfileOption
{
    public ProfileOption(ProfileStore.ProfileSummary summary, MappingProfile profile)
    {
        Summary = summary;
        Profile = profile;
    }

    public ProfileStore.ProfileSummary Summary { get; }

    public MappingProfile Profile { get; }

    public string FileName => Summary.FileName;

    public string DisplayName => $"{Summary.Name}（已连 {Summary.BoundCount} 项 · {Summary.UpdatedAt:MM-dd HH:mm}）";
}

/// <summary>一行"字段 ← 列"的可编辑绑定。</summary>
public sealed class FieldRowVm : ObservableObject
{
    private int _columnIndex = -1;
    private bool _mapped;

    public FieldRowVm(FieldDefinition definition, IReadOnlyList<ColumnOption> columns, int initialColumn)
    {
        Definition = definition;
        Columns = columns;
        _columnIndex = initialColumn;
        _mapped = initialColumn >= 0;
    }

    public FieldDefinition Definition { get; }

    public IReadOnlyList<ColumnOption> Columns { get; }

    public string DisplayName => Definition.ChineseName;

    public string EnglishHint => Definition.EnglishLabel.Length > 0 ? Definition.EnglishLabel : Definition.Key.ToString();

    public string FieldKey => Definition.Key.ToString();

    /// <summary>该列第一行的实际值，帮操作员确认"连对没有"。</summary>
    public string SampleValue { get; set; } = string.Empty;

    public int ColumnIndex
    {
        get => _columnIndex;
        set
        {
            if (Set(ref _columnIndex, value))
            {
                Mapped = value >= 0;
            }
        }
    }

    /// <summary>是否已连上列（界面上用底色区分）。</summary>
    public bool Mapped
    {
        get => _mapped;
        private set => Set(ref _mapped, value);
    }
}

/// <summary>
/// 主窗口视图模型：M1 的四步链路——导入、映射、选模板、预览。
/// <para>
/// 刻意把"能不能用"的逻辑都留在 <c>LabelGou.Core</c>，这里只做状态编排；
/// 这样 M2 拼版、M3 打印直接复用 Core，不必从界面里挖逻辑。
/// </para>
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore = new();
    private readonly TemplateStore _templateStore = new();

    private TabularData? _data;
    private MappingProfile? _working;
    private IReadOnlyList<MarkRecord> _records = Array.Empty<MarkRecord>();
    private IReadOnlyList<MappingIssue> _mappingIssues = Array.Empty<MappingIssue>();

    private string? _sourcePath;
    private string _statusMessage = "请打开工厂发来的 Excel / CSV 数据文件。";
    private string _headerInfoText = "尚未导入数据";
    private string _recordInfoText = "示意预览（未导入数据）";
    private string _templateInfoText = string.Empty;
    private int _currentIndex;
    private double _zoom = 2.4;
    private bool _showGuides;
    private bool _aiGateBlocked;

    public MainViewModel()
    {
        OpenFileCommand = new RelayCommand(OpenFile);
        ReloadSheetCommand = new RelayCommand(() => LoadSource(_sourcePath, SelectedSheet));
        AutoSuggestCommand = new RelayCommand(AutoSuggest, () => _data is not null);
        ApplyMappingCommand = new RelayCommand(ApplyMapping, () => _data is not null);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => _data is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedSavedProfile is not null);
        FirstRecordCommand = new RelayCommand(() => CurrentIndex = 1, () => RecordTotal > 0);
        PrevRecordCommand = new RelayCommand(() => CurrentIndex = Math.Max(1, CurrentIndex - 1), () => CurrentIndex > 1);
        NextRecordCommand = new RelayCommand(() => CurrentIndex = Math.Min(RecordTotal, CurrentIndex + 1), () => CurrentIndex < RecordTotal);
        LastRecordCommand = new RelayCommand(() => CurrentIndex = RecordTotal, () => RecordTotal > 0);
        SamplePreviewCommand = new RelayCommand(() => CurrentIndex = 0);
        ZoomInCommand = new RelayCommand(() => Zoom = Math.Min(8, Zoom * 1.25));
        ZoomOutCommand = new RelayCommand(() => Zoom = Math.Max(0.2, Zoom / 1.25));

        foreach (var template in _templateStore.ListAll()) TemplateOptions.Add(new TemplateOption(template));
        SelectedTemplate = TemplateOptions.FirstOrDefault(t => t.Id == BuiltInTemplates.IdStandard) ?? TemplateOptions.FirstOrDefault();
        RefreshProfiles();
        RebuildLayout();
    }

    // ---------- 命令 ----------

    public RelayCommand OpenFileCommand { get; }
    public RelayCommand ReloadSheetCommand { get; }
    public RelayCommand AutoSuggestCommand { get; }
    public RelayCommand ApplyMappingCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand FirstRecordCommand { get; }
    public RelayCommand PrevRecordCommand { get; }
    public RelayCommand NextRecordCommand { get; }
    public RelayCommand LastRecordCommand { get; }
    public RelayCommand SamplePreviewCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }

    /// <summary>界面用它弹错误框（保持 VM 不直接依赖 MessageBox）。</summary>
    public event Action<string>? ErrorRaised;

    // ---------- 数据源 ----------

    public string? SourcePath
    {
        get => _sourcePath;
        private set
        {
            if (Set(ref _sourcePath, value)) Raise(nameof(HasData));
        }
    }

    public bool HasData => _data is not null;

    public ObservableCollection<string> Sheets { get; } = new();

    private string? _selectedSheet;

    public string? SelectedSheet
    {
        get => _selectedSheet;
        set
        {
            if (Set(ref _selectedSheet, value) && value is not null && HasData && !string.Equals(value, _data?.SheetName, StringComparison.Ordinal))
            {
                LoadSource(_sourcePath, value);
            }
        }
    }

    public string HeaderInfoText
    {
        get => _headerInfoText;
        private set => Set(ref _headerInfoText, value);
    }

    public DataTable? PreviewTable { get; private set; }

    // ---------- 映射 ----------

    public ObservableCollection<ColumnOption> ColumnOptions { get; private set; } = new();

    public ObservableCollection<FieldRowVm> FieldRows { get; } = new();

    public ObservableCollection<ProfileOption> SavedProfiles { get; } = new();

    private ProfileOption? _selectedSavedProfile;

    public ProfileOption? SelectedSavedProfile
    {
        get => _selectedSavedProfile;
        set
        {
            if (Set(ref _selectedSavedProfile, value) && value is not null) ApplySavedProfile(value.Profile);
        }
    }

    private string _profileName = string.Empty;

    /// <summary>保存方案时用的名字，默认取数据文件名。</summary>
    public string ProfileName
    {
        get => _profileName;
        set => Set(ref _profileName, value);
    }

    private bool _autoNumberCartons = true;

    public bool AutoNumberCartons
    {
        get => _autoNumberCartons;
        set
        {
            if (Set(ref _autoNumberCartons, value) && _working is not null)
            {
                _working.AutoNumberCartons = value;
                ApplyMapping();
            }
        }
    }

    // ---------- 模板 ----------

    public ObservableCollection<TemplateOption> TemplateOptions { get; } = new();

    private TemplateOption? _selectedTemplate;

    public TemplateOption? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (Set(ref _selectedTemplate, value))
            {
                TemplateInfoText = DescribeTemplate(value?.Template);
                RebuildLayout();
            }
        }
    }

    public string TemplateInfoText
    {
        get => _templateInfoText;
        private set => Set(ref _templateInfoText, value);
    }

    // ---------- 结果与预览 ----------

    public ObservableCollection<string> IssueLines { get; } = new();

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string RecordInfoText
    {
        get => _recordInfoText;
        private set => Set(ref _recordInfoText, value);
    }

    private string _tokenNoteText = string.Empty;

    /// <summary>模板变量没数据时的提示（单独占一行，不括展主状态栏）。</summary>
    public string TokenNoteText
    {
        get => _tokenNoteText;
        private set => Set(ref _tokenNoteText, value);
    }

    public int RecordTotal => _records.Count;

    /// <summary>当前预览第几条（1 起）；0 表示示意预览。</summary>
    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            var v = Math.Max(0, Math.Min(value, Math.Max(RecordTotal, 0)));
            if (Set(ref _currentIndex, v)) RebuildLayout();
        }
    }

    public double Zoom
    {
        get => _zoom;
        set => Set(ref _zoom, Math.Max(0.2, Math.Min(value, 8)));
    }

    public bool ShowGuides
    {
        get => _showGuides;
        set => Set(ref _showGuides, value);
    }

    /// <summary>
        /// 是否存在"未人工核对"的字段。M3 的打印前闸门读这个标志；
        /// M1 只有 Excel 数据，恒为 false，但接口现在就定下来。
        /// </summary>
    public bool AiGateBlocked
    {
        get => _aiGateBlocked;
        private set => Set(ref _aiGateBlocked, value);
    }

    private LabelLayout? _currentLayout;

    public LabelLayout? CurrentLayout
    {
        get => _currentLayout;
        private set
        {
            if (Set(ref _currentLayout, value)) Raise(nameof(HasLayout));
        }
    }

    public bool HasLayout => CurrentLayout is not null;

    /// <summary>预览区尺寸变化时由界面调用。</summary>
    public void FitTo(double availableWidth, double availableHeight)
    {
        var layout = CurrentLayout;
        if (layout is null || availableWidth < 20 || availableHeight < 20) return;
        Zoom = Rendering.LabelPreviewControl.FitZoom(layout, availableWidth - 24, availableHeight - 24);
    }

    // ---------- 流程实现 ----------

    private void OpenFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择工厂发来的数据文件",
            Filter = TableImporter.OpenFileFilter,
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(_sourcePath))
        {
            var dir = Path.GetDirectoryName(_sourcePath);
            if (dir is not null && Directory.Exists(dir)) dialog.InitialDirectory = dir;
        }
        if (dialog.ShowDialog() != true) return;

        LoadSource(dialog.FileName, null);
    }

    private void LoadSource(string? path, string? sheet)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var data = TableImporter.Import(path, sheet);
            _data = data;
            SourcePath = data.SourceFile;

            Sheets.Clear();
            foreach (var name in TableImporter.ListSheets(data.SourceFile)) Sheets.Add(name);
            _selectedSheet = data.SheetName;
            Raise(nameof(SelectedSheet));

            HeaderInfoText = $"{data.Describe()} · 表头在第 {data.HeaderRowIndex + 1} 行" +
                             (data.Encoding is not null ? $" · 编码 {data.Encoding.WebName}" : string.Empty);

            BuildPreviewTable(data);
            ColumnOptions = new ObservableCollection<ColumnOption> { new(-1, "（不映射）") };
            for (var c = 0; c < data.ColumnCount; c++)
            {
                ColumnOptions.Add(new ColumnOption(c, $"{HeaderRowDetector.ColumnLetter(c)} · {data.Headers[c]}"));
            }
            Raise(nameof(ColumnOptions));

            // 优先套用已保存的同格式方案，其次自动猜
            ProfileName = Path.GetFileNameWithoutExtension(data.SourceFile);
            var known = _profileStore.ListAll()
                .Select(s => new { Summary = s, Profile = _profileStore.Load(s.FileName) })
                .Where(x => x.Profile is not null)
                .Select(x => x.Profile!)
                .ToList();

            var matched = MappingSuggester.FindBestMatch(data.Headers, known);
            if (matched is not null)
            {
                _working = matched;
                StatusMessage = $"已套用保存过的映射方案「{matched.Name}」，请检查有没有连错。";
            }
            else
            {
                _working = MappingSuggester.Suggest(data.Headers, ProfileName);
                StatusMessage = "已按表头自动连接字段，请检查后点「应用映射」。";
            }
            AutoNumberCartons = _working.AutoNumberCartons;
            RebuildFieldRows();
            ApplyMapping();
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(ex.Message);
            StatusMessage = "打开失败：" + ex.Message;
        }
    }

    private void BuildPreviewTable(TabularData data)
    {
        var table = new DataTable(data.SheetName);
        foreach (var header in data.Headers) table.Columns.Add(header, typeof(string));

        const int maxPreviewRows = 300;
        var rowCount = Math.Min(maxPreviewRows, data.RowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var values = new object[data.ColumnCount];
            for (var c = 0; c < data.ColumnCount; c++) values[c] = data.GetCell(r, c);
            table.Rows.Add(values);
        }
        table.DefaultView.AllowNew = false;
        table.DefaultView.AllowDelete = false;
        table.DefaultView.AllowEdit = false;

        PreviewTable = table;
        Raise(nameof(PreviewTable));
    }

    private void RebuildFieldRows()
    {
        var data = _data;
        var working = _working;
        FieldRows.Clear();
        if (data is null || working is null) return;

        var first = data.Rows.Count > 0 ? data.Rows[0] : Array.Empty<string>();
        foreach (var def in MarkFieldCatalog.All)
        {
            var column = working.ColumnIndexOf(def.Key);
            var row = new FieldRowVm(def, ColumnOptions, column)
            {
                SampleValue = column >= 0 && column < first.Count ? first[column] : string.Empty,
            };
            FieldRows.Add(row);
        }
    }

    private MappingProfile? CollectProfile()
    {
        var data = _data;
        if (data is null) return null;

        var profile = MappingProfile.CreateFor(data.Headers,
            string.IsNullOrWhiteSpace(ProfileName) ? "未命名方案" : ProfileName.Trim());
        profile.AutoNumberCartons = AutoNumberCartons;
        profile.Note = _working?.Note ?? string.Empty;

        foreach (var row in FieldRows)
        {
            if (row.ColumnIndex >= 0) profile.Bind(row.Definition.Key, row.ColumnIndex, data.Headers);
        }
        return profile;
    }

    private void AutoSuggest()
    {
        var data = _data;
        if (data is null) return;

        _working = MappingSuggester.Suggest(data.Headers,
            string.IsNullOrWhiteSpace(ProfileName) ? "自动匹配方案" : ProfileName.Trim());
        AutoNumberCartons = _working.AutoNumberCartons;
        RebuildFieldRows();
        ApplyMapping();
        StatusMessage = "已按表头别名重新自动连接。";
    }

    private void ApplyMapping()
    {
        var data = _data;
        var profile = CollectProfile();
        if (data is null || profile is null) return;

        _working = profile;
        var result = RecordMapper.Map(data, profile);
        _records = result.Records;
        _mappingIssues = result.Issues;

        IssueLines.Clear();
        foreach (var issue in result.Issues.Take(200))
        {
            var fieldName = issue.Field is null ? "整行" : MarkFieldCatalog.Get(issue.Field.Value).ChineseName;
            IssueLines.Add($"第 {issue.RowNumber} 条 · {fieldName}：{issue.Message}");
        }

        AiGateBlocked = _records.Any(r => r.PendingReview().Any());
        CurrentIndex = _records.Count > 0 ? 1 : 0;
        Raise(nameof(RecordTotal));
        Raise(nameof(HasData));

        var boundCount = profile.BoundCount;
        var suffix = result.Issues.Count == 0 ? "无告警" : $"{result.Issues.Count} 条告警，建议核对";
        StatusMessage = $"已连接 {boundCount} 个字段，共 {_records.Count} 条唛头记录 · {suffix}";
        RebuildLayout();
    }

    private void ApplySavedProfile(MappingProfile saved)
    {
        var data = _data;
        if (data is null)
        {
            _working = saved;
            return;
        }

        // 按列标题重连（列顺序可能变了）
        var rebound = MappingProfile.CreateFor(data.Headers, saved.Name);
        rebound.Note = saved.Note;
        rebound.AutoNumberCartons = saved.AutoNumberCartons;
        var hits = 0;
        foreach (var mapping in saved.Mappings)
        {
            if (!mapping.IsBound) continue;
            var header = mapping.ColumnHeader ?? (mapping.ColumnIndex < saved.HeaderHints.Count ? saved.HeaderHints[mapping.ColumnIndex] : null);
            if (header is null) continue;
            var index = data.IndexOfHeader(header);
            if (index >= 0)
            {
                rebound.Bind(mapping.Field, index, data.Headers);
                hits++;
            }
        }

        _working = rebound;
        ProfileName = saved.Name;
        AutoNumberCartons = rebound.AutoNumberCartons;
        RebuildFieldRows();
        ApplyMapping();
        StatusMessage = $"已套用方案「{saved.Name}」，成功连上 {hits} 个字段（其余需手工确认）。";
    }

    private void SaveProfile()
    {
        var profile = CollectProfile();
        if (profile is null) return;

        try
        {
            var fileName = _profileStore.Save(profile);
            RefreshProfiles();
            SelectedSavedProfile = SavedProfiles.FirstOrDefault(p => p.FileName == fileName);
            StatusMessage = $"映射方案「{profile.Name}」已保存（{fileName}），下次同格式表格会自动套用。";
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke("保存失败：" + ex.Message);
        }
    }

    private void DeleteProfile()
    {
        var selected = SelectedSavedProfile;
        if (selected is null) return;
        _profileStore.Delete(selected.FileName);
        RefreshProfiles();
        StatusMessage = $"已删除方案「{selected.Summary.Name}」。";
    }

    private void RefreshProfiles()
    {
        var current = SelectedSavedProfile?.FileName;
        SavedProfiles.Clear();
        foreach (var summary in _profileStore.ListAll())
        {
            var profile = _profileStore.Load(summary.FileName);
            if (profile is null) continue;
            SavedProfiles.Add(new ProfileOption(summary, profile));
        }
        if (current is not null)
        {
            _selectedSavedProfile = SavedProfiles.FirstOrDefault(p => p.FileName == current);
            Raise(nameof(SelectedSavedProfile));
        }
    }

    private static string DescribeTemplate(LabelTemplate? template)
    {
        if (template is null) return string.Empty;
        var issues = TemplateValidator.Validate(template);
        var errors = issues.Count(i => i.Severity == IssueLevel.Error);
        var warnings = issues.Count(i => i.Severity == IssueLevel.Warning);
        var note = string.IsNullOrWhiteSpace(template.Note) ? template.Name : template.Note;
        var health = errors > 0 ? $"⚠ 校验有 {errors} 个错误" : warnings > 0 ? $"{warnings} 条提示" : "校验通过";
        return $"{note} · {template.Elements.Count} 个要素 · {health}";
    }

    private void RebuildLayout()
    {
        var option = SelectedTemplate;
        if (option is null)
        {
            CurrentLayout = null;
            return;
        }

        var template = option.Template;
        MarkRecord record;
        int index;
        int total;

        if (_currentIndex >= 1 && _currentIndex <= _records.Count)
        {
            record = _records[_currentIndex - 1];
            index = _currentIndex;
            total = Math.Max(1, _records.Count);
            RecordInfoText = $"第 {index} / {total} 条";
        }
        else
        {
            record = SampleRecords.StandardSample();
            index = 1;
            total = Math.Max(1, _records.Count);
            RecordInfoText = "示意预览（未导入数据）";
        }

        var context = new LayoutContext(index, total, Path.GetFileName(_sourcePath));
        var layout = LayoutEngine.Build(template, record, context);
        CurrentLayout = layout;

        TokenNoteText = layout.UnresolvedTokens.Count == 0
            ? string.Empty
            : $"模板里 {string.Join("、", layout.UnresolvedTokens.Distinct().Take(6))} 无数据，已留空" +
              (layout.HiddenElementCount > 0 ? $"（共隐去 {layout.HiddenElementCount} 个要素）" : string.Empty);

        AiGateBlocked = layout.HasUnconfirmed;
    }

    /// <summary>调试/自动化用：当前记录集合（不暴露可变引用）。</summary>
    public IReadOnlyList<MarkRecord> CurrentRecords => _records;

    /// <summary>调试/自动化用：当前告警。</summary>
    public IReadOnlyList<MappingIssue> CurrentIssues => _mappingIssues;
}
