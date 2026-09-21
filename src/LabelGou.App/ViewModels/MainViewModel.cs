using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using LabelGou.App.Export;
using LabelGou.App.Mvvm;
using LabelGou.App.Services;
using LabelGou.Core;
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

/// <summary>
/// 首页海报墙的一张卡（第 97 棒）：一份模板 + 拿内置样例排出来的那张纸。
/// <para><strong>为什么卡里放的是真排版而不是示意图</strong>：概念稿那句「每张卡就是模板排出来的真预览」
/// 是硬要求——画法唯一，卡上一个样、纸上另一个样就是第 73 棒要防的那件事。所以这里收的是
/// <see cref="LayoutEngine.Build"/> 的产物，与单标签预览、出纸走同一个入口。</para>
/// </summary>
public sealed class PosterCard : INotifyPropertyChanged
{
    private bool _isSelected;

    public PosterCard(LabelTemplate template, LabelLayout layout)
    {
        Template = template;
        Layout = layout;
    }

    public LabelTemplate Template { get; }

    public LabelLayout Layout { get; }

    public string Id => Template.Id;

    public string Name => Template.Name;

    public string SizeText => $"{Template.WidthMm:0.#} × {Template.HeightMm:0.#} mm";

    public string BadgeText => Template.BuiltIn ? $"{SizeText} · 内置" : SizeText;

    /// <summary>是不是当前选中的那份模板（高亮那一圈边）。由 <see cref="MainViewModel"/> 统一刷，卡不自己记。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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
public sealed partial class MainViewModel : ObservableObject, ILabelSource
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

    /// <summary>
    /// 他从文件对话框里挑的那一份<strong>原件</strong>的路径（<see cref="_sourcePath"/> 是软件自己暂存的那份副本）。
    /// <para>两件事必须拿原件说：① 「这是不是一张新表」的判据（副本每暂存一次换一个时间戳目录）；
    /// ② 下次打开文件对话框该摆在哪个文件夹——拿副本的目录会把人带进 %APPDATA% 里。</para>
    /// </summary>
    private string? _sourceOriginalPath;
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

    /// <summary>
    /// 这一次换模板<strong>不</strong>连带换纸（第 68 棒，只顶一次）。
    /// <para>用它的只有 ④ 步那句错配提示旁的「换成配套模板」：那个动作是拿模板去就他手上这张纸，
    /// 换完再按模板把纸搬走，等于把他刚点下的修复撤销一遍。</para>
    /// </summary>
    private bool _suppressSheetFollow;

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

    /// <summary>模板写死文字与这批货对不上号的告警（第 24 棒，活账 A-1）：② 步橙色区列它，⑤ 闸门拿它拦一次。</summary>
    private IReadOnlyList<string> _templateCautions = Array.Empty<string>();

    /// <summary>上一次算出的字面量告警（与 IssueLines 同源；ExportViewModel 取这一份拼闸门文案）。</summary>
    public IReadOnlyList<string> TemplateCautions => _templateCautions;

    public MainViewModel() : this(uiState: null)
    {
    }

    /// <param name="uiState">界面状态；单测传临时目录那份，不往用户机器上写（§五-48）。</param>
    public MainViewModel(UiStateStore? uiState)
    {
        _uiState = uiState ?? new UiStateStore();
        OpenFileCommand = new RelayCommand(OpenFile);
        ReloadSheetCommand = new RelayCommand(() => LoadSource(_sourcePath, SelectedSheet, newTable: false));
        AutoSuggestCommand = new RelayCommand(AutoSuggest, () => _data is not null);
        ApplyMappingCommand = new RelayCommand(ApplyMapping, () => _data is not null);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => _data is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedSavedProfile is not null);
        FirstRecordCommand = new RelayCommand(() => CurrentIndex = 1, () => RecordTotal > 0);
        PrevRecordCommand = new RelayCommand(() => CurrentIndex = Math.Max(1, CurrentIndex - 1), () => CurrentIndex > 1);
        NextRecordCommand = new RelayCommand(() => CurrentIndex = Math.Min(RecordTotal, CurrentIndex + 1), () => CurrentIndex < RecordTotal);
        LastRecordCommand = new RelayCommand(() => CurrentIndex = RecordTotal, () => RecordTotal > 0);
        SamplePreviewCommand = new RelayCommand(() => CurrentIndex = 0);
        HealthFixAllCommand = new RelayCommand(ApplyAllHealthFixes, () => HealthRows.Any(r => r.CanFix));
        HealthFixOneCommand = new RelayCommand(row => ApplyHealthFix(row as HealthRow),
            row => row is HealthRow r && r.CanFix);
        HealthUndoCommand = new RelayCommand(UndoHealthFix, () => CanUndoHealthFix);
        ZoomInCommand = new RelayCommand(() => Zoom = Math.Min(8, Zoom * 1.25));
        ZoomOutCommand = new RelayCommand(() => Zoom = Math.Max(0.2, Zoom / 1.25));
        // 第 89 棒③：一览一行摆几格。－ 是放大（每行少一格），＋ 是缩小（多一格，到默认十格为止）。
        RowThumbCellsFewerCommand = new RelayCommand(() => RowThumbCellsPerRow--);
        RowThumbCellsMoreCommand = new RelayCommand(() => RowThumbCellsPerRow++);
        InitRowCheckTools();

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
        // 首页海报墙那些卡（第 97 棒）：等大小写口径接回来了再排——卡上那张纸要跟他下次真打时看到的一致。
        RebuildPosterCards();
        // ① 步表格高度接回上次选的那档（没记过、或记的不是三档里的数，用默认）。同样直接写字段不走 setter。
        _previewTableHeight = RememberedPreviewTableHeight(remembered.PreviewTableHeight);
        // 运行模式（第 30 棒）：没记过就是 AI 模式（用户 2026-09-10 定的默认）。直接写字段，理由同上。
        _mode = RememberedMode(remembered.RunMode);
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
        _suppressSheetFollow = true;      // 这一步是拿模板来就纸，别再按模板把纸搬走（第 68 棒）
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
        return new PageContentSource(template, records, _sourcePath ?? string.Empty, _textCase,
            record => SnapshotOf(record) is { } snap ? new LabelSnapshot(snap.Template, snap.UnreviewedValueCount) : null,
            RowSnapshotNotes);
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
        RebuildPosterCards();
    }

    /// <summary>首页海报墙那些卡（第 97 棒）。集合不换，只重算内容——绑 <c>ItemsSource</c> 的不用额外通知。</summary>
    public ObservableCollection<PosterCard> PosterCards { get; } = new();

    /// <summary>
    /// 每张卡都拿同一份内置样例走一遍 <see cref="LayoutEngine.Build"/>：卡上那张纸和纸上印的那张
    /// 出自同一个入口（画法唯一）。参考底图按<strong>出纸</strong>那一档关掉——卡要对的是"打出来长什么样"。
    /// </summary>
    private void RebuildPosterCards()
    {
        PosterCards.Clear();
        var sample = SampleRecords.StandardSample();
        foreach (var option in TemplateOptions)
        {
            var layout = LayoutEngine.Build(option.Template, sample,
                new LayoutContext(1, 1, string.Empty, IncludeReference: false, TextCase: _textCase));
            if (layout is not null) PosterCards.Add(new PosterCard(option.Template, layout));
        }
        RefreshPosterSelection();
    }

    /// <summary>选中那一张的高亮跟着 <see cref="SelectedTemplate"/> 走——状态只有一份，卡不自己记。</summary>
    private void RefreshPosterSelection()
    {
        var picked = SelectedTemplate?.Id;
        foreach (var card in PosterCards) card.IsSelected = card.Id == picked;
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
        RebuildRowSheets();            // 行检查的行表跟着标签集走（第 69 棒）
        CurrentIndex = labels.Count > 0 ? 1 : 0;
        Raise(nameof(RecordTotal));
        RebuildLayout();
        RebuildRowThumbs();
    }

    // ---------- 单张定稿（第 73 棒）----------

    /// <summary>
    /// 某一行那张纸自己的版式。用户 2026-09-16 的口径：「把那张纸和原本列表代替符切开，
    /// 显示的就是那张那行的内容，修改后对其他没影响」，又补了一句「不能导致多出一次」。
    /// <para>所以三条边界一条都不许越：<strong>不进模板库</strong>（③ 步下拉不会多出一份，
    /// 它只活在这次会话里）、<strong>不改标签宽高</strong>（拼版格子按一个标量尺寸排，
    /// 改了就会多一页或错位——正是"多出一次"）、<strong>不改张数</strong>（那一行 5 箱仍是 5 张，
    /// 件号仍由编号引擎逐张算，见 <see cref="LabelFlattener"/>）。</para>
    /// </summary>
    private sealed class RowSnapshot
    {
        public RowSnapshot(int sourceRowIndex, string itemNo, LabelTemplate template, string baseTemplateId,
            int unreviewedValueCount, IReadOnlyList<string> blankedTokens)
        {
            SourceRowIndex = sourceRowIndex;
            ItemNo = itemNo;
            Template = template;
            BaseTemplateId = baseTemplateId;
            UnreviewedValueCount = unreviewedValueCount;
            BlankedTokens = blankedTokens;
        }

        /// <summary>Excel 里的那一行（1 起）。一览与标题都按「第几行」说话，这里翻译一次。</summary>
        public int SourceRowIndex { get; }

        /// <summary>做定稿那一刻这一行的货号：行号会因剔行/改切法而位移，货号对不上就当这条定稿不认。</summary>
        public string ItemNo { get; }

        public LabelTemplate Template { get; set; }

        /// <summary>这份定稿是从哪一份模板烤出来的——换了模板它就不该再挂上来。</summary>
        public string BaseTemplateId { get; }

        /// <summary>被写死时还挂着「需人工核对」的值有几笔（出纸闸靠它，别被烤平绕过去）。</summary>
        public int UnreviewedValueCount { get; }

        /// <summary>这一行取不到值、被烤成空白的占位符（第 66 棒：空必须点名）。</summary>
        public IReadOnlyList<string> BlankedTokens { get; }
    }

    private readonly Dictionary<int, RowSnapshot> _rowSnapshots = new();

    /// <summary>这一条记录所属那一行的定稿；模板已经换过就不算（定稿是从某一份模板烤出来的）。</summary>
    private RowSnapshot? SnapshotOf(MarkRecord record)
        => _rowSnapshots.TryGetValue(record.SourceRowIndex, out var snap)
           && string.Equals(snap.BaseTemplateId, SelectedTemplate?.Id, StringComparison.Ordinal)
            ? snap : null;

    /// <summary>
    /// 这一张该用哪份模板。<strong>预览、整版、打印、PDF、位图、SVG 六道消费方共用这一个答案</strong>；
    /// 出纸那一路走 <see cref="PageContentSource.TemplateFor"/>，两边问的是同一份字典。
    /// <para>基准模板由调用方传：那边已经判过"没选模板"，这里再判一次会多出一条永不发生的 null 分支。</para>
    /// </summary>
    private LabelTemplate TemplateForRecord(LabelTemplate baseTemplate, MarkRecord record)
        => SnapshotOf(record)?.Template ?? baseTemplate;

    /// <summary>本次会话里有几行做了单张定稿。</summary>
    public int RowSnapshotCount => _rowSnapshots.Count;

    /// <summary>⑤ 步那句提示显示不显示（有定稿才占一行）。</summary>
    public bool HasRowSnapshots => _rowSnapshots.Count > 0;

    /// <summary>印前最后一道闸旁边那句照实说：哪几张是单独定稿的、它意味着什么。</summary>
    public string RowSnapshotSummary => RowSnapshotNotes.Count == 0
        ? string.Empty
        : $"这一批里有 {RowSnapshotNotes.Count} 张是单独定稿的（只改了那一张，模板本身没动）。";

    /// <summary>⑤ 步摘要与导出说明照实念的那几句：哪几行是单独定稿的、意味着什么。</summary>
    public IReadOnlyList<string> RowSnapshotNotes =>
        _rowSnapshots.Values.OrderBy(s => s.SourceRowIndex)
            .Select(s => $"Excel 第 {s.SourceRowIndex} 行{(string.IsNullOrWhiteSpace(s.ItemNo) ? "" : "（" + s.ItemNo + "）")}是单独定稿的：那一行的文字已写死，" +
                         "改表格里的值不会回到这张上（件号仍由软件逐张算）。")
            .ToList();

    /// <summary>一览那一格（按第几行）挂不挂「已单独定稿」角标。</summary>
    public bool RowHasSnapshot(int ordinal)
        => RowSheetAt(ordinal) is { } row && SnapshotAlive(row.SourceRowIndex);

    private bool SnapshotAlive(int sourceRowIndex)
        => _rowSnapshots.TryGetValue(sourceRowIndex, out var snap)
           && string.Equals(snap.BaseTemplateId, SelectedTemplate?.Id, StringComparison.Ordinal);

    /// <summary>
    /// 「编辑这一张…」递给编辑器的东西。已经定过稿就在<b>那份定稿</b>上接着改（他上一次的改动不许被重新烤平抹掉）。
    /// </summary>
    /// <param name="Ordinal">第几行（一览与翻页用的那个序）。</param>
    /// <param name="Template">可改的那份定稿模板（编辑器改的就是它，保存回写到这里）。</param>
    /// <param name="Record">画布上当真值用的那条记录（这一行的第一张）。</param>
    /// <param name="Title">窗口与弹窗里说清「这是哪一张」。</param>
    /// <param name="SourceRowIndex">Excel 里的行号（定稿的键）。</param>
    /// <param name="Warnings">编辑器校验列表里必须点名的事：烤成空白的值、还没核对的值、认不出的占位符。</param>
    internal sealed record RowSnapshotTarget(
        int Ordinal, LabelTemplate Template, MarkRecord Record, string Title, int SourceRowIndex,
        IReadOnlyList<string> Warnings);

    internal RowSnapshotTarget? BeginRowSnapshot(int ordinal)
    {
        if (RowSheetAt(ordinal) is not { } row) return null;
        var baseTemplate = SelectedTemplate?.Template;
        if (baseTemplate is null) return null;
        if (row.FirstLabelIndex < 1 || row.FirstLabelIndex > _records.Count) return null;

        var record = _records[row.FirstLabelIndex - 1];
        var title = RowTitle(ordinal, row);
        var existing = SnapshotOf(record);

        // 那三句点名按「基准模板 + 这一行」现烤一遍量出来；已有定稿则沿用它当初记下的那两笔，
        // 因为写死之后模板里已经没有令牌可量了（重烤一份基准模板只为拿告警，成本远低于两套口径）。
        var evidence = LabelFlattener.Flatten(baseTemplate, record);
        var warnings = SnapshotWarnings(existing is not null ? existing.BlankedTokens : evidence.BlankedTokens,
            existing?.UnreviewedValueCount ?? evidence.UnreviewedFields.Count, evidence.UnknownTokens);

        var working = existing is not null
            ? existing.Template.CloneAsUserCopy(existing.Template.Name)
            : evidence.Template;
        return new RowSnapshotTarget(ordinal, working, record, title, row.SourceRowIndex, warnings);
    }

    private static IReadOnlyList<string> SnapshotWarnings(
        IReadOnlyList<string> blanks, int unreviewedCount, IReadOnlyList<string> unknown)
    {
        var lines = new List<string>();
        if (blanks.Count > 0)
            lines.Add($"这一行这几项本来就没值，已烤成空白：{string.Join("、", blanks)}——那一行的位置会留着，要它有字得回 ② 步把列连上。");
        if (unreviewedCount > 0)
            lines.Add($"这一张上有 {unreviewedCount} 个值还没人工核对：写进定稿不等于核过了，⑤ 步出纸前那道闸照旧拦。");
        if (unknown.Count > 0)
            lines.Add($"模板里有认不出的占位符：{string.Join("、", unknown)}，定稿不替它编值，按空处理。");
        return lines;
    }

    /// <summary>
    /// 编辑器保存定稿。<strong>只写这张内存里的副本，一个字节都不进模板库</strong>，
    /// 所以主窗的模板列表、④ 步的纸规、张数与页数全都不会跟着动。
    /// </summary>
    internal void CommitRowSnapshot(RowSnapshotTarget target, LabelTemplate edited)
    {
        if (edited is null) throw new ArgumentNullException(nameof(edited));
        // 宽高是拼版格子的唯一依据（ImpositionEngine 吃一个标量尺寸）。定稿改了它 = 多一页或装不下，
        // 界面上那两格在定稿模式里是灰的，这里再兜一道：数值被动过就按基准模板的尺寸钉回去。
        var baseTemplate = SelectedTemplate?.Template
                           ?? throw new InvalidOperationException("定稿期间模板被清空了，没地方钉尺寸。");
        edited.WidthMm = baseTemplate.WidthMm;
        edited.HeightMm = baseTemplate.HeightMm;

        // 那两笔账按「基准模板 + 这一行」现烤一遍量出来：一次存盘算一次，不贵，
        // 分成"新做的"与"改过的"两条路迟早各算各的（§五-122 那一族）。
        var evidence = LabelFlattener.Flatten(baseTemplate, target.Record);

        _rowSnapshots[target.SourceRowIndex] = new RowSnapshot(
            target.SourceRowIndex, target.Record.GetText(MarkFieldKey.ItemNo), edited, baseTemplate.Id,
            evidence.UnreviewedFields.Count, evidence.BlankedTokens);

        StatusMessage = $"{target.Title}：已只给这一张定稿，其他行与这份模板本身都没动。";
        RowSnapshotLostNotice = null;         // 上一条「已作废」的告示别再挂着
        Raise(nameof(RowSnapshotLostNotice));
        AppLog.Info($"单张定稿：{target.Title}（基准模板 {baseTemplate.Id}，写死元素 {edited.Elements.Count} 个）");
        RefreshLayoutAndThumbs();
    }

    /// <summary>某一行恢复成跟模板一致。返回 true 表示真撤掉了什么（没定过稿不该报"已恢复"）。</summary>
    public bool RestoreRowSnapshot(int ordinal)
    {
        if (RowSheetAt(ordinal) is not { } row || !_rowSnapshots.Remove(row.SourceRowIndex)) return false;
        StatusMessage = $"{RowTitle(ordinal, row)}：已恢复成跟模板一致。";
        RefreshLayoutAndThumbs();
        return true;
    }

    /// <summary>全部恢复成跟模板一致。</summary>
    public void RestoreAllRowSnapshots()
    {
        if (_rowSnapshots.Count == 0) return;
        var n = _rowSnapshots.Count;
        _rowSnapshots.Clear();
        StatusMessage = $"已恢复 {n} 张单独定稿，现在全部照模板排。";
        RefreshLayoutAndThumbs();
    }

    /// <summary>
    /// 最近一次定稿作废的说明（占一条自己的界面行，不只靠状态栏）。
    /// <para>状态栏那一句会被后面的写家盖掉——换模板时纸规跟着换要说一句、导入完连线结果要说一句
    /// （§五-129 同一族）。"你单独改的那几张没了"这种消息不能被盖。</para>
    /// </summary>
    public string? RowSnapshotLostNotice { get; private set; }

    /// <summary>
    /// 结构一动，定稿必须<strong>当面作废</strong>：定稿的键是 Excel 行号，而剔一行、改列名行、换表、
    /// 换模板都会让行号位移或让"从哪份模板烤的"失效。留着它 = 静默把旧字挂到别的行上（赔钱形态）。
    /// </summary>
    private void InvalidateRowSnapshots(string reason)
    {
        if (_rowSnapshots.Count == 0) return;
        var rows = string.Join("、", _rowSnapshots.Keys.OrderBy(k => k).Select(k => $"第 {k} 行"));
        _rowSnapshots.Clear();
        RowSnapshotLostNotice = $"{reason}，之前单独定稿的 {rows} 已作废（现在全部照模板排）。要再改那一张，重新点「编辑这一张…」。";
        StatusMessage = RowSnapshotLostNotice;
        AppLog.Warning($"单张定稿作废（{reason}）：{rows}");
        // 版面由调用那一路重算（换模板的 setter 与 LoadSource 后面各自都会排一遍），这里只发通知
        Raise(nameof(RowSnapshotCount));
        Raise(nameof(HasRowSnapshots));
        Raise(nameof(RowSnapshotSummary));
        Raise(nameof(RowSnapshotLostNotice));
    }

    /// <summary>定稿变了以后要重算的那几样：单标签、整版、缩略一览（模板没换，编号与张数一律不动）。</summary>
    private void RefreshLayoutAndThumbs()
    {
        RebuildLayout();
        Sheet.RebuildPlan();
        RebuildRowThumbs();
        Raise(nameof(RowSnapshotCount));
        Raise(nameof(HasRowSnapshots));
        Raise(nameof(RowSnapshotSummary));
    }

    IReadOnlyList<MarkRecord> ILabelSource.RawRecords => _rawRecords;

    /// <summary>
    /// ③ 步「编辑模板…」与「编辑这一张…」递给编辑器的样例记录（第 65 棒③ 定的口径，第 70 棒加一层）。
    /// <para>编号跑过就用<strong>会印出来的那条标签</strong>（含 <c>col:本行箱数</c> 这类推算量）；
    /// 还没编号就与主预览同一份兜底样例。<strong>不能拿表里第一行</strong>：那条记录没经过编号引擎，
    /// <c>{{col:本行箱数}}</c> 是空的，模板里「Ctns：…件」那一行会命中「变量全空整条隐藏」——
    /// 无声少印一行正是第 9 棒批次一-11 记过的那类错，也是第 65 棒③ 他看到的症状。</para>
    /// <para>第 70 棒加一层：<strong>行检查开着时给眼前这一行的第一张</strong>——他从缩略里点进第 2 行
    /// 去改那一行的越界，画布还照第 1 行的数据画，他就看不见自己刚才点的那张到底哪里出纸。</para>
    /// </summary>
    internal MarkRecord EditorPreviewRecord
    {
        get
        {
            var labelIndex = RowCheck && RowSheetAt(Math.Max(1, CurrentIndex)) is { } row
                ? row.FirstLabelIndex
                : 1;
            return labelIndex >= 1 && labelIndex <= _records.Count
                ? _records[labelIndex - 1]
                : SampleRecords.StandardSample();
        }
    }

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
    /// <summary>① 步「一键修复」：把当前所有可修的体检发现合成一份新切法。</summary>
    public RelayCommand HealthFixAllCommand { get; }
    /// <summary>① 步每条发现旁的「执行」：只落点的那一条（CommandParameter 是 HealthRow）。</summary>
    public RelayCommand HealthFixOneCommand { get; }
    /// <summary>① 步「↩ 撤回这一步」：回退到上一次修复前的切法。</summary>
    public RelayCommand HealthUndoCommand { get; }
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
                // 定稿是从某一份模板烤出来的：换了模板还把它挂上来，那一行就会印成上一份模板的脸（静默、
                // 且用户以为已经跟着新模板改了）。所以换模板当面作废，不作废的那条路才是危险路。
                InvalidateRowSnapshots("换了模板");
                // 第 68 棒：纸规跟着模板走（用户「只要长宽是 140×100，纸规自动变成 280×200 的 2×2 排布」）。
                // 排在重算之前——拿旧纸先排一遍再换纸就是白算一趟，而且那一版的枚数会闪一下。
                // 这里不碰 StepIndex：他明确说「不用跳转到④页面」。
                var sheetNote = Sheet.ApplySheetForTemplate(follow: !_suppressSheetFollow);
                _suppressSheetFollow = false;
                RebuildLayout();
                Sheet.RebuildPlan();
                RebuildIssueLines();
                RebuildRowThumbs();       // 缩略一览里每张都来自这份模板，换了就得重画
                RememberTemplateId(value?.Id);
                RefreshPosterSelection();     // 首页海报墙那圈"选中"边框跟着换
                Raise(nameof(TemplateSheetHint));
                if (sheetNote.Length > 0) StatusMessage = sheetNote;
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
                RebuildRowThumbs();     // 一览里那排也得跟着改字，不然单张一个样、一览另一个样
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

    // ───────────────────────── 壳窗状态（第 93 棒起，第 94 棒改三栏） ─────────────────────────

    /// <summary>壳窗两根栏的宽度与上次开没开。宽度 0 / 开关 null = 没记过，调用方退回 <c>SimpleShellFlow</c> 的默认档。</summary>
    internal (double LeftWidth, double RightWidth, bool? LeftOpen, bool? RightOpen) LoadShellPanes()
    {
        var s = _uiState.Load();
        return (s.LeftPaneWidth, s.RightPaneWidth, s.LeftPaneOpen, s.RightPaneOpen);
    }

    internal void SaveShellPanes(double leftWidth, double rightWidth, bool leftOpen, bool rightOpen)
    {
        var state = _uiState.Load();
        if (Math.Abs(state.LeftPaneWidth - leftWidth) < 1
            && Math.Abs(state.RightPaneWidth - rightWidth) < 1
            && state.LeftPaneOpen == leftOpen && state.RightPaneOpen == rightOpen) return;
        state.LeftPaneWidth = leftWidth;
        state.RightPaneWidth = rightWidth;
        state.LeftPaneOpen = leftOpen;
        state.RightPaneOpen = rightOpen;
        _uiState.Save(state);
    }

    // ───────────────────────── 运行模式（第 30 棒） ─────────────────────────

    private RunMode _mode = RunMode.Ai;

    /// <summary>状态文件里那个名字只能当候选：只认 <c>"offline"</c>，其余（空/旧文件/写坏了/认不出）一律 AI 模式。</summary>
    private static RunMode RememberedMode(string? stored)
        => string.Equals(stored, "offline", StringComparison.OrdinalIgnoreCase) ? RunMode.Offline : RunMode.Ai;

    /// <summary>
    /// 运行模式（第 30 棒）：**AI 模式 = 导入后不先绑定**，先交 AI 读懂整张表、由它给出绑定方案，人逐条确认；
    /// **离线模式 = 现有五步人工程**，自动连线 + 人工连线照旧，全程不碰网络。
    /// <para>用户 2026-09-10 定的默认是 AI 模式（明确不要"首次问一次"），所以状态文件里没记过就是 AI。</para>
    /// <para>切模式只改「导入之后怎么走」，**两边的人工口子一个都不少**（手工连线、<c>col:</c> 直引、方案存/取照旧）——
    /// 没配密钥、断网、模型超时，人随时能切回离线接着干，不会被堵在"等 AI"上。</para>
    /// </summary>
    public RunMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            RememberMode(value);
            Raise(nameof(Mode));
            Raise(nameof(IsAiMode));
            Raise(nameof(ModeText));
            Raise(nameof(SelectedMode));
            StatusMessage = value == RunMode.Ai
                ? "已切到 AI 模式：下次导入表格后先不绑定，由 AI 读懂整张表再给绑定方案，逐条确认。"
                : "已切到离线模式：导入后照旧按表头自动连线、人工逐项可调；这一路不联网。";
        }
    }

    /// <summary>界面用：现在是不是 AI 模式（提示语与人工口子的显隐都看它）。</summary>
    public bool IsAiMode => _mode == RunMode.Ai;

    public string ModeText => _mode == RunMode.Ai ? "AI 模式" : "离线模式";

    public IReadOnlyList<ChoiceOption<RunMode>> ModeOptions { get; } = new[]
    {
        new ChoiceOption<RunMode>(RunMode.Ai, "AI 模式（推荐）"),
        new ChoiceOption<RunMode>(RunMode.Offline, "离线模式"),
    };

    public ChoiceOption<RunMode>? SelectedMode
    {
        get => ModeOptions.FirstOrDefault(o => o.Value == _mode);
        set
        {
            if (value is null || value.Value == _mode) return;
            Mode = value.Value;
        }
    }

    private void RememberMode(RunMode mode)
    {
        var name = mode == RunMode.Offline ? "offline" : "ai";
        var state = _uiState.Load();
        if (string.Equals(state.RunMode, name, StringComparison.OrdinalIgnoreCase)) return;
        state.RunMode = name;
        _uiState.Save(state);
    }

    // ───────────────────────── AI 改动的逐步撤回（第 33 棒） ─────────────────────────

    private readonly List<AiUndoPoint> _aiUndo = new();

    /// <summary>栈的上限（只活在内存里；跨会话撤回没意义，状态文件也不该塞这种大对象）。</summary>
    private const int MaxAiUndoSteps = 20;

    /// <summary>手上有没有可撤回的 AI 改动（界面据此决定那颗按钮亮不亮）。</summary>
    public bool CanUndoAiChange => _aiUndo.Count > 0;

    /// <summary>还能退几步（按钮上写出来，人心里有数）。</summary>
    public int AiUndoSteps => _aiUndo.Count;

    /// <summary>最近一步叫什么（按钮文案用它说清"退回哪一步"）。</summary>
    public string AiUndoLabel => _aiUndo.Count > 0 ? _aiUndo[^1].Label : string.Empty;

    /// <summary>
    /// AI 要动手之前压一份快照。**一次 AI 动手 = 一步**（用户 2026-09-10 选的撤回粒度是"逐步"）：
    /// 读表提案整份落一次是一步，他答一条问题落一次也是一步。
    /// </summary>
    public void BeginAiChange(string label)
    {
        var bindings = new Dictionary<MarkFieldKey, int>();
        foreach (var row in FieldRows)
            if (row.ColumnIndex >= 0 && Enum.TryParse<MarkFieldKey>(row.FieldKey, out var key))
                bindings[key] = row.ColumnIndex;

        _aiUndo.Add(new AiUndoPoint(label, _choice, bindings,
            SelectedTemplate?.Id, Sheet.SelectedSheetOption?.Spec.Name, DateTime.Now));
        if (_aiUndo.Count > MaxAiUndoSteps) _aiUndo.RemoveAt(0);
        Raise(nameof(CanUndoAiChange));
        Raise(nameof(AiUndoSteps));
        Raise(nameof(AiUndoLabel));
    }

    /// <summary>
    /// 撤回最近一次 AI 动手（**可连点，逐步往回退**——用户 2026-09-10 选的粒度）。
    /// <para>四样按「切法 → 绑定 → 模板 → 纸规」回退，**顺序不能反**：切法那一步要重读源文件，
    /// 而重读会把字段行按当前方案重建——先回绑定就会被它盖掉。</para>
    /// <para>各部分独立报结果，不假装"全成才算成"（与第 21 棒那条口径一致）。</para>
    /// </summary>
    public (bool Ok, string Message) UndoLastAiChange()
    {
        if (_aiUndo.Count == 0) return (false, "没有可撤回的 AI 改动。");
        var point = _aiUndo[^1];
        _aiUndo.RemoveAt(_aiUndo.Count - 1);
        Raise(nameof(CanUndoAiChange));
        Raise(nameof(AiUndoSteps));
        Raise(nameof(AiUndoLabel));

        var lines = new List<string>();

        var (cutOk, cutMsg) = ApplySheetChoice(point.Choice);
        if (!cutOk) lines.Add("切法没能退回（" + cutMsg + "）");

        var restored = 0;
        foreach (var row in FieldRows)
        {
            if (!Enum.TryParse<MarkFieldKey>(row.FieldKey, out var key)) continue;
            var want = point.Bindings.TryGetValue(key, out var col) ? col : -1;
            if (row.ColumnIndex == want) continue;
            row.ColumnIndex = want;
            restored++;
        }
        if (restored > 0) ApplyMapping();
        lines.Add(restored > 0 ? $"字段绑定退回了 {restored} 项" : "字段绑定与原来一样");

        if (point.TemplateId is { Length: > 0 } templateId)
        {
            var option = TemplateOptions.FirstOrDefault(t => t.Id == templateId);
            if (option is not null && !ReferenceEquals(SelectedTemplate, option))
            {
                SelectedTemplate = option;
                lines.Add($"模板退回到「{option.Name}」");
            }
        }
        if (point.SheetSpecName is { Length: > 0 } specName)
        {
            var answer = Sheet.SelectSheetSpecByName(specName);
            if (!answer.StartsWith("纸规已切到", StringComparison.Ordinal)) lines.Add("纸规没能退回（" + answer + "）");
            else lines.Add("纸规退回了「" + specName + "」");
        }

        var left = _aiUndo.Count;
        return (true, $"已撤回「{point.Label}」这一步：" + string.Join("；", lines) + "。"
            + (left > 0 ? $"还能再退 {left} 步。" : "没有更早的 AI 改动了。"));
    }

    /// <summary>
    /// AI 模式导入完表之后，请主窗口去让 AI 读这张表（第 30 棒）。
    /// <para>为什么用事件而不是让 VM 直接喊面板：面板是主窗口那一侧的物件（<c>WireAi</c> 里接线），
    /// VM 不该认识它——这条与"状态窗只存在这里、面板拿不到 UiStateStore"是同一条边界纪律。</para>
    /// </summary>
    public event Action? AiReadRequested;

    private void RequestAiRead() => AiReadRequested?.Invoke();

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

    /// <summary>
    /// 预览总条数：<strong>行检查开着 = 表格行数</strong>（一行一张），关着 = 要出的标签张数。
    /// <para>只影响预览的翻页，不影响出片：<see cref="CreatePageSource"/> 拿的仍是整份标签集。</para>
    /// </summary>
    public int RecordTotal => RowCheck ? _rowSheets.Count : _records.Count;

    private readonly List<RowSheet> _rowSheets = new();

    private bool _rowCheck;

    /// <summary>
    /// 行检查（用户 2026-09-14：「直接在单个标签页面添加一个开关（行检查）就把张数缩略了」）。
    /// <para>开着时一行只画<strong>该行的第一张</strong>，翻页按表格行走 —— 一行五张纸的货不必为核对内容翻五遍。
    /// 每张纸的件号仍是各自的号，所以看到的这一张就是那一行的第一张真品，不是拼出来的示意。</para>
    /// </summary>
    public bool RowCheck
    {
        get => _rowCheck;
        set
        {
            if (!Set(ref _rowCheck, value)) return;
            if (!value) StopSlideshow("行检查关了");
            RebuildRowSheets();
            CurrentIndex = Math.Min(Math.Max(1, CurrentIndex), RecordTotal);
            RebuildLayout();
            RebuildRowThumbs();
            Raise(nameof(RecordTotal));
            Raise(nameof(ArrowPagesRows));
            StatusMessage = value
                ? $"行检查开了：{RecordTotal} 行各看一张（一行几张纸的只画第一张），关掉回到逐张看；" +
                  "这时 ← / → 就是上一张、下一张。"
                : "行检查已关，逐张翻。";
        }
    }

    /// <summary>
    /// 这一态下裸 ← / → 归"翻页"管吗（第 89 棒②，用户：「向右箭头--下一张，向左箭头--上一张」）。
    /// <para>只在<strong>行检查开着、且没摊成全部行</strong>时接管：全部行那一屏满屏都是，没有"下一张"可翻；
    /// 关着时是逐张核对，那两条键仍归原来的控件（步骤条、下拉、表格）。焦点让不让由
    /// <see cref="Services.PreviewArrows"/> 判，规则只在那一处。</para>
    /// </summary>
    public bool ArrowPagesRows => _rowCheck && !_rowCheckAll;

    private bool _rowCheckAll;

    /// <summary>「折叠」那颗开关：开 = 全部行缩略一览（一屏各画一张，纵向滚着扫），关 = 当前行单张。</summary>
    public bool RowCheckAll
    {
        get => _rowCheckAll;
        set
        {
            if (!Set(ref _rowCheckAll, value)) return;
            if (value) StopSlideshow("摊成全部行了");
            RebuildRowThumbs();
            Raise(nameof(ArrowPagesRows));
        }
    }

    /// <summary>
    /// 缩略一览一格占几份宽：<strong>默认</strong>一行十格（用户 2026-09-14「默认一行 10 个，多的往下排一行」）。
    /// <para>第 89 棒③：这个数从前是写死的常量，他想"放大看看"只能把窗口拉宽——现在改由他定，
    /// <see cref="RowThumbCellsPerRow"/> 越小每格越大。</para>
    /// </summary>
    public const int RowThumbCellsPerRowDefault = 10;

    /// <summary>一行最多摆几格（= 默认那档），再小就不是"一览"而是逐张看了。</summary>
    public const int RowThumbCellsPerRowMax = 10;

    private int _rowThumbCellsPerRow = RowThumbCellsPerRowDefault;

    /// <summary>
    /// 一览一行摆几格（1 ~ <see cref="RowThumbCellsPerRowMax"/>，默认 10）。
    /// <para>用户 2026-09-20：「开启全部行时可以放大——调整一行几张进行放大查看」。这一格宽 = 视口宽 ÷ 格数，
    /// 所以<strong>调小 = 每格变大 = 放大</strong>；出片一张不多一张不少，改的只是这一屏怎么排。</para>
    /// </summary>
    public int RowThumbCellsPerRow
    {
        get => _rowThumbCellsPerRow;
        set
        {
            Set(ref _rowThumbCellsPerRow, Math.Clamp(value, 1, RowThumbCellsPerRowMax));
            ApplyRowThumbCellWidth();
            // 填了 99 夹回 10 时，框里那句也得跟着弹回来，不许留着一个 VM 里根本不存在的数
            Raise(nameof(RowThumbCellsText));
        }
    }

    /// <summary>格数的"直接填数字"那条路（与工具栏那对 －/＋ 并存，不许二选一）。</summary>
    public string RowThumbCellsText
    {
        get => _rowThumbCellsPerRow.ToString(CultureInfo.InvariantCulture);
        set
        {
            // 填不进就不改（半截数字、字母都算）：正在输入时把框清空比留着旧值更难用
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cells))
                RowThumbCellsPerRow = cells;
        }
    }

    /// <summary>放大一档（每行少摆一格）。</summary>
    public RelayCommand RowThumbCellsFewerCommand { get; }

    /// <summary>缩小一档（每行多摆一格，最多回到默认十格）。</summary>
    public RelayCommand RowThumbCellsMoreCommand { get; }

    private double _rowThumbCellWidth = 200;   // 还没量到预览区宽度时的兜底（首帧不至于零宽）

    private double _previewViewportWidth;

    /// <summary>缩略一览一格的宽度（WrapPanel 的 ItemWidth）：可用宽 ÷ <see cref="RowThumbCellsPerRow"/>。</summary>
    public double RowThumbItemWidth => _rowThumbCellWidth;

    /// <summary>
    /// 预览区量到宽度时由界面调用（<strong>不管自动适应开没开</strong>）：缩略一览按它分格。
    /// <para>第 71 棒：这一格宽从前只在「适应窗口」那条路里更新，而一览开着时那排按钮（含那颗勾）整排是
    /// 藏起来的 —— 于是格宽永远停在兜底值，他看到的「太小、显示不全」就从这里来（「不换行」另有一条，
    /// 是横向滚动把 WrapPanel 撑成无限宽，见 XAML 那侧的注释）。</para>
    /// <para>扣掉的 44 = ScrollViewer 左右内边距 16 + 竖向滚动条约 18 + 几 DIP 余量：
    /// ItemWidth 略大一点 WrapPanel 就只排得下九格，宁可留余量。</para>
    /// </summary>
    public void SetPreviewViewport(double width)
    {
        if (width < 60) return;
        _previewViewportWidth = width;
        ApplyRowThumbCellWidth();
    }

    /// <summary>按当前视口宽与格数重算一格多宽（两条入口共用：量到宽度、改了格数）。</summary>
    private void ApplyRowThumbCellWidth()
    {
        if (_previewViewportWidth < 60) return;
        var cell = Math.Max(80, (_previewViewportWidth - 44) / RowThumbCellsPerRow);
        if (Math.Abs(_rowThumbCellWidth - cell) < 1) return;
        _rowThumbCellWidth = cell;
        Raise(nameof(RowThumbItemWidth));
    }

    /// <summary>缩略一览的内容：一行一张，标题写「第几行 · 货号 · 本行几张」。</summary>
    public ObservableCollection<RowThumb> RowThumbs { get; } = new();

    /// <summary>把当前标签集按<strong>数据行</strong>折成行表（标签本来就按行序产出，所以相邻同号就是一行）。</summary>
    private void RebuildRowSheets()
    {
        _rowSheets.Clear();
        for (var i = 0; i < _records.Count; i++)
        {
            var record = _records[i];
            var last = _rowSheets.Count > 0 ? _rowSheets[^1] : null;
            if (last is not null && last.SourceRowIndex == record.SourceRowIndex) last.SheetCount++;
            else _rowSheets.Add(new RowSheet(record.SourceRowIndex, i + 1, record.GetText(MarkFieldKey.ItemNo)));
        }
    }

    private RowSheet? RowSheetAt(int ordinal) => ordinal >= 1 && ordinal <= _rowSheets.Count ? _rowSheets[ordinal - 1] : null;

    /// <summary>
    /// 眼前预览的这一张属于<strong>第几行</strong>。行检查开着时翻页本来就按行走，直接就是它；
    /// 关着时 <see cref="CurrentIndex"/> 是第几张标签，按那一行的数据行号反查回来。
    /// <para>「编辑这一张…」与「恢复这一张」都问这一处，界面不许自己拿下标猜（第 73 棒）。</para>
    /// </summary>
    public int? CurrentRowOrdinal()
    {
        if (_rowSheets.Count == 0) return null;
        if (_rowCheck) return Math.Clamp(Math.Max(1, _currentIndex), 1, _rowSheets.Count);
        if (_currentIndex < 1 || _currentIndex > _records.Count) return null;
        var rowIndex = _records[_currentIndex - 1].SourceRowIndex;
        for (var i = 0; i < _rowSheets.Count; i++)
        {
            if (_rowSheets[i].SourceRowIndex == rowIndex) return i + 1;
        }
        return null;
    }

    /// <summary>行检查下按「第几行」画那张（取该行第一张标签）。</summary>
    private LabelLayout? BuildLayoutForRow(int ordinal)
        => RowSheetAt(ordinal) is { } row ? BuildLayoutFor(row.FirstLabelIndex) : null;

    /// <summary>缩略一览只在「行检查 + 全部行」两颗开关都开着时才有内容，其余时候清空不占内存。</summary>
    private void RebuildRowThumbs()
    {
        RowThumbs.Clear();
        if (!_rowCheck || !_rowCheckAll) return;
        for (var ordinal = 1; ordinal <= _rowSheets.Count; ordinal++)
        {
            var row = _rowSheets[ordinal - 1];
            RowThumbs.Add(new RowThumb(ordinal, RowTitle(ordinal, row), BuildLayoutForRow(ordinal), RowHasSnapshot(ordinal)));
        }
    }

    /// <summary>
    /// 点缩略一览里的一格：跳到那一行并收起一览（关着「全部行」看见的就是他点的那一张）。
    /// <para>第 71 棒：入口从「每格一条 RelayCommand」改成界面鼠标事件 + 这一句 —— 命令绑定一旦接不上
    /// 是静默的（按钮照样画出来），他报的「随便点哪个都没反应」就是这种；鼠标事件直接拿被点那一格的
    /// DataContext 说话，接不上就什么都不做，不会装作办了事。</para>
    /// </summary>
    public void SelectRowThumb(RowThumb thumb)
    {
        CurrentIndex = thumb.Ordinal;
        RowCheckAll = false;
    }

    private string RowTitle(int ordinal, RowSheet row)
        => $"第 {ordinal} / {_rowSheets.Count} 行：{(string.IsNullOrWhiteSpace(row.ItemNo) ? "（无货号）" : row.ItemNo)}，本行 {row.SheetCount} 张";

    /// <summary>行检查的标题：第几行 / 共几行 · 货号 · 本行几张纸（单独定过稿的那一行要多说一句）。</summary>
    private string? RowCheckInfo(int ordinal) => RowSheetAt(ordinal) is { } row
        ? RowTitle(ordinal, row) + (SnapshotAlive(row.SourceRowIndex) ? " · 已单独定稿" : string.Empty)
        : null;

    /// <summary>一行折出来的检查条目：数据行号、该行第一张标签的序号、本行几张纸、货号（标题用）。</summary>
    public sealed class RowSheet
    {
        public RowSheet(int sourceRowIndex, int firstLabelIndex, string itemNo)
        {
            SourceRowIndex = sourceRowIndex;
            FirstLabelIndex = firstLabelIndex;
            ItemNo = itemNo;
        }

        public int SourceRowIndex { get; }

        public int FirstLabelIndex { get; }

        public string ItemNo { get; }

        /// <summary>本行要出几张纸（一行一张时就是 1；按列展开后是那一列的数，含「每件贴几张」的份数）。</summary>
        public int SheetCount { get; set; } = 1;
    }

    /// <summary>缩略一览里的一格：第几行、标题、那一行第一张的版面。点格跳转用 Ordinal。</summary>
    public sealed class RowThumb
    {
        public RowThumb(int ordinal, string title, LabelLayout? layout, bool hasSnapshot = false)
        {
            Ordinal = ordinal;
            Title = title;
            Layout = layout;
            HasSnapshot = hasSnapshot;
        }

        /// <summary>第几行（1 起，按表格行序）。测试与跳转都以它为准，不靠集合下标猜。</summary>
        public int Ordinal { get; }

        public string Title { get; }

        public LabelLayout? Layout { get; }

        /// <summary>这一行是不是单独定过稿（一览格上那颗角标 + 判据读这一个字段）。</summary>
        public bool HasSnapshot { get; }
    }

    /// <summary>当前预览第几条（1 起）；0 表示示意预览。</summary>
    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            // 第 89 棒②：幻灯片放着时他手动翻了一张（点按钮、按箭头、点缩略格都走这一处）就停下自动播放——
            // 自动的手不许跟人的手抢方向盘，否则他刚停在哪一行下一秒就被搬走。
            if (_slideshowPlaying && !_slideshowAdvancing) StopSlideshow("手动翻页");
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

    private bool _showLabelDividers = true;

    /// <summary>
    /// 整版拼版预览里每枚标签的<strong>分界虚线</strong>（第 82 棒③，用户：「仅预览使用不会被打印」）。
    /// <para>它与「显示要素边框」是两件事：那颗框的是单枚标签<em>里面</em>的元素，这颗画的是<em>枚与枚</em>的边界。
    /// 只活在这一块画布上——位图/PDF/TIFF/打印/SVG 五条出口走 <c>Image</c> / <c>Printer</c> 用途，拿不到这条线。</para>
    /// </summary>
    public bool ShowLabelDividers
    {
        get => _showLabelDividers;
        set => Set(ref _showLabelDividers, value);
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
        if (!string.IsNullOrEmpty(_sourceOriginalPath))
        {
            // 问的是原件在哪个文件夹，不是 _sourcePath——那份副本在 %APPDATA%\LabelGou\import-cache 里，
            // 拿它当初始目录会把人下一次挑文件时带进缓存目录（第 90 棒②）。
            var dir = Path.GetDirectoryName(_sourceOriginalPath);
            if (dir is not null && Directory.Exists(dir)) dialog.InitialDirectory = dir;
        }
        if (dialog.ShowDialog() != true) return;

        LoadSource(dialog.FileName, null);
    }

    /// <summary>
    /// 首页拖入区那条路（第 97 棒）：按路径导入，走与「打开数据文件…」对话框<strong>完全同一条</strong>
    /// <see cref="LoadSource"/> 链。
    /// <para>为什么在 VM 开这个方法而不是让壳窗自己导：界面另起一条导入路就是 §五-122 那一族——
    /// 两条链早晚对不上（一条记了原件路径、一条没记，下一次挑文件的初始目录与那份缓存副本就分叉）。</para>
    /// </summary>
    /// <returns>认下了这份文件没有（路径空、文件不在、扩展名不支持都算没认，界面什么都不用改）。</returns>
    public bool TryOpenFileAt(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !TableImporter.IsSupported(path))
        {
            // 拖进来一份认不了的，得说一句——不出声就像软件把东西吞了（第 21 阶段那条"有反馈"口径）
            StatusMessage = "这份我认不了：要 Excel / CSV 那一类表格文件（xlsx、xlsm、csv、tsv、txt），而且得还在原来那个位置。";
            return false;
        }
        LoadSource(path, null);
        return true;
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
            // 切法变了要重读，但**不是新表**：不清绑定、也不再问一次 AI（第 33 棒）。
            LoadSource(_sourcePath, previousSheet, newTable: false);
            return (true, _data?.Describe() ?? "已按新切法重读这张表。");
        }
        catch (Exception ex)
        {
            _choice = previous;
            try { LoadSource(_sourcePath, previousSheet, newTable: false); } catch { /* 连原样都读不回来就是文件本身变了，不拿这句话骗人 */ }
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
        // 用户开关过的合计行兜底原样带走：AI 没提这一项，它就不该被提案悄悄顶回来
        // 右侧块（① 步圈出的表内文字模板/指令列）同理：提案没提它，保留用户点过的。
        return new SheetLayoutChoice(header, p.HasHeader ?? _choice.HasHeader,
            rows.Count == 0 ? null : rows, _choice.SkipSummaryRows, _choice.SideBlocks, _choice.ValueRules);
    }

    /// <summary>
    /// 只把提案里<strong>被点名的那几类</strong>合到当前切法上（阶段 29 第 1 棒：改动卡逐条落地）。
    /// <para>没被点到的类一律保留 <see cref="_choice"/> 的值——人只 ✅ 了「哪几行不当货印」那一条，
    /// 列名行就不许跟着动。剔除行仍是<strong>并集</strong>：直接复用上面那份口径（<see cref="ChoiceFrom(AiSheetProposal)"/>），
    /// <strong>不另写第二套合并逻辑</strong>，否则"整份落地"与"逐条落地"会长得不一样（§五-62 那类换路复发）。</para>
    /// </summary>
    /// <param name="p">AI 提案。</param>
    /// <param name="only">只落地这几类。</param>
    public SheetLayoutChoice ChoiceFrom(LabelGou.Core.Recognition.AiSheetProposal p,
        IReadOnlyCollection<LabelGou.Core.Recognition.AiChangeKind> only)
    {
        var takeHeader = only.Contains(LabelGou.Core.Recognition.AiChangeKind.HeaderRow);
        var takeRows = only.Contains(LabelGou.Core.Recognition.AiChangeKind.ExcludedRows);
        if (!takeHeader && !takeRows) return _choice;      // 版式与纸规不走切表这条路

        var full = ChoiceFrom(p);
        return new SheetLayoutChoice(
            takeHeader ? full.HeaderRowIndex : _choice.HeaderRowIndex,
            takeHeader ? full.HasHeader : _choice.HasHeader,
            takeRows ? full.ExcludedRawRows : _choice.ExcludedRawRows,
            _choice.SkipSummaryRows, _choice.SideBlocks, _choice.ValueRules);
    }

    /// <summary>回到「软件自动猜表头、不剔行」的那一份切法（用户说「改错了，恢复」时走这条）。</summary>
    public (bool Ok, string Message) ResetSheetChoice() => ApplySheetChoice(SheetLayoutChoice.Auto);

    // ---------- ① 步的手动切法（第 24 棒，§十-A-14/33② 欠的那块控件） ----------

    /// <summary>① 步「列名那一行」下拉的一项：Label 给人看，另两项是落回 <see cref="SheetLayoutChoice"/> 的料。</summary>
    public sealed record HeaderRowOption(string Label, int? HeaderIndex, bool HasHeader);

    private bool _suppressHeaderRowOption;
    private HeaderRowOption? _selectedHeaderRowOption;

    /// <summary>① 步那张下拉的候选：自动猜 / 没列名 / 原表第 1…20 行（表短就只列到真有的行）。</summary>
    public ObservableCollection<HeaderRowOption> HeaderRowOptions { get; } = new();

    /// <summary>用户选中的切法。改了就重读一遍源文件（失败自动退回原样，不拿一张能用的表冒险）。</summary>
    public HeaderRowOption? SelectedHeaderRowOption
    {
        get => _selectedHeaderRowOption;
        set
        {
            if (_suppressHeaderRowOption || value is null || Equals(value, _selectedHeaderRowOption)) return;
            var previous = _selectedHeaderRowOption;
            // 只动表头那一项：点名剔过的行与合计行开关都原样带走（并集语义不变）
            var next = _choice with { HeaderRowIndex = value.HeaderIndex, HasHeader = value.HasHeader };
            _selectedHeaderRowOption = value;
            Raise(nameof(SelectedHeaderRowOption));
            if (next == _choice) return;
            if (string.IsNullOrWhiteSpace(_sourcePath)) { _choice = next; return; }

            var (ok, msg) = ApplySheetChoice(next);
            if (!ok)
            {
                _suppressHeaderRowOption = true;
                _selectedHeaderRowOption = previous;
                Raise(nameof(SelectedHeaderRowOption));
                _suppressHeaderRowOption = false;
                StatusMessage = "这条切法改不动（表保持原样）：" + msg;
                return;
            }
            StatusMessage = "已按你选的切法重读这张表：" + msg;
        }
    }

    /// <summary>① 步的「合计行兜底」开关（默认开）。关掉 = 一行都不自动剔，刚才被跳过的行全部回到数据里。</summary>
    public bool SkipSummaryRowsChecked
    {
        get => _choice.SkipSummaryRows;
        set
        {
            if (value == _choice.SkipSummaryRows) return;
            var next = _choice with { SkipSummaryRows = value };
            if (string.IsNullOrWhiteSpace(_sourcePath))
            {
                _choice = next;   // 还没表就只记着开关，下次读表直接生效
                Raise(nameof(SkipSummaryRowsChecked));
                return;
            }
            var (ok, msg) = ApplySheetChoice(next);
            if (!ok) StatusMessage = "这个开关没改成（表保持原样）：" + msg;
            else StatusMessage = value
                ? "已开合计行兜底：像合计的行会被跳过，剔了谁、凭什么会逐行写在状态栏。"
                : "已关合计行兜底：刚才被自动跳过的行都回到数据里了。";
            Raise(nameof(SkipSummaryRowsChecked));   // 失败时 ApplySheetChoice 已把 _choice 退回原样，开关跟着退回去
        }
    }

    /// <summary>
    /// ① 步「这张表体检」面板的一行：一条发现 + 界面要不要给那颗修键。
    /// <para>档位与键的对应（导入层第 1 棒，用户 2026-09-13 定的两颗键）：
    /// <c>Notice</c> = 软件已按判据自动处理，只说一声；<c>Suggestion</c> = 有可执行调整，
    /// 一键修复会带上它，也能逐条点；<c>Warning</c> = 有问题但软件没有足够事实去改，只报不修。</para>
    /// </summary>
    public sealed class HealthRow
    {
        public required HealthFinding Finding { get; init; }

        /// <summary>面板上那一句：<strong>只放短句</strong>（用户 2026-09-13「字很长所以我所乱」）。</summary>
        public string Text => Finding.Fact;

        /// <summary>整句（含凭什么与行号）——挂在 ToolTip 上，要看细节才展开。</summary>
        public string Detail => Finding.Describe();

        public string Tag => Finding.Level switch
        {
            HealthLevel.Notice => "已自动处理",
            HealthLevel.Suggestion => "可修",
            _ => "要你看",
        };

        public bool CanFix => Finding.Fix is not null;
    }

    /// <summary>① 步的体检清单（每次读表/改切法后重扫；关掉开关就清空）。</summary>
    public ObservableCollection<HealthRow> HealthRows { get; } = new();

    public bool HasHealthRows => HealthRows.Count > 0;

    /// <summary>① 步圈出去的右侧列号（自动连线跳过它们；人工连线照旧可以连——人工口子全留）。</summary>
    private IReadOnlyList<int>? SideColumnIndexes =>
        _choice.SideBlocks is { Count: > 0 } blocks ? blocks.Select(b => b.Column).ToList() : null;

    /// <summary>切表指令是不可变 record，撤回 = 压栈/出栈，不必另造一套撤销机制。</summary>
    private readonly List<SheetLayoutChoice> _healthUndoStack = new();

    public bool CanUndoHealthFix => _healthUndoStack.Count > 0;

    private bool _healthCheckEnabled = true;

    /// <summary>一级校验整层开关（用户 2026-09-13 定：这一层可以选择关闭）。默认开——那些病正是没人校验才留到今天。</summary>
    public bool HealthCheckEnabled
    {
        get => _healthCheckEnabled;
        set
        {
            if (value == _healthCheckEnabled) return;
            _healthCheckEnabled = value;
            Raise(nameof(HealthCheckEnabled));
            RefreshHealthFindings();
            StatusMessage = value
                ? "一级校验已打开：以后每次导入都会先扫这张表，能确定的当场说、可修的给键。"
                : "一级校验已关闭：导入后不再扫描与建议，直接进原来的五步流程。";
        }
    }

    /// <summary>重扫这张表。放在每次读表之后（<see cref="LoadSource"/> 末尾）——切法一改，发现就该跟着变。</summary>
    private void RefreshHealthFindings()
    {
        HealthRows.Clear();
        if (_healthCheckEnabled && _data is { } data)
        {
            foreach (var finding in TableHealthCheck.Scan(data))
                HealthRows.Add(new HealthRow { Finding = finding });
        }
        Raise(nameof(HasHealthRows));
        Raise(nameof(CanUndoHealthFix));
    }

    /// <summary>逐条执行：只落这一条。</summary>
    private void ApplyHealthFix(HealthRow? row)
    {
        if (row?.Finding.Fix is not { } fix) return;
        _healthUndoStack.Add(_choice);
        var (ok, msg) = ApplySheetChoice(fix.Apply(_choice));
        if (!ok)
        {
            _healthUndoStack.RemoveAt(_healthUndoStack.Count - 1);
            StatusMessage = "这条修复没改成（表保持原样）：" + msg;
            return;
        }
        StatusMessage = $"已修：{row.Finding.Fact}。在 ① 步预览里核对一眼，不对就点「↩ 撤回这一步」。";
    }

    /// <summary>一键修复：把当前所有可修的调整合成一份新切法，一次重读（不是逐条重读 N 遍）。</summary>
    private void ApplyAllHealthFixes()
    {
        var fixable = HealthRows.Where(r => r.CanFix).ToList();
        if (fixable.Count == 0) return;
        _healthUndoStack.Add(_choice);
        var next = fixable.Aggregate(_choice, (current, row) => row.Finding.Fix!.Apply(current));
        var (ok, msg) = ApplySheetChoice(next);
        if (!ok)
        {
            _healthUndoStack.RemoveAt(_healthUndoStack.Count - 1);
            StatusMessage = "一键修复没改成（表保持原样）：" + msg;
            return;
        }
        StatusMessage = $"一键修复 {fixable.Count} 处：{msg}。确认这张表切对了再往下走；要退回点「↩ 撤回这一步」。";
    }

    private void UndoHealthFix()
    {
        if (_healthUndoStack.Count == 0) return;
        var previous = _healthUndoStack[^1];
        _healthUndoStack.RemoveAt(_healthUndoStack.Count - 1);
        var (ok, msg) = ApplySheetChoice(previous);
        if (ok) StatusMessage = $"已撤回这一步：{msg}";
        else
        {
            _healthUndoStack.Add(previous);
            StatusMessage = "撤回失败（表保持当前样子）：" + msg;
        }
    }

    /// <summary>原表这一行是不是被合计行兜底跳过的。用户点名「这行要印」时得先关兜底，
    /// 否则点了按钮其实什么都没改（第 22 棒钉过的「点了报成功其实没改」不能拿兜底再犯一遍）。</summary>
    public bool RowAutoSkippedByHeuristic(int rawIndex0)
        => _data?.AutoSkippedSummaryRows.Any(h => h.RawRowIndex == rawIndex0) == true;

    /// <summary>重列 ① 步的候选与选中项（每次 LoadSource 之后跑；全程吃 suppress 旗，不拿赋值反抛 ApplySheetChoice）。</summary>
    private void BuildHeaderRowOptions()
    {
        _suppressHeaderRowOption = true;
        HeaderRowOptions.Clear();
        var detected = _data;
        var auto = new HeaderRowOption(detected is { HasHeaderRow: true }
            ? $"软件自动猜（现认第 {detected.HeaderRowIndex + 1} 行）"
            : "软件自动猜", null, true);
        HeaderRowOptions.Add(auto);
        HeaderRowOptions.Add(new HeaderRowOption("这张表没有列名（第一行也是货）", null, false));
        if (detected is not null)
        {
            var top = detected.RawRowCount - 1;   // 表头底下至少得留一行货（列名写在表尾的表，手选也要能点到那一行）
            for (var i = 0; i < top; i++)
                HeaderRowOptions.Add(new HeaderRowOption($"原表第 {i + 1} 行", i, true));
        }

        _selectedHeaderRowOption = _choice.HasHeader switch
        {
            false => HeaderRowOptions[1],
            _ when _choice.HeaderRowIndex is null => auto,
            _ => HeaderRowOptions.FirstOrDefault(o => o.HeaderIndex == _choice.HeaderRowIndex)
                 ?? AddPinnedRowOption(_choice.HeaderRowIndex.Value),
        };
        Raise(nameof(SelectedHeaderRowOption));
        _suppressHeaderRowOption = false;
        Raise(nameof(SkipSummaryRowsChecked));
    }

    /// <summary>钉在 20 行以外的表头（罕见但真有人把表头写到很下面）：补一条进候选，不然下拉显示不出当前选中。</summary>
    private HeaderRowOption AddPinnedRowOption(int headerIndex)
    {
        var option = new HeaderRowOption($"原表第 {headerIndex + 1} 行", headerIndex, true);
        HeaderRowOptions.Add(option);
        return option;
    }

    /// <summary>
    /// 读一份表（<paramref name="path"/> 为 null/空白时什么都不做）。对话框、重选工作表、
    /// 以后的“把文件拖到窗口上”都走这一个入口，单测也直接拿它喂数据。
    /// </summary>
    /// <param name="path">源文件。</param>
    /// <param name="sheet">工作表名（Excel）；null = 第一张。</param>
    /// <param name="newTable">
    /// **这是"新表进来了"还是"同一张表按新切法重读一遍"**（第 33 棒加的口子）。
    /// <para>为什么必须有这个区分：AI 模式下这两件事该做的完全相反——
    /// 新表要"**先不绑定、并自动请 AI 读**"；而重读（切法变了、按了重读）**绝不能**再发一次 AI 读表
    /// （白花一两分钟与一份 token，用户截图里那次重复的「开始读这张表」就是这么来的），
    /// 也不能把已经绑好的字段清成空白（列根本没动）。</para>
    /// </param>
    public void LoadSource(string? path, string? sheet, bool newTable = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // 第 90 棒②（用户方案「导入表格后暂时把表格存在软件内，切换文件或退出软件清掉文件缓存」）：
        // 外部原件先暂存一份进软件自己的缓存，之后每一次重读都读那一份副本——换工作表、改切法、
        // 一键修复、撤回、AI 每轮重算表画像（一趟两开 zip）全在这一条路上，一次都不许再碰他的原件。
        // 两头各治一件事：一面是他报的「WPS 说文件被其他软件使用」，另一面是他在 WPS 里存过一次盘
        // 之后，内存里那张表和磁盘上那份就对不上了——而 AI 报的是原表行号，行号一错就是剔错行、少印货。
        var fromCache = ImportCache.IsCached(path);
        var original = fromCache ? _sourceOriginalPath ?? Path.GetFullPath(path) : Path.GetFullPath(path);
        if (!fromCache)
        {
            var staged = ImportCache.Stage(path);
            if (staged.Note is not null) AppLog.Info($"导入暂存没成（这一趟直接读原件）：{staged.Note}");
            path = staged.Path;
        }

        // 换文件 = 切表指令作废的那只手：上一张表剔的「第 412 行」对新表毫无意义（拿它切新表就是切错行）。
        // 第 90 棒②把判据换成比"原件"：副本目录按原件路径归，但拿副本比总归绕了一层，说原件最清楚。
        var anotherFile = !string.Equals(_sourceOriginalPath, original, StringComparison.OrdinalIgnoreCase);
        if (anotherFile)
        {
            _choice = SheetLayoutChoice.Auto;
            // 上一张表的暂存跟着作废（用户：「切换文件或退出软件清掉文件缓存」）。只删它自己那个目录——
            // 清整个缓存会把另一条正在读表的路的副本一起端掉。
            if (_sourceOriginalPath is not null) ImportCache.Drop(_sourceOriginalPath);
        }
        _sourceOriginalPath = original;
        try
        {
            var data = TableImporter.Import(path, sheet, _choice);
            _data = data;
            BumpDataGeneration();
            // 定稿的键是 Excel 行号。换表、换工作表、改列名行、剔行、一键修复都会让行号整体位移，
            // 留着旧定稿＝把上一行烤死的字挂到下一行上，所以重读一次就当面作废一次。
            InvalidateRowSnapshots(newTable ? "换了数据源" : "这张表重新切过");
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
                // 人工口子全留：圈出去的列照样列得出来、照样能手连，只是名字上说清它被认成了什么。
                var asSide = _choice.SideBlocks?.FirstOrDefault(b => b.Column == c);
                var mark = asSide is null ? string.Empty
                    : asSide.Kind == SideBlockKind.TextTemplate ? "（① 步圈为表内文字模板）" : "（① 步圈为指令）";
                ColumnOptions.Add(new ColumnOption(c, $"{HeaderRowDetector.ColumnLetter(c)} · {data.Headers[c]}{mark}"));
            }
            Raise(nameof(ColumnOptions));
            // ① 步的「列名那一行」下拉跟着这张表重列，选中项摆回当前切法（第 24 棒）。
            BuildHeaderRowOptions();
            // ③ 步那个条码栏目列的是「这张表真有的列」，所以换表之后必须重列一次。
            Barcode.RefreshSources();
            // 导入层第 1 棒：表一读进来就体检一遍（切法变了也要重扫——上一轮的发现可能已经被这一轮的修复消掉了）。
            RefreshHealthFindings();

            // 优先套用已保存的同格式方案，其次自动猜
            ProfileName = Path.GetFileNameWithoutExtension(data.SourceFile);
            var known = _profileStore.ListAll()
                .Select(s => new { Summary = s, Profile = _profileStore.Load(s.FileName) })
                .Where(x => x.Profile is not null)
                .Select(x => x.Profile!)
                .ToList();

            if (_mode == RunMode.Ai && newTable)
            {
                // 第 30 棒：AI 模式下**新表进来先不绑定**。用户的原话是「导入表格后不应该直接绑定数据，
                // 先把表格给 AI 理解后由 AI 绑定」——程序按表头猜出来的那套绑定，正是他要换掉的东西。
                // 第 33 棒加 `newTable` 这道口：**重读**（切法变了 / 按了重读）不该走这里——
                // 那会把已经绑好的字段清成空白，而列根本就没动。
                _working = MappingProfile.CreateFor(data.Headers, ProfileName);
            }
            else
            {
                var matched = MappingSuggester.FindBestMatch(data.Headers, known);
                if (matched is not null)
                {
                    _working = matched;
                    StatusMessage = $"已套用保存过的映射方案「{matched.Name}」，请检查有没有连错。";
                }
                else
                {
                    _working = MappingSuggester.Suggest(data.Headers, ProfileName, SideColumnIndexes);
                    StatusMessage = "已按表头自动连接字段，请检查后点「应用映射」。";
                }
            }
            AutoNumberCartons = _working.AutoNumberCartons;
            RebuildFieldRows();
            ApplyMapping();
            PickTemplateFittingData();
            AdvanceAfterImport();
            if (_mode == RunMode.Ai && newTable)
            {
                // 状态那句必须放在最后：ApplyMapping / AdvanceAfterImport 都会改写状态栏，
                // 早设一句会被它们盖掉（第一版就是这么写的，单测当场抓出来了）。
                StatusMessage = "表已导入（AI 模式）：先不绑定字段，正在让 AI 读懂这张表…";
                // 自动读**只在新表进来时发生一次**（第 33 棒把发起处从"重读"里摘出来）：
                // 以前挂在导入路上，于是每次切法变更都偷偷再读一次表，白花一两分钟与一份 token。
                RequestAiRead();
            }
            else if (_mode == RunMode.Ai)
                StatusMessage = "已按新切法重读这张表（字段绑定照旧，没有重新问 AI）。";
        }
        catch (Exception ex)
        {
            // 栈必须落日志：界面上那句 MessageBox 只带 Message，打印店弹窗一闪而过，
            // 没有栈就永远查不出是谁炸的（2026-09-13 用户实测的 NRE 弹窗就是这么卡住的）。
            Services.AppLog.Error("导入/重读这张表时抛异常：" + ex);
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
        // 第一列摆 Excel 行号：体检清单、合计行理由、AI 报的都是「原表第 N 行」，
        // 对面这张表却没有行号，人就只能拿手指一行行数（用户 2026-09-13 实测第三条）。
        var rowNumberColumn = "行号";
        if (data.Headers.Any(h => h.Trim() == rowNumberColumn)) rowNumberColumn += " ";
        table.Columns.Add(rowNumberColumn, typeof(string));
        foreach (var header in data.Headers) table.Columns.Add(header, typeof(string));

        const int maxPreviewRows = 300;
        var rowCount = Math.Min(maxPreviewRows, data.RowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var values = new object[data.ColumnCount + 1];
            values[0] = ((r < data.DataRowRawIndexes.Count ? data.DataRowRawIndexes[r] : r) + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (var c = 0; c < data.ColumnCount; c++) values[c + 1] = data.GetCell(r, c);
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
    public (IReadOnlyList<ColumnPortrait> Columns, string Portrait, IReadOnlyList<CellFormat>? CellFormats)? BuildTablePortrait()
    {
        var data = _data;
        if (data is null) return null;
        var columns = TablePortrait.Build(data, _working);
        // 第 31 棒：把「哪几块的字长得跟别处不一样」也递过去——这是字号/粗体/居中的**唯一依据**。
        // 用户 2026-09-10 实测指出「AI 排版效果差，差在字体大小」，并自己判断出根因是「AI 读不到表格中
        // 字体、粗细、居中」；确实如此（styles.xml 以前只用来看日期与小数位）。CSV 没有格式可言，跳过。
        // 第 39 棒补一句：读是读到了，可**接着让模型把数字猜回来**才是真错处——所以现在同一份 xlsx 多读一份
        // 结构化的（ReadCellFormats）留给软件自己算，散文那一份照旧发出去（它还负责告诉模型哪一块是抄标签的样例）。
        var ext = Path.GetExtension(SourcePath ?? string.Empty).ToLowerInvariant();
        var isXlsx = ext is ".xlsx" or ".xlsm";
        IReadOnlyList<string>? formats = isXlsx
            ? XlsxTableReader.DescribeCellFormats(SourcePath!, SelectedSheet)
            : null;
        var cellFormats = isXlsx
            ? XlsxTableReader.ReadCellFormats(SourcePath!, SelectedSheet)
            : null;
        // 前导批注行与贴图也一并摊出去（第 20 棒）：纸规常写在表头以上，样张常贴在右侧，
        // 不递过去模型就只能凭列名猜，而用户 2026-09-09 定的主路径是「AI 自己看这张表」。
        return (columns, TablePortrait.Describe(columns, data.RowCount, data.Preamble, data.Images, formats), cellFormats);
    }

    /// <summary>这张表里贴着的图（模板截图 / 效果照片）。CSV 与没图的表是空表。</summary>
    public IReadOnlyList<SheetImage> SheetImages => _data?.Images ?? Array.Empty<SheetImage>();

    private void AutoSuggest()
    {
        var data = _data;
        if (data is null) return;

        var previousFixed = _working is null ? null : new Dictionary<string, string>(_working.FixedValues);
        _working = MappingSuggester.Suggest(data.Headers,
            string.IsNullOrWhiteSpace(ProfileName) ? "自动匹配方案" : ProfileName.Trim(),
            SideColumnIndexes);
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
        StatusMessage = $"这张表能把「{best.Option.Name}」填到 {best.Fit.Rate:P0}（比「{name}」贴），已自动改用前者；在第 3 步可以随时换回。"
                        // 换模板会连带换纸（第 68 棒）：两句都要让他看见，只留一句就是暗改了他另一件事。
                        + (Sheet.SheetFollowNote.Length > 0 ? "\n" + Sheet.SheetFollowNote : string.Empty);
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

    /// <summary>
    /// 落地一条 AI 给的字段绑定（第 30 棒）：把某个字段连到某一列，**只动这一个字段**。
    /// <para>走的仍是界面那条唯一的路——改 <see cref="FieldRowVm.ColumnIndex"/> 再 <c>ApplyMapping()</c>
    /// （<c>CollectProfile</c> 是从字段行收方案的，所以不存在"第二套落地口径"）。</para>
    /// <para>列下标越界、或这个字段本来就连在这一列，都如实回一句，不装作办了事。</para>
    /// </summary>
    public (bool Ok, string Message) BindField(LabelGou.Core.Marks.MarkFieldKey field, int columnIndex)
    {
        var data = _data;
        if (data is null) return (false, "还没导入表格。");
        if (columnIndex < 0 || columnIndex >= data.Headers.Count)
            return (false, $"这一列（第 {columnIndex + 1} 列）不在表里，没动。");
        var row = FieldRows.FirstOrDefault(r => r.FieldKey == field.ToString());
        if (row is null) return (false, "字段清单里没有这一项，没动。");
        var header = data.Headers[columnIndex];
        if (row.ColumnIndex == columnIndex)
            return (true, $"「{row.DisplayName}」本来就连在「{header}」这一列，不用改。");

        row.ColumnIndex = columnIndex;
        ApplyMapping();
        return (true, $"「{row.DisplayName}」已连到「{header}」"
            + $"（{LabelGou.Core.Data.HeaderRowDetector.ColumnLetter(columnIndex)}列），预览已跟着重算。");
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

    /// <summary>重列第 2 步那块提示：映射告警 + 当前模板的缺值提醒 + 字面量对不上号的告警（都是派生值，换模板/换表要跟着变）。</summary>
    private void RebuildIssueLines()
    {
        IssueLines.Clear();
        foreach (var line in _mapIssueLines) IssueLines.Add(line);
        foreach (var line in MissingValueLines()) IssueLines.Add(line);
        _templateCautions = LiteralGuardLines();
        foreach (var line in _templateCautions) IssueLines.Add(line);
        Raise(nameof(TemplateCautions));
    }

    /// <summary>
    /// 模板里写死的字面量与这批货对一遍（第 24 棒，活账 A-1）：判据本体在 Core（<see cref="TemplateLiteralGuard"/>），
    /// 这里只备三样料：这批已知的收货人/产地（字段值 + 整批固定值）与这张表出现过的全部格子。
    /// 一条数据都没映射出来时不对（那是「还没连好」，不是「对不上」，拿它喊人会越喊越乱）。</summary>
    private IReadOnlyList<string> LiteralGuardLines()
    {
        var template = SelectedTemplate?.Template;
        var data = _data;
        if (template is null || data is null || _rawRecords.Count == 0) return Array.Empty<string>();

        var consignees = _rawRecords.Select(r => r.GetText(MarkFieldKey.Consignee)).ToList();
        var origins = _rawRecords.Select(r => r.GetText(MarkFieldKey.Origin)).ToList();
        if (_working is { } profile)
        {
            consignees.Add(profile.FixedValueFor(MarkFieldKey.Consignee) ?? string.Empty);
            origins.Add(profile.FixedValueFor(MarkFieldKey.Origin) ?? string.Empty);
        }

        var cells = new List<string>();
        foreach (var row in data.Rows)
        {
            cells.AddRange(row);
            if (cells.Count >= 100_000) break;   // 封顶：判据宽一寸只是少喊一声，告警不是「绝无别家字样」的担保
        }
        cells.AddRange(data.Headers);
        foreach (var row in data.Preamble) cells.AddRange(row);
        return TemplateLiteralGuard.Check(template, consignees, origins, cells);
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

        // 第 89 棒①：行检查开着而"这一行"还不存在（还没导数据、或序号越界）时，退回这份模板的示意版式。
        // 从前那一路直接把整块预览清成 null，于是 ③ 步选模板时右侧一片空白——用户圈的那片红框就是这个
        // （他图上那颗「行检查」是勾着的，而表还没导）。有数据时画的仍是真那一行，兜底只在拿不到行时生效。
        var layout = (RowCheck ? BuildLayoutForRow(_currentIndex) : null) ?? BuildLayoutFor(_currentIndex);
        if (layout is null)
        {
            CurrentLayout = null;
            return;
        }

        RecordInfoText = RowCheck
            ? RowCheckInfo(_currentIndex) ?? "示意预览（未导入数据）"
            : _currentIndex >= 1 && _currentIndex <= _records.Count
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
        var baseTemplate = SelectedTemplate?.Template;
        if (baseTemplate is null) return null;

        MarkRecord record;
        int effectiveIndex;
        int total;
        bool realRecord;
        if (index >= 1 && index <= _records.Count)
        {
            record = _records[index - 1];
            effectiveIndex = index;
            total = Math.Max(1, _records.Count);
            realRecord = true;
        }
        else
        {
            record = SampleRecords.StandardSample();
            effectiveIndex = 1;
            total = Math.Max(1, _records.Count);
            realRecord = false;
        }

        // 定稿只认真数据：退回样例那一路不挂定稿。这一处与 PageContentSource 那边问的是同一份字典——
        // 屏幕上一支、纸上一支就是第 73 棒要防的那件事。
        var template = realRecord ? TemplateForRecord(baseTemplate, record) : baseTemplate;

        // 预览（单标签与整版）带参考底图，打印/导出走 PageContentSource，那边默认不含
        return LayoutEngine.Build(template, record,
            new LayoutContext(effectiveIndex, total, Path.GetFileName(_sourcePath),
                IncludeReference: true, TextCase: _textCase));
    }

    /// <summary>
    /// 出纸闸（第 38 棒 · 三道闸之一）：这份模板拿当前数据渲染后到底印不印得出东西。
    /// <para>第 36 棒的整张白纸就是从这漏的：令牌全取不到值 → 四行整条隐藏 → 空版照样落地
    /// （第 33 棒免点头之后这一层没人拦了）。<strong>只判「全空」不判「个别空」</strong>——
    /// 某一列本来就没值是常态，拦那种就是误伤（第 36 棒原口径）。</para>
    /// <para><c>IncludeReference:false</c>：参考底图不上纸，拿它撑「有内容」就是骗闸门。</para>
    /// </summary>
    public bool TemplatePrintsAnything(LabelTemplate template)
    {
        var record = _records.Count > 0 ? _records[0] : SampleRecords.StandardSample();
        var layout = LayoutEngine.Build(template, record,
            new LayoutContext(1, Math.Max(1, _records.Count), Path.GetFileName(_sourcePath),
                IncludeReference: false, TextCase: _textCase));
        return layout.Items.Count > 0;
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
        _sourceOriginalPath = null;   // 上一张表的"原件"也跟着作废：下次再导同一份文件算新表

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
