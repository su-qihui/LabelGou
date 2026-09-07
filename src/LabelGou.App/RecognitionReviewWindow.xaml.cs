using System.Windows;
using System.Windows.Controls;
using LabelGou.App.ViewModels;
using LabelGou.Core.Marks;

namespace LabelGou.App;

/// <summary>
/// 识别结果核对窗口（M6）：把 <see cref="RecognitionReviewViewModel"/> 摊开给人逐条确认。
/// <para>View 只做三件事：挂 DataContext、把 VM 的"关窗"翻译成窗口动作、把控件暴露给单测。
/// 判定、置信、能否导入这些规矩全在 Core 与 VM 里。</para>
/// <para><strong>"提交"这个动作是「导入这一份」按钮，不是关窗</strong>：
/// 用户点过导入的记录就已经算数了，之后哪怕用 ESC 或右上角 X 关掉窗口也不会被撤销——
/// 所以主窗口无条件读 <see cref="ImportedRecords"/>，不看 <c>DialogResult</c>。</para>
/// </summary>
public sealed partial class RecognitionReviewWindow : Window
{
    private readonly RecognitionReviewViewModel _viewModel;

    public RecognitionReviewWindow(RecognitionReviewViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.CloseRequested += () => Dispatcher.Invoke(Close);
    }

    /// <summary>用户在窗口里逐份确认并导入的记录（可能为空）。</summary>
    public IReadOnlyList<MarkRecord> ImportedRecords => _viewModel.ResultRecords;

    public bool AnyImported => _viewModel.AnyImported;

    /// <summary>没核完就关窗的文档数说明，主窗口拿它决定要不要提醒一句。</summary>
    public string LeftoverSummary => _viewModel.LeftoverSummary();

    // ---------- 单测钩子：验证 XAML 真加载了、绑定真通了，不靠手点 ----------

    public ListBox BatchListBox => BatchList;

    public DataGrid FieldDataGrid => FieldGrid;

    public ListBox EvidenceListBox => EvidenceList;
}
