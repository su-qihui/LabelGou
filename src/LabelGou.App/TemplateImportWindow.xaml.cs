using System.Windows;
using LabelGou.App.ViewModels;
using LabelGou.Core.Templates;

namespace LabelGou.App;

/// <summary>
/// 「从底稿导入」结果窗口（M5）：把 <see cref="TemplateImportViewModel"/> 摊开给人核对，点头后才落库。
/// <para>
/// View 依旧只做三件事：挂 DataContext、把 VM 的"存好了/关窗"翻译成 <see cref="DialogResult"/>、
/// 以及暴露控件给单测。判定、勾选、落库全在 VM 与 Core，不在这儿。
/// 不弹 MessageBox——校验结果直接写在窗口下方（§七-13）。
/// </para>
/// </summary>
public sealed partial class TemplateImportWindow : Window
{
    private readonly TemplateImportViewModel _viewModel;

    public TemplateImportWindow(TemplateImportViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.Saved += template => Dispatcher.Invoke(() =>
        {
            ResultTemplate = template;
            DialogResult = true;
            Close();
        });
        _viewModel.CloseRequested += () => Dispatcher.Invoke(Close);
    }

    /// <summary>导入成功后的模板（取消时为 null）。</summary>
    public LabelTemplate? ResultTemplate { get; private set; }

    /// <summary>文字候选表（单测拿它验证真加载了 XAML、绑定通了，不靠手点）。</summary>
    public System.Windows.Controls.DataGrid CandidateGrid => RowsGrid;
}
