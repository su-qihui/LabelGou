using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LabelGou.App.Mvvm;
using LabelGou.App.Rendering;
using LabelGou.App.Services;
using LabelGou.Core.Colors;
using LabelGou.Core.Editing;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

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

    /// <summary>本机已安装的系统字体名，属性面板「字体」下拉用。枚举一次即可（字体不会中途变）。</summary>
    public IReadOnlyList<string> SystemFonts { get; } = BuildSystemFonts();

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
    public ICommand AddRectCommand { get; private set; } = null!;
    public ICommand AddImageCommand { get; private set; } = null!;
    public ICommand RemoveCommand { get; private set; } = null!;
    public ICommand DuplicateCommand { get; private set; } = null!;
    public ICommand BringToFrontCommand { get; private set; } = null!;
    public ICommand SendToBackCommand { get; private set; } = null!;
    public ICommand MoveUpCommand { get; private set; } = null!;
    public ICommand MoveDownCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand RedoCommand { get; private set; } = null!;
    public ICommand SaveCommand { get; private set; } = null!;
    public ICommand SaveAsCopyCommand { get; private set; } = null!;
    public ICommand RevertCommand { get; private set; } = null!;
    public ICommand InsertFieldCommand { get; private set; } = null!;
    public RelayCommand AlignCommand { get; private set; } = null!;

    /// <summary>第 46 棒：永不折行的文字可能排到纸外，一键把字号与位置收回内容区。</summary>
    public RelayCommand ShrinkIntoLabelCommand { get; private set; } = null!;
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
        AddRectCommand = new RelayCommand(() => AddElement(TemplateFactory.NewRect(_template.PaddingMm, _template.PaddingMm, 30, 14), "矩形框"));
        BuildNodeCommands();
        AddImageCommand = new RelayCommand(AddImage);
        RemoveCommand = new RelayCommand(RemoveSelected, () => SelectedRow is not null);
        DuplicateCommand = new RelayCommand(DuplicateSelected, () => SelectedRow is not null);
        BringToFrontCommand = new RelayCommand(() => ChangeLayer(999), () => SelectedRow is not null);
        SendToBackCommand = new RelayCommand(() => ChangeLayer(-999), () => SelectedRow is not null);
        // 逐层档：原来只有「置最上/置最下」两个极端，想微调一层只能一步到顶，看着就像"图层动不了"。
        MoveUpCommand = new RelayCommand(() => ChangeLayer(1), () => SelectedRow is not null);
        MoveDownCommand = new RelayCommand(() => ChangeLayer(-1), () => SelectedRow is not null);
        UndoCommand = new RelayCommand(Undo, () => _history.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _history.CanRedo);
        SaveCommand = new RelayCommand(Save);
        SaveAsCopyCommand = new RelayCommand(SaveAsCopy);
        RevertCommand = new RelayCommand(Revert, () => IsDirty);
        InsertFieldCommand = new RelayCommand(parameter => InsertField(parameter as string));
        AlignCommand = new RelayCommand(parameter => AlignSelected(parameter as string));
        ShrinkIntoLabelCommand = new RelayCommand(_ => ShrinkIntoLabel());
        PaddingCommand = new RelayCommand(parameter => PadSelected(parameter as string));
    }

    private void AddElement(TemplateElement element, string kindText)
    {
        Capture();
        var added = TemplateFactory.AddElement(_template, element, _template.PaddingMm, _template.PaddingMm);
        if (added is null)
        {
            ReleaseCapture();
            // 这句只在真撞上限时才出现。第 42 棒之前「纸面没空位」也报这一句，
            // 于是用户对着只有 4 个元素的模板看到「最多 80 个元素」——两种失败混成一句谎话。
            Report($"加不下了：这张标签已经有 {_template.Elements.Count} 个元素，上限是 {TemplateValidator.MaxElements} 个。先删掉不用的再加。");
            return;
        }
        RefreshElements();
        SelectedRow = Elements[Math.Min(added.Index, Elements.Count - 1)];
        Touch();
        // 第 42 棒补：原来这里漏了 RebuildSample —— 元素框画出来了，但画布上"印出来长什么样"那一层
        // 仍取自旧的 SampleLayout，于是刚加的文字看不见，要等下一次别的操作才冒出来。
        RebuildSample();
        // 叠放是正常结局不是失败：AI 的行式模板每行都是全宽行带，纸面上本来就没有"空位"可言。
        StatusText = added.Overlapped
            ? $"已添加{kindText}（第 {added.Index + 1} 个），它叠在现有内容上，拖到想要的位置即可。"
            : $"已添加{kindText}（第 {added.Index + 1} 个），拖动即可摆位置。";
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
            Report($"复制不了：这张标签已经有 {_template.Elements.Count} 个元素，上限是 {TemplateValidator.MaxElements} 个。");
            return;
        }
        RefreshElements();
        SelectedRow = Elements[added.Index];
        Touch();
        RebuildSample();
        // 复制的东西故意偏移 2mm 落在原件旁边，纸面满了就会压在原件上 —— 那是叠放不是失败，说清楚就行。
        if (added.Overlapped) StatusText = "已复制一份，它叠在原件上，拖开即可。";
    }

    private void ChangeLayer(int delta)
    {
        var row = SelectedRow;
        if (row is null) return;
        var index = _template.Elements.IndexOf(row.Element);
        if (index < 0) return;

        // 到顶/到底的极值档用 ±元素数（MoveLayer 内部夹到边界），逐层档用 ±1。
        // 原来只有极值档，用户想「往上挪一层」只能一下子弹到最顶，看着就像图层"被固定"了。
        var step = Math.Abs(delta) >= 999 ? delta : Math.Sign(delta);
        Capture();
        var before = index;
        var target = EditGeometry.MoveLayer(_template, index, step);
        RefreshElements();
        SelectedRow = target < Elements.Count ? Elements[target] : null;
        Touch();
        RebuildSample();
        StatusText = LayerStatus(delta, before, target);
    }

    private static string LayerStatus(int delta, int from, int to)
    {
        var up = delta > 0;
        if (to == from) return up ? "已经在最上层了，再点也不会动。" : "已经在最下层了，再点也不会动。";
        if (Math.Abs(delta) >= 999) return up ? "已移到最上层。" : "已移到最下层。";
        return up ? $"已上移一层（现在第 {to + 1} 层）。" : $"已下移一层（现在第 {to + 1} 层）。";
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
        EditGeometry.AlignToLabel(_template, index, h, v, OccupancyOf(_template.Elements[index]));
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
        EditGeometry.SnapToPadding(_template, index, h.Value, v.Value, OccupancyOf(_template.Elements[index]));
        Touch();
        RebuildSample();
        StatusText = "已贴到内边距线（" + mode + "）。";
    }

    /// <summary>
    /// 「缩回纸内」（第 46 棒配套）：把选中文字的墨迹收回到内容区——字号与盒高按能容下的最大倍率一起缩，
    /// 再按缩完的新墨迹做最小平移归位。
    /// <para>为什么要有这颗键：折行边界从隐形行带手里交出去之后，长值会照实排到纸外（这是用户要的"看得见超出去"），
    /// 收回来这件事得有人办。缩到多少算在软件里，不让人手挑字号试。</para>
    /// <para>它只按屏幕上这份样例量（编辑器手上没有真表）——真数据更长时出纸前那道闸还会再拦一次，
    /// 状态栏把这件事说明白，不假装包办。</para>
    /// </summary>
    private void ShrinkIntoLabel()
    {
        var row = SelectedRow;
        if (row is null) return;
        var element = row.Element;
        var index = _template.Elements.IndexOf(element);
        if (index < 0) return;
        if (element.Kind != ElementKind.Text)
        {
            StatusText = "只有文字需要缩回纸内——图片、矩形和线条的框就是它自己，直接拖或拖角即可。";
            return;
        }
        var ink = DisplayBoxOf(element);
        if (ink is null)
        {
            StatusText = "这一行现在没有可见文字（被隐藏或变量都空），不需要缩回。";
            return;
        }
        if (InkOverflowMm(element) <= TemplateValidator.ToleranceMm)
        {
            StatusText = "这一行已经在纸内，不用缩。";
            return;
        }

        var left = Math.Clamp(_template.PaddingMm, 0, _template.WidthMm);
        var top = Math.Clamp(_template.PaddingMm, 0, _template.HeightMm);
        var availW = Math.Max(1, _template.WidthMm - 2 * left);
        var availH = Math.Max(1, _template.HeightMm - 2 * top);
        var factor = Math.Min(1, Math.Min(
            availW / Math.Max(0.1, ink.Value.Width),
            availH / Math.Max(0.1, ink.Value.Height)));

        Capture();
        element.FontSizePt = Math.Max(TemplateValidator.MinFontPt, Math.Round(element.FontSizePt * factor, 2));
        element.Height = Math.Max(EditGeometry.MinSideMm, Math.Round(element.Height * factor, 3));
        RebuildSample();

        var shrunk = DisplayBoxOf(element);
        if (shrunk is not null)
        {
            var dx = shrunk.Value.X < left ? left - shrunk.Value.X
                : _template.WidthMm - left - (shrunk.Value.X + shrunk.Value.Width) < 0
                    ? _template.WidthMm - left - (shrunk.Value.X + shrunk.Value.Width) : 0;
            var dy = shrunk.Value.Y < top ? top - shrunk.Value.Y
                : _template.HeightMm - top - (shrunk.Value.Y + shrunk.Value.Height) < 0
                    ? _template.HeightMm - top - (shrunk.Value.Y + shrunk.Value.Height) : 0;
            if (dx != 0 || dy != 0)
            {
                EditGeometry.MoveBy(_template, index, dx, dy, shrunk);
                RebuildSample();
            }
        }
        Touch();
        RecomputeIssues();
        CanvasChanged?.Invoke();

        var still = InkOverflowMm(element);
        StatusText = still > TemplateValidator.ToleranceMm
            ? $"已经缩到可印下限 {element.FontSizePt:0.#}pt，还是探出 {still:0.#} mm——这一行的内容比纸还宽，" +
              "要么改数据，要么在右侧给它填一个「折行宽度(mm)」让它折行。"
            : $"已按屏幕上这份样例缩回纸内（字号 {element.FontSizePt:0.#}pt）。真表里的值更长时，出纸前还会再拦你一次。";
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
            // 第 42 棒补：原来这一支直接 return，状态栏还停在上一次操作的话上 ——
            // 用户点了「插入字段」，画布里的文字明明变了，底下却一个字没说。
            StatusText = $"已把 {{{{{token}}}}} 接到选中的那行文字末尾。";
            return;
        }

        Capture();
        var added = TemplateFactory.AddFieldText(_template, token);
        if (added is null)
        {
            ReleaseCapture();
            Report($"字段 {token} 加不进去了：这张标签已经有 {_template.Elements.Count} 个元素，上限是 {TemplateValidator.MaxElements} 个。");
            return;
        }
        RefreshElements();
        SelectedRow = Elements[added.Index];
        Touch();
        RebuildSample();
        StatusText = added.Overlapped
            ? $"已插入字段 {{{{{token}}}}}，它叠在现有内容上，拖开即可；打印时会自动填对应列的值。"
            : $"已插入字段 {{{{{token}}}}}，打印时会自动填对应列的值。";
    }

    // ---------- 画布交互（控件调用，坐标一律毫米） ----------

    /// <summary>画布当前用什么工具点。第 49 棒：「线条」从"两点直线"升级成贝塞尔曲线。</summary>
    public enum EditorTool
    {
        /// <summary>选择/移动/缩放（从前那把箭头）。</summary>
        Select = 0,

        /// <summary>贝塞尔：按下拖出节点的控制柄，松开定下一点；<strong>按住 Shift 拖＝直线（不出柄）</strong>。</summary>
        Bezier = 1,
    }

    public enum DragMode
    {
        None = 0,
        Move = 1,
        Resize = 2,

        /// <summary>拖曲线的某个节点（整段弯度跟着平移）。</summary>
        Node = 3,

        /// <summary>拖某根控制柄（节点不动，只改切线方向）。</summary>
        Handle = 4,
    }

    /// <summary>当前工具。贝塞尔模式下画布上的按下/拖动都走 <see cref="BeginPath"/> 那一路。</summary>
    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            // 切走之前把没画完的这条收尾：不然画布上会留一条半截曲线，而人已经去点别的东西了。
            if (_tool == EditorTool.Bezier && IsDrawingPath) FinishPath();
            _tool = value;
            Raise(nameof(Tool));
            Raise(nameof(IsBezierTool));
            StatusText = value == EditorTool.Bezier
                ? "曲线工具：点一下＝一个尖角（这一笔是直线）；按下拖开＝这一点带柄，刚画的那一段跟着弯。" +
                  "接着点下一处续画，双击或回车收尾，Esc 取消。"
                : "已切回选择工具。";
            CanvasChanged?.Invoke();
        }
    }
    private EditorTool _tool = EditorTool.Select;

    /// <summary>那颗「曲线」按钮绑的就是它（双向）。</summary>
    public bool IsBezierTool
    {
        get => Tool == EditorTool.Bezier;
        set => Tool = value ? EditorTool.Bezier : EditorTool.Select;
    }

    // ---------- 贝塞尔：正在画的那条路径（第 49 棒）----------

    /// <summary>正在画的节点表（绝对毫米 + 各自两根柄）。</summary>
    private readonly List<CurveNode> _path = new();

    /// <summary>画布上是否有一条正在画的曲线（决定 Enter/Esc 归谁、以及要不要画节点标记）。</summary>
    public bool IsDrawingPath => _path.Count > 0;

    /// <summary>正在画的那条路径的节点（控件画圈用；柄已经在元素本体里）。</summary>
    public IReadOnlyList<CurveNode> PathNodes => _path;

    /// <summary>正在画的元素下标（-1 = 还没落地）。</summary>
    public int PathIndex { get; private set; } = -1;

    /// <summary>
    /// 贝塞尔工具下按下鼠标：落下一个节点。<strong>只点不按＝尖角＝这一笔是直线段</strong>
    /// （CorelDRAW 的口径，2026-09-13 用户看图更正：不按 Shift 本来就是直线，只有把点拖开才弯）。
    /// <para>第一个点会真的建一个元素（走 <see cref="TemplateFactory.AddElement"/> 那一路，
    /// 所以撤销栈、越界校验、重叠提醒都与其他元素同一套），之后每个点只改这个元素的节点表。</para>
    /// </summary>
    public bool BeginPath(double xMm, double yMm, bool constrain = false)
    {
        if (constrain && _path.Count > 0)
        {
            // CorelDRAW 的贝塞尔工具写着"按住 Ctrl 键单击可限制线条"（VGCoreIntl.dll 自带文案，实测挖出来的）。
            // 这里按同一口径实现：这一笔被夹成水平或垂直，跟选择工具里 Ctrl 锁轴是一个意思。
            (xMm, yMm) = ConstrainFrom(_path[^1], xMm, yMm);
        }
        if (_path.Count == 0)
        {
            var element = TemplateFactory.NewLine(xMm, yMm, xMm, yMm, DefaultLineThicknessMm);
            Capture();                       // 必须在加入之前录：快照里带着这个新元素的话，Esc/撤销会把它原样放回来
            var outcome = TemplateFactory.AddElement(_template, element, xMm, yMm);
            if (outcome is null)
            {
                StatusText = "元素数量已到上限，画不下去了。";
                ReleaseCapture();
                return false;
            }
            PathIndex = outcome.Index;
            var placed = _template.Elements[outcome.Index];
            SelectedRow = Elements.FirstOrDefault(r => ReferenceEquals(r.Element, placed)) ?? SelectedRow;
            // 落点以元素为准：AddElement 找不到原位时会把它挪到最近的空位，拿点击坐标当第一个节点就会与元素对不上。
            _path.Add(new CurveNode(placed.X, placed.Y, 0, 0, 0, 0));
        }
        else
        {
            // 新节点一律先当"单击＝尖角"。刻意不把上一节点的出柄镜像过来：那样等于替用户决定"这段要平滑"，
            // 几笔下来柄互相牵着走，画出来的就是乱绕的圈（2026-09-13 用户实拍的那张乱画）。
            _path.Add(new CurveNode(xMm, yMm, 0, 0, 0, 0));
        }
        CurrentNodeIndex = _path.Count - 1;  // 画的过程中也要看得见方向线，否则"这一拖会把上一段弯成什么样"全靠猜
        WritePath();
        return true;
    }

    /// <summary>
    /// 拖动中：<strong>拖的是"刚画出来的那一段"</strong>（CorelDRAW / 通用钢笔的语义，用户 2026-09-13 看图纠正）。
    /// <para>第一个点后面还没有段可弯，所以它拖的是<strong>出柄</strong>；从第二个点起，拖的是这一点的
    /// <strong>进柄</strong>（它决定上一段怎么进到这个点），出柄按镜像跟着走，节点保持平滑。</para>
    /// </summary>
    public void DragPath(double xMm, double yMm, bool constrain = false)
    {
        if (_path.Count == 0) return;
        var n = _path[^1];
        if (constrain)
        {
            (xMm, yMm) = ConstrainFrom(n, xMm, yMm);
        }
        var dx = xMm - n.X;
        var dy = yMm - n.Y;
        _path[^1] = _path.Count == 1
            ? n with { OutX = dx, OutY = dy }
            : n with { InX = dx, InY = dy, OutX = -dx, OutY = -dy };
        WritePath();
    }

    /// <summary>把目标点夹到参照点的正上/正下/正左/正右（位移更大的那根轴说了算）。</summary>
    private static (double X, double Y) ConstrainFrom(CurveNode from, double xMm, double yMm)
        => Math.Abs(xMm - from.X) >= Math.Abs(yMm - from.Y)
            ? (xMm, from.Y)
            : (from.X, yMm);

    /// <summary>松开鼠标：这一节的柄定下来了（此时才允许下一段接上去）。</summary>
    public void EndPathSegment()
    {
        if (_path.Count == 0) return;
        SnapPathForStorage();
        Touch();
        RebuildSample();
        RecomputeIssues();
        CanvasChanged?.Invoke();
    }

    /// <summary>
    /// 收尾（双击或回车）：最后一个节点没有出柄，路径变成一条完整的曲线元素。
    /// <para>只点了一下就收尾（不足两点）时把刚建的那个元素撤掉——留一条零长度线在列表里是垃圾。</para>
    /// </summary>
    public bool FinishPath()
    {
        if (_path.Count < 2)
        {
            AbandonPath();
            return false;
        }
        var keep = _path[^1] with { OutX = 0, OutY = 0 };
        _path[^1] = keep;
        WritePath();
        _path.Clear();
        PathIndex = -1;
        Touch();
        RebuildSample();
        RecomputeIssues();
        CanvasChanged?.Invoke();
        StatusText = "曲线画好了。拖节点或拖柄可以继续调。";
        return true;
    }

    /// <summary>Esc：丢掉正在画的这条路径（含刚建出来的那个元素），撤销栈一起收回。</summary>
    public void CancelPath()
    {
        if (_path.Count == 0) return;
        _path.Clear();
        if (PathIndex >= 0 && PathIndex < _template.Elements.Count) _template.Elements.RemoveAt(PathIndex);
        PathIndex = -1;
        ReleaseCapture();
        RefreshElements();
        RebuildSample();
        RecomputeIssues();
        CanvasChanged?.Invoke();
        StatusText = "取消这条曲线。";
    }

    private void AbandonPath()
    {
        var index = PathIndex;
        _path.Clear();
        if (index >= 0 && index < _template.Elements.Count) _template.Elements.RemoveAt(index);
        PathIndex = -1;
        ReleaseCapture();
        RefreshElements();
        RebuildSample();
        RecomputeIssues();
        CanvasChanged?.Invoke();
    }

    /// <summary>把节点表写回元素（唯一的写入口，见 <see cref="CurveGeometry.ApplyNodes"/>）。</summary>
    private void WritePath()
    {
        if (PathIndex < 0 || PathIndex >= _template.Elements.Count || _path.Count < 1) return;
        var pts = _path.Count == 1
            ? new List<CurveNode> { _path[0], _path[0] }          // 只点了一下：零长度线，等下一个点
            : new List<CurveNode>(_path);
        CurveGeometry.ApplyNodes(_template.Elements[PathIndex], pts);
        CanvasChanged?.Invoke();
    }

    private void SnapPathForStorage()
    {
        for (var i = 0; i < _path.Count; i++)
        {
            var n = _path[i];
            _path[i] = n with
            {
                X = Math.Round(n.X, 3), Y = Math.Round(n.Y, 3),
                InX = Math.Round(n.InX, 3), InY = Math.Round(n.InY, 3),
                OutX = Math.Round(n.OutX, 3), OutY = Math.Round(n.OutY, 3),
            };
        }
        if (PathIndex >= 0 && PathIndex < _template.Elements.Count) CurveGeometry.SnapForStorage(_template.Elements[PathIndex]);
    }

    /// <summary>新画曲线的默认线宽（与「添加线条」那颗按钮同源，不另定一份）。</summary>
    public const double DefaultLineThicknessMm = 0.35;

    // ---------- 当前节点与三态（CorelDRAW：使节点成为尖突 / 平滑节点 / 生成对称节点）----------

    /// <summary>
    /// 刚抓过的那个节点（<see cref="CurveGeometry.NodesOf"/> 那张全节点表里的下标，0 = 起点）。
    /// <para>画布上"哪个方块是实心的"、方向线画在谁身上、那三颗按钮能不能点，全看这一个数。</para>
    /// </summary>
    public int CurrentNodeIndex
    {
        get => _currentNode;
        private set
        {
            if (_currentNode == value) return;
            _currentNode = value;
            Raise(nameof(CurrentNodeIndex));
            Raise(nameof(HasCurrentNode));
            CanvasChanged?.Invoke();
        }
    }
    private int _currentNode = -1;

    public bool HasCurrentNode => _currentNode >= 0;

    /// <summary>使当前节点成为尖突（两柄收掉，曲线在此折断）。</summary>
    public RelayCommand NodeCornerCommand { get; private set; } = null!;

    /// <summary>平滑当前节点：两柄共线、各留原长。</summary>
    public RelayCommand NodeSmoothCommand { get; private set; } = null!;

    /// <summary>给当前节点生成对称柄：共线且等长。</summary>
    public RelayCommand NodeSymmetricCommand { get; private set; } = null!;

    /// <summary>三态命令共用的一步编辑：录撤销 → 改当前节点 → 刷新（与拖动那几处同一套口径）。</summary>
    private void ApplyNodeType(Action<TemplateElement, int> edit)
    {
        var element = SelectedRow?.Element;
        if (element is null || _currentNode < 0) return;
        Capture();
        edit(element, _currentNode);
        Touch();
        RebuildSample();
        RecomputeIssues();
        CanvasChanged?.Invoke();
    }

    private void BuildNodeCommands()
    {
        bool Can() => HasCurrentNode && SelectedRow?.Element is { Kind: ElementKind.Line } e && CurveGeometry.IsCurved(e);
        NodeCornerCommand = new RelayCommand(() => ApplyNodeType(CurveGeometry.MakeCorner), Can);
        NodeSmoothCommand = new RelayCommand(() => ApplyNodeType(CurveGeometry.MakeSmooth), Can);
        NodeSymmetricCommand = new RelayCommand(() => ApplyNodeType(CurveGeometry.MakeSymmetric), Can);
    }

    /// <summary>
    /// 按下鼠标：<paramref name="handleRadiusMm"/> 由控件按当前缩放换算（屏幕上约 8 像素）。
    /// 命中元素即选中并准备拖动；点空白则取消选中。
    /// <para><paramref name="anchor"/> = 按住 Shift 时的锚点（<see cref="ResizeAnchor.Center"/> = 绕中心向四周，
    /// 与 CorelDRAW 同口径，第 45 棒实测）；<paramref name="lockAxis"/> = 按住 Ctrl，
    /// 移动时锁到水平或垂直一根轴上。</para>
    /// <para><strong>贝塞尔工具不走这条路</strong>：那颗工具切过去之后，控件直接调
    /// <see cref="BeginPath"/>/<see cref="DragPath"/>/<see cref="EndPathSegment"/>。</para>
    /// </summary>
    public DragMode BeginDrag(double xMm, double yMm, double handleRadiusMm,
        ResizeAnchor anchor = ResizeAnchor.Opposite, bool lockAxis = false)
    {
        // 命中【不】用墨迹盒：AI 行式模板每行是一条全宽行带，只认墨迹会让"点文字旁边的空白选不中这一行"，
        // 比改之前更难选。分工是刻意的：**点得中 = 行带（宽容）**，**看得见框、抓得到句柄 = 墨迹（精确）**。
        var index = EditGeometry.TopmostAt(_template, xMm, yMm);
        if (index < 0 && SelectedRow?.Element is { Kind: ElementKind.Line } selected && CurveGeometry.IsCurved(selected)
            && CurveGeometry.HandleAt(selected, xMm, yMm, handleRadiusMm) is not null)
        {
            // 柄头常常伸在弧的外面：先按"元素本体命中"拦一道就永远抓不到它（CDR 同样是先选中物件、再拖节点）。
            index = _template.Elements.IndexOf(selected);
        }
        _dragMode = DragMode.None;
        _dragIndex = -1;
        _dragSnapshot = null;
        _dragAnchor = anchor;
        _dragLockAxis = lockAxis;
        _activeGuides = Array.Empty<GuideLine>();

        if (index < 0)
        {
            SelectedRow = null;
            CurrentNodeIndex = -1;
            return DragMode.None;
        }

        var element = _template.Elements[index];
        if (!ReferenceEquals(SelectedRow?.Element, element)) CurrentNodeIndex = -1;  // 换了一个元素，别把上一个的当前节点带过去
        SelectedRow = Elements.FirstOrDefault(r => ReferenceEquals(r.Element, element));

        Capture();
        _dragSnapshot = element.CloneTemplate();
        // 墨迹盒"相对排版盒"的偏移与尺寸，按下这一刻量一次就够（此时缓存与版面都是新鲜的）。
        // 拖动中若每帧再去读 DisplayBoxOf，拿到的是**上一帧位置**量出来的盒：RestoreGeometry 把元素
        // 搬回快照后，那个盒还留在上一帧的地方，钳制基准逐帧错位 —— 用户看到的就是抖动 + 莫名被限制。
        _dragInkRel = RelativeInkOf(element);
        _dragStart = (xMm, yMm);
        _dragIndex = index;

        // 曲线上的节点/柄优先：抓中它们时动的是那一个点，不是整条元素。
        if (element.Kind == ElementKind.Line && CurveGeometry.IsCurved(element)
            && CurveGeometry.HandleAt(element, xMm, yMm, handleRadiusMm) is { } hit)
        {
            _curveHit = hit;
            CurrentNodeIndex = hit.Index;  // 抓到的这个点就是当前节点：方向线与三态按钮都作用在它身上
            _dragMode = hit.Part == CurvePart.Node ? DragMode.Node : DragMode.Handle;
            _dragHandle = ResizeHandle.None;
            return _dragMode;
        }

        var handle = EditGeometry.HandleAt(element, xMm, yMm, handleRadiusMm, DisplayBoxOf(element));
        _dragHandle = handle;
        _dragMode = handle == ResizeHandle.None ? DragMode.Move : DragMode.Resize;
        return _dragMode;
    }

    // ---------- 显示盒（第 44 棒：选中框贴文字墨迹，不再框整条行带） ----------

    private readonly Dictionary<TemplateElement, (double X, double Y, double Width, double Height)?> _displayBoxes = new();
    private ResizeAnchor _dragAnchor;
    private bool _dragLockAxis;
    private (double X, double Y, double Width, double Height)? _dragInkRel;
    private CurveHit? _curveHit;

    /// <summary>
    /// 元素"看得见的那一块"（毫米、未旋转）：文本 = <see cref="TextFit"/> 实测的墨迹盒按字面拉伸
    /// 绕排版盒中心放大（与渲染同一锚点同一顺序）；其余元素返回 null = 用默认 VisualBoxOf。
    /// <para>结果随 <see cref="SampleLayout"/> 一起作废（RebuildSample 里清缓存）——量的是"这一版排出来
    /// 什么样"，内容/字号/拉伸一变墨迹就变。命中、句柄、选择框三处共用它，不许各量各的。</para>
    /// </summary>
    public (double X, double Y, double Width, double Height)? DisplayBoxOf(TemplateElement element)
    {
        if (_displayBoxes.TryGetValue(element, out var cached)) return cached;
        var box = ComputeDisplayBox(element);
        _displayBoxes[element] = box;
        return box;
    }

    private (double X, double Y, double Width, double Height)? ComputeDisplayBox(TemplateElement element)
    {
        if (element.Kind != ElementKind.Text) return null;

        // 按引用找回这一元素排出来的那条文本项（被隐藏的文本行没有版面项 → 退回默认盒）。
        var item = SampleLayout.Items.OfType<LabelGou.Core.Layout.TextItem>()
            .FirstOrDefault(i => ReferenceEquals(i.Source, element));
        if (item is null) return null;

        // 量法只有一份（TextInkBox）：编辑器画框、摆位钳制、越界检查共用它，不许各推一遍。
        return LabelGou.App.Rendering.TextInkBox.Measure(item);
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

        if ((_dragMode == DragMode.Node || _dragMode == DragMode.Handle) && _curveHit is { } hit)
        {
            // 每帧从按下时的快照重算（与 Move/Resize 同一口径）：不累积误差，也保证 Esc 一次回到原位。
            var pts = CurveGeometry.NodesOf(_dragSnapshot);
            if (hit.Index >= pts.Count) return;
            var n = pts[hit.Index];
            if (_dragMode == DragMode.Node)
            {
                pts[hit.Index] = n with { X = n.X + dx, Y = n.Y + dy };
            }
            else
            {
                var (hx, hy) = hit.Part == CurvePart.Out ? (n.OutX + dx, n.OutY + dy) : (n.InX + dx, n.InY + dy);
                // 平滑节点（两根柄成一直线）拖一边另一边跟着镜像——这是"类 CDR"里最常用的一半手感；
                // 尖角节点（两柄不在一条线上，或只有一根）只动这一根，不然调不动单侧切线。
                var smooth = CurveGeometry.IsSmooth(n);
                pts[hit.Index] = hit.Part == CurvePart.Out
                    ? n with { OutX = hx, OutY = hy, InX = smooth ? -hx : n.InX, InY = smooth ? -hy : n.InY }
                    : n with { InX = hx, InY = hy, OutX = smooth ? -hx : n.OutX, OutY = smooth ? -hy : n.OutY };
            }
            CurveGeometry.ApplyNodes(element, pts);
            RebuildSample();
            CanvasChanged?.Invoke();
            return;
        }

        if (_dragMode == DragMode.Move)
        {
            if (_dragLockAxis)
            {
                // Ctrl = 只沿一根轴走（CorelDRAW 实测：Ctrl+移动锁水平或垂直），位移更大的那根说了算。
                if (Math.Abs(dx) >= Math.Abs(dy)) dy = 0; else dx = 0;
            }
            // proposed 用的是【包围盒左上角】而不是元素原点：MoveTo/Snap 的坐标口径是盒。
            // 两者对直线以下元素并不相同（从右往左画的线、或弧身凸出两端的曲线），差多少就跳多少。
            var boxAtStart = EditGeometry.BoxOf(_dragSnapshot);
            var proposed = (X: boxAtStart.X + dx, Y: boxAtStart.Y + dy);
            var occupancy = DragInk(element);
            if (_snapEnabled)
            {
                var snapped = EditGeometry.Snap(_template, _dragIndex, proposed.X, proposed.Y, new SnapOptions
                {
                    Enabled = true,
                    SnapToGrid = _snapToGrid,
                    GridStepMm = _gridStepMm,
                    SnapNeighbors = true,
                }, occupancy);
                _activeGuides = snapped.Guides;
                EditGeometry.MoveTo(_template, _dragIndex, snapped.X, snapped.Y, occupancy);
            }
            else
            {
                _activeGuides = Array.Empty<GuideLine>();
                EditGeometry.MoveBy(_template, _dragIndex, dx, dy, occupancy);
            }
        }
        else
        {
            // 第 45 棒的根治口：**锚点闭环**。Core 量不了字（不许碰 WPF），所以在这里用生产量具
            // 量出拖前/拖后两块墨迹盒，把"该钉住的那个点"的残差用**纯平移**补掉。
            // 不这么做，文本的拉伸就永远绕着排版盒中心长（中心 = X + Width/2 会随宽度自己搬走），
            // 用户看到的永远是"每拖一次缩放，位置就偏一次"。
            var before = DragInk(element) ?? MeasuredBox(element);
            EditGeometry.ResizeBy(_template, _dragIndex, _dragHandle, dx, dy, _dragAnchor, before);
            RebuildSample();
            var after = MeasuredBox(element);
            var (shiftX, shiftY) = EditGeometry.AnchorShift(before, after, _dragHandle, _dragAnchor);
            if (Math.Abs(shiftX) > 0.002 || Math.Abs(shiftY) > 0.002)
            {
                element.X += shiftX;
                element.Y += shiftY;
                RebuildSample();
            }
            WarnWhenScalingCappedByBandWidth(element, before, after, dx, dy);
        }

        RebuildSample();
        CanvasChanged?.Invoke();
    }

    /// <summary>元素"看得见的那一块"：文本 = 实测墨迹盒，其余 = 视觉盒（含拉伸）。量不到就退回视觉盒，不猜。</summary>
    private (double X, double Y, double Width, double Height) MeasuredBox(TemplateElement element)
        => DisplayBoxOf(element) ?? EditGeometry.VisualBoxOf(element);

    /// <summary>
    /// 摆位/吸附/对齐该看哪个盒（第 46 棒）：文本 = 看得见的墨迹；其余返回 null = 沿用排版盒（两者本就相同）。
    /// <para>不这么分，一行 20mm 的字会被一条 130mm 的隐形行带顶住——纸 140 时整行只有 5mm 活动量。</para>
    /// </summary>
    private (double X, double Y, double Width, double Height)? OccupancyOf(TemplateElement element)
        => element.Kind == ElementKind.Text ? MeasuredBox(element) : null;

    /// <summary>墨迹盒相对排版盒的偏移与尺寸（按下那一刻量一次；两者永远同幅，只差一个平移）。</summary>
    private (double X, double Y, double Width, double Height)? RelativeInkOf(TemplateElement element)
    {
        var ink = OccupancyOf(element);
        if (ink is null) return null;
        var box = EditGeometry.BoxOf(element);
        return (ink.Value.X - box.X, ink.Value.Y - box.Y, ink.Value.Width, ink.Value.Height);
    }

    /// <summary>这一帧该用的占位盒：排版盒当前位置 + 按下时记下的相对偏移。不读缓存，避免拿到上一帧的盒。</summary>
    private (double X, double Y, double Width, double Height)? DragInk(TemplateElement element)
    {
        var rel = _dragInkRel;
        if (rel is null) return null;
        return (element.X + rel.Value.X, element.Y + rel.Value.Y, rel.Value.Width, rel.Value.Height);
    }

    /// <summary>
    /// 文本角柄放大被行带宽度顶住时说一句人话（第 45 棒）。
    /// <para>为什么需要：<c>ShrinkToFit</c>（自动缩字）在"字号撑到比盒宽还宽"时会把字号压回去，
    /// 于是拖了角却不见得变大——那是设计（宁可缩字也不让字折出去印糊），不是坏了。
    /// 不说清楚，用户只会以为手势失灵（同族：§十-A 的"点了没反应"那一类）。</para>
    /// </summary>
    private void WarnWhenScalingCappedByBandWidth(TemplateElement element,
        (double X, double Y, double Width, double Height) before,
        (double X, double Y, double Width, double Height) after, double dxMm, double dyMm)
    {
        if (element.Kind != ElementKind.Text) return;
        var isCorner = (dxMm != 0 || dyMm != 0)
            && (_dragHandle & (ResizeHandle.Left | ResizeHandle.Right)) != 0
            && (_dragHandle & (ResizeHandle.Top | ResizeHandle.Bottom)) != 0;
        if (!isCorner || before.Width <= 0) return;

        var (ldx, ldy) = EditGeometry.ToLocalDelta(element, dxMm, dyMm);
        var wanted = EditGeometry.UniformRatio(before, _dragHandle, _dragAnchor, ldx, ldy);
        var got = after.Width / before.Width;
        if (wanted - got > 0.02)
            StatusText = $"这行已经撑满行带宽度，只能到 {got:0.##} 倍——要再大，先在右侧把「宽(mm)」加宽（加宽会改折行判定，所以不代你动）。";
    }

    public void EndDrag()
    {
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;
        _dragIndex = -1;
        _dragSnapshot = null;
        _dragHandle = ResizeHandle.None;
        _curveHit = null;
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
        _curveHit = null;
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
        EditGeometry.MoveBy(_template, index, dxMm, dyMm, OccupancyOf(row.Element));
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

    /// <summary>
    /// 存盘前的闸门：Core 的校验器量不到墨迹（不许碰 WPF），所以"字排到纸外"这一条只能由编辑器把。
    /// <para>第 46 棒：不加这道闸，永不折行的文字就能带着出纸的版面存进模板库——正是"会印错且看不见"那一类。</para>
    /// </summary>
    private bool BlocksSave()
    {
        RecomputeIssues();
        if (!HasError) return false;
        var first = Issues.FirstOrDefault(m => m.Contains("探出标签")) ?? Issues.FirstOrDefault();
        Report("模板还有问题，先解决再存：" + Environment.NewLine + string.Join(Environment.NewLine, Issues));
        AppLog.Warning($"编辑器拒绝存盘：{first}");
        return true;
    }

    public void Save()
    {
        SyncIntoTemplate();
        if (BlocksSave()) return;
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
        if (BlocksSave()) return;
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
        // 第 42 棒补：拖角柄时字号会跟着等比缩放，而 DragTo 是「每帧从快照重算」的写法 ——
        // 不把字号一起恢复，缩放就会逐帧累积（拖一下字大得离谱，撤销也只能退回拖动前）。
        target.FontSizePt = snapshot.FontSizePt;
        // 第 43 棒同因：文本边柄拖的是字面拉伸倍率（RotationDeg 由面板改、拖动不碰，一并带上以防别的改动漏恢复）。
        target.TextScaleX = snapshot.TextScaleX;
        target.TextScaleY = snapshot.TextScaleY;
        // 第 49 棒：曲线三个字段必须一起恢复。DragTo 是"每帧从快照重算"，漏了节点就会出现
        // 拖一下节点、弧身留在原地，而且误差逐帧累积。列表要拷一份，不能与快照共用同一个 List。
        target.Nodes = snapshot.Nodes is { } nodes ? new List<CurveNode>(nodes) : null;
        target.StartOut = snapshot.StartOut;
        target.EndIn = snapshot.EndIn;
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
        // 显示盒量的是"这一版排出来什么样"，版面一变缓存必须作废（命中/句柄/选择框三处共用它）。
        _displayBoxes.Clear();
        try
        {
            // 编辑器的职责就是”照参考图对齐”，所以画布里得看得见那张不上纸的底图（includeReference:true）。
            // 第 42 棒：样例记录改用 TemplateSample.ForTemplate —— 按这份模板实际引用的列补样例值，
            // 否则 AI 行式模板里那些 {{col:列名}} 全取不到值、整行被隐藏，画布就成了”框在字没了”
            // （用户报的第三条）。这条只服务”给人看版面”，出纸闸那条路一律不用它（见 TemplateSample 注释）。
            SampleLayout = LayoutEngine.Build(_template, TemplateSample.ForTemplate(_template),
                new LayoutContext(1, 1, "样例数据.xlsx", IncludeReference: true));
        }
        catch (Exception ex)
        {
            AppLog.Warning("样例版面构建失败，画布退回空白：" + ex.Message);
            SampleLayout = new LabelLayout();
            // 第 42 棒补：原来异常被悄悄吞成空白，用户分不清”模板本来就是空的”还是”画布坏了”。
            // 状态栏说一句人话，让人知道该去看日志、而不是以为模板丢了。
            StatusText = "画布渲染出了问题，这一版显示为空白（详见诊断日志）。";
        }
        CanvasChanged?.Invoke();
    }

    public void RecomputeIssues() => RecomputeIssues(WithInkOverflow(TemplateValidator.Validate(_template)));

    /// <summary>
    /// 永不折行的文本，越界只能按<strong>看得见的墨迹</strong>判（Core 量不了字，这里用屏幕上那份样例版面量）。
    /// <para>第 46 棒：折行边界从行带手里交出去之后，"内容别出纸"这份保护由这里接手；出纸前还有一道
    /// 按真数据量的闸门（<c>PageContentSource</c>），两处共用 <see cref="Rendering.TextInkBox"/> 同一份量法。</para>
    /// </summary>
    private IReadOnlyList<TemplateIssue> WithInkOverflow(IReadOnlyList<TemplateIssue> issues)
    {
        var list = issues.ToList();
        for (var i = 0; i < _template.Elements.Count; i++)
        {
            var element = _template.Elements[i];
            if (element.Kind != ElementKind.Text || !element.NoWrap || !element.Visible) continue;
            var over = InkOverflowMm(element);
            if (over <= TemplateValidator.ToleranceMm) continue;
            list.Add(new TemplateIssue(IssueLevel.Error,
                $"第 {i + 1} 个元素的字排出来探出标签约 {over:0.#} mm。这一行是「永不折行」，不会被行带默默收回去——" +
                "请挪回纸内、改小字号，或点工具栏「缩回纸内」。", i));
        }
        list.AddRange(ArtworkDegradations());
        return list;
    }

    /// <summary>
    /// 矢量底稿的降级告警（第 48 棒）。<c>SvgRenderPlan.Issues</c> 从前只有导出摘要里说一次：
    /// 同一份底稿，画布上看着正常、渐变被抹成纯色这件事要到出片才看得见，那就是静默降级。
    /// 底稿路径与缓存都走 <see cref="SampleLayout"/> 那份（与上面按墨迹量越界同一条来源）。
    /// </summary>
    private IEnumerable<TemplateIssue> ArtworkDegradations()
    {
        for (var i = 0; i < _template.Elements.Count; i++)
        {
            var element = _template.Elements[i];
            if (element.Kind != ElementKind.Vector || !element.Visible) continue;
            var vector = SampleLayout.Items.OfType<VectorItem>().FirstOrDefault(v =>
                v.X == element.X && v.Y == element.Y && v.Width == element.Width && v.Height == element.Height);
            if (vector is null) continue;
            var plan = Rendering.SvgDrawableBuilder.Load(vector.AbsolutePath);
            if (plan is null) continue;
            foreach (var issue in plan.Issues.Distinct())
            {
                yield return new TemplateIssue(IssueLevel.Warning, $"第 {i + 1} 个元素的底稿：{issue}", i);
            }
        }
    }

    /// <summary>这一元素看得见墨迹（含旋转外接）探出纸边几毫米；量不到就返回 0（不猜）。</summary>
    private double InkOverflowMm(TemplateElement element)
    {
        var ink = DisplayBoxOf(element);
        if (ink is null) return 0;
        var item = SampleLayout.Items.OfType<LabelGou.Core.Layout.TextItem>()
            .FirstOrDefault(t => ReferenceEquals(t.Source, element));
        if (item is null) return 0;
        var occ = Rendering.TextInkBox.RotatedOf(item, ink.Value);
        return Math.Max(
            Math.Max(occ.Right - _template.WidthMm, occ.Bottom - _template.HeightMm),
            Math.Max(-occ.X, -occ.Y));
    }

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

    /// <summary>
    /// 枚举本机系统字体名（WPF <c>Fonts.SystemFontFamilies</c>，含用户字体目录），按名字排序。
    /// <para>模板默认字体（微软雅黑）无论枚举结果如何都保证在列——缺字体的机器上让用户仍能看到
    /// "当前用的是谁"，渲染端 <c>RenderRules.SafeFontFamily</c> 本来就有回退兜底。</para>
    /// </summary>
    private static IReadOnlyList<string> BuildSystemFonts()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            if (!string.IsNullOrWhiteSpace(family.Source)) names.Add(family.Source);
        }
        names.Add(TemplateElement.DefaultFont);
        return names.ToList();
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
        ApplyInkPresetCommand = new RelayCommand(p => { if (p is InkPreset preset) InkColor = preset.Color; });
        ResetInkCommand = new RelayCommand(() => InkColor = null, () => _element.InkColor is not null);
        StraightenCommand = new RelayCommand(() => CurveEdit(CurveGeometry.Straighten), () => IsCurve);
        FlattenToEndsCommand = new RelayCommand(() => CurveEdit(Flatten), () => IsCurve);
        SyncPickerFromColour();   // 选中一行时调色盘要停在那支墨真正的位置，别默认给左上角
    }

    /// <summary>只留两端：中间节点与柄全清掉，ApplyNodes 会把三个曲线字段一起置空（与老文件里一条直线同形）。</summary>
    private static void Flatten(TemplateElement element) => CurveGeometry.ApplyNodes(element, new[]
    {
        new CurveNode(element.X, element.Y, 0, 0, 0, 0),
        new CurveNode(element.X2, element.Y2, 0, 0, 0, 0),
    });

    public TemplateElement Element => _element;

    public ElementKind Kind => _element.Kind;

    public bool IsText => _element.Kind == ElementKind.Text;

    public bool IsLine => _element.Kind == ElementKind.Line;

    public bool IsImage => _element.Kind == ElementKind.Image;

    public bool HasBox => _element.Kind != ElementKind.Line;

    /// <summary>只有文本才有「折行宽度」这一格（第 46 棒）。</summary>
    public bool HasText => _element.Kind == ElementKind.Text;

    /// <summary>
    /// 折行宽度（毫米）。<strong>0 = 永不折行</strong>：内容多长排多长，排到纸外由「缩回纸内」收回。
    /// <para>填一个正值（例如与「宽(mm)」相同）就恢复老行为：按这个宽度折行、按它缩字号。
    /// 这条字段是第 46 棒把折行边界从隐形行带手里交出来的产物，别与「宽(mm)」混为一谈——
    /// 后者只管字在哪对齐。</para>
    /// </summary>
    public double WrapWidthMm
    {
        get => _element.WrapWidthMm;
        set
        {
            if (Near(value, _element.WrapWidthMm)) return;
            Prepare(nameof(WrapWidthMm));
            _element.WrapWidthMm = Math.Max(0, Math.Round(value, 2));
            Done();
        }
    }

    /// <summary>
    /// 这一支墨的颜色（第 47 棒）。<strong>null = 黑</strong> = 老模板的观感，不填就不写进 JSON。
    /// <para>面板上 CMYK 与 RGB 两档改的是<em>同一个</em> <see cref="LabelColor"/>：两端分量本来就并存，
    /// 换档不换数（切过去看到的仍是这支墨在另一档的写法），所以不会有"切一下档颜色就变了"的怪事。</para>
    /// </summary>
    public LabelColor? InkColor
    {
        get => _element.InkColor;
        set
        {
            if (Equals(value, _element.InkColor)) return;
            Prepare(nameof(InkColor));
            _element.InkColor = value;
            if (!_applyingPicker) SyncPickerFromColour();     // 调色盘自己拖出来的那一路以面板为准，别反推回去
            Done();
        }
    }

    private LabelColor InkOrBlack => _element.InkColor ?? LabelColor.Black;

    /// <summary>色块画成什么色。<paramref name="InkColor"/> 为空时也画黑——面板上要看得见"这支是黑"，不许留白。</summary>
    public Brush InkSwatch => RenderRules.InkOf(_element.InkColor);

    /// <summary>色块旁边那行字：印刷口径的分量打头（他跟印刷店就是这么说话的），十六进制跟着。</summary>
    public string InkSummary => _element.InkColor is { } ink ? $"{ink.ToPrintText()}　{ink.ToHex()}" : "黑（默认）";

    /// <summary>面板当前给 CMYK 档。没填过颜色时默认给 CMYK——这活是印刷活。</summary>
    public bool InkUsesCmyk
    {
        get => _element.InkColor?.Entry != ColorEntrySpace.Srgb;
        set
        {
            if (value == InkUsesCmyk) return;
            var ink = InkOrBlack;
            InkColor = value
                ? LabelColor.FromCmyk(ink.C, ink.M, ink.Y, ink.K)
                : LabelColor.FromSrgb(ink.R, ink.G, ink.B);
        }
    }

    /// <summary>RGB 档那颗单选钮（与 <see cref="InkUsesCmyk"/> 互为反面，绑到同一对 GroupName 上会打架，所以单开一格）。</summary>
    public bool InkUsesRgb
    {
        get => !InkUsesCmyk;
        set => InkUsesCmyk = !value;
    }

    public int InkC
    {
        get => InkOrBlack.C;
        set => SetCmyk(value, InkOrBlack.M, InkOrBlack.Y, InkOrBlack.K);
    }

    public int InkM
    {
        get => InkOrBlack.M;
        set => SetCmyk(InkOrBlack.C, value, InkOrBlack.Y, InkOrBlack.K);
    }

    public int InkY
    {
        get => InkOrBlack.Y;
        set => SetCmyk(InkOrBlack.C, InkOrBlack.M, value, InkOrBlack.K);
    }

    public int InkK
    {
        get => InkOrBlack.K;
        set => SetCmyk(InkOrBlack.C, InkOrBlack.M, InkOrBlack.Y, value);
    }

    public int InkR
    {
        get => InkOrBlack.R;
        set => SetRgb(value, InkOrBlack.G, InkOrBlack.B);
    }

    public int InkG
    {
        get => InkOrBlack.G;
        set => SetRgb(InkOrBlack.R, value, InkOrBlack.B);
    }

    public int InkB
    {
        get => InkOrBlack.B;
        set => SetRgb(InkOrBlack.R, InkOrBlack.G, value);
    }

    /// <summary>十六进制那一格：认 <c>#rrggbb</c>、<c>#rgb</c>，也认 <c>cmyk(0 91 90 0)</c> 与 <c>device-cmyk(...)</c>。</summary>
    public string InkHex
    {
        get => InkOrBlack.ToHex();
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!LabelColor.TryParse(value.Trim(), out var parsed) || parsed is null) return;   // 打字打一半先不当真
            InkColor = parsed;
        }
    }

    /// <summary>
    /// 调色盘（第 47 棒补刀）：一方块 + 一条色相带，见 <see cref="PickerSaturation"/>。
    /// 尺寸常量给 XAML 与拇指坐标共用，别在两处各写一个数字。
    /// </summary>
    public const double PickerSquareW = 250;

    public const double PickerSquareH = 200;

    /// <summary>色相条与方块同高，好对齐。</summary>
    public const double PickerHueBarH = PickerSquareH;

    /// <summary>
    /// 调色盘自己记的那三值（<strong>界面状态，不落盘</strong>）。
    /// <para>为什么不能每次从当前墨色反推：黑色上饱和度没有定义（<see cref="HsvMath.FromRgb"/> 交回 0），
    /// 圈一旦拖到方块底边那条黑线上，再往上拖就变成白的了——手感受到了"颜色丢了"。
    /// 所以拖方块期间这三值以面板为准，只有<em>别的</em>口子改颜色（墨量滑条、常用预设、十六进制、撤销、换选中行）
    /// 时才反推一次同步过来，见 <see cref="SyncPickerFromColour"/>。</para>
    /// </summary>
    private double _pickerHueDeg;

    private double _pickerSat;

    private double _pickerVal;

    private bool _applyingPicker;

    /// <summary>当前色相（0~360），对应方块右边那条色相带。</summary>
    public double PickerHueDeg
    {
        get => _pickerHueDeg;
        set
        {
            var hue = HsvMath.NormalizeHue(value);
            if (Near(hue, _pickerHueDeg)) return;
            _pickerHueDeg = hue;
            ApplyPicker();
            RaisePicker();        // 灰上加色相不改变颜色，InkColor 那条链会静默，这里必须自己刷一次
        }
    }

    /// <summary>方块横轴＝饱和度（左白右纯）。</summary>
    public double PickerSaturation
    {
        get => _pickerSat;
        set
        {
            var s = Math.Clamp(value, 0, 1);
            if (Near(s, _pickerSat)) return;
            _pickerSat = s;
            ApplyPicker();
            RaisePicker();
        }
    }

    /// <summary>方块纵轴＝明度（上亮下黑）。鼠标在下方，所以与 <see cref="PickerThumbY"/> 反号。</summary>
    public double PickerValue
    {
        get => _pickerVal;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Near(v, _pickerVal)) return;
            _pickerVal = v;
            ApplyPicker();
            RaisePicker();
        }
    }

    private void ApplyPicker()
    {
        var (r, g, b) = HsvMath.ToRgb(_pickerHueDeg, _pickerSat, _pickerVal);
        ApplyPickerRgb(r, g, b);
    }

    /// <summary>
    /// 调色盘交回来的是屏幕色，但<strong>面板停在哪一档就不跳档</strong>：CMYK 档下先把屏幕色折成
    /// naive 印刷分量、再按分量录进去（<c>Entry</c> 仍是 Cmyk）。两条公式互逆，折回来的 RGB 与原值
    /// 至多差 1/255，换来的是"拖完方块四格墨量立刻能读能改"，而不是面板突然换成 RGB 滑条。
    /// </summary>
    private void ApplyPickerRgb(int r, int g, int b)
    {
        var byScreen = LabelColor.FromSrgb(r, g, b);
        _applyingPicker = true;
        try
        {
            InkColor = InkUsesCmyk
                ? LabelColor.FromCmyk(byScreen.C, byScreen.M, byScreen.Y, byScreen.K)
                : byScreen;
        }
        finally
        {
            _applyingPicker = false;
        }
    }

    public double PickerThumbX => _pickerSat * PickerSquareW;

    public double PickerThumbY => (1 - _pickerVal) * PickerSquareH;

    public double PickerHueThumbY => _pickerHueDeg / HsvMath.HueMax * PickerHueBarH;

    /// <summary>方块底色：左白 → 右当前色相的纯色。上面再叠一层 XAML 里写死的"透明→黑"，就是截图那种方块。</summary>
    public Brush PickerSvFill
    {
        get
        {
            var (r, g, b) = HsvMath.ToRgb(_pickerHueDeg, 1, 1);
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(Colors.White, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), 1));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>颜色从调色盘以外的口子变了（墨量滑条、预设、十六进制、撤销、换选中行）之后，把面板上的圈搬回那支墨真正的位置。</summary>
    private void SyncPickerFromColour()
    {
        var ink = InkOrBlack;
        var (hue, sat, val) = HsvMath.FromRgb(ink.R, ink.G, ink.B);
        if (!double.IsNaN(hue)) _pickerHueDeg = hue;      // 无彩时保留上一次选的色相
        _pickerSat = sat;
        _pickerVal = val;
    }

    private void RaisePicker()
    {
        foreach (var name in new[]
        {
            nameof(PickerHueDeg), nameof(PickerSaturation), nameof(PickerValue),
            nameof(PickerThumbX), nameof(PickerThumbY), nameof(PickerHueThumbY), nameof(PickerSvFill),
        })
        {
            Raise(name);
        }
    }

    /// <summary>「恢复默认（黑）」＝把这格清空（null），存盘时这个字段整个不写，与老模板逐字同形。</summary>
    public ICommand ResetInkCommand { get; }

    // ---------- 曲线（第 49 棒）----------

    /// <summary>这条元素是曲线：线条带了中间节点或控制柄才算（两点直线不算）。</summary>
    public bool IsCurve => _element.Kind == ElementKind.Line && CurveGeometry.IsCurved(_element);

    /// <summary>节点总数（含两端）。两点直线交回 2。</summary>
    public int NodeCount => _element.Kind == ElementKind.Line ? CurveGeometry.NodesOf(_element).Count : 0;

    /// <summary>拉直：所有柄归零，节点位置不动（拖歪了想退回直线段，比删了重画快）。</summary>
    public ICommand StraightenCommand { get; }

    /// <summary>只留两端：删掉所有中间节点与柄，回到从前那条两点直线。</summary>
    public ICommand FlattenToEndsCommand { get; }

    /// <summary>曲线编辑的撤销括号：与墨色、摆位那几处同一套（Prepare 录快照、Done 刷新并置脏）。</summary>
    private void CurveEdit(Action<TemplateElement> work)
    {
        Prepare(nameof(IsCurve));
        work(_element);
        Done();
    }

    /// <summary>常用墨色（印刷口径的整数百分数）。唛头常用的就这几支，一键选比手打数字快。</summary>
    public static IReadOnlyList<InkPreset> InkPresets { get; } = new[]
    {
        new InkPreset("黑", LabelColor.FromCmyk(0, 0, 0, 100)),
        new InkPreset("红", LabelColor.FromCmyk(0, 100, 100, 0)),
        new InkPreset("蓝", LabelColor.FromCmyk(100, 100, 0, 0)),
        new InkPreset("绿", LabelColor.FromCmyk(100, 0, 100, 0)),
        new InkPreset("橙", LabelColor.FromCmyk(0, 40, 100, 0)),
    };

    public ICommand ApplyInkPresetCommand { get; }

    private void SetCmyk(int c, int m, int y, int k) => InkColor = LabelColor.FromCmyk(c, m, y, k);

    private void SetRgb(int r, int g, int b) => InkColor = LabelColor.FromSrgb(r, g, b);

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

    /// <summary>绕元素中心旋转角度（度，顺时针）。第 43 棒：变换叠在 TextFit 决定之后，五出口共用。</summary>
    public double RotationDeg
    {
        get => _element.RotationDeg;
        set
        {
            if (Near(value, _element.RotationDeg)) return;
            Prepare(nameof(RotationDeg));
            _element.RotationDeg = value;
            Done();
        }
    }

    /// <summary>字面横向放大倍率（1=原样，把字身抻宽/压扁）。仅文本用。</summary>
    public double TextScaleX
    {
        get => _element.TextScaleX;
        set
        {
            if (Near(value, _element.TextScaleX)) return;
            Prepare(nameof(TextScaleX));
            _element.TextScaleX = Math.Max(0.05, value);
            Done();
        }
    }

    /// <summary>字面纵向放大倍率（1=原样）。仅文本用。</summary>
    public double TextScaleY
    {
        get => _element.TextScaleY;
        set
        {
            if (Near(value, _element.TextScaleY)) return;
            Prepare(nameof(TextScaleY));
            _element.TextScaleY = Math.Max(0.05, value);
            Done();
        }
    }

    /// <summary>当前元素能否旋转（条码不许转，线段由两端点决定）。面板按它显隐旋转框。</summary>
    public bool CanRotate => _element.Kind is ElementKind.Text or ElementKind.Rect or ElementKind.Image or ElementKind.Vector;

    /// <summary>当前元素能否用"字面拉伸"（只文本有；图片的拉伸就是它的宽高，不设倍率字段）。</summary>
    public bool CanStretchText => _element.Kind == ElementKind.Text;

    /// <summary>字体行只对带文字的元素有意义（文本 + 条码的可读数字）。</summary>
    public bool CanSetFont => _element.Kind is ElementKind.Text or ElementKind.Barcode;

    /// <summary>
    /// 「墨色」这一格对哪些元素有效（第 47 棒）：只有我们<em>自己落墨</em>的那几类——文本、线条、矩形框、条码。
    /// <para>图片与矢量底图自带颜色（一张 Logo、一份 CDR 底稿），给它们一支笔色只会让人以为能改，
    /// 而改了什么都没发生，那种格子宁可不给。</para>
    /// </summary>
    public bool CanTintInk => _element.Kind is ElementKind.Text or ElementKind.Line or ElementKind.Rect or ElementKind.Barcode;

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
        SyncPickerFromColour();   // 撤销之后按这支墨真正的颜色重摆，不能留在刚才手拖的位置
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(X), nameof(Y), nameof(Width), nameof(Height), nameof(X2), nameof(Y2), nameof(Text), nameof(ImagePath),
            nameof(FontFamily), nameof(FontSizePt), nameof(Bold), nameof(ThicknessMm), nameof(MaxLines),
            nameof(ShrinkToFit), nameof(Visible), nameof(Align), nameof(WrapWidthMm),
            nameof(RotationDeg), nameof(TextScaleX), nameof(TextScaleY), nameof(CanRotate), nameof(CanStretchText), nameof(CanSetFont), nameof(CanTintInk),
            // 第 47 棒：墨色那一整组。撤销之后面板上还得刷回真值，漏一个就是一格数字在骗人。
            nameof(InkColor), nameof(InkSwatch), nameof(InkSummary), nameof(InkUsesCmyk), nameof(InkUsesRgb),
            nameof(InkC), nameof(InkM), nameof(InkY), nameof(InkK),
            nameof(InkR), nameof(InkG), nameof(InkB), nameof(InkHex),
            nameof(IsCurve), nameof(NodeCount),
        })
        {
            Raise(name);
        }
        RaisePicker();        // 调色盘那一组跟着一起刷：漏了就是数字变了、圈还停在老地方
    }
}

/// <summary>
/// 面板上的一枚常用墨（第 47 棒）：名字 + 那支色。色块画笔与预览/导出读的是<em>同一个</em>
/// <see cref="RenderRules.InkOf"/>，不另开一份颜色定义。
/// </summary>
public sealed record InkPreset(string Name, LabelColor Color)
{
    public Brush Swatch => RenderRules.InkOf(Color);
}
