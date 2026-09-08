using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using LabelGou.App.Mvvm;
using LabelGou.App.Services;
using LabelGou.Core.Editing;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.App.ViewModels;

/// <summary>
/// B 类模板编辑器的大脑：持有在编模板、选中项、撤销栈、实时校验结果，
/// 并把画布上的鼠标动作翻译成 <see cref="EditGeometry"/> 的毫米运算。
/// <para>
/// 分工原则：<strong>所有几何判断在 Core，本类只管状态与命令，控件只管像素与鼠标事件</strong>。
/// 这样"拖动会不会印出界"这类问题能靠单测覆盖，不依赖手点。
/// </para>
/// </summary>
public sealed class TemplateEditorViewModel : ObservableObject
{
    private readonly TemplateStore _store;
    private readonly TemplateHistory _history = new();
    private readonly LabelTemplate _template;
    private string? _savedFileName;
    private bool _suppressCapture;

    private DragMode _dragMode = DragMode.None;
    private int _dragIndex = -1;
    private ResizeHandle _dragHandle = ResizeHandle.None;
    private TemplateElement? _dragSnapshot;
    private (double X, double Y) _dragStart;

    private string _name;
    private string _note;
    private double _widthMm;
    private double _heightMm;
    private double _paddingMm;
    private double _borderMm;
    private ElementRow? _selectedRow;
    private string _statusText = "拖一拖看效果，改完记得保存。";
    private bool _showGrid = true;
    private bool _snapEnabled = true;
    private bool _snapToGrid = true;
    private double _gridStepMm = 1;
    private bool _showPreviewText = true;
    private IReadOnlyList<GuideLine> _activeGuides = Array.Empty<GuideLine>();

    public TemplateEditorViewModel(LabelTemplate working, TemplateStore store, string? savedFileName = null)
    {
        _template = working ?? throw new ArgumentNullException(nameof(working));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _savedFileName = savedFileName;
        _name = working.Name;
        _note = working.Note;
        _widthMm = working.WidthMm;
        _heightMm = working.HeightMm;
        _paddingMm = working.PaddingMm;
        _borderMm = working.BorderMm;

        RefreshElements();
        RebuildSample();
        RecomputeIssues();
        BuildCommands();
    }

    /// <summary>画布需要重绘。</summary>
    public event Action? CanvasChanged;

    /// <summary>当前这份保存成功（主窗口据此刷新模板列表，编辑器也可以安心关窗）。</summary>
    public event Action<LabelTemplate>? Saved;

    /// <summary>
    /// 另存出了一份新副本。与 <see cref="Saved"/> 的语义分开：库里多了一个模板，
    /// 但当前这份的未保存改动并没存进去，不能顺手把「关窗不必再问」的守卫解掉。
    /// </summary>
    public event Action<LabelTemplate>? SavedAsCopy;

    /// <summary>需要弹窗级别提醒的错误。</summary>
    public event Action<string>? ErrorRaised;

    /// <summary>请求关闭窗口（由 View 执行）。</summary>
    public event Action? CloseRequested;

    public LabelTemplate Template => _template;

    public bool IsBuiltInSource { get; set; }

    public ObservableCollection<ElementRow> Elements { get; } = new();

    public IReadOnlyList<FieldOption> FieldOptions { get; } = BuildFieldOptions();

    private FieldOption? _selectedFieldOption;

    /// <summary>「插入字段」下拉的当前项。</summary>
    public FieldOption? SelectedFieldOption
    {
        get => _selectedFieldOption;
        set => Set(ref _selectedFieldOption, value);
    }

    public IReadOnlyList<GuideLine> ActiveGuides => _activeGuides;

    /// <summary>样例数据排出来的版面，画布据此画出"印出来长什么样"。</summary>
    public LabelLayout SampleLayout { get; private set; } = new();

    public ICommand AddTextCommand { get; private set; } = null!;
    public ICommand AddLineCommand { get; private set; } = null!;
    public ICommand AddRectCommand { get; private set; } = null!;
    public ICommand AddImageCommand { get; private set; } = null!;
    public ICommand RemoveCommand { get; private set; } = null!;
    public ICommand DuplicateCommand { get; private set; } = null!;
    public ICommand BringToFrontCommand { get; private set; } = null!;
    public ICommand SendToBackCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand RedoCommand { get; private set; } = null!;
    public ICommand SaveCommand { get; private set; } = null!;
    public ICommand SaveAsCopyCommand { get; private set; } = null!;
    public ICommand RevertCommand { get; private set; } = null!;
    public ICommand InsertFieldCommand { get; private set; } = null!;
    public RelayCommand AlignCommand { get; private set; } = null!;
    public RelayCommand PaddingCommand { get; private set; } = null!;

    // ---------- 模板级属性 ----------

    public string Title => IsBuiltInSource
        ? $"编辑模板（内置模板的副本）：{_template.Name}"
        : $"编辑模板：{_template.Name}";

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                Capture();
                _template.Name = string.IsNullOrWhiteSpace(value) ? "未命名模板" : value.Trim();
                Raise(nameof(Title));
                Touch();
            }
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value))
            {
                Capture();
                _template.Note = value;
                Touch();
            }
        }
    }

    public double WidthMm
    {
        get => _widthMm;
        set
        {
            if (Set(ref _widthMm, value))
            {
                Capture();
                _template.WidthMm = Math.Max(1, value);
                ClampAllElements();
                Touch();
            }
        }
    }

    public double HeightMm
    {
        get => _heightMm;
        set
        {
            if (Set(ref _heightMm, value))
            {
                Capture();
                _template.HeightMm = Math.Max(1, value);
                ClampAllElements();
                Touch();
            }
        }
    }

    public double PaddingMm
    {
        get => _paddingMm;
        set
        {
            if (Set(ref _paddingMm, value))
            {
                Capture();
                _template.PaddingMm = Math.Max(0, value);
                Touch();
            }
        }
    }

    public double BorderMm
    {
        get => _borderMm;
        set
        {
            if (Set(ref _borderMm, value))
            {
                Capture();
                _template.BorderMm = Math.Max(0, value);
                Touch();
            }
        }
    }

    public bool ShowGrid
    {
        get => _showGrid;
        set { if (Set(ref _showGrid, value)) CanvasChanged?.Invoke(); }
    }

    public bool SnapEnabled
    {
        get => _snapEnabled;
        set { Set(ref _snapEnabled, value); }
    }

    public bool SnapToGrid
    {
        get => _snapToGrid;
        set { Set(ref _snapToGrid, value); }
    }

    public double GridStepMm
    {
        get => _gridStepMm;
        set { Set(ref _gridStepMm, Math.Max(0.1, value)); CanvasChanged?.Invoke(); }
    }

    /// <summary>true 时画布画样例数据排版结果，false 时只画元素框（摆位置更快）。</summary>
    public bool ShowPreviewText
    {
        get => _showPreviewText;
        set { if (Set(ref _showPreviewText, value)) CanvasChanged?.Invoke(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (Set(ref _statusText, value)) { /* 状态栏 */ } }
    }

    public bool IsDirty { get; private set; }

    public IReadOnlyList<string> Issues { get; private set; } = Array.Empty<string>();

    public bool HasIssues => Issues.Count > 0;

    public bool HasError { get; private set; }

    public ElementRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
            {
                Editing = value is null ? null : new EditableElement(value.Element, Capture, OnElementEdited);
                Raise(nameof(Editing));
                Raise(nameof(HasSelection));
                CanvasChanged?.Invoke();
            }
        }
    }

    public bool HasSelection => SelectedRow is not null;

    /// <summary>右侧属性面板的绑定源。</summary>
    public EditableElement? Editing { get; private set; }

    // ---------- 命令实现 ----------

    /// <summary>命令在这里装配（构造完就能直接用，单测也走同一条路）。</summary>
    public void BuildCommands()
    {
        AddTextCommand = new RelayCommand(() => AddElement(TemplateFactory.NewText("新文本", _template.PaddingMm, _template.PaddingMm, 30, 6), "文本"));
        AddLineCommand = new RelayCommand(() => AddElement(TemplateFactory.NewLine(
            _template.PaddingMm, _template.PaddingMm + 14,
            Math.Max(_template.PaddingMm + 14, _template.WidthMm - _template.PaddingMm), _template.PaddingMm + 14), "线条"));
        AddRectCommand = new RelayCommand(() => AddElement(TemplateFactory.NewRect(_template.PaddingMm, _template.PaddingMm, 30, 14), "矩形框"));
        AddImageCommand = new RelayCommand(AddImage);
        RemoveCommand = new RelayCommand(RemoveSelected, () => SelectedRow is not null);
        DuplicateCommand = new RelayCommand(DuplicateSelected, () => SelectedRow is not null);
        BringToFrontCommand = new RelayCommand(() => ChangeLayer(1), () => SelectedRow is not null);
        SendToBackCommand = new RelayCommand(() => ChangeLayer(-1), () => SelectedRow is not null);
        UndoCommand = new RelayCommand(Undo, () => _history.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _history.CanRedo);
        SaveCommand = new RelayCommand(Save);
        SaveAsCopyCommand = new RelayCommand(SaveAsCopy);
        RevertCommand = new RelayCommand(Revert, () => IsDirty);
        InsertFieldCommand = new RelayCommand(parameter => InsertField(parameter as string));
        AlignCommand = new RelayCommand(parameter => AlignSelected(parameter as string));
        PaddingCommand = new RelayCommand(parameter => PadSelected(parameter as string));
    }

    private void AddElement(TemplateElement element, string kindText)
    {
        Capture();
        var added = TemplateFactory.AddElement(_template, element, _template.PaddingMm, _template.PaddingMm);
        if (added is null)
        {
            ReleaseCapture();
            Report($"加不下了：一张标签最多 {TemplateValidator.MaxElements} 个元素，且纸面已无空位。");
            return;
        }
        RefreshElements();
        SelectedRow = Elements[Math.Min(added.Value, Elements.Count - 1)];
        Touch();
        StatusText = $"已添加{kindText}（第 {added.Value + 1} 个），拖动即可摆位置。";
    }

    private void AddImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要放进标签的图片（Logo / 条码图）",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        var targetDirectory = Path.Combine(_store.UserDirectory, "assets");
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, Path.GetFileName(dialog.FileName));
        try
        {
            File.Copy(dialog.FileName, target, overwrite: true);
        }
        catch (Exception ex)
        {
            Report("图片没能复制到模板目录：" + ex.Message);
            return;
        }

        AddElement(TemplateFactory.NewImage(
            Path.Combine("assets", Path.GetFileName(dialog.FileName)),
            _template.PaddingMm, _template.PaddingMm, 20, 12), "图片");
    }

    private void RemoveSelected()
    {
        var row = SelectedRow;
        if (row is null) return;
        var index = _template.Elements.IndexOf(row.Element);
        if (index < 0) return;

        Capture();
        _template.Elements.RemoveAt(index);
        RefreshElements();
        SelectedRow = index < Elements.Count ? Elements[index] : Elements.LastOrDefault();
        Touch();
        RebuildSample();
        RecomputeIssues();
        StatusText = $"已删除第 {index + 1} 个元素，可点「撤销」找回。";
    }

    private void DuplicateSelected()
    {
        var row = SelectedRow;
        if (row is null) return;
        var index = _template.Elements.IndexOf(row.Element);
        if (index < 0) return;

        Capture();
        var copy = TemplateFactory.Duplicate(_template, index, 2);
        var added = TemplateFactory.AddElement(_template, copy, copy.X, copy.Y);
        if (added is null)
        {
            ReleaseCapture();
            Report("元素数量已达上限，复制不了。");
            return;
        }
        RefreshElements();
        SelectedRow = Elements[added.Value];
        Touch();
        RebuildSample();
    }

    private void ChangeLayer(int delta)
    {
        var row = SelectedRow;
        if (row is null) return;
        var index = _template.Elements.IndexOf(row.Element);
        if (index < 0) return;

        Capture();
        var target = delta > 0
            ? EditGeometry.BringToFront(_template, index)
            : EditGeometry.SendToBack(_template, index);
        RefreshElements();
        SelectedRow = target < Elements.Count ? Elements[target] : null;
        Touch();
        RebuildSample();
        StatusText = delta > 0 ? "已移到最上层。" : "已移到最下层。";
    }

    private void AlignSelected(string? mode)
    {
        var row = SelectedRow;
        if (row is null || string.IsNullOrEmpty(mode)) return;
        var index = _template.Elements.IndexOf(row.Element);
        if (index < 0) return;

        Capture();
        // 六个按钮各管一个轴：上一版把两个轴一起给了（点「顶对齐」会把水平位置甩到最左），
        // 结果是「想贴顶就得重贴一次左」。
        AlignHorizontal? h = null;
        AlignVertical? v = null;
        switch (mode)
        {
            case "Left": h = AlignHorizontal.Left; break;
            case "Center": h = AlignHorizontal.Center; break;
            case "Right": h = AlignHorizontal.Right; break;
            case "Top": v = AlignVertical.Top; break;
            case "Middle":
            case "CenterV": v = AlignVertical.Middle; break;
            case "Bottom": v = AlignVertical.Bottom; break;
        }
        if (h is null && v is null)
        {
            ReleaseCapture();
            return;
        }
        EditGeometry.AlignToLabel(_template, index, h, v);
        Touch();
        RebuildSample();
        StatusText = "已按标签对齐（" + mode + "）。";
    }

    private void PadSelected(string? mode)
    {
        var row = SelectedRow;
        if (row is null || string.IsNullOrEmpty(mode)) return;
        var index = _template.Elements.IndexOf(row.Element);
        if (index < 0) return;

        Capture();
        var (h, v) = mode switch
        {
            "Left" => (AlignHorizontal.Left, AlignVertical.Top),
            "Center" => (AlignHorizontal.Center, AlignVertical.Top),
            "Right" => (AlignHorizontal.Right, AlignVertical.Top),
            "Top" => (AlignHorizontal.Left, AlignVertical.Top),
            "Middle" => (AlignHorizontal.Left, AlignVertical.Middle),
            "Bottom" => (AlignHorizontal.Left, AlignVertical.Bottom),
            _ => ((AlignHorizontal?)null, (AlignVertical?)null),
        };
        if (h is null || v is null)
        {
            ReleaseCapture();
            return;
        }
        EditGeometry.SnapToPadding(_template, index, h.Value, v.Value);
        Touch();
        RebuildSample();
        StatusText = "已贴到内边距线（" + mode + "）。";
    }

    private void InsertField(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var editing = Editing;
        if (editing is not null && editing.Kind == ElementKind.Text)
        {
            // 有选中的文本元素 → 把字段插到它内容末尾
            Capture();
            editing.Text = (editing.Text ?? string.Empty) + "{{" + token + "}}";
            return;
        }

        Capture();
        var added = TemplateFactory.AddFieldText(_template, token);
        if (added is null)
        {
            ReleaseCapture();
            Report($"字段 {token} 加不进去了：元素数量已达上限。");
            return;
        }
        RefreshElements();
        SelectedRow = Elements[added.Value];
        Touch();
        RebuildSample();
        StatusText = $"已插入字段 {{{{{token}}}}}，打印时会自动填对应列的值。";
    }

    // ---------- 画布交互（控件调用，坐标一律毫米） ----------

    public enum DragMode
    {
        None = 0,
        Move = 1,
        Resize = 2,
    }

    /// <summary>
    /// 按下鼠标：<paramref name="handleRadiusMm"/> 由控件按当前缩放换算（屏幕上约 8 像素）。
    /// 命中元素即选中并准备拖动；点空白则取消选中。
    /// </summary>
    public DragMode BeginDrag(double xMm, double yMm, double handleRadiusMm)
    {
        var index = EditGeometry.TopmostAt(_template, xMm, yMm);
        _dragMode = DragMode.None;
        _dragIndex = -1;
        _dragSnapshot = null;
        _activeGuides = Array.Empty<GuideLine>();

        if (index < 0)
        {
            SelectedRow = null;
            return DragMode.None;
        }

        var element = _template.Elements[index];
        SelectedRow = Elements.FirstOrDefault(r => ReferenceEquals(r.Element, element));

        var handle = EditGeometry.HandleAt(element, xMm, yMm, handleRadiusMm);
        Capture();
        _dragSnapshot = element.CloneTemplate();
        _dragStart = (xMm, yMm);
        _dragIndex = index;
        _dragMode = handle == ResizeHandle.None ? DragMode.Move : DragMode.Resize;
        _dragHandle = handle;
        return _dragMode;
    }

    /// <summary>拖动中：每次都从按下时的快照重算，保证不累积误差、也不会越界。</summary>
    public void DragTo(double xMm, double yMm)
    {
        if (_dragMode == DragMode.None || _dragSnapshot is null || _dragIndex < 0) return;
        if (_dragIndex >= _template.Elements.Count) return;

        var element = _template.Elements[_dragIndex];
        RestoreGeometry(element, _dragSnapshot);
        var dx = xMm - _dragStart.X;
        var dy = yMm - _dragStart.Y;

        if (_dragMode == DragMode.Move)
        {
            var proposed = (X: _dragSnapshot.X + dx, Y: _dragSnapshot.Y + dy);
            if (_snapEnabled)
            {
                var snapped = EditGeometry.Snap(_template, _dragIndex, proposed.X, proposed.Y, new SnapOptions
                {
                    Enabled = true,
                    SnapToGrid = _snapToGrid,
                    GridStepMm = _gridStepMm,
                    SnapNeighbors = true,
                });
                _activeGuides = snapped.Guides;
                EditGeometry.MoveTo(_template, _dragIndex, snapped.X, snapped.Y);
            }
            else
            {
                _activeGuides = Array.Empty<GuideLine>();
                EditGeometry.MoveBy(_template, _dragIndex, dx, dy);
            }
        }
        else
        {
            EditGeometry.ResizeBy(_template, _dragIndex, _dragHandle, dx, dy);
        }

        RebuildSample();
        CanvasChanged?.Invoke();
    }

    public void EndDrag()
    {
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;
        _dragIndex = -1;
        _dragSnapshot = null;
        _dragHandle = ResizeHandle.None;
        _activeGuides = Array.Empty<GuideLine>();
        Editing?.Reload();
        Touch();
        RecomputeIssues();
        CanvasChanged?.Invoke();
    }

    /// <summary>Esc 取消本次拖动：元素回到按下时的位置，刚录的那步历史也收回去。</summary>
    public void CancelDrag()
    {
        if (_dragMode == DragMode.None) return;
        if (_dragSnapshot is not null && _dragIndex >= 0 && _dragIndex < _template.Elements.Count)
            RestoreGeometry(_template.Elements[_dragIndex], _dragSnapshot);
        _dragMode = DragMode.None;
        _dragIndex = -1;
        _dragSnapshot = null;
        _dragHandle = ResizeHandle.None;
        _activeGuides = Array.Empty<GuideLine>();
        ReleaseCapture();
        StatusText = "已取消这次拖动。";
    }

    /// <summary>方向键微调：一步 <paramref name="stepMm"/> 毫米。</summary>
    public void Nudge(double dxMm, double dyMm)
    {
        var row = SelectedRow;
        if (row is null) return;
        var index = _template.Elements.IndexOf(row.Element);
        if (index < 0) return;

        Capture();
        EditGeometry.MoveBy(_template, index, dxMm, dyMm);
        Touch();
        RebuildSample();
        RecomputeIssues();
        CanvasChanged?.Invoke();
    }

    // ---------- 撤销 / 保存 ----------

    public void Undo()
    {
        if (!_history.Undo(_template)) return;
        AfterHistoryChange("已撤销一步。");
    }

    public void Redo()
    {
        if (!_history.Redo(_template)) return;
        AfterHistoryChange("已重做一步。");
    }

    private void AfterHistoryChange(string message)
    {
        _suppressCapture = true;
        try
        {
            SyncTemplateLevel();
            RefreshElements();
        }
        finally
        {
            _suppressCapture = false;
        }
        Touch();
        RebuildSample();
        RecomputeIssues();
        StatusText = message;
    }

    private void SyncTemplateLevel()
    {
        _name = _template.Name;
        _note = _template.Note;
        _widthMm = _template.WidthMm;
        _heightMm = _template.HeightMm;
        _paddingMm = _template.PaddingMm;
        _borderMm = _template.BorderMm;
        foreach (var property in new[] { nameof(Name), nameof(Note), nameof(WidthMm), nameof(HeightMm), nameof(PaddingMm), nameof(BorderMm), nameof(Title) })
        {
            Raise(property);
        }
    }

    public void Save()
    {
        SyncIntoTemplate();
        var stale = _savedFileName is null ? null : Path.Combine(_store.UserDirectory, _savedFileName);
        var (saved, fileName, issues) = _store.Save(_template);
        if (!saved)
        {
            RecomputeIssues(issues);
            Report("模板还没存上，先解决下面这些问题：" + Environment.NewLine + string.Join(Environment.NewLine, issues.ErrorMessages()));
            return;
        }

        _savedFileName = fileName;
        if (stale is not null && File.Exists(stale) && !string.Equals(Path.GetFileName(stale), fileName, StringComparison.Ordinal))
        {
            try
            {
                File.Delete(stale);   // 改过名 → 删掉旧文件，免得列表里出现两个同名模板
            }
            catch (Exception ex)
            {
                AppLog.Warning("删除改名前的模板文件失败：" + ex.Message);
            }
        }

        IsDirty = false;
        Raise(nameof(IsDirty));
        RecomputeIssues(issues);
        Saved?.Invoke(_template);
        StatusText = $"已保存到 {fileName}，主窗口选它就能用了。";
        CloseRequested?.Invoke();
    }

    /// <summary>另存为用户副本（内置模板或想留底时用）。</summary>
    public void SaveAsCopy()
    {
        SyncIntoTemplate();
        var copy = TemplateFactory.CopyOf(_template, string.IsNullOrWhiteSpace(Name) ? _template.Name + " 副本" : Name.Trim() + " 副本");
        var (saved, fileName, issues) = _store.Save(copy);
        if (!saved)
        {
            RecomputeIssues(issues);
            Report("副本没存上：" + Environment.NewLine + string.Join(Environment.NewLine, issues.ErrorMessages()));
            return;
        }

        RefreshElements();
        RecomputeIssues(issues);
        // 走 SavedAsCopy 而不是 Saved：存到盘上的是一份新副本，当前这份的未保存改动还在窗口里。
        // 上一版在这里发 Saved，界面就把「关窗不必再问」的守卫解了，用户接着关窗就静默丢了原模板的改动。
        SavedAsCopy?.Invoke(copy);
        StatusText = $"已另存为用户模板 {fileName}。";
    }

    /// <summary>丢弃未保存的改动，回到上次保存的样子。</summary>
    public void Revert()
    {
        if (_savedFileName is null)
        {
            Report("这份模板还没保存过，没有可回退的版本。");
            return;
        }
        var path = Path.Combine(_store.UserDirectory, _savedFileName);
        if (!File.Exists(path))
        {
            Report("找不到上次保存的文件，无法回退。");
            return;
        }

        var (template, issues) = _store.ReadFile(path);
        if (template is null)
        {
            RecomputeIssues(issues);
            Report("上次保存的文件读不回来了。");
            return;
        }

        _history.Clear();
        _suppressCapture = true;
        try
        {
            template.CopyInto(_template);
            SyncTemplateLevel();
            RefreshElements();
        }
        finally
        {
            _suppressCapture = false;
        }
        IsDirty = false;
        Raise(nameof(IsDirty));
        RebuildSample();
        RecomputeIssues();
        StatusText = "已回退到上次保存的版本。";
        CanvasChanged?.Invoke();
    }

    private void SyncIntoTemplate()
    {
        _template.Name = string.IsNullOrWhiteSpace(Name) ? "未命名模板" : Name.Trim();
        _template.Note = Note;
        _template.WidthMm = Math.Max(1, WidthMm);
        _template.HeightMm = Math.Max(1, HeightMm);
        _template.PaddingMm = Math.Max(0, PaddingMm);
        _template.BorderMm = Math.Max(0, BorderMm);
        _template.BuiltIn = false;   // 存进去的一律是用户模板；内置不可覆盖由 Store 再兜一道
    }

    // ---------- 内部支撑 ----------

    private void ClampAllElements()
    {
        foreach (var element in _template.Elements)
        {
            var box = EditGeometry.BoxOf(element);
            element.Width = Math.Min(Math.Max(EditGeometry.MinSideMm, element.Width), Math.Max(EditGeometry.MinSideMm, _template.WidthMm));
            element.Height = Math.Min(Math.Max(EditGeometry.MinSideMm, element.Height), Math.Max(EditGeometry.MinSideMm, _template.HeightMm));
            EditGeometry.MoveTo(_template, _template.Elements.IndexOf(element),
                Math.Min(box.X, Math.Max(0, _template.WidthMm - box.Width)),
                Math.Min(box.Y, Math.Max(0, _template.HeightMm - box.Height)));
            if (element.Kind == ElementKind.Line)
            {
                element.X2 = Math.Clamp(element.X2, 0, _template.WidthMm);
                element.Y2 = Math.Clamp(element.Y2, 0, _template.HeightMm);
            }
        }
        RebuildSample();
        RecomputeIssues();
        CanvasChanged?.Invoke();
    }

    private static void RestoreGeometry(TemplateElement target, TemplateElement snapshot)
    {
        target.X = snapshot.X;
        target.Y = snapshot.Y;
        target.Width = snapshot.Width;
        target.Height = snapshot.Height;
        target.X2 = snapshot.X2;
        target.Y2 = snapshot.Y2;
    }

    /// <summary>改动之前录一步快照（同一属性的连续输入只录第一次，避免打字打出一个栈）。</summary>
    private void Capture()
    {
        if (_suppressCapture) return;
        _history.Capture(_template);
    }

    /// <summary>操作发现做不下去时把刚录的快照丢掉，免得撤销时跳过一个空步。</summary>
    private void ReleaseCapture()
    {
        if (_history.CanUndo) _history.Undo(_template);
        _suppressCapture = true;
        try
        {
            SyncTemplateLevel();
            RefreshElements();
        }
        finally
        {
            _suppressCapture = false;
        }
        RebuildSample();
        RecomputeIssues();
        CanvasChanged?.Invoke();
    }

    private void OnElementEdited()
    {
        Touch();
        RebuildSample();
        RecomputeIssues();
        CanvasChanged?.Invoke();
    }

    private void Touch()
    {
        if (!IsDirty)
        {
            IsDirty = true;
            Raise(nameof(IsDirty));
        }
        CommandManager.InvalidateRequerySuggested();
    }

    public void RefreshElements()
    {
        var keep = SelectedRow?.Element;
        Elements.Clear();
        for (var i = 0; i < _template.Elements.Count; i++) Elements.Add(new ElementRow(i, _template.Elements[i]));
        SelectedRow = keep is null ? null : Elements.FirstOrDefault(r => ReferenceEquals(r.Element, keep)) ?? Elements.LastOrDefault();
        CanvasChanged?.Invoke();
    }

    public void RebuildSample()
    {
        try
        {
            // 编辑器的职责就是“照参考图对齐”，所以画布里得看得见那张不上纸的底图
            SampleLayout = LayoutEngine.BuildSample(_template, includeReference: true);
        }
        catch (Exception ex)
        {
            AppLog.Warning("样例版面构建失败，画布退回空白：" + ex.Message);
            SampleLayout = new LabelLayout();
        }
        CanvasChanged?.Invoke();
    }

    public void RecomputeIssues() => RecomputeIssues(TemplateValidator.Validate(_template));

    private void RecomputeIssues(IReadOnlyList<TemplateIssue> issues)
    {
        Issues = issues.Select(i => i.Message).ToList();
        HasError = issues.HasError();
        Raise(nameof(Issues));
        Raise(nameof(HasIssues));
        Raise(nameof(HasError));
    }

    private void Report(string message)
    {
        AppLog.Warning(message.Replace(Environment.NewLine, " / "));
        StatusText = message.Split('\n')[0];
        ErrorRaised?.Invoke(message);
    }

    private static IReadOnlyList<FieldOption> BuildFieldOptions()
    {
        var list = new List<FieldOption>();
        foreach (var field in MarkFieldCatalog.All)
        {
            list.Add(new FieldOption(field.Key.ToString(), $"{field.ChineseName}　{field.EnglishLabel}"));
        }
        // 内置计算量由 TemplateTokenizer 那份清单生成：上一版这里手写名单，写了 TotalCarton / TotalQty
        // 两个引擎根本不认的名字，插进去就是校验 Error（而且只有这一处有它们）。
        foreach (var (token, description) in TemplateTokenizer.BuiltInTokens)
        {
            if (list.Any(f => string.Equals(f.Token, token, StringComparison.Ordinal))) continue;
            list.Add(new FieldOption(token, $"内置计算量 {token}（{description}）"));
        }
        return list;
    }
}

/// <summary>「插入字段」下拉的一项。</summary>
public sealed record FieldOption(string Token, string Display)
{
    public override string ToString() => Display;
}

/// <summary>左侧元素列表的一行（按引用认元素，重排后仍能保持选中）。</summary>
public sealed class ElementRow
{
    public ElementRow(int ordinal, TemplateElement element)
    {
        Ordinal = ordinal;
        Element = element;
    }

    public int Ordinal { get; internal set; }

    public TemplateElement Element { get; }

    public string KindText => Element.Kind switch
    {
        ElementKind.Text => "文本",
        ElementKind.Barcode => "条码",
        ElementKind.Line => "线条",
        ElementKind.Rect => "矩形框",
        ElementKind.Image => "图片",
        _ => "元素",
    };

    public string Display => $"{Ordinal + 1:00}　{KindText}　{Summary}";

    private string Summary
    {
        get
        {
            if (Element.Kind == ElementKind.Text)
                return Truncate(string.IsNullOrWhiteSpace(Element.Text) ? "（空文本）" : Element.Text);
            if (Element.Kind == ElementKind.Image)
                return Truncate(Path.GetFileName(Element.ImagePath ?? "未选图片"));
            var box = EditGeometry.BoxOf(Element);
            return $"{box.Width:0.#} × {box.Height:0.#} mm";
        }
    }

    public string GeometryText
    {
        get
        {
            var box = EditGeometry.BoxOf(Element);
            return $"X {box.X:0.#}　Y {box.Y:0.#} mm" + (Element.Visible ? string.Empty : "（已隐藏）");
        }
    }

    private static string Truncate(string text)
        => text.Length <= 22 ? text : text[..22] + "…";
}

/// <summary>
/// 右侧属性面板的绑定包装：读写直接落在 <see cref="TemplateElement"/> 上，
/// 写之前录一次历史快照（同一属性连续输入只录一次）。
/// </summary>
public sealed class EditableElement : ObservableObject
{
    private readonly TemplateElement _element;
    private readonly Action _capture;
    private readonly Action _changed;
    private string _lastCapturedFor = string.Empty;
    private DateTime _lastCaptureAt = DateTime.MinValue;

    public EditableElement(TemplateElement element, Action capture, Action changed)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _capture = capture;
        _changed = changed;
    }

    public TemplateElement Element => _element;

    public ElementKind Kind => _element.Kind;

    public bool IsText => _element.Kind == ElementKind.Text;

    public bool IsLine => _element.Kind == ElementKind.Line;

    public bool IsImage => _element.Kind == ElementKind.Image;

    public bool HasBox => _element.Kind != ElementKind.Line;

    public string KindText => _element.Kind switch
    {
        ElementKind.Text => "文本",
        ElementKind.Barcode => "条码",
        ElementKind.Line => "线条",
        ElementKind.Rect => "矩形框",
        ElementKind.Image => "图片",
        _ => "元素",
    };

    public double X { get => _element.X; set { if (Near(value, _element.X)) return; Prepare(nameof(X)); _element.X = value; Done(); } }

    public double Y { get => _element.Y; set { if (Near(value, _element.Y)) return; Prepare(nameof(Y)); _element.Y = value; Done(); } }

    public double Width { get => _element.Width; set { if (Near(value, _element.Width)) return; Prepare(nameof(Width)); _element.Width = Math.Max(EditGeometry.MinSideMm, value); Done(); } }

    public double Height { get => _element.Height; set { if (Near(value, _element.Height)) return; Prepare(nameof(Height)); _element.Height = Math.Max(EditGeometry.MinSideMm, value); Done(); } }

    public double X2 { get => _element.X2; set { if (Near(value, _element.X2)) return; Prepare(nameof(X2)); _element.X2 = value; Done(); } }

    public double Y2 { get => _element.Y2; set { if (Near(value, _element.Y2)) return; Prepare(nameof(Y2)); _element.Y2 = value; Done(); } }

    public string Text { get => _element.Text ?? string.Empty; set { if (string.Equals(value, _element.Text, StringComparison.Ordinal)) return; Prepare(nameof(Text)); _element.Text = value; Done(); } }

    public string ImagePath { get => _element.ImagePath ?? string.Empty; set { if (string.Equals(value, _element.ImagePath, StringComparison.Ordinal)) return; Prepare(nameof(ImagePath)); _element.ImagePath = value; Done(); } }

    public string FontFamily { get => _element.FontFamily; set { if (string.Equals(value, _element.FontFamily, StringComparison.Ordinal)) return; Prepare(nameof(FontFamily)); _element.FontFamily = string.IsNullOrWhiteSpace(value) ? TemplateElement.DefaultFont : value; Done(); } }

    public double FontSizePt { get => _element.FontSizePt; set { if (Near(value, _element.FontSizePt)) return; Prepare(nameof(FontSizePt)); _element.FontSizePt = value; Done(); } }

    public bool Bold { get => _element.Bold; set { if (value == _element.Bold) return; Prepare(nameof(Bold)); _element.Bold = value; Done(); } }

    public double ThicknessMm { get => _element.ThicknessMm; set { if (Near(value, _element.ThicknessMm)) return; Prepare(nameof(ThicknessMm)); _element.ThicknessMm = Math.Max(0.05, value); Done(); } }

    public int MaxLines { get => _element.MaxLines; set { if (value == _element.MaxLines) return; Prepare(nameof(MaxLines)); _element.MaxLines = Math.Max(0, value); Done(); } }

    public bool ShrinkToFit { get => _element.ShrinkToFit; set { if (value == _element.ShrinkToFit) return; Prepare(nameof(ShrinkToFit)); _element.ShrinkToFit = value; Done(); } }

    public bool Visible { get => _element.Visible; set { if (value == _element.Visible) return; Prepare(nameof(Visible)); _element.Visible = value; Done(); } }

    public HorizontalAlign Align
    {
        get => _element.Align;
        set
        {
            if (value == _element.Align) return;
            Prepare(nameof(Align));
            _element.Align = value;
            Done();
        }
    }

    private bool Near(double value, double current)
        => Math.Abs(value - current) < 1e-6 || double.IsNaN(value);

    /// <summary>1.5 秒内同一属性的连续改动算一步（拖滑块、连续打字不至于塞满撤销栈）。</summary>
    private void Prepare(string property)
    {
        var now = DateTime.UtcNow;
        if (!string.Equals(_lastCapturedFor, property, StringComparison.Ordinal) || (now - _lastCaptureAt).TotalSeconds > 1.5)
        {
            _capture();
            _lastCapturedFor = property;
            _lastCaptureAt = now;
        }
    }

    private void Done()
    {
        RaiseAll();
        _changed();
    }

    /// <summary>撤销/外部改动之后把面板上的数字刷回来。</summary>
    public void Reload()
    {
        _lastCapturedFor = string.Empty;
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(X), nameof(Y), nameof(Width), nameof(Height), nameof(X2), nameof(Y2), nameof(Text), nameof(ImagePath),
            nameof(FontFamily), nameof(FontSizePt), nameof(Bold), nameof(ThicknessMm), nameof(MaxLines),
            nameof(ShrinkToFit), nameof(Visible), nameof(Align),
        })
        {
            Raise(name);
        }
    }
}
