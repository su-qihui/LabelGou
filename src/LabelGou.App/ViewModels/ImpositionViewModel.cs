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
            RememberTemplateBinding(value?.Spec.Id);
            // 第 17 棒：主 VM 要在纸规换了之后重算「模板尺寸与这张纸配不配」那句提示。
            SheetSelectionChanged?.Invoke();
        }
    }

    /// <summary>纸规选项换了（不是改数值）时喊一声。只有选错一张纸才会让模板与刀模对不上，改页边不会。</summary>
    public event Action? SheetSelectionChanged;

    /// <summary>构造兜底那一次赋值不写盘（第 23 棒：与 MainViewModel.RememberTemplateId 同一个理由）。</summary>
    private bool _bootstrapping;

    /// <summary>第 68 棒：正在按模板自动换纸。那不算用户的选择，所以既不当作绑定来记，也不写全局「上次用的纸」。</summary>
    private bool _applyingTemplateSheet;

    /// <summary>
    /// 上一次「按模板自动换纸」说的那句人话；没换就是空串。
    /// <para>留成属性而不是直接写状态栏：换模板有时会连带换纸（第 68 棒），而自动挑模板那条路
    /// <c>PickTemplateFittingData</c> 紧跟着要写自己那句——两句都得让他看见，只留一句就是暗改了他另一件事。</para>
    /// </summary>
    public string SheetFollowNote { get; private set; } = string.Empty;

    /// <summary>
    /// 纸规跟着模板走（用户 2026-09-14：「只要长宽是 140×100，纸规自动变成 280×200 的 2×2 排布」
    /// 「模版和纸归是绑定的，若后续再次使用那个模板纸归也会变成此模板对应纸归」）。
    /// <para>两步判据，前一步命中就不再看后一步：
    /// ① <strong>这份模板绑过纸</strong>（他自己为它挑过一张，见 <see cref="RememberTemplateBinding"/>）—— 用它，
    /// 这条就是「再用那个模板，纸也回来」，也是他手动改纸之后不会被系统档顶掉的理由；
    /// ② <strong>按单枚尺寸找预设档</strong>（一开四 / 一开八 / 大开二 / 小开二 / A4 小标那些填了刀模的）：
    /// 他手上这张就在候选里，就不搬（同样是 140×100，他从「自己另存的那张一开四」换到另一份 140×100 模板，
    /// 没道理被换成内置那一张），否则正着对得上的优先，其次才是转 90° 对得上的。
    /// 两步都没有（新建的怪尺寸模板）就<strong>保持现状不建纸规</strong>，纸由他在 ④ 步自己定 —— 那是他给的口径。</para>
    /// <para>「一页一枚」与 A4/A3 这类<strong>刀模留空（跟随模板）</strong>的档不算尺寸匹配的证据：它们天生装得下任何模板，
    /// 拿它们当「已经配好」就永远跳不到一开四那一档了。它们能被记住，靠的是第 ① 步（他点过就是他的选择）。</para>
    /// <para>只换下拉里真存着的那一张，不新建、不改谁的刀模；<strong>不跳步骤</strong>（他明确说「不用跳转到④页面」）。
    /// ④ 步那句错配提示旁的「换成配套模板」是反过来的动作（拿模板去就纸），所以那条路会临时不接这一步，
    /// 见 <c>MainViewModel._suppressSheetFollow</c>。</para>
    /// </summary>
    /// <param name="follow">
    /// false = 这一次换模板<strong>不</strong>连带换纸（④ 步「换成配套模板」：那是拿模板来就他手上这张纸）。
    /// 进来仍会把上一次的说明清空，免得他看到一句属于上一次的「纸规已跟到…」。
    /// </param>
    /// <returns>一句人话（什么都没换就是空串）。</returns>
    public string ApplySheetForTemplate(bool follow = true)
    {
        SheetFollowNote = string.Empty;
        if (!follow) return string.Empty;
        var template = _source.Template;
        if (template is null) return string.Empty;
        var (w, h) = (template.WidthMm, template.HeightMm);

        var hit = BoundSheetOption(template.Id) ?? SizeMatchedOption(w, h);
        if (hit is null || ReferenceEquals(hit, SelectedSheetOption)) return string.Empty;

        _applyingTemplateSheet = true;
        try
        {
            SelectedSheetOption = hit;
        }
        finally
        {
            _applyingTemplateSheet = false;
        }

        SheetFollowNote = $"模板「{template.Name}」是 {w:0.#}×{h:0.#} mm，纸规已跟到「{hit.Spec.Name}」；"
                          + "不合适就在 ④ 步换一张，换完这张就归这份模板了。";
        return SheetFollowNote;
    }

    /// <summary>
    /// 表里写着怎么开纸，就照它改纸规（第 101 棒，用户 2026-09-24：「②调整为看到就修改」）。
    /// <para>调用时机在「纸规跟着模板走」**之后**：那一刻软件刚按单枚尺寸推断完一档，而表里那句
    /// 「一开四」是厂方对这一批货的明确说法，比尺寸推断更近，所以它覆盖前者。</para>
    /// <para><strong>但它不写进模板↔纸规那份绑定</strong>（走 <see cref="_applyingTemplateSheet"/> 那道旗）：
    /// 那份记录是他自己为这份模板挑过的长期选择，一批表里的一句指令不该去改它。</para>
    /// </summary>
    public string ApplySheetFromTable(LabelGou.Core.Data.TabularData data)
    {
        var found = Services.SheetSpecHints.DetectInTable(data);
        if (found is null) return string.Empty;

        if (!found.MatchesSpec)
        {
            // 认到了写法却没有那一档：不猜一张纸出来，只把这句话回显给他（他要就自己去 ④ 步新建）。
            SheetFollowNote = $"表里那句「{found.Evidence}」写的是尺寸，可软件没有这一档纸规——纸规没动，" +
                              "要就在 ④ 步自己挑一张或新建一档。";
            return SheetFollowNote;
        }

        var hit = SheetOptions.FirstOrDefault(o => o.Spec.Id == found.SpecId);
        if (hit is null || ReferenceEquals(hit, SelectedSheetOption)) return string.Empty;

        _applyingTemplateSheet = true;
        try
        {
            SelectedSheetOption = hit;
        }
        finally
        {
            _applyingTemplateSheet = false;
        }
        SheetFollowNote = $"表里写着「{found.Evidence}」，纸规已照它改成「{hit.Spec.Name}」；不对就在 ④ 步换回来。";
        return SheetFollowNote;
    }

    /// <summary>与 MainViewModel.TemplateSheetHint 同一口径：0.6 mm 以内就是同一张刀模。</summary>
    private static bool Nearly(double a, double b) => Math.Abs(a - b) < 0.6;
    /// <summary>这份模板绑过的那张纸（纸规被删了、或没记过 → null，退回下一条判据）。</summary>
    private SheetOption? BoundSheetOption(string templateId)
    {
        var store = _uiState;
        if (store is null) return null;
        var bound = store.Load().TemplateSheetIds?.TryGetValue(templateId, out var id) == true ? id : null;
        return string.IsNullOrEmpty(bound) ? null : SheetOptions.FirstOrDefault(o => o.Spec.Id == bound);
    }

    /// <summary>按单枚尺寸找预设档：他现在这张就在候选里则不搬，其次正着对得上的，最后才是转 90° 对得上的。</summary>
    private SheetOption? SizeMatchedOption(double w, double h)
    {
        SheetOption? exact = null;
        SheetOption? swapped = null;
        foreach (var option in SheetOptions)
        {
            var spec = option.Spec;
            if (spec.FollowsLabel || spec.FollowTemplateSize) continue;
            var same = Nearly(spec.LabelWidthMm, w) && Nearly(spec.LabelHeightMm, h);
            if (same && ReferenceEquals(option, SelectedSheetOption)) return option;
            if (same) exact ??= option;
            else if (swapped is null && Nearly(spec.LabelWidthMm, h) && Nearly(spec.LabelHeightMm, w)) swapped = option;
        }
        return exact ?? swapped;
    }

    /// <summary>
    /// 他自己换纸那一下：把「这份模板 → 这张纸」记下来（第 68 棒绑定的写入点，只此一处）。
    /// <para>自动接手（<see cref="_applyingTemplateSheet"/>）与启动兜底（<see cref="_bootstrapping"/>）都不记：
    /// 前者不是他的选择，后者那时模板还没选好。</para>
    /// </summary>
    private void RememberTemplateBinding(string? specId)
    {
        var store = _uiState;
        var templateId = _source.Template?.Id;
        if (store is null || _bootstrapping || _applyingTemplateSheet) return;
        if (string.IsNullOrEmpty(templateId) || string.IsNullOrEmpty(specId)) return;

        var state = store.Load();
        state.TemplateSheetIds ??= new Dictionary<string, string>();
        if (state.TemplateSheetIds.TryGetValue(templateId, out var existing)
            && string.Equals(existing, specId, StringComparison.Ordinal)) return;
        state.TemplateSheetIds[templateId] = specId;
        store.Save(state);
    }

    /// <summary>把纸规 id 写进界面状态；与已记的相同就不写盘（启动那一次赋值不该产生 IO）。</summary>
    private void RememberSheetSpecId(string? specId)
    {
        if (_bootstrapping || _applyingTemplateSheet) return;     // 启动兜底与自动跟模板都不写盘（第 23、68 棒）
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

    /// <summary>
    /// AI 提案落地时把「按哪一列数张数」真接到拼版上（第 40 棒补）。
    /// <para>用户实测：AI 问了「是否将 x 列设为张数」、答了「是」，预览仍是每行一张——因为提案只把那一列
    /// 记在 <c>Readout.QtyColumn</c> 上（够拼那五行摘要里的「预览:31个,155张」），却没接到这里的展开列，
    /// 拼版引擎仍按默认的「沿用数据件号」一行出一张。这就是他说的「权限接口没给到 AI」。</para>
    /// <para>切表会重建展开列候选并把展开列复位，所以这一步必须在切表与字段绑定都落完之后调用。
    /// 表里找不到那一列（切表后列名变了）就如实说没接上，不猜一列——猜错就是数错张数、印错货。</para>
    /// </summary>
    /// <returns>一句人话：接上了 / 没接上（表里没这一列）；header 为空返回空串（调用方不必显示）。</returns>
    public string ApplyQtyColumn(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return string.Empty;
        var match = ExpandColumnOptions.FirstOrDefault(o => o.Value == header);
        if (match is null)
            return $"「按哪一列数张数」没接上：表里找不到「{ColumnLabel.SingleLine(header)}」这一列，你在 ④ 步自己点一列";
        // 先选列再换档：选列时还是默认档（展开列被忽略，重算一遍无害），换到「按张数展开」再重算才真按那一列展开。
        SelectedExpandColumn = match;
        Mode = NumberingMode.ExpandByCartonTotal;
        return $"已按「{ColumnLabel.SingleLine(header)}」这一列数张数（一行写几张就出几张整张纸）";
    }

    /// <summary>
    /// 「一行打几张纸」的两档（用户 2026-09-14：④ 步那块「写的挺乱的，我都看不懂了……改成张数就好了，
    /// 默认是一张，也可以绑定 x 列」）。
    /// <para>真源仍是 <see cref="Mode"/>：这一档只是把「张数」从「件号怎么编」里拆出来给人看。
    /// 选「按表里某一列」时走引擎的展开档（每张各占一个号），所以件号那一档同时被锁成强制重排 ——
    /// 这不是新规矩，是原来第三档本来就带的语义，从前藏在一个下拉里没人分得清。</para>
    /// </summary>
    public const string SheetCountOne = "one";

    public const string SheetCountByColumn = "column";

    public ObservableCollection<ChoiceOption<string>> SheetCountOptions { get; } = new()
    {
        new(SheetCountOne, "1 张（默认：一行一个模板出一张纸）"),
        new(SheetCountByColumn, "按表里某一列（那一列写 5 就出 5 张纸）"),
    };

    public ChoiceOption<string>? SelectedSheetCount
    {
        get => SheetCountOptions.FirstOrDefault(o => o.Value == (IsExpandMode ? SheetCountByColumn : SheetCountOne));
        set
        {
            if (value is null) return;
            Mode = value.Value == SheetCountByColumn ? NumberingMode.ExpandByCartonTotal : NumberStyle;
        }
    }

    private NumberingMode _numberStyle = NumberingMode.KeepData;

    /// <summary>
    /// 「件号怎么编」的两档：沿用表里的 / 强制重排。张数选了「按列」时这一档锁成强制重排
    /// （展开出来的每张都要占号，不然 5 张纸会印成同一个件号）。
    /// </summary>
    public ObservableCollection<ChoiceOption<NumberingMode>> NumberStyleOptions { get; } = new()
    {
        new(NumberingMode.KeepData, "沿用表里的件号（缺项才按规则补）"),
        new(NumberingMode.ForceSequence, "强制重排：忽略表里件号，按规则连续编号"),
    };

    public NumberingMode NumberStyle
    {
        get => _numberStyle;
        set
        {
            if (_numberStyle == value) return;
            _numberStyle = value;
            if (!IsExpandMode) Mode = value;
            Raise(nameof(SelectedNumberStyle));
        }
    }

    public ChoiceOption<NumberingMode>? SelectedNumberStyle
    {
        get => NumberStyleOptions.FirstOrDefault(o => o.Value == (IsExpandMode ? NumberingMode.ForceSequence : _numberStyle));
        set { if (value is not null) NumberStyle = value.Value; }
    }

    public NumberingMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            if (SelectedMode?.Value != value) SelectedMode = ModeOptions.FirstOrDefault(o => o.Value == value);
            Raise(nameof(IsExpandMode));
            Raise(nameof(IsNumberStyleEnabled));
            Raise(nameof(SelectedSheetCount));
            Raise(nameof(SelectedNumberStyle));
            RecomputeNumbering();
        }
    }

    /// <summary>只有「按每行的张数展开」这一档才需要选展开列，界面拿它灰掉那个下拉。</summary>
    public bool IsExpandMode => Mode == NumberingMode.ExpandByCartonTotal;

    /// <summary>展开档下每张纸都要占一个号，所以「件号怎么编」被锁成强制重排 —— 界面灰掉那颗，别留假旋钮。</summary>
    public bool IsNumberStyleEnabled => !IsExpandMode;

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
        _numberStyle = saved.Mode == NumberingMode.ExpandByCartonTotal ? _numberStyle : saved.Mode;
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
        Raise(nameof(SelectedSheetCount));
        Raise(nameof(SelectedNumberStyle));
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

    /// <summary>④ 步与出纸确认顶上那一行。样张模式必须打头写明「样张」（第 104 棒的红线）。</summary>
    public string PlanText => Plan is null ? "尚未生成整版方案"
        : IsSampleSheet ? "【样张】" + Plan.Describe() : Plan.Describe();

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
    /// <summary>
    /// 这张纸要排几枚（第 104 棒「无表格也能改、改完能打」）：有真标签照真标签；
    /// <strong>一张表都没导但有模板</strong>时给 1 枚样张 —— 于是它走的是同一条拼版与出纸路，不另开第二条。
    /// 没模板仍给 0：那种情况连纸都无从画，硬塞一枚就是凭空造版。
    /// </summary>
    public static int LabelsIntoPlan(int labelCount, bool hasTemplate)
        => labelCount > 0 ? labelCount : hasTemplate ? 1 : 0;

    /// <summary>
    /// 现在这一版是不是<strong>样张</strong>（没导表、只有示意样例那一枚）。
    /// <para>这条标记是这一棒的红线：屏幕上、④ 步那行提示、送打任务名、日志四处都要带着它——
    /// 打出去的纸绝不能长得像"这批货的纸"（三道闸里"不猜"与"可退"就靠这个撑着）。</para>
    /// </summary>
    public bool IsSampleSheet { get; private set; }

    public void RebuildPlan()
    {
        var spec = Working;
        var template = _source.Template;
        if (spec is null || template is null)
        {
            Plan = null;
            IsSampleSheet = false;
            SheetIssues.Clear();
            if (template is null) SheetIssues.Add("还没选模板，先回第 ③ 步。");
            return;
        }

        // 「一页只排同一枚」开不开只看纸规那个开关：开着就是一枚唛头独占一页、页内铺满全同份数，
        // 所以不再需要把分组键递给引擎（上一棒递的 SourceRowIndex 已无意义，那正是用户拿红框否掉的旧语义）。
        var labels = LabelsIntoPlan(_labels.Count, hasTemplate: true);
        IsSampleSheet = labels > 0 && _labels.Count == 0;   // 没一张表、只有示意样例那一枚
        var plan = ImpositionEngine.Build(spec, template.WidthMm, template.HeightMm, labels);
        Plan = plan;
        SheetIssues.Clear();
        if (IsSampleSheet)
            SheetIssues.Add("⚠ 现在没有表格数据：这一版是【样张】（内容是示意的，不是这批货）。导表进来就自动换成真的。");
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
        Raise(nameof(PlanText));
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
