using System.Windows;
using System.Windows.Input;
using LabelGou.App.ViewModels;

namespace LabelGou.App;

/// <summary>主窗口：左侧五步流程（导入→映射→模板→拼版编号→核对），右侧按毫米真实尺寸预览单标签与整版。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.ErrorRaised += OnErrorRaised;

        // 整版控件靠回调取标签版面（Func 没法在 XAML 里绑），按页临时算，不预先展开几百页
        SheetView.LayoutProvider = index => _viewModel.Sheet.LayoutFor(index);

        Loaded += (_, _) =>
        {
            FitNow();
            SheetFitNow();
            Services.AppLog.Info($"主窗口已显示：{_viewModel.CurrentRecords.Count} 张标签 / {_viewModel.RawRecords.Count} 条记录，整版方案={(_viewModel.Sheet.Plan is null ? "无" : _viewModel.Sheet.Plan.Describe())}");
        };
        Services.AppLog.Info("主窗口初始化完成");
    }

    private void OnErrorRaised(string message)
        => MessageBox.Show(this, message, "LabelGou", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object sender, RoutedEventArgs e)
        => MessageBox.Show(this, AboutText, "关于 LabelGou", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.AppLog.DirectoryPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Services.AppLog.DirectoryPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Services.AppLog.Error("打开日志目录失败", ex);
            MessageBox.Show(this, "打不开日志目录：" + ex.Message, "LabelGou",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => FitNow();

    private void OnSheetFitClick(object sender, RoutedEventArgs e) => SheetFitNow();

    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AutoFitBox.IsChecked == true) FitNow();
    }

    private void OnSheetHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SheetAutoFitBox.IsChecked == true) SheetFitNow();
    }

    private void FitNow() => _viewModel.FitTo(PreviewHost.ActualWidth, PreviewHost.ActualHeight);

    private void SheetFitNow() => _viewModel.FitSheetTo(SheetHost.ActualWidth, SheetHost.ActualHeight);

    private const string AboutText =
        "LabelGou · 唛头标签助手 v0.2.0（M2）\n\n" +
        "面向打印店 / 印刷厂的唛头标签自动化工具。\n" +
        "当前进度 M2：Excel/CSV 导入 → 字段映射 → 套模板 → 件号规则编号 → 整版拼版 → 单标签/整版预览。\n\n" +
        "后续里程碑：M3 直连打印/PDF/整版图片、M4 拖拽自定义模板、\n" +
        "M5 CorelDRAW 衔接、M6 AI Agent 文档识别、M7 AI 智能排版、M8 打磨发布。\n\n" +
        "开发计划与进度详见 labelgou-word 目录下的文档。授权：MIT。";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (_viewModel.OpenFileCommand.CanExecute(null)) _viewModel.OpenFileCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            _viewModel.PrevRecordCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            _viewModel.NextRecordCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.G && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _viewModel.ShowGuides = !_viewModel.ShowGuides;
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }
}
