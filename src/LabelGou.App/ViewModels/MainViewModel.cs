using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Media;
using LabelGou.App.Export;
using LabelGou.App.Mvvm;
using LabelGou.App.Services;
using LabelGou.Core.Data;
using LabelGou.Core.Docking;
using LabelGou.Core.Layout;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using ColumnPortrait = LabelGou.Core.Recognition.ColumnPortrait;
using TablePortrait = LabelGou.Core.Recognition.TablePortrait;

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

    /// <summary>M7：上次用的模板与纸规（打开软件就能接着上次干，不用每次重选）。</summary>
    private readonly UiStateStore _uiState;
    private readonly TemplateStore _templateStore = new();

    private TabularData? _data;
    private MappingProfile? _working;

    /// <summary>
    /// 这张表当前按哪份指令切的（表头在哪、有没有表头、剔了哪几行）。
    /// <para>第 21 棒：这份指令可以由 AI 提、人点头后落下来，也可以用户在 ① 步手选。
    /// 每次改都走「重读一遍源文件」，所以不存在「内存里改了、下次打开又变回去」这种两套真源。</para>
    /// </summary>
    private SheetLayoutChoice _choice = SheetLayoutChoice.Auto;
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
    private int _stepIndex;

    /// <summary>自动接手换模板那一次不写盘（只顶一次，下一次用户手工选还是会被记住）。</summary>
    private bool _suppressTemplateRemember;

    /// <summary>构造兜底那一次赋值不写盘（第 23 棒：记的模板被删了时，兜底 id 不得顶掉用户记的那条）。</summary>
    private bool _bootstrapping;

    /// <summary>
    /// 数据的「代数」：换文件/换工作表/重切表/应用映射/换模板都会自增。
    /// <para>AI 面板发请求前记一份、回来时对一遍——不等就作废那轮结果（第 23 棒）：
    /// 旧的提案落在换过的表上会把旧行号夹进新表剔行，静默少印。</para>
    /// </summary>
    public int DataGeneration { get; private set; }

    private void BumpDataGeneration() => ++DataGeneration;

    /// <summary>映射告警那几行（与「模板还差哪几项」分家存，后者是派生值，每次重列）。</summary>
    private List<string> _mapIssueLines = new();

    public MainViewModel() : this(uiState: null)
    {
    }

    /// <param name="uiState">界面状态；单测传临时目录那份，不往用户机器上写（§五-48）。</param>
    public MainViewModel(UiStateStore? uiState)
    {
        _uiState = uiState ?? new UiStateStore();
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
        Sheet = new ImpositionViewModel(this, _uiState);
        Sheet.NumberingApplied += OnNumberedLabelsChanged;
        // 纸规一换，③/④ 步那句「模板尺寸与这张纸配不配」就得重算（四档开法同名同尺寸，配不上就是选错了档）。
        Sheet.SheetSelectionChanged += () => Raise(nameof(TemplateSheetHint));
        UseMatchingTemplateCommand = new RelayCommand(UseMatchingTemplate, () => MatchingTemplateOption is not null);
        SheetZoomInCommand = new RelayCommand(() => Sheet.SheetZoom = Math.Min(8, Sheet.SheetZoom * 1.25));
        SheetZoomOutCommand = new RelayCommand(() => Sheet.SheetZoom = Math.Max(0.1, Sheet.SheetZoom / 1.25));
        PrevStepCommand = new RelayCommand(() => StepIndex--, () => !IsFirstStep);
        NextStepCommand = new RelayCommand(() => StepIndex++, () => !IsLastStep);

        // M3：输出与打印（靠上面的拼版结果吃饭，所以必须建在 Sheet 之后）
        Export = new ExportViewModel(this);

        // 第 17 棒：③ 步的「条码」栏目。它只读表与模板，落盘仍走下面这条 _templateStore 那一路（不开第二个写入口）。
        Barcode = new BarcodePanelViewModel(this, AddBarcodeElement);

        foreach (var template in _templateStore.ListAll()) TemplateOptions.Add(new TemplateOption(template));
        // 先接上次用的那套（内置模板每加一批都退回标准模板，会让新版式在界面上等于不存在），
        // 没记过、或记的那个已被删了，才退回标准内置。
        var remembered = _uiState.Load();
        // 大小写口径接回上次选的。这里直接写字段不走 setter：那时预览与拼版都还没建，
        // 去重算一次只会拿到半成品（而且启动那一次不该产生写盘 IO）。
        _textCase = remembered.TextCase;
        // ① 步表格高度接回上次选的那档（没记过、或记的不是三档里的数，用默认）。同样直接写字段不走 setter。
        _previewTableHeight = RememberedPreviewTableHeight(remembered.PreviewTableHeight);
        // 兜底跟 ReloadTemplates 用同一个档（行式四行）：上一版构造兜 IdStandard、刷新兜 IdRowsFour，
        // 冷启动与触发一次刷新后看到的不是同一套模板。
        _bootstrapping = true;
        SelectedTemplate = TemplateOptions.FirstOrDefault(t => t.Id == remembered.TemplateId)
            ?? TemplateOptions.FirstOrDefault(t => t.Id == BuiltInTemplates.IdRowsFour)
            ?? TemplateOptions.FirstOrDefault();
        _bootstrapping = false;
        RefreshProfiles();
        Sheet.RefreshFromSource();
    }

    /// <summary>M2：整版拼版 + 件号自动编号。</summary>
    public ImpositionViewModel Sheet { get; }

    /// <summary>
    /// ③/④ 步的那句配套提示：当前模板的尺寸与选中的那张纸的刀模尺寸对不上时才非空。
    /// <para>为什么需要它：用户 2026-09-09 把四档开法（一开四/一开八/大开二/小开二）定成常用档，
    /// 那么「纸选了一开八、模板还是 140×100」就是最常见的一次错配；以前只有拼版 issue 里那句
    /// 「与模板不一致」，不告诉他该换成哪一份。</para>
    /// </summary>
    public string TemplateSheetHint
    {
        get
        {
            var template = SelectedTemplate?.Template;
            var spec = Sheet.SelectedSheetOption?.Spec;
            if (template is null || spec is null) return string.Empty;
            // 纸面跟着标签走 / 刀模尺寸没填（跟随模板）的两档天生不会错配，不该拿这句话去烦用户
            if (spec.FollowsLabel || spec.FollowTemplateSize) return string.Empty;
            var (w, h) = (spec.LabelWidthMm, spec.LabelHeightMm);
            if (w <= 0 || h <= 0) return string.Empty;
            if (MatchesLabelSize(template, w, h)) return string.Empty;

            var match = MatchingTemplateOption;
            return $"纸规「{spec.Name}」的单枚是 {w:0.#}×{h:0.#} mm，当前模板是 {template.WidthMm:0.#}×{template.HeightMm:0.#} mm，对不上"
                   + (match is null ? "：表里没有这个尺寸的模板，要么换纸、要么把这份模板的尺寸改成它。"
                                     : $"；点这里换成「{match.Name}」。") + "\n" +
                   "（旋转 90° 摆位不算错配：裁下来贴到箱子上字仍是正的。）";
        }
    }

    /// <summary>模板尺寸与纸规刀模尺寸是否同一张（两个轴向都算，转 90° 是正常开料）。</summary>
    private static bool MatchesLabelSize(LabelTemplate template, double w, double h)
        => (Nearly(template.WidthMm, w) && Nearly(template.HeightMm, h))
           || (Nearly(template.WidthMm, h) && Nearly(template.HeightMm, w));

    private static bool Nearly(double a, double b) => Math.Abs(a - b) < 0.6;

    /// <summary>与当前纸规的刀模尺寸同尺寸的那份模板（没得配返 null，按钮就灰着，不猜一个给用户）。</summary>
    private TemplateOption? MatchingTemplateOption
    {
        get
        {
            var spec = Sheet.SelectedSheetOption?.Spec;
            if (spec is null || spec.FollowsLabel || spec.FollowTemplateSize) return null;
            var (w, h) = (spec.LabelWidthMm, spec.LabelHeightMm);
            if (w <= 0 || h <= 0) return null;
            return TemplateOptions.FirstOrDefault(t => !ReferenceEquals(t, SelectedTemplate) && MatchesLabelSize(t.Template, w, h));
        }
    }

    /// <summary>「换成配套模板」：只换下拉里真的存着的那一份，不替用户新建或改尺寸。</summary>
    private void UseMatchingTemplate()
    {
        var match = MatchingTemplateOption;
        if (match is null) return;
        SelectedTemplate = match;
        StatusMessage = $"已换成与「{Sheet.SelectedSheetOption?.Spec.Name}」同尺寸的模板：{match.Name}（{match.SizeText}）。";
        Raise(nameof(TemplateSheetHint));
    }

    /// <summary>M3：打印与导出（PDF / PNG / TIFF）。</summary>
    public ExportViewModel Export { get; }

    /// <summary>③ 步的条码栏目（第 17 棒）：选制式、选它读哪一列、摆哪儿。</summary>
    public BarcodePanelViewModel Barcode { get; }

    /// <summary>③/④ 步那句错配提示旁边的「换成配套模板」：只有下拉里真存着同尺寸那份才可点。</summary>
    public RelayCommand UseMatchingTemplateCommand { get; }

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
        return new PageContentSource(template, records, _sourcePath ?? string.Empty, _textCase);
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
        // 兜底不再是「标准箱唛 100×80」：那是 M1 时代的框线分格模板，要 9 个字段，
        // 而厂牌表基本只有货号/件数/数量三列——用户每次打开看到四格黑框加两格空白，
        // 就变成“改了没变化”。行式四行才是真样张那一套。
        var keepId = preferId ?? SelectedTemplate?.Id ?? BuiltInTemplates.IdRowsFour;
        TemplateOptions.Clear();
        foreach (var template in _templateStore.ListAll()) TemplateOptions.Add(new TemplateOption(template));
        SelectedTemplate = TemplateOptions.FirstOrDefault(t => t.Id == keepId)
            ?? TemplateOptions.FirstOrDefault(t => t.Id == BuiltInTemplates.IdRowsFour)
            ?? TemplateOptions.FirstOrDefault();
    }

    /// <summary>
    /// ③ 步「条码」栏目点「加到当前模板」那一下：把一条条码元素装进当前模板并存盘。
    /// <para>内置模板不能直接改（改了下次启动会被种子覆盖），所以先另存成用户副本再动——
    /// 与「编辑模板…」同一条路，不开第二个写模板的入口。<see cref="TemplateStore.Save"/> 自己还会再过一道校验，
    /// 这里不放宽。</para>
    /// </summary>
    private (bool Ok, string Message) AddBarcodeElement(TemplateElement element, string description)
    {
        var current = SelectedTemplate?.Template;
        if (current is null) return (false, "还没选模板。");

        // 深拷统一走 CloneAsUserCopy（内置那份改不得，下次启动会被种子覆盖）；
        // 已经是用户模板就把 Id 改回来 → 原地覆盖那一份（文件名按名字算，名字没变就是同一个文件），不产副本。
        var target = current.BuiltIn
            ? current.CloneAsUserCopy(current.Name + "（带条码）")
            : current.CloneAsUserCopy(current.Name);
        if (!current.BuiltIn) target.Id = current.Id;
        // 一份模板只放一条码：重复点不该叠出三根来（要两条就再另存一份副本）。
        target.Elements.RemoveAll(e => e.Kind == ElementKind.Barcode);
        target.Elements.Add(element);

        var issues = TemplateValidator.Validate(target);
        if (issues.HasError())
            return (false, "校验拦下了（没存）：" + string.Join("；", issues.ErrorMessages()));

        var saved = _templateStore.Save(target);
        if (!saved.Saved)
            return (false, "没存进去：" + string.Join("；", saved.Issues.ErrorMessages()));

        ReloadTemplates(target.Id);
        Services.AppLog.Info($"③ 步加条码到模板「{target.Name}」：{description}");
        return (true, description + "；已存为模板「" + target.Name + "」并选中。要挪位置或改大小，去「编辑模板…」拖。");
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

    /// <summary><see cref="ILabelSource"/>：④ 步「按哪一列数张数」的候选 —— 表头原样，没导数据就是空清单。</summary>
    IReadOnlyList<string> ILabelSource.ColumnHeaders => _data?.Headers ?? Array.Empty<string>();

    /// <summary>
    /// <see cref="ILabelSource"/>：这一列连到了哪个内置字段（没连就是 null）。
    /// <para>因为连上了的那一列不会另存一份 <c>col:</c> 键，按列名取数时必须能退回这个字段，
    /// 否则金沐那种「件数 CTN 已连总件数」的列会被报成「没有值」。</para>
    /// </summary>
    MarkFieldKey? ILabelSource.FieldBoundToColumn(string column)
        => string.IsNullOrWhiteSpace(column) || _working is null
            ? null
            : _working.Mappings.FirstOrDefault(m => m.IsBound
                && string.Equals(m.ColumnHeader?.Trim(), column.Trim(), StringComparison.Ordinal))?.Field;

    /// <summary>
    /// 某个内置字段现在连的是表里哪一列（表头原样）；没连上返回 null。
    /// <para>AI 那条「货号里 * 后面要不要保留」要按这个改写占位符：保留 = 改成 <c>{{col:那一列}}</c> 读原样，
    /// 不保留 = 用 <c>{{ItemNo}}</c>（软件默认去掉 * 后那截，三家真样张都是这么印的）。</para>
    /// </summary>
    public string? ColumnBoundToField(MarkFieldKey field)
        => _working?.Mappings.FirstOrDefault(m => m.IsBound && m.Field == field)?.ColumnHeader;

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

    /// <summary>五步向导的后退 / 前进（到头时命令自己变灰，不让点出界）。</summary>
    public RelayCommand PrevStepCommand { get; }
    public RelayCommand NextStepCommand { get; }

    // ---------- 五步向导 ----------

    /// <summary>六个面板、五个步骤：数据核对与告警归到“输出”那一步，因为它是印前最后一道闸。</summary>
    private static readonly string[] StepTitlesField =
    {
        "① 导入数据",
        "② 连接字段",
        "③ 选模板",
        "④ 拼版与编号",
        "⑤ 核对与输出",
    };

    /// <summary>步骤条上那一排名字（界面上的步骤条与“第 x / 5 步”共用这一份，别在 XAML 里再写一遍）。</summary>
    public IReadOnlyList<string> StepTitles => StepTitlesField;

    /// <summary>
    /// 当前停在第几步（0 基，对应 <see cref="StepTitles"/>）。
    /// 以前这六块是一次全摊开的长滚动条，操作员得自己找“下一步在哪”，新版式藏在中部更是看不见；
    /// 现在一步只露一块，步骤条点哪块露哪块。
    /// </summary>
    public int StepIndex
    {
        get => _stepIndex;
        set
        {
            if (Set(ref _stepIndex, Math.Max(0, Math.Min(value, StepTitlesField.Length - 1))))
            {
                Raise(nameof(IsFirstStep));
                Raise(nameof(IsLastStep));
                Raise(nameof(StepHint));
            }
        }
    }

    public bool IsFirstStep => _stepIndex == 0;

    public bool IsLastStep => _stepIndex == StepTitlesField.Length - 1;

    /// <summary>底栏那行字，告诉操作员走到哪了、下一步该干什么。</summary>
    public string StepHint => $"第 {_stepIndex + 1} / {StepTitlesField.Length} 步 · {StepTitlesField[_stepIndex]}";

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
                BumpDataGeneration();
                RebuildLayout();
                Sheet.RebuildPlan();
                RebuildIssueLines();
                RememberTemplateId(value?.Id);
                Raise(nameof(TemplateSheetHint));
            }
        }
    }

    /// <summary>把模板 id 写进界面状态；与已记的相同就不写盘（启动那一次赋值不该产生 IO）。</summary>
    private void RememberTemplateId(string? templateId)
    {
        if (string.IsNullOrEmpty(templateId)) return;
        // 启动兜底不写盘（第 23 棒）：记的那个模板被删了时，旧代码会把兜底 id 存回去，
        // 用户状态文件里那条记录被静默顶掉——下回他真选过什么已经无从对起。
        if (_bootstrapping) return;
        // 自动接手那一次不算用户的选择：写盘会把他在第 3 步手工记的模板顶掉，
        // 下一张表进来又按数据说话，等于他用手工选的模板被一次自动换档静默覆盖了。
        if (_suppressTemplateRemember)
        {
            _suppressTemplateRemember = false;
            return;
        }
        var state = _uiState.Load();
        if (state.TemplateId == templateId) return;
        state.TemplateId = templateId;
        _uiState.Save(state);
    }

    private MarkTextCase _textCase = MarkTextCase.AsSource;

    /// <summary>
    /// 唛头文字的大小写口径（⑤ 步那个三档下拉：按表格里的 / 全部大写 / 全部小写）。
    /// <para>它只影响 <see cref="LayoutEngine"/> 合成后的文字：不改表里的数据、也不改模板存的内容，
    /// 所以换档不会弄脏任何人的原值。预览与五个出口都从同一个点取版面，不会漂成两套。</para>
    /// </summary>
    public MarkTextCase TextCase
    {
        get => _textCase;
        set
        {
            if (Set(ref _textCase, value))
            {
                RebuildLayout();
                RebuildIssueLines();
                RememberTextCase(value);
                StatusMessage = $"唛头文字已改为「{value.ChineseName()}」（预览与打印/PDF/图片/SVG 同一口径，表里的原值没动）。";
            }
        }
    }

    /// <summary>大小写口径写进界面状态；与已记的相同就不写盘（同 <see cref="RememberTemplateId"/> 的口径）。</summary>
    private void RememberTextCase(MarkTextCase value)
    {
        var state = _uiState.Load();
        if (state.TextCase == value) return;
        state.TextCase = value;
        _uiState.Save(state);
    }

    /// <summary>
    /// ⑤ 步「唛头文字」下拉的三项。名字从 Core 的 <see cref="MarkTextCaseExtensions.ChineseName"/> 取，
    /// 不在 XAML 里再手打一遍中文（两处各写一份早晚对不上）。
    /// </summary>
    public IReadOnlyList<ChoiceOption<MarkTextCase>> TextCaseOptions { get; } = new[]
    {
        MarkTextCase.AsSource, MarkTextCase.Upper, MarkTextCase.Lower,
    }.Select(v => new ChoiceOption<MarkTextCase>(v, v.ChineseName())).ToList();

    public ChoiceOption<MarkTextCase>? SelectedTextCase
    {
        get => TextCaseOptions.FirstOrDefault(o => o.Value == _textCase);
        set
        {
            if (value is not null) TextCase = value.Value;
        }
    }

    /// <summary>默认那一档：就是以前 XAML 里写死的 170，谁都没被改变。</summary>
    public const double DefaultPreviewTableHeight = 170;

    /// <summary>
    /// ① 步预览表可选的三档高度（用户 2026-09-08：「这个表格显示区太小了可以选择扩大」）。
    /// <para>为什么是三档而不是拖拽：那块表格在一个 <c>StackPanel</c> 里，StackPanel 给无限高，
    /// <c>GridSplitter</c> 放进去不生效；要拖就得先把整个步骤面板改成 Grid，首屏高度会跳。
    /// 想横向变宽另有左栏与右栏之间的 splitter。</para>
    /// </summary>
    public static readonly IReadOnlyList<(double Height, string Name)> PreviewTableHeights = new[]
    {
        (170.0, "小（四五行）"),
        (380.0, "中（一屏十几行）"),
        (620.0, "大（尽量多看）"),
    };

    private double _previewTableHeight = DefaultPreviewTableHeight;

    /// <summary>状态文件里那个数只能当候选：认不出（旧文件没这个字段 = 0，或三档被改过）就退回默认。</summary>
    private static double RememberedPreviewTableHeight(double stored)
    {
        foreach (var option in PreviewTableHeights)
            if (Math.Abs(option.Height - stored) < 1) return option.Height;
        return DefaultPreviewTableHeight;
    }

    /// <summary>① 步预览表当前高度（XAML 直接绑这个数）。</summary>
    public double PreviewTableHeight => _previewTableHeight;

    public IReadOnlyList<ChoiceOption<double>> PreviewTableHeightOptions { get; } =
        PreviewTableHeights.Select(p => new ChoiceOption<double>(p.Height, p.Name)).ToList();

    public ChoiceOption<double>? SelectedPreviewTableHeight
    {
        get => PreviewTableHeightOptions.FirstOrDefault(o => Math.Abs(o.Value - _previewTableHeight) < 1);
        set
        {
            if (value is null || Math.Abs(value.Value - _previewTableHeight) < 1) return;
            _previewTableHeight = value.Value;
            Raise(nameof(PreviewTableHeight));
            Raise(nameof(SelectedPreviewTableHeight));
            RememberPreviewTableHeight(value.Value);
            StatusMessage = $"① 步表格已改为「{value.Label}」（高 {value.Value:0} 像素）；嫌窄还可以拖左栏与右边之间那条分隔线。";
        }
    }

    private void RememberPreviewTableHeight(double height)
    {
        var state = _uiState.Load();
        if (Math.Abs(state.PreviewTableHeight - height) < 1) return;
        state.PreviewTableHeight = height;
        _uiState.Save(state);
    }

    /// <summary>
    /// 拆出来的 AI 浮动窗口上次摆在哪、多大（全 0 = 没记过，<see cref="Services.DetachablePanel"/> 会退回默认摆位）。
    /// <para>状态窗只存在这里：面板与搬移器都不该拿到整个 <see cref="UiStateStore"/>，那等于开后门改别的字段。</para>
    /// </summary>
    public (double Left, double Top, double Width, double Height)? LoadAiFloatGeometry()
    {
        var s = _uiState.Load();
        return s.AiFloatWidth > 0 && s.AiFloatHeight > 0 ? (s.AiFloatLeft, s.AiFloatTop, s.AiFloatWidth, s.AiFloatHeight) : null;
    }

    /// <summary>记下浮动窗口的位置与大小（只在收回/关窗那一次调，不跟着拖动写盘）。</summary>
    public void SaveAiFloatGeometry(double left, double top, double width, double height)
    {
        var s = _uiState.Load();
        if (Math.Abs(s.AiFloatLeft - left) < 1 && Math.Abs(s.AiFloatTop - top) < 1
            && Math.Abs(s.AiFloatWidth - width) < 1 && Math.Abs(s.AiFloatHeight - height) < 1) return;
        s.AiFloatLeft = left;
        s.AiFloatTop = top;
        s.AiFloatWidth = width;
        s.AiFloatHeight = height;
        _uiState.Save(s);
    }

    /// <summary>
    /// AI 那块上次停在哪个泊位，以及右栏上次多宽。
    /// <para>认不出的字符串（包括「没记过」那句空值）一律退回<strong>右栏</strong>：用户 2026-09-09
    /// 「调整到默认打开软件是左栏导数选模版 中栏是预览 右栏是AI 不要显示在中栏下面」，
    /// 而上一版的默认是底部那一行（那正是他被误伤的地方）。<strong>不拿旧状态文件拦启动</strong>，
    /// 但旧默认与新默认不同这件事得说清楚：只有「没记过 / 认不出」才走右栏，他真存过的 <c>"Bottom"</c> 仍是底部（那一栏另有搬家规则，见 <c>DockSnap.ReconcileRightPane</c>）。</para>
    /// </summary>
    public (DockSite Site, double RightWidth) LoadAiDock()
    {
        var s = _uiState.Load();
        // Enum.IsDefined 与 ParsePaneMode 同一个理由（第 23 棒）：TryParse 对「9」这种没定义的数字也返回 true。
        var site = Enum.TryParse<DockSite>(s.AiDockSite, ignoreCase: true, out var parsed)
                   && parsed != DockSite.Float
                   && Enum.IsDefined(parsed)
            ? parsed
            : DockSite.Right;
        return (site, s.AiRightColumnWidth);
    }

    /// <summary>记下泊位与右栏宽度（拖完/点完那一次调，不跟着分隔条每像素写盘）。</summary>
    public void SaveAiDock(DockSite site, double rightWidth)
    {
        if (site == DockSite.Float) return;     // 浮动不存：下次启动不该莫名多开一个窗口
        var s = _uiState.Load();
        var name = site.ToString();
        if (string.Equals(s.AiDockSite, name, StringComparison.Ordinal)
            && Math.Abs(s.AiRightColumnWidth - rightWidth) < 1) return;
        s.AiDockSite = name;
        s.AiRightColumnWidth = rightWidth;
        _uiState.Save(s);
    }

    /// <summary>读左右两栏各自停在哪一态（第 18 棒：收起与关闭都得留到下次启动）。没记过/认不出 = 展开。</summary>
    public (PaneMode Left, PaneMode Right) LoadPaneModes()
    {
        var s = _uiState.Load();
        return (ParsePaneMode(s.LeftPaneMode), ParsePaneMode(s.RightPaneMode));
    }

    /// <summary>按名字认，认不出退回展开——与 <c>AiDockSite</c> 同一个口径。再多校一道 <c>Enum.IsDefined</c>：
    /// <c>Enum.TryParse</c> 对「99」这种没定义的数字也返回 true（越界的枚举值要到用的时候才露馅）。</summary>
    private static PaneMode ParsePaneMode(string raw)
        => Enum.TryParse<PaneMode>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : PaneMode.Open;

    /// <summary>写下两栏形态（按一下那格按钮就写一次，不跟着布局每像素写盘）。</summary>
    public void SavePaneModes(PaneMode left, PaneMode right)
    {
        var s = _uiState.Load();
        var l = left.ToString();
        var r = right.ToString();
        if (string.Equals(s.LeftPaneMode, l, StringComparison.Ordinal)
            && string.Equals(s.RightPaneMode, r, StringComparison.Ordinal)) return;
        s.LeftPaneMode = l;
        s.RightPaneMode = r;
        _uiState.Save(s);
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

    /// <summary>当前这张表是按哪份指令切的（界面与 AI 面板要能说清「这不是自动猜的那一份」）。</summary>
    public SheetLayoutChoice CurrentChoice => _choice;

    /// <summary>原表总行数（含被跳过的与被剔的）——AI 报行号时的合法上界。</summary>
    public int RawRowCount => _data?.RawRowCount ?? 0;

    /// <summary>软件目前把表头认在原表第几行（1 起）；0 = 还没导数据，-1+1=0 = 按指令当它没表头。</summary>
    public int DetectedHeaderRow => _data is null ? 0 : _data.HeaderRowIndex + 1;

    /// <summary>这台机器上真有的纸规名（内置 + 用户自建）。AI 只能从这份清单里点名，造不出新纸规。</summary>
    public IReadOnlyList<string> SheetSpecNames => Sheet.SheetOptions.Select(o => o.Spec.Name).ToList();

    /// <summary>
    /// 数一遍「共几枚标签、共几张纸」——AI 那五行里的「预览」用软件自己算的数，<strong>不信模型报的那一个</strong>。
    /// <para>用户 2026-09-09 要的那句是「预览:31个模板,155张」：31 = 切完还剩几行货，
    /// 155 = 按他指定的那一列（件数）逐行加出来。模型说 155 而表里加出 160 时，上屏的必须是真数——
    /// 报错一个总数就是少印或多印一垛箱子。</para>
    /// </summary>
    /// <param name="qtyColumnName">按哪一列数张数（表头原样）；null 或不在这张表里则一行算一张。</param>
    public (int Labels, int Sheets)? CountOutput(string? qtyColumnName)
    {
        if (_data is not { } data) return null;
        var colIndex = -1;
        if (!string.IsNullOrWhiteSpace(qtyColumnName))
        {
            for (var c = 0; c < data.Headers.Count; c++)
            {
                if (string.Equals(data.Headers[c].Trim(), qtyColumnName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    colIndex = c;
                    break;
                }
            }
        }
        var sheets = 0;
        for (var r = 0; r < data.RowCount; r++)
        {
            var n = 1;
            if (colIndex >= 0 && int.TryParse(data.GetCell(r, colIndex)?.Trim(), out var v) && v > 0) n = v;
            sheets += n;
        }
        return (data.RowCount, sheets);
    }

    /// <summary>
    /// 换一份切表指令重读这张表（唯一能改「表头在哪、哪几行不当数据」的入口）。
    /// <para><strong>失败必须退回原样</strong>：这是一张能用的表被改坏的唯一机会，
    /// 宁可拒绝指令，也不能让用户面对一个 0 行或表头错位的工作区。</para>
    /// </summary>
    public (bool Ok, string Message) ApplySheetChoice(SheetLayoutChoice next)
    {
        if (string.IsNullOrWhiteSpace(_sourcePath)) return (false, "还没导入任何表，没有可改切法的对象。");
        var previous = _choice;
        var previousSheet = _data?.SheetName ?? SelectedSheet;
        try
        {
            _choice = next;
            LoadSource(_sourcePath, previousSheet);
            return (true, _data?.Describe() ?? "已按新切法重读这张表。");
        }
        catch (Exception ex)
        {
            _choice = previous;
            try { LoadSource(_sourcePath, previousSheet); } catch { /* 连原样都读不回来就是文件本身变了，不拿这句话骗人 */ }
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 把一份 AI 提案合到当前切法上（没提的那一项保持不动）。
    /// <para>三个细则：① 行号一律按<strong>原表行号 1 起</strong>进来，这里换成 0 起并夹在合法区间；
    /// ② 表头那一行永远不能同时被当合计行剔掉；③ 剔除行取<strong>并集</strong>而不是覆盖——
    /// 上一轮剔掉的行这一轮模型不会再提（它看到的表已经没那几行了），覆盖会把它们静默放回来，
    /// 等于用户点两次「用这个」反而多印几张。</para>
    /// </summary>
    public SheetLayoutChoice ChoiceFrom(LabelGou.Core.Recognition.AiSheetProposal p)
    {
        var raw = Math.Max(1, RawRowCount);
        int? header = p.HasHeader == false ? 0
            : p.HeaderRow is int hr ? Math.Clamp(hr - 1, 0, raw - 1)
            : _choice.HeaderRowIndex;
        var rows = new List<int>(_choice.ExcludedRawRows ?? Array.Empty<int>());
        foreach (var r in p.TotalValueRows)
        {
            if (r < 1 || r > raw) continue;
            if (header is int h && r - 1 == h) continue;
            if (!rows.Contains(r - 1)) rows.Add(r - 1);
        }
        rows.Sort();
        return new SheetLayoutChoice(header, p.HasHeader ?? _choice.HasHeader, rows.Count == 0 ? null : rows);
    }

    /// <summary>回到「软件自动猜表头、不剔行」的那一份切法（用户说「改错了，恢复」时走这条）。</summary>
    public (bool Ok, string Message) ResetSheetChoice() => ApplySheetChoice(SheetLayoutChoice.Auto);

    /// <summary>
    /// 读一份表（<paramref name="path"/> 为 null/空白时什么都不做）。对话框、重选工作表、
    /// 以后的“把文件拖到窗口上”都走这一个入口，单测也直接拿它喂数据。
    /// </summary>
    public void LoadSource(string? path, string? sheet)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        // 换文件 = 切表指令作废的那只手：上一张表剔的「第 412 行」对新表毫无意义（拿它切新表就是切错行）。
        if (!string.Equals(_sourcePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            _choice = SheetLayoutChoice.Auto;
        try
        {
            var data = TableImporter.Import(path, sheet, _choice);
            _data = data;
            BumpDataGeneration();
            SourcePath = data.SourceFile;

            Sheets.Clear();
            foreach (var name in TableImporter.ListSheets(data.SourceFile)) Sheets.Add(name);
            _selectedSheet = data.SheetName;
            Raise(nameof(SelectedSheet));

            // Describe() 自己会报表头行（没表头时说的是另一句话），不再在这儿拼一遍拼错的
            HeaderInfoText = data.Describe() +
                             (data.Encoding is not null ? $" · 编码 {data.Encoding.WebName}" : string.Empty) +
                             (data.Choice.ExcludedCount > 0 ? $" · 已按指令剔除 {data.Choice.ExcludedCount} 行" : string.Empty);

            BuildPreviewTable(data);
            ColumnOptions = new ObservableCollection<ColumnOption> { new(-1, "（不映射）") };
            for (var c = 0; c < data.ColumnCount; c++)
            {
                ColumnOptions.Add(new ColumnOption(c, $"{HeaderRowDetector.ColumnLetter(c)} · {data.Headers[c]}"));
            }
            Raise(nameof(ColumnOptions));
            // ③ 步那个条码栏目列的是「这张表真有的列」，所以换表之后必须重列一次。
            Barcode.RefreshSources();

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
            PickTemplateFittingData();
            AdvanceAfterImport();
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(ex.Message);
            StatusMessage = "打开失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 导入成功后把界面推到“连接字段”那一步：自动连接已经做完了，但要用户看一眼连对没有——
    /// 这块面板以前在长滚动条中间，用户导完表根本没瞧见，才会说“导入文件没绑定列”。
    /// </summary>
    private void AdvanceAfterImport()
    {
        if (StepIndex == 0) StepIndex = 1;
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
        // 整批固定值不来自列绑定，上面的循环带不回来，只能从旧方案里接——否则改一个下拉就把用户填的 BOLAROM 抹了
        if (_working is not null)
        {
            foreach (var (key, text) in _working.FixedValues) profile.FixedValues[key] = text;
        }

        foreach (var row in FieldRows)
        {
            if (row.ColumnIndex >= 0) profile.Bind(row.Definition.Key, row.ColumnIndex, data.Headers);
        }
        return profile;
    }

    /// <summary>② 区「整批固定值…」对话框的一行：字段名 + 它有没有表格列 + 当前固定值。</summary>
    public sealed class FixedValueRow : INotifyPropertyChanged
    {
        public required FieldDefinition Definition { get; init; }

        /// <summary>该字段已连到表格列：固定值只在单元格空着时兜底，不覆盖表里的值。</summary>
        public bool HasColumn { get; init; }

        private string _value = string.Empty;

        /// <summary>
        /// 固定值本体。必须带变更通知：上一版它是个普通自动属性，
        /// 点「清空全部」后模型空了、框里还顶着旧值（所见非所得）。
        /// </summary>
        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        // 下面三个只服务 FixedValuesWindow 的 DataTemplate：没列的字段才是这个对话框的主角，所以加重、提蓝。
        public string Hint => HasColumn ? "已连表格列，仅在该列空着时兜底" : "表里没这一列，整批共用";

        public FontWeight NameWeight => HasColumn ? FontWeights.Normal : FontWeights.SemiBold;

        public Brush HintBrush => new SolidColorBrush(HasColumn
            ? Color.FromRgb(0x88, 0x8F, 0x96)
            : Color.FromRgb(0x1D, 0x4E, 0xD8));
    }

    public IReadOnlyList<FixedValueRow> BuildFixedValueRows()
    {
        var profile = _working;
        return FieldRows.Select(row => new FixedValueRow
        {
            Definition = row.Definition,
            HasColumn = row.ColumnIndex >= 0,
            Value = profile?.FixedValueFor(row.Definition.Key) ?? string.Empty,
        }).ToList();
    }

    /// <summary>
    /// 对话框确认：写回方案并立刻重算，预览当场能看到行式模板的第一行有了。
    /// <para>这里故意为接 <see cref="ApplyMapping"/>，因为它走 <c>CollectProfile</c>（已带住旧固定值），
    /// 而不是另开一条重算路径——两条算法将来一定会飘。</para>
    /// </summary>
    public void ApplyFixedValues(IReadOnlyList<FixedValueRow> rows)
    {
        var profile = _working;
        if (profile is null) return;
        foreach (var row in rows) profile.SetFixedValue(row.Definition.Key, row.Value);
        var filled = profile.FixedValues.Count;
        ApplyMapping();
        StatusMessage = filled == 0
            ? "整批固定值已清空，表里没有的列回到不印。"
            : $"已记下 {filled} 个整批固定值（表里没这些列，整批共用，改方案才会变）。";
    }

    /// <summary>
    /// 整张表的画像（<strong>含没连上字段的列</strong>），给 AI 排版当第一份材料。
    /// <para>以前只把「已连上的字段」递给模型，于是 ① 自动绑定猜错了它无从纠正
    /// ② 流水号 / 每箱品名这类没连上的列对它干脆不存在 ③ 一张全没连上的表（TOP 那种）直接把它挡在门外
    /// （用户 2026-09-08：「即使我把正确的排版给它，它也按表格的来」）。这里只摊事实，不替模型裁决。</para>
    /// </summary>
    public (IReadOnlyList<ColumnPortrait> Columns, string Portrait)? BuildTablePortrait()
    {
        var data = _data;
        if (data is null) return null;
        var columns = TablePortrait.Build(data, _working);
        // 前导批注行与贴图也一并摊出去（第 20 棒）：纸规常写在表头以上，样张常贴在右侧，
        // 不递过去模型就只能凭列名猜，而用户 2026-09-09 定的主路径是「AI 自己看这张表」。
        return (columns, TablePortrait.Describe(columns, data.RowCount, data.Preamble, data.Images));
    }

    /// <summary>这张表里贴着的图（模板截图 / 效果照片）。CSV 与没图的表是空表。</summary>
    public IReadOnlyList<SheetImage> SheetImages => _data?.Images ?? Array.Empty<SheetImage>();

    private void AutoSuggest()
    {
        var data = _data;
        if (data is null) return;

        var previousFixed = _working is null ? null : new Dictionary<string, string>(_working.FixedValues);
        _working = MappingSuggester.Suggest(data.Headers,
            string.IsNullOrWhiteSpace(ProfileName) ? "自动匹配方案" : ProfileName.Trim());
        // 重接列不该抹掉已经填好的整批固定值（它们与列无关）
        if (previousFixed is not null)
        {
            foreach (var (key, text) in previousFixed) _working.FixedValues[key] = text;
        }
        AutoNumberCartons = _working.AutoNumberCartons;
        RebuildFieldRows();
        ApplyMapping();
        // 以前这里无条件说「已按表头别名重新自动连接」，连上 0 个也是这句（用户 2026-09-08 拿 TOP 那张
        // 全（不映射）的截图问我们为什么骗人）。现在报真数，并在一个都没连上时把当表头用的那行摊出来：
        // 那张表根本没有表头，不是用户没点推荐。
        var bound = _working.BoundCount;
        // 表头那一串只在「一个都没连上」时才需要，而且 _data 此时理论上可能已被清（取消导入那条路），所以可空取。
        var headerHint = _data is null ? string.Empty : string.Join(" / ", _data.Headers.Take(6));
        StatusMessage = bound switch
        {
            0 => "自动连接一个字段都没连上" +
                 (headerHint.Length == 0 ? string.Empty : $"（表头认的是「{headerHint}」这些值）") +
                 "—— 这张表可能根本没有表头行，或列名不常见；请对着下面那列自己选，别信这一句。",
            1 => "自动连接只连上 1 个字段，其余请在第 2 步自己挑。",
            _ => $"自动连接连上 {bound} 个字段，没连上的那一格会印成空白，请第 2 步过一眼。",
        };
    }

    private static readonly System.Text.RegularExpressions.Regex FieldTokenPattern =
        new(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary><c>{{col:列标题}}</c> 这种直取列的写法（上一版的 <see cref="FieldTokenPattern"/> 认不到它，
    /// 注释里又假设「col: 永远有值」——而 <c>LayoutEngine</c> 碰到表里没这个列是会留空白的）。</summary>
    private static readonly System.Text.RegularExpressions.Regex ColTokenPattern =
        new(@"\{\{\s*col\s*:\s*([^{}]+?)\s*\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 模板缺值的提醒（派生值：换模板、改映射都会让它变，不该由某一次操作把句子钉死）。
    /// <para>用户圈的「字没填过来」就是这个：金沐那张表的客户名 BOLAROM 不在任何一列里，只能靠整批固定值，
    /// 但入口藏在一个按钮里没人看见。这里不猜他该填什么，只把缺哪几项、去哪儿补直接说出来。</para>
    /// </summary>
    private IEnumerable<string> MissingValueLines()
    {
        var profile = _working;
        var template = SelectedTemplate?.Template;
        if (profile is null || template is null) yield break;

        foreach (var key in FieldsUsedBy(template))
        {
            if (HasValueFor(key, profile)) continue;
            yield return $"模板要用的「{MarkFieldCatalog.Get(key).ChineseName}」既没连到列也没填固定值，那一格会印成空白 —— 在第 2 步点「整批固定值…」补上。";
        }

        foreach (var column in ColumnsUsedBy(template))
        {
            if (!HasComputedColumn(column)) continue;
            yield return $"模板用了 {{{{col:{column}}}}}，但算完编号后没有一张标签带这个量 —— 那一行会整条不印（表里没这一列，也不是编号引擎会补的量）。";
        }
    }

    /// <summary>
    /// 这批标签里到底有没有 <c>col:某名</c> 这个量。
    /// <para>拿渲染端真正会查的东西说事，而不是去查表头：<c>col:组内序</c>、<c>col:本行箱数</c> 这类是
    /// 编号引擎补的计算量，表里本来就没有这一列，按表头判会误报一堆缺值。</para>
    /// </summary>
    private bool HasComputedColumn(string column)
    {
        if (_records.Count == 0) return true;   // 还没编号，谈不上缺不缺
        var key = "col:" + column;
        return _records.Any(r => (r.GetCustom(key)?.Text ?? string.Empty).Trim().Length > 0);
    }

    /// <summary>这份模板的文字里用到哪些字段（<c>{{col:…}}</c> 那种直取列的另算，见 <see cref="ColumnsUsedBy"/>）。
    /// <para>条码也算：它的数据同样是从某一列取的（第 17 棒），不把它算进来，
    /// 「还差哪几项」就会漏说「条码那一列没值」。</para></summary>
    private static List<MarkFieldKey> FieldsUsedBy(LabelTemplate? template)
    {
        var used = new List<MarkFieldKey>();
        if (template is null) return used;
        foreach (var element in template.Elements)
        {
            if (!CarriesTokens(element) || string.IsNullOrEmpty(element.Text)) continue;
            foreach (System.Text.RegularExpressions.Match m in FieldTokenPattern.Matches(element.Text))
            {
                if (!MarkFieldCatalog.TryParseKey(m.Groups[1].Value, out var key)) continue;
                if (!used.Contains(key)) used.Add(key);
            }
        }
        return used;
    }

    /// <summary>文本与条码共用同一套占位符，所以「这份元素吃不吃字段」也是一句判据，不在两处各写一遍。</summary>
    private static bool CarriesTokens(TemplateElement element)
        => element.Kind is ElementKind.Text or ElementKind.Barcode;

    /// <summary>这份模板直取了哪几个列标题（去重）。</summary>
    private static List<string> ColumnsUsedBy(LabelTemplate? template)
    {
        var used = new List<string>();
        if (template is null) return used;
        foreach (var element in template.Elements)
        {
            if (!CarriesTokens(element) || string.IsNullOrEmpty(element.Text)) continue;
            foreach (System.Text.RegularExpressions.Match m in ColTokenPattern.Matches(element.Text))
            {
                var name = m.Groups[1].Value.Trim();
                if (name.Length > 0 && !used.Contains(name, StringComparer.OrdinalIgnoreCase)) used.Add(name);
            }
        }
        return used;
    }

    /// <summary>
    /// 这份模板被这批数据填上了几成：命中率 = 填得上的引用数 / 模板总引用数（字段 + 直取列）。
    /// <para>上一版比的是「命中数」的绝对值，于是字段越多越容易赢——九格标准箱唛永远压住行式四行，
    /// 就是用户圈过的那件事。平手时取引用数更少的那个（少而贴比多而空好）。</para>
    /// </summary>
    private (double Rate, int References) TemplateFitScore(LabelTemplate template, MappingProfile profile)
    {
        var fields = FieldsUsedBy(template);
        var columns = ColumnsUsedBy(template);
        var total = fields.Count + columns.Count;
        if (total == 0) return (0, 0);
        var hit = fields.Count(k => HasValueFor(k, profile))
            + columns.Count(HasComputedColumn);
        return (hit / (double)total, total);
    }

    /// <summary>
    /// 导入完一张表后，按「这张表能把这套模板填多满」挑一套最贴合的。
    /// <para>为什么不能只在“当前模板完全印不出”时才接手：用户 uistate 里记着 M1 时代的
    /// 「标准箱唛 100×80」，那张表里只要货号连上了就算“印得出”，于是一直是那四格粗黑框在眼前，
    /// 他看的根本不是真样张那一套 —— 这就是“改了还是没变化”的直接原因。</para>
    /// <para>只在导入时接手（不在每次改映射时接手）：用户手工选过的模板不能被他改一个字段就被顶掉，
    /// 但下一张表进来就该重新按数据说话。</para>
    /// </summary>
    private void PickTemplateFittingData()
    {
        var profile = _working;
        if (profile is null || TemplateOptions.Count == 0) return;

        var scored = TemplateOptions
            .Select(o => (Option: o, Fit: TemplateFitScore(o.Template, profile)))
            .Where(t => t.Fit.References > 0)
            .OrderByDescending(t => t.Fit.Rate)
            .ThenBy(t => t.Fit.References)
            .ToList();
        if (scored.Count == 0) return;
        var current = scored.FirstOrDefault(t => t.Option.Id == SelectedTemplate?.Id);
        var best = scored[0];
        var currentRate = current.Option.Id == SelectedTemplate?.Id ? current.Fit.Rate : 0;
        if (best.Option.Id == SelectedTemplate?.Id || best.Fit.Rate <= currentRate) return;

        var name = SelectedTemplate?.Name ?? "原模板";
        _suppressTemplateRemember = true;
        SelectedTemplate = best.Option;
        StatusMessage = $"这张表能把「{best.Option.Name}」填到 {best.Fit.Rate:P0}（比「{name}」贴），已自动改用前者；在第 3 步可以随时换回。";
        RebuildIssueLines();
    }

    /// <summary>
    /// 这个字段现在有没有值：连到了列，或填了整批固定值。
    /// <para>件号与总件数除外——那两格由编号引擎每次都补上（沿用数据、缺项才按规则补），
    /// 让用户去为它们填整批固定值只是把人支到一条白跑的道上。</para>
    /// </summary>
    private bool HasValueFor(MarkFieldKey key, MappingProfile profile)
        => key is MarkFieldKey.CartonNo or MarkFieldKey.CartonTotal
        || FieldRows.Any(r => r.Definition.Key == key && r.ColumnIndex >= 0)
        || !string.IsNullOrWhiteSpace(profile.FixedValueFor(key));

    private void ApplyMapping()
    {
        var data = _data;
        var profile = CollectProfile();
        if (data is null || profile is null) return;

        _working = profile;
        var result = RecordMapper.Map(data, profile);
        _rawRecords = result.Records;
        _mappingIssues = result.Issues;
        BumpDataGeneration();

        _mapIssueLines = new List<string>();
        foreach (var issue in result.Issues.Take(200))
        {
            var fieldName = issue.Field is null ? "整行" : MarkFieldCatalog.Get(issue.Field.Value).ChineseName;
            _mapIssueLines.Add($"第 {issue.RowNumber} 条 · {fieldName}：{issue.Message}");
        }
        RebuildIssueLines();

        AiGateBlocked = _rawRecords.Any(r => r.PendingReview().Any());
        Raise(nameof(RecordTotal));
        Raise(nameof(HasData));

        var boundCount = profile.BoundCount;
        var suffix = result.Issues.Count == 0 ? "无告警" : $"{result.Issues.Count} 条告警，建议核对";
        StatusMessage = $"已连接 {boundCount} 个字段，共 {_rawRecords.Count} 条唛头记录 · {suffix}";

        // 模板缺哪几项不再拼进这一句：它是派生值，拼一次就会被后面的换模板/改映射钉成陈话。
        // 它现在住在 IssueLines 里（第 2 步那块橙色区），由 RebuildIssueLines 统一重列。

        // 件号交给 M2 编号引擎统一处理（它会回贴标签集并触发重算）
        Sheet.RefreshFromSource();
    }

    /// <summary>重列第 2 步那块提示：映射告警 + 当前模板的缺值提醒（派生值，换模板也要跟着变）。</summary>
    private void RebuildIssueLines()
    {
        IssueLines.Clear();
        foreach (var line in _mapIssueLines) IssueLines.Add(line);
        foreach (var line in MissingValueLines()) IssueLines.Add(line);
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
        foreach (var (key, text) in saved.FixedValues) rebound.FixedValues[key] = text;
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
            new LayoutContext(effectiveIndex, total, Path.GetFileName(_sourcePath),
                IncludeReference: true, TextCase: _textCase));
    }

    /// <summary>调试/自动化用：当前标签记录集（已按编号规则展开）。</summary>
    public IReadOnlyList<MarkRecord> CurrentRecords => _records;

    /// <summary>调试/自动化用：未展开的原始记录集（Excel 一行一条）。</summary>
    public IReadOnlyList<MarkRecord> RawRecords => _rawRecords;

    /// <summary>调试/自动化用：当前告警。</summary>
    public IReadOnlyList<MappingIssue> CurrentIssues => _mappingIssues;

    // ---------- M6：识别结果接入 ----------

    /// <summary>状态栏文案（识别这类长流程由界面回报进度；业务分支自己改它）。</summary>
    public void ReportStatus(string message) => StatusMessage = message;

    /// <summary>
    /// 把核对完的识别记录接进来当数据源。<b>表格链路随之停用</b>，直到用户重新「打开数据文件」。
    /// <para>为什么是替换而不是追加：一条唛头记录只能有一个来源，混着 Excel 与识别结果会让
    /// 「这条数据是谁给的」这个问题失去答案，而 M3 的打印闸门、事后追责都要问它。</para>
    /// <para>未确认的字段已经带着 <see cref="MarkValue.NeedsReview"/> 写进记录，所以接进来之后
    /// <see cref="AiGateBlocked"/> 会自己亮起来 —— 这里不再判第二遍。</para>
    /// </summary>
    public void AdoptRecognizedRecords(IReadOnlyList<MarkRecord> records, string sourceDescription)
    {
        if (records is null || records.Count == 0) return;

        _data = null;
        _working = null;
        BumpDataGeneration();
        PreviewTable = null;
        Raise(nameof(PreviewTable));

        ColumnOptions = new ObservableCollection<ColumnOption>();
        Raise(nameof(ColumnOptions));
        FieldRows.Clear();
        Sheets.Clear();
        _selectedSheet = null;
        Raise(nameof(SelectedSheet));
        IssueLines.Clear();

        _rawRecords = records;
        _mappingIssues = Array.Empty<MappingIssue>();
        SourcePath = null;      // 记录不再来自某个表格文件；数据路径框留空，说明写在 HeaderInfoText 里

        AiGateBlocked = _rawRecords.Any(r => r.PendingReview().Any());
        HeaderInfoText = $"记录来自智能识别：{sourceDescription}（{records.Count} 条）· 表格映射已暂停";
        StatusMessage = AiGateBlocked
            ? $"已接入 {records.Count} 条识别记录，其中仍有未核对字段 —— 打印会被闸门拦下，请回到核对窗口确认。"
            : $"已接入 {records.Count} 条识别记录（全部已人工核对），可以预览与打印。";

        Raise(nameof(RecordTotal));
        Raise(nameof(HasData));
        Sheet.RefreshFromSource();
        AppLog.Info($"接入识别记录 {records.Count} 条：{sourceDescription}");
    }
}
