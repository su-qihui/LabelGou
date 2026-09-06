using System.IO;
using System.Windows;
using System.Windows.Input;
using LabelGou.App.ViewModels;
using LabelGou.Core.Editing;
using LabelGou.Core.Templates;

namespace LabelGou.App;

/// <summary>主窗口：左侧六步流程（导入→映射→模板→拼版编号→核对→输出打印），右侧按毫米真实尺寸预览单标签与整版。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.ErrorRaised += OnErrorRaised;

        // M3：打印/导出前的复核闸门（§七-11）——问人的事留给窗口，VM 不直接弹框
        _viewModel.ConfirmGate = text => MessageBox.Show(this, text, "LabelGou 打印前复核",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        // 整版控件靠回调取标签版面（Func 没法在 XAML 里绑），按页临时算，不预先展开几百页
        SheetView.LayoutProvider = index => _viewModel.Sheet.LayoutFor(index);

        Loaded += (_, _) =>
        {
            FitNow();
            SheetFitNow();
            // 枚举打印机要问后台服务，放到首屏画完之后再说，不拖慢启动
            Dispatcher.BeginInvoke(new Action(_viewModel.Export.WarmUpPrinters),
                System.Windows.Threading.DispatcherPriority.Background);
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

    // ---------- M4：模板编辑器 ----------

    private TemplateEditorWindow? _editorWindow;

    /// <summary>
    /// 打开编辑器。同时只允许开一个：两个编辑器改同一份模板库只会互相覆盖，
    /// 对打印店操作员来说“另一个窗口还开着”这种坑必须用程序拦住而不是靠记性。
    /// </summary>
    private void OpenTemplateEditor(LabelTemplate working, bool asBuiltInCopy, string? savedFileName = null)
    {
        if (_editorWindow is not null)
        {
            _editorWindow.Activate();
            return;
        }

        var vm = new TemplateEditorViewModel(working, _viewModel.Templates, savedFileName)
        {
            IsBuiltInSource = asBuiltInCopy,
        };
        var window = new TemplateEditorWindow(vm) { Owner = this };
        window.Saved += saved => _viewModel.ReloadTemplates(saved.Id);
        window.Closed += (_, _) => _editorWindow = null;
        _editorWindow = window;
        window.Show();
        Services.AppLog.Info($"打开模板编辑器：{working.Name}（{working.Elements.Count} 个元素，{(asBuiltInCopy ? "内置副本" : "用户模板")}）");
    }

    private void OnNewTemplateClick(object sender, RoutedEventArgs e)
        => OpenTemplateEditor(TemplateFactory.Blank("我的唛头模板"), asBuiltInCopy: false);

    private void OnEditTemplateClick(object sender, RoutedEventArgs e)
    {
        var option = _viewModel.SelectedTemplate;
        if (option is null)
        {
            MessageBox.Show(this, "还没有选中模板，先在③ 里选一个。", "模板编辑",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var template = option.Template;
        if (template.BuiltIn)
        {
            // 内置模板只读：自动先存一份副本再编，不要求用户理解“另存为”
            var copy = TemplateFactory.CopyOf(template, template.Name + "（自定义）");
            OpenTemplateEditor(copy, asBuiltInCopy: true);
            return;
        }

        var stale = _viewModel.Templates.FindFileFor(template.Id);
        OpenTemplateEditor(template.CloneTemplate(), asBuiltInCopy: false, savedFileName: stale is null ? null : Path.GetFileName(stale));
    }

    private void OnDuplicateTemplateClick(object sender, RoutedEventArgs e)
    {
        var template = _viewModel.SelectedTemplate?.Template;
        if (template is null) return;
        OpenTemplateEditor(TemplateFactory.CopyOf(template, template.Name + " 副本"), asBuiltInCopy: false);
    }

    private void OnImportTemplateClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择模板 JSON 文件",
            Filter = "LabelGou 模板|*.json|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        var (template, issues) = _viewModel.Templates.ReadFile(dialog.FileName);
        if (template is null)
        {
            MessageBox.Show(this,
                "这个文件不能当模板用：\n" + string.Join("\n", issues.Select(i => i.Message)),
                "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 同 id 已存在（比如把备份拽回本机）→ 换新 id，不覆盖本机同名模板
        if (_viewModel.Templates.GetById(template.Id) is not null)
            template.Id = "user." + Guid.NewGuid().ToString("N")[..8];
        template.BuiltIn = false;

        var (saved, fileName, saveIssues) = _viewModel.Templates.Save(template);
        if (!saved)
        {
            MessageBox.Show(this,
                "存进模板库失败：\n" + string.Join("\n", saveIssues.Select(i => i.Message)),
                "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _viewModel.ReloadTemplates(template.Id);
        Services.AppLog.Info($"导入模板成功：{template.Name} → {fileName}");
        MessageBox.Show(this, $"已导入模板「{template.Name}」并选中。", "导入完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportTemplateClick(object sender, RoutedEventArgs e)
    {
        var template = _viewModel.SelectedTemplate?.Template;
        if (template is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出模板为 JSON 文件（可拷到另一台机器导入）",
            Filter = "LabelGou 模板|*.json",
            FileName = $"{template.Name}.labelgou.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _viewModel.Templates.ExportFile(template, dialog.FileName);
            Services.AppLog.Info($"模板已导出：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            Services.AppLog.Error("模板导出失败", ex);
            MessageBox.Show(this, "导出的时候没写成文件：" + ex.Message, "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteTemplateClick(object sender, RoutedEventArgs e)
    {
        var option = _viewModel.SelectedTemplate;
        if (option is null) return;
        if (option.Template.BuiltIn)
        {
            MessageBox.Show(this, "内置模板删不了（也不占磁盘），只删自己存的模板。", "删除模板",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"确定删除用户模板「{option.Name}」吗？\n删了就找不回来了（除非另有导出的 JSON）。",
            "删除模板", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        if (!_viewModel.Templates.Delete(option.Id))
        {
            MessageBox.Show(this, "没在模板目录里找到这份模板，可能已经被删过了。", "删除模板",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _viewModel.ReloadTemplates();
        Services.AppLog.Info($"删除用户模板：{option.Name}");
    }

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
        "LabelGou ·唛头标签助手 v0.4.0（M4）\n\n" +
        "面向打印店 / 印刷厂的唛头标签自动化工具。\n" +
        "当前进度 M4：Excel/CSV 导入 → 字段映射 → 套模板（内置或自己拖的）→ 件号规则编号 → 整版拼版 → 预览 → 直连打印 / PDF / PNG / TIFF。\n\n" +
        "后续里程碑：M5 CorelDRAW 衔接、\n" +
        "M6 AI Agent 文档识别、M7 AI 智能排版、M8 打磨发布。\n\n" +
        "开发计划与进度详见 labelgou-word 目录下的文档。授权：MIT。";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (_viewModel.OpenFileCommand.CanExecute(null)) _viewModel.OpenFileCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (_viewModel.Export.PrintCommand.CanExecute(null)) _viewModel.Export.PrintCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.T && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnEditTemplateClick(this, new RoutedEventArgs());
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
