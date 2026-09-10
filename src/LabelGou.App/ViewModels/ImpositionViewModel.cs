using System.Collections.ObjectModel;
using LabelGou.App.Mvvm;
using LabelGou.App.Services;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Numbering;
using LabelGou.Core.Templates;

namespace LabelGou.App.ViewModels;

/// <summary>
/// 拼版视图模型需要的最小数据口：只读原始记录与模板，回头把编号结果交还给主视图模型。
/// 这样 M2 的状态不糊进 <see cref="MainViewModel"/>，两个 VM 也不互相引用成环。
/// </summary>
public interface ILabelSource
{
    /// <summary>映射后的原始记录（一行 = Excel 一行）。</summary>
    IReadOnlyList<MarkRecord> RawRecords { get; }

    /// <summary>当前所选模板。</summary>
    LabelTemplate? Template { get; }

    /// <summary>
    /// 表里的列名（原样，含空格与中英混排）。④ 步的「按哪一列数张数」要列出它们 ——
    /// 用户 2026-09-08：「张数一般表格里会有一列写的」，而那些列名不在 <see cref="MarkFieldKey"/> 那 19 个枚举里。
    /// </summary>
    IReadOnlyList<string> ColumnHeaders { get; }

    /// <summary>
    /// 某一列是否已经连到某个内置字段。<see cref="ColumnHeaders"/> 只给列名，而
    /// <strong>连上了的那一列不会另存一份 <c>col:</c> 键</strong>，展开取数时必须知道这个对应关系。
    /// </summary>
    MarkFieldKey? FieldBoundToColumn(string column);

    /// <summary>取第 N 张标签（1 起）的版面；整版预览按页临时取用。</summary>
    LabelLayout? BuildLayoutAt(int labelIndex);
}

/// <summary>纸规下拉项。</summary>
public sealed class SheetOption
{
    public SheetOption(SheetSpec spec) => Spec = spec;

    public SheetSpec Spec { get; }

    public string DisplayName => Spec.BuiltIn ? $"{Spec.Description} [内置]" : Spec.Description;
}

/// <summary>带中文说明的下拉项包装。</summary>
public sealed class ChoiceOption<T>
{
    public ChoiceOption(T value, string label)
    {
        Value = value;
        Label = label;
    }

    public T Value { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>
/// M2 的界面状态：整版拼版 + 件号自动编号。
/// <para>
/// 几何与规则一律交给 Core 的 <see cref="ImpositionEngine"/> / <see cref="NumberingEngine"/>，
/// 这里只做输入绑定、结果展示与翻页。
/// </para>
/// </summary>
public sealed class ImpositionViewModel : ObservableObject
{
    private readonly SheetSpecStore _sheetStore = new();
    private readonly ILabelSource _source;
    private readonly UiStateStore? _uiState;

    private SheetOption? _selectedSheetOption;
    private SheetSpec? _working;
    private SheetPlan? _plan;
    private int _pageIndex = 1;
    private double _sheetZoom = 1.6;
    private NumberingMode _mode = NumberingMode.KeepData;
    private NumberingScope _scope = NumberingScope.Global;
    private MarkFieldKey? _groupBy;
    private int _start = 1;
    private int _step = 1;
    private int _padDigits;
    private string _prefix = string.Empty;
    private string _suffix = string.Empty;
    private int _copies = 1;
    private IReadOnlyList<MarkRecord> _labels = Array.Empty<MarkRecord>();

    /// <param name="source">数据口（模板与原始记录）。</param>
    /// <param name="uiState">界面状态；传了才会记住上次用的纸规（单测不传，避免写用户目录）。</param>
    public ImpositionViewModel(ILabelSource source, UiStateStore? uiState = null)
    {
        _source = source;
        _uiState = uiState;

        FirstPageCommand = new RelayCommand(() => PageIndex = 1, () => PageCount > 1);
        PrevPageCommand = new RelayCommand(() => PageIndex = Math.Max(1, PageIndex - 1), () => PageIndex > 1);
        NextPageCommand = new RelayCommand(() => PageIndex = Math.Min(PageCount, PageIndex + 1), () => PageIndex < PageCount);
        LastPageCommand = new RelayCommand(() => PageIndex = PageCount, () => PageCount > 1);
        ApplySheetCommand = new RelayCommand(RebuildPlan, () => Working is not null);
        // 另存对用户改的一律开放：上一版判的是 !Working.BuiltIn，而工作副本的 BuiltIn 是从种子回填过来的 true，
        // 默认档（一页一枚，内置）就这样把按钮永久灰掉 —— 用户改的页边重启即丢。
        // SaveSheetAs 走的是 CloneAsUserCopy，不会碰到种子，没有需要挡的那只手。
        SaveSheetAsCommand = new RelayCommand(SaveSheetAs, () => Working is not null);
        ResetSheetCommand = new RelayCommand(ResetSheet, () => SelectedSheetOption is not null);

        ReloadSheetOptions();
        // 先接上次用的那张纸（一开四这类纸规选过一次就不该每次重选），没记过才退回一页一枚 / A4。
        var rememberedId = _uiState?.Load().SheetSpecId;
        // 上一棒这里把记着的「一开四」当陈旧值强制让位给一页一枚，理由是「那只是裁切指令」。
        // 用户 2026-09-08 拿红框否掉了这个理解（「开四就是一张排 4 个一模一样的」），于是它不再是一次迁移，
        // 而是静默改掉用户选的纸 —— 直接拿用户记下的那张。
        _bootstrapping = true;
        SelectedSheetOption = SheetOptions.FirstOrDefault(s => s.Spec.Id == rememberedId)
                              ?? SheetOptions.FirstOrDefault(s => s.Spec.Id == BuiltInSheetSpecs.IdOnePerLabel)
                              ?? SheetOptions.FirstOrDefault(s => s.Spec.Id == BuiltInSheetSpecs.IdA4)
                              ?? SheetOptions.FirstOrDefault();
        _bootstrapping = false;

        foreach (var (mode, label) in new[]
                 {
                     (NumberingMode.KeepData, "沿用数据里的件号（缺项才补号）"),
                     (NumberingMode.ForceSequence, "强制重排：忽略数据件号，按规则连续编号"),
                     (NumberingMode.ExpandByCartonTotal, "按每行的张数展开：一行写几张纸就出几张整张纸"),
                 })
        {
            ModeOptions.Add(new ChoiceOption<NumberingMode>(mode, label));
        }
        Mode = NumberingMode.KeepData;

        ScopeOptions.Add(new ChoiceOption<NumberingScope>(NumberingScope.Global, "全局连续（总件数 = 全部箱数）"));
        ScopeOptions.Add(new ChoiceOption<NumberingScope>(NumberingScope.PerGroup, "按字段分组（组内重新起号，总件数 = 组内箱数）"));
        Scope = NumberingScope.Global;

        GroupOptions.Add(new ChoiceOption<MarkFieldKey?>(null, "（不分组）"));
        foreach (var def in MarkFieldCatalog.All)
        {
            GroupOptions.Add(new ChoiceOption<MarkFieldKey?>(def.Key, def.ChineseName));
        }
        SelectedGroup = GroupOptions[0];

        SelectedMode = ModeOptions[0];
        SelectedScope = ScopeOptions[0];
    }

    // ---------- 命令 ----------

    public RelayCommand FirstPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand LastPageCommand { get; }
    public RelayCommand ApplySheetCommand { get; }
    public RelayCommand SaveSheetAsCommand { get; }
    public RelayCommand ResetSheetCommand { get; }

    /// <summary>编号结果变了：主 VM 需要把预览与记录导航换成新的标签集。</summary>
    public event Action<IReadOnlyList<MarkRecord>>? NumberingApplied;

    // ---------- 纸规 ----------

    public ObservableCollection<SheetOption> SheetOptions { get; } = new();

    /// <summary>裁切线三种画法的中文选项。</summary>
    public ObservableCollection<ChoiceOption<CropMarkMode>> CropMarkOptions { get; } = new()
    {
        new(CropMarkMode.None, "不画裁切线"),
        new(CropMarkMode.SheetCorners, "只画整版四角（密排首选）"),
        new(CropMarkMode.EveryLabel, "每枚四角都画（手工裁切）"),
    };

    private ChoiceOption<CropMarkMode>? _selectedCropMark;

    public ChoiceOption<CropMarkMode>? SelectedCropMark
    {
        get => _selectedCropMark;
        set
        {
            if (!Set(ref _selectedCropMark, value) || value is null) return;
            if (Working is not null) Working.CropMarks = value.Value;
            RebuildPlan();
        }
    }

    /// <summary>是否已选好纸规（面板可用）。</summary>
    public bool HasSheet => Working is not null;

    /// <summary>
    /// 一页只排同一枚唛头（④ 步那个勾，2026-09-08 用户要「一整张排同一个」，默认开）。
    /// <para>绑 VM 属性而不是直接绑 <c>Sheet.Working.RepeatSameLabelPerPage</c>：纸规不是
    /// <c>INotifyPropertyChanged</c>，直接绑完勾完不会重算方案，页数不动，就是第 11 棒修过的那类
    /// 「改了没反应」。上面的 <see cref="SelectedCropMark"/> 已经是同一个做法。</para>
    /// </summary>
    public bool RepeatSameLabelPerPage
    {
        get => Working?.RepeatSameLabelPerPage ?? true;
        set
        {
            if (Working is null || Working.RepeatSameLabelPerPage == value) return;
            Working.RepeatSameLabelPerPage = value;
            RebuildPlan();
        }
    }

    public SheetOption? SelectedSheetOption
    {
        get => _selectedSheetOption;
        set
        {
            if (!Set(ref _selectedSheetOption, value)) return;
            LoadWorkingFromSelection();
            RebuildPlan();
            RememberSheetSpecId(value?.Spec.Id);
            // 第 17 棒：主 VM 要在纸规换了之后重算「模板尺寸与这张纸配不配」那句提示。
            SheetSelectionChanged?.Invoke();
        }
    }

    /// <summary>纸规选项换了（不是改数值）时喊一声。只有选错一张纸才会让模板与刀模对不上，改页边不会。</summary>
    public event Action? SheetSelectionChanged;

    /// <summary>构造兜底那一次赋值不写盘（第 23 棒：与 MainViewModel.RememberTemplateId 同一个理由）。</summary>
    private bool _bootstrapping;

    /// <summary>把纸规 id 写进界面状态；与已记的相同就不写盘（启动那一次赋值不该产生 IO）。</summary>
    private void RememberSheetSpecId(string? specId)
    {
        if (_bootstrapping) return;     // 启动兜底不写盘（第 23 棒）
        var store = _uiState;
        if (store is null || string.IsNullOrEmpty(specId)) return;
        var state = store.Load();
        if (state.SheetSpecId == specId) return;
        state.SheetSpecId = specId;
        store.Save(state);
    }

    /// <summary>true 表示当前选的是内置纸规（改动需另存）。</summary>
    public bool IsBuiltInSheet => Working?.BuiltIn ?? false;

    /// <summary>当前是不是「一页一枚」（纸面跟随标签）。</summary>
    public bool FollowsLabelSheet => Working?.FollowsLabel ?? false;

    /// <summary>
    /// 纸宽/纸高这两个框能不能改。
    /// <para>FollowsLabel 下它们每次都被引擎按标签尺寸覆写，让输入框看起来能改就是假旋钮。</para>
    /// </summary>
    public bool PaperSizeEditable => !FollowsLabelSheet;

    /// <summary>界面直接编辑的那份纸规（内置纸规的副本，改它不会污染种子）。</summary>
    public SheetSpec? Working
    {
        get => _working;
        private set => Set(ref _working, value);
    }

    /// <summary>把选中纸规复制一份出来给界面改（内置种子只读，改副本不会污染种子）。</summary>
    private void LoadWorkingFromSelection()
    {
        var origin = SelectedSheetOption?.Spec;
        if (origin is null)
        {
            Working = null;
            Raise(nameof(IsBuiltInSheet));
            Raise(nameof(FollowsLabelSheet));
            Raise(nameof(PaperSizeEditable));
            Raise(nameof(RepeatSameLabelPerPage));
            return;
        }

        var copy = origin.CloneAsUserCopy(origin.Name);
        copy.Id = origin.Id;
        copy.BuiltIn = origin.BuiltIn;
        Working = copy;
        Raise(nameof(IsBuiltInSheet));
        Raise(nameof(HasSheet));
        Raise(nameof(FollowsLabelSheet));
        Raise(nameof(PaperSizeEditable));
        Raise(nameof(RepeatSameLabelPerPage));
        _selectedCropMark = CropMarkOptions.FirstOrDefault(o => o.Value == copy.CropMarks);
        Raise(nameof(SelectedCropMark));
    }

    /// <summary>把用户纸规目录重列一遍，顺便记下被跳过的坏文件（上一版它们静默蒸发，没人知道）。</summary>
    private void ReloadSheetOptions()
    {
        var report = _sheetStore.ListWithReport();
        SheetOptions.Clear();
        foreach (var spec in report.Specs) SheetOptions.Add(new SheetOption(spec));
        _skippedSheetFiles = report.SkippedFiles;
        if (_skippedSheetFiles.Count > 0)
        {
            AppLog.Warning($"纸规目录里有 {_skippedSheetFiles.Count} 个文件没读进来：{string.Join("；", _skippedSheetFiles)}");
            AppendSkippedSheetFiles();
        }
    }

    /// <summary>被跳过的坏纸规文件（文件名 + 原因）；每次重列 SheetIssues 后都要补回去。</summary>
    private IReadOnlyList<string> _skippedSheetFiles = Array.Empty<string>();

    private void AppendSkippedSheetFiles()
    {
        foreach (var line in _skippedSheetFiles)
            SheetIssues.Add($"⚠ 这个纸规文件被跳过（改坏了或不属于本程序）：{line}");
    }

    private void ResetSheet()
    {
        LoadWorkingFromSelection();
        RebuildPlan();
    }

    // ---------- 编号规则 ----------

    public ObservableCollection<ChoiceOption<NumberingMode>> ModeOptions { get; } = new();

    public ObservableCollection<ChoiceOption<NumberingScope>> ScopeOptions { get; } = new();

    public ObservableCollection<ChoiceOption<MarkFieldKey?>> GroupOptions { get; } = new();

    private ChoiceOption<NumberingMode>? _selectedMode;

    public ChoiceOption<NumberingMode>? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (Set(ref _selectedMode, value) && value is not null) Mode = value.Value;
        }
    }

    private ChoiceOption<NumberingScope>? _selectedScope;

    public ChoiceOption<NumberingScope>? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (Set(ref _selectedScope, value) && value is not null) Scope = value.Value;
        }
    }

    private ChoiceOption<MarkFieldKey?>? _selectedGroup;

    public ChoiceOption<MarkFieldKey?>? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (Set(ref _selectedGroup, value) && value is not null) GroupBy = value.Value;
        }
    }

    /// <summary>「按哪一列数张数」的候选：第一项是不选（沿用连接好的字段），其余是表里的列名原样。</summary>
    public ObservableCollection<ChoiceOption<string?>> ExpandColumnOptions { get; } = new();

    private ChoiceOption<string?>? _selectedExpandColumn;
    private string? _expandColumn;

    /// <summary>展开时按表里哪一列数张数（null = 沿用 NumberingRule.ExpandCountField 那个字段）。</summary>
    public ChoiceOption<string?>? SelectedExpandColumn
    {
        get => _selectedExpandColumn;
        set
        {
            if (!Set(ref _selectedExpandColumn, value) || value is null) return;
            _expandColumn = value.Value;
            RecomputeNumbering();
        }
    }

    /// <summary>
    /// 换表或重导后重建展开列候选。列名一律原样取自表头（含空格与中英混排），不猜也不洗。
    /// <para>此前它根本没法选：ExpandCountField 是封闭的 19 个枚举，表里那列叫「打印张数」就选不到。</para>
    /// </summary>
    public void ReloadExpandColumns()
    {
        var keep = _expandColumn;
        ExpandColumnOptions.Clear();
        ExpandColumnOptions.Add(new ChoiceOption<string?>(null, "（不选，用连接好的「总件数」字段）"));
        foreach (var header in _source.ColumnHeaders)
        {
            if (!string.IsNullOrWhiteSpace(header))
            {
                // 值用表头原样（精确匹配才取到数），只把给人看的那一行折平：真表头常带换行（「件数(换行)CTN」）。
                ExpandColumnOptions.Add(new ChoiceOption<string?>(header, ColumnLabel.SingleLine(header)));
            }
        }
        // 直接改私有字段再 Raise：走 setter 会再触发一次重算，而调用方紧接着就要重算
        var match = ExpandColumnOptions.FirstOrDefault(o => o.Value == keep) ?? ExpandColumnOptions[0];
        _selectedExpandColumn = match;
        _expandColumn = match.Value;
        Raise(nameof(SelectedExpandColumn));
    }

    public NumberingMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            if (SelectedMode?.Value != value) SelectedMode = ModeOptions.FirstOrDefault(o => o.Value == value);
            Raise(nameof(IsExpandMode));
            RecomputeNumbering();
        }
    }

    /// <summary>只有「按每行的张数展开」这一档才需要选展开列，界面拿它灰掉那个下拉。</summary>
    public bool IsExpandMode => Mode == NumberingMode.ExpandByCartonTotal;

    public NumberingScope Scope
    {
        get => _scope;
        set
        {
            if (!Set(ref _scope, value)) return;
            if (SelectedScope?.Value != value) SelectedScope = ScopeOptions.FirstOrDefault(o => o.Value == value);
            RecomputeNumbering();
        }
    }

    public MarkFieldKey? GroupBy
    {
        get => _groupBy;
        set
        {
            if (!Set(ref _groupBy, value)) return;
            if (!Equals(SelectedGroup?.Value, value)) SelectedGroup = GroupOptions.FirstOrDefault(o => o.Value == value);
            RecomputeNumbering();
        }
    }

    public int Start
    {
        get => _start;
        set { if (Set(ref _start, value)) RecomputeNumbering(); }
    }

    public int Step
    {
        get => _step;
        set { if (Set(ref _step, value)) RecomputeNumbering(); }
    }

    public int PadDigits
    {
        get => _padDigits;
        set { if (Set(ref _padDigits, value)) RecomputeNumbering(); }
    }

    public string Prefix
    {
        get => _prefix;
        set { if (Set(ref _prefix, value ?? string.Empty)) RecomputeNumbering(); }
    }

    public string Suffix
    {
        get => _suffix;
        set { if (Set(ref _suffix, value ?? string.Empty)) RecomputeNumbering(); }
    }

    public int Copies
    {
        get => _copies;
        set { if (Set(ref _copies, value)) RecomputeNumbering(); }
    }

    /// <summary>当前编号规则（每次现拼，保证与界面上的各个属性一致）。</summary>
    public NumberingRule BuildRule()
    {
        // 用户选的那一列如果已经连上某个内置字段，就把那个字段一起递过去（Core 会先查列名再退回字段）；
        // 没连上的列保持默认的「总件数」占位，那时真的按列名读。
        var bound = _expandColumn is { } column ? _source.FieldBoundToColumn(column) : null;
        return new NumberingRule
        {
            Name = "当前任务",
            Mode = Mode,
            Scope = Scope,
            GroupByField = GroupBy,
            Start = Start,
            Step = Step,
            PadDigits = PadDigits,
            Prefix = Prefix,
            Suffix = Suffix,
            Copies = Copies,
            ExpandCountField = bound ?? MarkFieldKey.CartonTotal,
            ExpandCountColumn = _expandColumn,
        };
    }

    /// <summary>从映射方案里恢复规则（同一客户反复来单不必再设一遍）。</summary>
    public void ApplySavedRule(NumberingRule? saved)
    {
        if (saved is null) return;
        _mode = saved.Mode;
        _scope = saved.Scope;
        _groupBy = saved.GroupByField;
        _start = saved.Start;
        _step = saved.Step;
        _padDigits = saved.PadDigits;
        _prefix = saved.Prefix;
        _suffix = saved.Suffix;
        _copies = saved.Copies;
        _expandColumn = saved.ExpandCountColumn;
        SelectedMode = ModeOptions.FirstOrDefault(o => o.Value == _mode) ?? SelectedMode;
        SelectedScope = ScopeOptions.FirstOrDefault(o => o.Value == _scope) ?? SelectedScope;
        SelectedGroup = GroupOptions.FirstOrDefault(o => Equals(o.Value, _groupBy)) ?? SelectedGroup;
        // 方案里记的那一列这张新表没有：清回「不选」。旧行为下拉停在旧选项、引擎却按 1 张/行跑，
        // 界面说的与实际做的两套（第 23 棒审计-9）。
        var expandMatch = ExpandColumnOptions.FirstOrDefault(o => o.Value == _expandColumn);
        if (expandMatch is null) _expandColumn = null;
        SelectedExpandColumn = expandMatch ?? ExpandColumnOptions.FirstOrDefault() ?? SelectedExpandColumn;
        Raise(nameof(IsExpandMode));
        RaiseAll();
        RecomputeNumbering();
    }

    private void RaiseAll()
    {
        Raise(nameof(Mode));
        Raise(nameof(Scope));
        Raise(nameof(SelectedExpandColumn));
        Raise(nameof(GroupBy));
        Raise(nameof(Start));
        Raise(nameof(Step));
        Raise(nameof(PadDigits));
        Raise(nameof(Prefix));
        Raise(nameof(Suffix));
        Raise(nameof(Copies));
    }

    // ---------- 结果 ----------

    /// <summary>编号后的标签记录集（一箱一张，含每张份数的复制件）。</summary>
    public IReadOnlyList<MarkRecord> Labels => _labels;

    public SheetPlan? Plan
    {
        get => _plan;
        private set
        {
            if (Set(ref _plan, value))
            {
                Raise(nameof(PageCount));
                Raise(nameof(PlanText));
                Raise(nameof(HasPlan));
                PlanChanged?.Invoke();
            }
        }
    }

    /// <summary>整版方案换了（重算/换纸规/编号变化）。M3 的输出面板订阅它刷新页数与体积估算。</summary>
    public event Action? PlanChanged;

    public bool HasPlan => Plan is not null;

    public ObservableCollection<string> SheetIssues { get; } = new();

    public ObservableCollection<string> NumberingIssues { get; } = new();

    public string PlanText => Plan is null ? "尚未生成整版方案" : Plan.Describe();

    public string PageText => Plan is null || Plan.PageCount == 0
        ? "第 0 / 0 页"
        : $"第 {PageIndex} / {Plan.PageCount} 页（本页 {Plan.PlacementsOnPage(PageIndex).Count} 枚）";

    public int PageCount => Plan?.PageCount ?? 0;

    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            var max = Math.Max(1, PageCount);
            if (!Set(ref _pageIndex, Math.Max(1, Math.Min(value, max)))) return;
            Raise(nameof(PageText));
            Raise(nameof(PageIndex));
            RaiseCommands();
        }
    }

    public double SheetZoom
    {
        get => _sheetZoom;
        set => Set(ref _sheetZoom, Math.Max(0.1, Math.Min(value, 8)));
    }

    /// <summary>整版预览控件取标签版面的回调（按页临时取，不一次算完）。</summary>
    public LabelLayout? LayoutFor(int labelIndex) => _source.BuildLayoutAt(labelIndex);

    private void RaiseCommands() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    /// <summary>数据或模板变了（主 VM 调用）：重建展开列候选，再重算编号与拼版。</summary>
    public void RefreshFromSource()
    {
        ReloadExpandColumns();
        RecomputeNumbering();
    }

    /// <summary>只重算拼版（纸规/模板变了，但标签数没变时用）。</summary>
    public void RebuildPlan()
    {
        var spec = Working;
        var template = _source.Template;
        if (spec is null || template is null)
        {
            Plan = null;
            SheetIssues.Clear();
            if (template is null) SheetIssues.Add("还没选模板，先回第 ③ 步。");
            return;
        }

        // 「一页只排同一枚」开不开只看纸规那个开关：开着就是一枚唛头独占一页、页内铺满全同份数，
        // 所以不再需要把分组键递给引擎（上一棒递的 SourceRowIndex 已无意义，那正是用户拿红框否掉的旧语义）。
        var plan = ImpositionEngine.Build(spec, template.WidthMm, template.HeightMm, _labels.Count);
        Plan = plan;
        SheetIssues.Clear();
        foreach (var issue in plan.Issues)
        {
            SheetIssues.Add($"{Icon(issue.Severity)} {issue.Message}");
        }
        AppendSkippedSheetFiles();
        if (plan.PerPage <= 0)
        {
            PageIndex = 1;
        }
        PageIndex = Math.Min(PageIndex, Math.Max(1, plan.PageCount));
        Raise(nameof(PageText));
        // Build 会就地改写 Working 的纸宽/纸高（FollowsLabel 的那次展开），而 SheetSpec 不带变更通知，
        // 绑定不会自己回读：不 Raise 一下，界面上还留着用户刚填的旧数，看着就是「改了没反应」。
        Raise(nameof(Working));
        RaiseCommands();
    }

    private void RecomputeNumbering()
    {
        var rule = BuildRule();
        var result = NumberingEngine.Apply(_source.RawRecords, rule);
        _labels = result.Labels;

        NumberingIssues.Clear();
        foreach (var issue in result.Issues)
        {
            NumberingIssues.Add($"{Icon(issue.Severity)} {issue.Message}");
        }
        NumberingIssues.Insert(0, $"{result.Describe()} · {rule.Describe()}");

        NumberingApplied?.Invoke(_labels);
        RebuildPlan();
    }

    private static string Icon(IssueLevel level) => level switch
    {
        IssueLevel.Error => "❌",
        IssueLevel.Warning => "⚠",
        _ => "ℹ",
    };

    /// <summary>
    /// 按名字选一张这台机器上真有的纸规（第 21 棒：AI 提案点名用）。
    /// <para>只做精确匹配（去空格后的原名或忽略大小写），<strong>认不出就保持现状并说一句人话</strong>：
    /// 拿「一页一枚」模糊匹配到「A4 一行两枚」这种邻家名字，结果是换错了纸，比不换还坏。</para>
    /// </summary>
    public string SelectSheetSpecByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "没点名纸规，保持现在这张。";
        var wanted = name.Replace(" ", string.Empty);
        var hit = SheetOptions.FirstOrDefault(o => string.Equals(o.Spec.Name.Replace(" ", string.Empty), wanted, StringComparison.Ordinal))
                  ?? SheetOptions.FirstOrDefault(o => string.Equals(o.Spec.Name.Replace(" ", string.Empty), wanted, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            return $"这台机器上没有叫「{name}」的纸规，保持现在这张（可选：{string.Join("、", SheetOptions.Select(o => o.Spec.Name).Take(8))}）。";
        SelectedSheetOption = hit;
        return $"纸规已切到「{hit.Spec.Name}」。";
    }

    private void SaveSheetAs()
    {
        var working = Working;
        if (working is null) return;

        var copy = working.CloneAsUserCopy(string.IsNullOrWhiteSpace(working.Name) ? "我的纸规" : working.Name);
        var (saved, fileName, issues) = _sheetStore.Save(copy);
        SheetIssues.Clear();
        foreach (var issue in issues)
        {
            SheetIssues.Add($"{Icon(issue.Severity)} {issue.Message}");
        }
        AppendSkippedSheetFiles();
        if (!saved) return;

        ReloadSheetOptions();
        SelectedSheetOption = SheetOptions.FirstOrDefault(s => s.Spec.Id == copy.Id) ?? SelectedSheetOption;
    }
}
