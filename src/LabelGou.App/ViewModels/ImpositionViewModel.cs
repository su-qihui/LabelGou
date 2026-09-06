using System.Collections.ObjectModel;
using LabelGou.App.Mvvm;
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

    public ImpositionViewModel(ILabelSource source)
    {
        _source = source;

        FirstPageCommand = new RelayCommand(() => PageIndex = 1, () => PageCount > 1);
        PrevPageCommand = new RelayCommand(() => PageIndex = Math.Max(1, PageIndex - 1), () => PageIndex > 1);
        NextPageCommand = new RelayCommand(() => PageIndex = Math.Min(PageCount, PageIndex + 1), () => PageIndex < PageCount);
        LastPageCommand = new RelayCommand(() => PageIndex = PageCount, () => PageCount > 1);
        ApplySheetCommand = new RelayCommand(RebuildPlan, () => Working is not null);
        SaveSheetAsCommand = new RelayCommand(SaveSheetAs, () => Working is not null && !Working.BuiltIn);
        ResetSheetCommand = new RelayCommand(ResetSheet, () => SelectedSheetOption is not null);

        foreach (var spec in _sheetStore.ListAll()) SheetOptions.Add(new SheetOption(spec));
        SelectedSheetOption = SheetOptions.FirstOrDefault(s => s.Spec.Id == BuiltInSheetSpecs.IdA4)
                              ?? SheetOptions.FirstOrDefault();

        foreach (var (mode, label) in new[]
                 {
                     (NumberingMode.KeepData, "沿用数据里的件号（缺项才补号）"),
                     (NumberingMode.ForceSequence, "强制重排：忽略数据件号，按规则连续编号"),
                     (NumberingMode.ExpandByCartonTotal, "按箱数展开：一行有几箱就出几张标签"),
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

    public SheetOption? SelectedSheetOption
    {
        get => _selectedSheetOption;
        set
        {
            if (!Set(ref _selectedSheetOption, value)) return;
            LoadWorkingFromSelection();
            RebuildPlan();
        }
    }

    /// <summary>true 表示当前选的是内置纸规（改动需另存）。</summary>
    public bool IsBuiltInSheet => Working?.BuiltIn ?? false;

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
            return;
        }

        var copy = origin.CloneAsUserCopy(origin.Name);
        copy.Id = origin.Id;
        copy.BuiltIn = origin.BuiltIn;
        Working = copy;
        Raise(nameof(IsBuiltInSheet));
        Raise(nameof(HasSheet));
        _selectedCropMark = CropMarkOptions.FirstOrDefault(o => o.Value == copy.CropMarks);
        Raise(nameof(SelectedCropMark));
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

    public NumberingMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            if (SelectedMode?.Value != value) SelectedMode = ModeOptions.FirstOrDefault(o => o.Value == value);
            RecomputeNumbering();
        }
    }

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
    public NumberingRule BuildRule() => new()
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
    };

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
        SelectedMode = ModeOptions.FirstOrDefault(o => o.Value == _mode) ?? SelectedMode;
        SelectedScope = ScopeOptions.FirstOrDefault(o => o.Value == _scope) ?? SelectedScope;
        SelectedGroup = GroupOptions.FirstOrDefault(o => Equals(o.Value, _groupBy)) ?? SelectedGroup;
        RaiseAll();
        RecomputeNumbering();
    }

    private void RaiseAll()
    {
        Raise(nameof(Mode));
        Raise(nameof(Scope));
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
            }
        }
    }

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

    /// <summary>数据或模板变了（主 VM 调用）：重算编号与拼版。</summary>
    public void RefreshFromSource()
    {
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

        var plan = ImpositionEngine.Build(spec, template.WidthMm, template.HeightMm, _labels.Count);
        Plan = plan;
        SheetIssues.Clear();
        foreach (var issue in plan.Issues)
        {
            SheetIssues.Add($"{Icon(issue.Severity)} {issue.Message}");
        }
        if (plan.PerPage <= 0)
        {
            PageIndex = 1;
        }
        PageIndex = Math.Min(PageIndex, Math.Max(1, plan.PageCount));
        Raise(nameof(PageText));
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
        if (!saved) return;

        SheetOptions.Clear();
        foreach (var spec in _sheetStore.ListAll()) SheetOptions.Add(new SheetOption(spec));
        SelectedSheetOption = SheetOptions.FirstOrDefault(s => s.Spec.Id == copy.Id) ?? SelectedSheetOption;
    }
}
