using System.ComponentModel;
using System.Windows;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
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

    public TemplateEditorWindow(TemplateEditorViewModel editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        InitializeComponent();
        DataContext = _editor;

        _editor.Saved += template => Dispatcher.Invoke(() =>
        {
            ResultTemplate = template;
            AllowCloseWithoutPrompt = true;   // 存好了，关窗口不用再问
            Saved?.Invoke(template);
        });
        _editor.ErrorRaised += message => Dispatcher.Invoke(() =>
            MessageBox.Show(this, message, "模板编辑", MessageBoxButton.OK, MessageBoxImage.Warning));
        _editor.CloseRequested += () => Dispatcher.Invoke(Close);
    }

    /// <summary>保存成功（主窗口据此刷新模板列表并选中）。</summary>
    public event Action<LabelTemplate>? Saved;

    /// <summary>关闭时若还有未保存改动，问一句——打印店常用的动作就是"改完直接关窗口"。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_editor.IsDirty || AllowCloseWithoutPrompt) return;
        var answer = MessageBox.Show(this,
            "这个模板还有没保存的改动，确定直接关闭吗？\n\n选「否」回去点「保存」。",
            "模板未保存", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) e.Cancel = true;
    }

    /// <summary>保存成功时由 VM 触发 Close，此时不该再问一遍。</summary>
    public bool AllowCloseWithoutPrompt { get; set; }

    /// <summary>编辑结果（主窗口据此刷新模板列表）。</summary>
    public LabelTemplate? ResultTemplate { get; internal set; }

    /// <summary>画布控件（单测拿它验证布局与渲染，不靠手点）。</summary>
    public TemplateEditorControl EditorCanvas => Canvas;
}
