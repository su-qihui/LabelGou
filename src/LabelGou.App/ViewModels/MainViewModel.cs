using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Windows;
using LabelGou.App.Export;
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
/// 主窗口视图模型：M1 的四步链路——导入、映射、选模板、预览；M2 的整版拼版与件号编号在 <see cref="Sheet"/> 里。
/// <para>
/// 刻意把"能不能用"的逻辑都留在 <c>LabelGou.Core</c>，这里只做状态编排；
/// 这样 M3 打印直接复用 Core，不必从界面里挖逻辑。
/// </para>
/// </summary>
public sealed class MainViewModel : ObservableObject, ILabelSource
{
    private readonly ProfileStore _profileStore = new();
    private readonly TemplateStore _templateStore = new();

    private TabularData? _data;
    private MappingProfile? _working;
    private IReadOnlyList<MarkRecord> _rawRecords = Array.Empty<MarkRecord>();
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

        // M2：拼版与编号的界面状态独立成一个 VM，它通过 ILabelSource 反过来取记录与模板
        // （先建好再选模板，因为 SelectedTemplate 的 setter 会通知它重算）
        Sheet = new ImpositionViewModel(this);
        Sheet.NumberingApplied += OnNumberedLabelsChanged;
        SheetZoomInCommand = new RelayCommand(() => Sheet.SheetZoom = Math.Min(8, Sheet.SheetZoom * 1.25));
        SheetZoomOutCommand = new RelayCommand(() => Sheet.SheetZoom = Math.Max(0.1, Sheet.SheetZoom / 1.25));

        // M3：输出与打印（靠上面的拼版结果吃饭，所以必须建在 Sheet 之后）
        Export = new ExportViewModel(this);

        foreach (var template in _templateStore.ListAll()) TemplateOptions.Add(new TemplateOption(template));
        SelectedTemplate = TemplateOptions.FirstOrDefault(t => t.Id == BuiltInTemplates.IdStandard) ?? TemplateOptions.FirstOrDefault();
        RefreshProfiles();
        Sheet.RefreshFromSource();
    }

    /// <summary>M2：整版拼版 + 件号自动编号。</summary>
    public ImpositionViewModel Sheet { get; }

    /// <summary>M3：打印与导出（PDF / PNG / TIFF）。</summary>
    public ExportViewModel Export { get; }

    /// <summary>
    /// M3：需要用户点头的闸门（比如“有 N 张标签带未核对标记，还要印吗”）。
    /// 由 MainWindow 挂上 MessageBox 询问；未挂时视为不阻塞（单测环境里就是这个情况）。
    /// </summary>
    public Func<string, bool>? ConfirmGate { get; set; }

    /// <summary>
    /// M3：给导出/打印用的只读快照。必须在 UI 线程上调用（它读的都是 UI 线程持有的字段），
    /// 拿到之后的后台线程只读这个对象，不再回头碰 VM。
    /// 没数据时用样例记录顶上，让“先打一张看看对齐”这个常见动作能成。
    /// </summary>
    public PageContentSource? CreatePageSource()
    {
        var template = SelectedTemplate?.Template;
        if (template is null) return null;
        var records = _records.Count > 0 ? _records : new[] { SampleRecords.StandardSample() };
        return new PageContentSource(template, records, _sourcePath ?? string.Empty);
    }

    /// <summary>M4：模板库。编辑器与菜单共用这一个实例，不开第二份。</summary>
    public TemplateStore Templates => _templateStore;

    /// <summary>
    /// M4：编辑器保存或删除之后重建模板下拉。给了 <paramref name="preferId"/> 就选中它，
    /// 选不到就保住当前这份（用户模板改了名再存也还能接着用），都没有退回标准内置。
    /// <para>这里每次都 new 新的 <see cref="TemplateOption"/>，故意让 <see cref="SelectedTemplate"/>
    /// 的 setter 认为“换了”，预览与拼版才会跟着重画。</para>
    /// </summary>
    public void ReloadTemplates(string? preferId = null)
    {
        var keepId = preferId ?? SelectedTemplate?.Id ?? BuiltInTemplates.IdStandard;
        TemplateOptions.Clear();
        foreach (var template in _templateStore.ListAll()) TemplateOptions.Add(new TemplateOption(template));
        SelectedTemplate = TemplateOptions.FirstOrDefault(t => t.Id == keepId)
            ?? TemplateOptions.FirstOrDefault(t => t.Id == BuiltInTemplates.IdStandard)
            ?? TemplateOptions.FirstOrDefault();
    }

    /// <summary>拼版 VM 算完编号后回贴：记录集换成「一箱一张」的标签集。</summary>
    private void OnNumberedLabelsChanged(IReadOnlyList<MarkRecord> labels)
    {
        _records = labels;
        CurrentIndex = labels.Count > 0 ? 1 : 0;
        Raise(nameof(RecordTotal));
        RebuildLayout();
    }

    IReadOnlyList<MarkRecord> ILabelSource.RawRecords => _rawRecords;

    LabelLayout? ILabelSource.BuildLayoutAt(int labelIndex) => BuildLayoutFor(labelIndex);

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

    /// <summary>整版预览的缩放（与单标签缩放互不影响）。</summary>
    public RelayCommand SheetZoomInCommand { get; }
    public RelayCommand SheetZoomOutCommand { get; }

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
                Sheet.RebuildPlan();
            }
        }
    }

    /// <summary><see cref="ILabelSource"/>：拼版 VM 用它拿当前模板。</summary>
    LabelTemplate? ILabelSource.Template => SelectedTemplate?.Template;

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

    /// <summary>整版预览适应窗口（没方案时什么也不做）。</summary>
    public void FitSheetTo(double availableWidth, double availableHeight)
    {
        var plan = Sheet.Plan;
        if (plan is null || availableWidth < 20 || availableHeight < 20) return;
        Sheet.SheetZoom = Rendering.SheetPreviewControl.FitZoom(plan, availableWidth - 28, availableHeight - 28, maxZoom: 3);
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
        profile.Numbering = Sheet.BuildRule();
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
        _rawRecords = result.Records;
        _mappingIssues = result.Issues;

        IssueLines.Clear();
        foreach (var issue in result.Issues.Take(200))
        {
            var fieldName = issue.Field is null ? "整行" : MarkFieldCatalog.Get(issue.Field.Value).ChineseName;
            IssueLines.Add($"第 {issue.RowNumber} 条 · {fieldName}：{issue.Message}");
        }

        AiGateBlocked = _rawRecords.Any(r => r.PendingReview().Any());
        Raise(nameof(RecordTotal));
        Raise(nameof(HasData));

        var boundCount = profile.BoundCount;
        var suffix = result.Issues.Count == 0 ? "无告警" : $"{result.Issues.Count} 条告警，建议核对";
        StatusMessage = $"已连接 {boundCount} 个字段，共 {_rawRecords.Count} 条唛头记录 · {suffix}";

        // 件号交给 M2 编号引擎统一处理（它会回贴标签集并触发重算）
        Sheet.RefreshFromSource();
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
        rebound.Numbering = saved.Numbering;
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
        Sheet.ApplySavedRule(saved.Numbering);
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

        var layout = BuildLayoutFor(_currentIndex);
        if (layout is null)
        {
            CurrentLayout = null;
            return;
        }

        RecordInfoText = _currentIndex >= 1 && _currentIndex <= _records.Count
            ? $"第 {_currentIndex} / {_records.Count} 张标签 · 来自数据第 {_records[_currentIndex - 1].SourceRowIndex} 行"
            : "示意预览（未导入数据）";

        CurrentLayout = layout;

        TokenNoteText = layout.UnresolvedTokens.Count == 0
            ? string.Empty
            : $"模板里 {string.Join("、", layout.UnresolvedTokens.Distinct().Take(6))} 无数据，已留空" +
              (layout.HiddenElementCount > 0 ? $"（共隐去 {layout.HiddenElementCount} 个要素）" : string.Empty);

        AiGateBlocked = layout.HasUnconfirmed;
    }

    /// <summary>
    /// 按序号算一张标签。序号越界（或为 0）时退回内置样例，用于示意预览。
    /// 单标签预览与整版预览共用它，两边看到的才是同一张标签。
    /// </summary>
    private LabelLayout? BuildLayoutFor(int index)
    {
        var template = SelectedTemplate?.Template;
        if (template is null) return null;

        MarkRecord record;
        int effectiveIndex;
        int total;
        if (index >= 1 && index <= _records.Count)
        {
            record = _records[index - 1];
            effectiveIndex = index;
            total = Math.Max(1, _records.Count);
        }
        else
        {
            record = SampleRecords.StandardSample();
            effectiveIndex = 1;
            total = Math.Max(1, _records.Count);
        }

        // 预览（单标签与整版）带参考底图，打印/导出走 PageContentSource，那边默认不含
        return LayoutEngine.Build(template, record,
            new LayoutContext(effectiveIndex, total, Path.GetFileName(_sourcePath), IncludeReference: true));
    }

    /// <summary>调试/自动化用：当前标签记录集（已按编号规则展开）。</summary>
    public IReadOnlyList<MarkRecord> CurrentRecords => _records;

    /// <summary>调试/自动化用：未展开的原始记录集（Excel 一行一条）。</summary>
    public IReadOnlyList<MarkRecord> RawRecords => _rawRecords;

    /// <summary>调试/自动化用：当前告警。</summary>
    public IReadOnlyList<MappingIssue> CurrentIssues => _mappingIssues;
}
