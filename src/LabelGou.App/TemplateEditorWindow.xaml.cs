using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Colors;
using LabelGou.Core.Templates;

namespace LabelGou.App;

/// <summary>
/// B 类模板编辑器窗口（非模态：可以开着它边改边看主窗口预览）。
/// <para>
/// View 只做三件事：把 <see cref="TemplateEditorViewModel"/> 挂到画布、
/// 按 VM 的要求弹窗与关闭、以及"有改动没保存"时拦一道。所有编辑逻辑都在 VM 与 Core。
/// </para>
/// </summary>
public sealed partial class TemplateEditorWindow : Window
{
    private readonly TemplateEditorViewModel _editor;

    /// <summary>
    /// 未保存时关窗口问一句的判定（可替换：单测换掉它就不弹窗，三条关闭路径各自被钉）。
    /// <para>第 57 棒按用户口径换成原生的三键提示：「<strong>是(Y)</strong> 存盘并关闭、
    /// <strong>否(N)</strong> 不存直接关闭（改动就丢了）、<strong>取消</strong> 留下继续编辑」。
    /// 前一版是"左『保存』右『否』"的两键框——那颗『否』其实只等于这里的『取消』，
    /// 想不存就走当时只能先点工具条的「放弃改动」，他要点三键那种熟悉的问法。</para>
    /// </summary>
    internal Func<SavePromptAnswer> AskSaveBeforeClose { get; set; }

    /// <summary>退出编辑器时那句问话的三个答案。</summary>
    internal enum SavePromptAnswer
    {
        /// <summary>是：存盘并关闭。</summary>
        Save,

        /// <summary>否：不存盘直接关闭（本次改动就此丢掉）。</summary>
        Discard,

        /// <summary>取消：不关，回去继续编辑。</summary>
        Cancel,
    }

    public TemplateEditorWindow(TemplateEditorViewModel editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        AskSaveBeforeClose = ShowSavePrompt;
        InitializeComponent();
        DataContext = _editor;

        _editor.Saved += template => Dispatcher.Invoke(() =>
        {
            ResultTemplate = template;
            AllowCloseWithoutPrompt = true;   // 存好了，关窗口不用再问
            Saved?.Invoke(template);
        });
        _editor.SavedAsCopy += template => Dispatcher.Invoke(() =>
        {
            // 另存只告诉主窗口「库里多了一份」，不动 AllowCloseWithoutPrompt：当前这份还没存
            ResultTemplate = template;
            SavedAsCopy?.Invoke(template);
        });
        _editor.ErrorRaised += message => Dispatcher.Invoke(() =>
            MessageBox.Show(this, message, "模板编辑", MessageBoxButton.OK, MessageBoxImage.Warning));
        _editor.CloseRequested += () => Dispatcher.Invoke(Close);
    }

    /// <summary>当前这份保存成功（主窗口据此刷新模板列表并选中）。</summary>
    public event Action<LabelTemplate>? Saved;

    /// <summary>另存出一份新副本（主窗口刷新列表并选中它，但编辑器仍开着改原模板）。</summary>
    public event Action<LabelTemplate>? SavedAsCopy;

    /// <summary>关闭时若还有未保存改动，问一句——打印店常用的动作就是"改完直接关窗口"。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_editor.IsDirty || AllowCloseWithoutPrompt) return;
        switch (AskSaveBeforeClose())
        {
            case SavePromptAnswer.Cancel:
                e.Cancel = true;                       // 取消＝留下继续编辑
                return;

            case SavePromptAnswer.Discard:
                return;                                // 否＝不存就走：本次改动就此丢掉（磁盘上还是上次存的那份）
        }
        // 是＝存盘并关闭：先取消本次关闭，事件返回后再执行保存——VM 的 Save() 成功路径自己会再 Close 一次
        // （AllowCloseWithoutPrompt 放行）。在 OnClosing 里直接跑保存命令 = 关闭中重入 Close，WPF 当场抛。
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(() => _editor.SaveCommand.Execute(null)));
    }

    /// <summary>
    /// 那句三键问话（第 57 棒，用户原话照抄）：「要在退出之前存储对 LabelGou 文档“文件名”的保存更改吗？」。
    /// <para>用原生 MessageBox 而不是自己画：他要的就是 是(Y)／否(N)／取消 这三颗键与它们的排位、快捷键，
    /// 原生框给的顺序和助记符正是那一份，自己画反而每次都要重新对一遍。</para>
    /// </summary>
    private SavePromptAnswer ShowSavePrompt() => MessageBox.Show(this,
            $"要在退出之前存储对 LabelGou 文档“{_editor.DocumentFileName}”的保存更改吗？",
            "LabelGou", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
    {
        MessageBoxResult.Yes => SavePromptAnswer.Save,
        MessageBoxResult.No => SavePromptAnswer.Discard,
        _ => SavePromptAnswer.Cancel,      // 取消键、Esc、连右上角的叉都算「不关」
    };

    // ---------- 图层行按住上下拖＝换位（第 55 棒） ----------

    private ElementRow? _layerDragRow;
    private Point _layerDragStart;
    private bool _layerDragCaptured;

    private void LayerList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        var row = RowUnder(list, e.GetPosition(list));
        if (row is null) return;
        _layerDragRow = row;
        _layerDragStart = e.GetPosition(list);
        _layerDragCaptured = false;
        // 这里不吃掉事件也不捕获鼠标：单击选行照旧走 ListBox 的路；越过拖动阈值才接管。
    }

    private void LayerList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_layerDragRow is null || e.LeftButton != MouseButtonState.Pressed || sender is not ListBox list) return;
        var p = e.GetPosition(list);
        if (Math.Abs(p.Y - _layerDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var target = list.Items.IndexOf(RowUnder(list, p));
        if (target < 0) return;
        if (!_layerDragCaptured)
        {
            _editor.CaptureLayerDrag();            // 一次拖动只录一步撤销：越过几行都退得回拖之前（撤销按手势数）
            _layerDragCaptured = true;
        }
        if (_editor.MoveLayerTo(_layerDragRow.Element, target))
        {
            list.CaptureMouse();                   // 越过行之后指针可能划过别的行甚至划出列表：抓稳，松手才交还
            e.Handled = true;
        }
    }

    private void LayerList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_layerDragCaptured && sender is ListBox captured) captured.ReleaseMouseCapture();
        _layerDragRow = null;
        _layerDragCaptured = false;
    }

    /// <summary>指针下那一行的数据行（沿可视树上溯到 ListBoxItem；没指在行上返回 null）。</summary>
    private static ElementRow? RowUnder(ListBox list, Point p)
    {
        var node = list.InputHitTest(p) as DependencyObject;
        while (node is not null and not ListBoxItem) node = VisualTreeHelper.GetParent(node);
        return (node as ListBoxItem)?.DataContext as ElementRow;
    }

    /// <summary>保存成功时由 VM 触发 Close，此时不该再问一遍。</summary>
    public bool AllowCloseWithoutPrompt { get; set; }

    /// <summary>编辑结果（主窗口据此刷新模板列表）。</summary>
    public LabelTemplate? ResultTemplate { get; internal set; }

    /// <summary>画布控件（单测拿它验证布局与渲染，不靠手点）。</summary>
    public TemplateEditorControl EditorCanvas => Canvas;

    // ---------- 墨色调色盘（第 47 棒补刀）----------
    // View 只把像素换算成 0~1 的比例，颜色怎么落、面板跳不跳档全在 EditableElement 里。

    private void InkSvArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        el.CaptureMouse();
        PushSv(el, e.GetPosition(el));
        e.Handled = true;
    }

    private void InkSvArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { IsMouseCaptured: true } el) return;
        PushSv(el, e.GetPosition(el));
    }

    private void InkHueArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        el.CaptureMouse();
        PushHue(el, e.GetPosition(el));
        e.Handled = true;
    }

    private void InkHueArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { IsMouseCaptured: true } el) return;
        PushHue(el, e.GetPosition(el));
    }

    private void InkPickArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } el) el.ReleaseMouseCapture();
    }

    private void PushSv(FrameworkElement el, Point p)
    {
        var row = _editor.Editing;
        if (row is null) return;
        row.PickerSaturation = Clamp01(p.X / el.ActualWidth);
        row.PickerValue = 1 - Clamp01(p.Y / el.ActualHeight);
    }

    private void PushHue(FrameworkElement el, Point p)
    {
        var row = _editor.Editing;
        if (row is null) return;
        row.PickerHueDeg = HsvMath.HueMax * Clamp01(p.Y / el.ActualHeight);
    }

    /// <summary>除以的可能是刚加载完还没量尺寸的 0，NaN 一律当 0 收（不然调色盘会写进一个 NaN 颜色）。</summary>
    private static double Clamp01(double v) => double.IsNaN(v) || v < 0 ? 0 : v > 1 ? 1 : v;
}
