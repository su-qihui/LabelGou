using System.Windows;
using System.Windows.Input;
using LabelGou.App.ViewModels;

namespace LabelGou.App;

/// <summary>主窗口：左侧四步流程，右侧按毫米真实尺寸预览。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.ErrorRaised += OnErrorRaised;
        Loaded += (_, _) =>
        {
            FitNow();
            Services.AppLog.Info($"主窗口已显示：{_viewModel.RecordTotal} 条记录可用，当前布局={(_viewModel.CurrentLayout is null ? "无" : "有")}");
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

    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AutoFitBox.IsChecked == true) FitNow();
    }

    private void FitNow() => _viewModel.FitTo(PreviewHost.ActualWidth, PreviewHost.ActualHeight);

    private const string AboutText =
        "LabelGou · 唛头标签助手 v0.1.0（M1）\n\n" +
        "面向打印店 / 印刷厂的唛头标签自动化工具。\n" +
        "当前进度 M1：Excel/CSV 导入 → 字段映射 → 套唛头模板 → 单标签所见即所得预览。\n\n" +
        "后续里程碑：M2 整版拼版与自动编号、M3 直连打印/PDF/整版图片、M4 拖拽自定义模板、\n" +
        "M5 CorelDRAW 衔接、M6 AI Agent 文档识别、M7 AI 智能排版、M8 打磨发布。\n\n" +
        "开发计划与进度详见 labelgou-word 目录下的两份文档。授权：MIT。";

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
