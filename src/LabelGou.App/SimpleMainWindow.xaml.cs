using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;

namespace LabelGou.App;

/// <summary>
/// 简洁版壳窗（第 91 棒 · 阶段一第一刀）：预览当主角的工作台 + 表格抽屉 + 指令岛 + 出纸全屏确认。
/// <para><strong>它和主窗共享同一个 <see cref="MainViewModel"/> 实例</strong>（构造时递进来），
/// 所以两代界面之间没有"同步"这回事——只有一份状态。开关动作由主窗的
/// 「视图 → 简洁版工作台」发起，回专业版就是关掉本窗（面板搬回去、主窗现形）。</para>
/// <para>本窗不许长出第二条业务路：打印/导出/翻页/缩放全是 VM 现成命令；
/// 抽屉里那张表与 ① 步共用 <see cref="PreviewGridColumns"/>（列名带斜杠也不许整列变空，§五-177）。</para>
/// </summary>
public partial class SimpleMainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _drawerOpen;

    /// <summary>指令岛的宿主：主窗把 AI 面板本体搬进这里（<see cref="SimpleShellFlow.Park"/>）。</summary>
    public ContentControl IslandHost => Island;

    /// <summary>抽屉当前开没开（判据与主窗都读这一处，不另存第二份）。</summary>
    public bool DrawerOpen => _drawerOpen;

    public SimpleMainWindow(MainViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        InitializeComponent();
        Title = AppInfo.WindowTitle + " · 简洁版";
        DataContext = _vm;
        // 整版控件靠回调取标签版面（Func 没法在 XAML 里绑）——与主窗同一句接线，不开第二套取数
        ShellSheetView.LayoutProvider = index => _vm.Sheet.LayoutFor(index);
        TableToggleBtn.Click += (_, _) => SetDrawer(SimpleShellFlow.ToggleTableDrawer(_drawerOpen));
        TableCloseBtn.Click += (_, _) => SetDrawer(false);
        PrintAskBtn.Click += (_, _) => PrintOverlay.Visibility = Visibility.Visible;
        PrintBackBtn.Click += (_, _) => PrintOverlay.Visibility = Visibility.Collapsed;
        BackToProBtn.Click += (_, _) => Close();
        _vm.PropertyChanged += OnVmPropertyChanged;
        RefreshFlow();
        SetDrawer(false, animate: false);
        AppLog.Info("简洁版壳窗已构造（与专业版共享同一份 MainViewModel）");
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StepIndex)) RefreshFlow();
    }

    /// <summary>
    /// 按 <see cref="SimpleShellFlow"/> 的判据刷活件流四节点：过了的绿、正在的蓝且加粗、没轮到的灰。
    /// <para>档位算式不在这里——窗口只负责把算出来的档画上去，判据归静态方法（测试读得到）。</para>
    /// </summary>
    private void RefreshFlow()
    {
        var brand = (Brush)Resources["BrandBrush"];
        var ok = (Brush)Resources["OkBrush"];
        var line = (Brush)Resources["LineBrush"];
        var ink = (Brush)Resources["InkBrush"];
        var sub = (Brush)Resources["SubInkBrush"];
        for (var node = 0; node < SimpleShellFlow.NodeCount; node++)
        {
            var dot = (System.Windows.Shapes.Ellipse)FindName($"FlowDot{node}")!;
            var label = (TextBlock)FindName($"FlowLabel{node}")!;
            switch (SimpleShellFlow.StateOf(_vm.StepIndex, node))
            {
                case SimpleShellFlow.NodeState.Done:
                    dot.Fill = ok; label.Foreground = ok; label.FontWeight = FontWeights.Normal;
                    break;
                case SimpleShellFlow.NodeState.Now:
                    dot.Fill = brand; label.Foreground = brand; label.FontWeight = FontWeights.Bold;
                    break;
                default:
                    dot.Fill = line; label.Foreground = sub; label.FontWeight = FontWeights.Normal;
                    break;
            }
        }
    }

    /// <summary>开/关表格抽屉：滑入 180ms（概念稿那条"上浮 200ms"同族，克制不炫）。关到位才藏，免得半路 Visibility 掐断动画。</summary>
    private void SetDrawer(bool open, bool animate = true)
    {
        _drawerOpen = open;
        var target = open ? 0d : -620d;
        if (!animate || !TableDrawer.IsLoaded)
        {
            DrawerShift.X = target;
            TableDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (open) TableDrawer.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => { if (!open) TableDrawer.Visibility = Visibility.Collapsed; };
        DrawerShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>① 步那张表的列在这里也是自动生成的——换绑口径必须与主窗同一份（§五-177）。</summary>
    private void TableGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
        => PreviewGridColumns.AutoGenerating(sender, e);

    /// <summary>打印还在跑就想关壳窗 → 与主窗同一句问话、同一个出口（第 90 棒③，不开第二套文案）。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.Export.IsBusy)
        {
            var answer = MessageBox.Show(this,
                ExportViewModel.ComposeExitDuringJobText(_vm.Export.RunningJob),
                "LabelGou 正在输出", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
            _vm.Export.Cancel();
            AppLog.Info("用户在输出任务进行中确认关闭简洁版壳窗：已请求停止当前任务");
        }
        base.OnClosing(e);
    }
}
