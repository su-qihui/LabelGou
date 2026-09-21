using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;

namespace LabelGou.App;

/// <summary>
/// 简洁版壳窗（第 91 棒开这一刀，第 94 棒改成三栏）：<strong>左=这张表 / 中=预览（主导）/ 右=AI 指令岛</strong>，
/// 外加出纸全屏确认。左右两根栏随时开关、能拖宽，收起来中间自己补位（用户 2026-09-21 指着 Qoder 界面定的口径）。
/// <para><strong>它和主窗共享同一个 <see cref="MainViewModel"/> 实例</strong>（构造时递进来），
/// 所以两代界面之间没有"同步"这回事——只有一份状态。开关动作由主窗的「视图 → 简洁版工作台」发起；
/// 点「回专业版」才是关掉本窗把主窗掀回来，直接关窗 = 整个软件退出（<see cref="SwitchingToPro"/> 分这两种）。</para>
/// <para>本窗不许长出第二条业务路：打印/导出/翻页/缩放全是 VM 现成命令；
/// 左栏那张表与 ① 步共用 <see cref="PreviewGridColumns"/>（列名带斜杠也不许整列变空，§五-177）。</para>
/// </summary>
public partial class SimpleMainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _leftOpen;
    private bool _rightOpen = true;
    private double _leftWidth = SimpleShellFlow.LeftPaneDefaultWidth;
    private double _rightWidth = SimpleShellFlow.RightPaneDefaultWidth;
    // 按下那一刻的起点（当时的宽 + 指针 X）——拖拽全程照它算**总位移**，不吃 DragDelta 的累计量（第 93 棒那条教训）
    private (double W, double X) _leftGrab;
    private (double W, double X) _rightGrab;
    // 中间那张纸自动显示全（用户 2026-09-21 ④）。默认开着；点 −/＋ 就交回他手上，点「适应」再要回来。
    private bool _autoFit = true;

    /// <summary>指令岛的宿主：主窗把 AI 面板本体搬进这里（<see cref="SimpleShellFlow.Park"/>）。</summary>
    public ContentControl IslandHost => Island;

    /// <summary>左栏（这张表）开没开。判据与测试都读这一处，不另存第二份。</summary>
    public bool LeftPaneOpen => _leftOpen;

    /// <summary>右栏（AI）开没开。</summary>
    public bool RightPaneOpen => _rightOpen;

    /// <summary>中间预览是不是"自动显示全"（第 95 棒④）。手动 −/＋ 会把它关掉。</summary>
    public bool AutoFitPreview => _autoFit;

    /// <summary>这扇窗是被「回专业版」关掉的，还是用户直接关窗：前者要把主窗掀回来，后者整个软件退出（用户 2026-09-21：关两次不算关完）。</summary>
    public bool SwitchingToPro { get; private set; }

    public SimpleMainWindow(MainViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        InitializeComponent();
        // 简洁版给短标题：主窗那句（含五步向导提示）不该原样搬来——壳窗没有五步摊开的样子
        Title = $"LabelGou 简洁版 · v{AppInfo.Version}";
        DataContext = _vm;
        // 两根栏上次多宽、开没开（0 / null = 没记过 = 默认档：左关右开，中间才留得住看纸的地方）
        var (leftW, rightW, leftOpen, rightOpen) = _vm.LoadShellPanes();
        _leftWidth = SimpleShellFlow.ClampLeftPaneWidth(leftW > 0 ? leftW : SimpleShellFlow.LeftPaneDefaultWidth);
        _rightWidth = SimpleShellFlow.ClampRightPaneWidth(rightW > 0 ? rightW : SimpleShellFlow.RightPaneDefaultWidth);
        _leftOpen = leftOpen ?? false;
        _rightOpen = rightOpen ?? true;
        ApplyPaneLayout();
        // 整版控件靠回调取标签版面（Func 没法在 XAML 里绑）——与主窗同一句接线，不开第二套取数
        ShellSheetView.LayoutProvider = index => _vm.Sheet.LayoutFor(index);
        TableToggleBtn.Click += (_, _) => SetLeftPane(SimpleShellFlow.TogglePane(_leftOpen));
        TableCloseBtn.Click += (_, _) => SetLeftPane(false);
        AiToggleBtn.Click += (_, _) => SetRightPane(SimpleShellFlow.TogglePane(_rightOpen));
        AiCloseBtn.Click += (_, _) => SetRightPane(false);
        LeftSplitThumb.DragStarted += (_, _) => _leftGrab = (LeftPane.Width, Mouse.GetPosition(Stage).X);
        LeftSplitThumb.DragDelta += (_, _) =>
        {
            _leftWidth = SimpleShellFlow.ClampLeftPaneWidth(_leftGrab.W + (Mouse.GetPosition(Stage).X - _leftGrab.X));
            ApplyPaneLayout();
        };
        RightSplitThumb.DragStarted += (_, _) => _rightGrab = (RightPane.Width, Mouse.GetPosition(Stage).X);
        RightSplitThumb.DragDelta += (_, _) =>
        {
            // 右栏钉在右边：往右拖是**变窄**，所以位移取反
            _rightWidth = SimpleShellFlow.ClampRightPaneWidth(_rightGrab.W - (Mouse.GetPosition(Stage).X - _rightGrab.X));
            ApplyPaneLayout();
        };
        PrintAskBtn.Click += (_, _) => PrintOverlay.Visibility = Visibility.Visible;
        PrintBackBtn.Click += (_, _) => PrintOverlay.Visibility = Visibility.Collapsed;
        BackToProBtn.Click += (_, _) =>
        {
            SwitchingToPro = true;
            Close();
        };
        _vm.PropertyChanged += OnVmPropertyChanged;
        Loaded += (_, _) =>
        {
            // 直达简洁版的用户第一次开「打印整版」，打印机下拉不能是空的——主窗那句预热在这条路上不会跑，这里补一次
            Dispatcher.BeginInvoke(new Action(_vm.Export.WarmUpPrinters), System.Windows.Threading.DispatcherPriority.Background);
        };
        Closing += (_, _) => _vm.SaveShellPanes(_leftWidth, _rightWidth, _leftOpen, _rightOpen);
        RefreshFlow();
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

    /// <summary>左栏开关：收起时占 0（连竖柄一起藏），中间那格自己补位——用户 2026-09-21 要的就是
    /// 「左右可以随时关闭或开启，中间为主导界面」。</summary>
    private void SetLeftPane(bool open)
    {
        _leftOpen = open;
        ApplyPaneLayout(animate: true);
    }

    private void SetRightPane(bool open)
    {
        _rightOpen = open;
        ApplyPaneLayout(animate: true);
    }

    /// <summary>把两根栏的开关与宽度落到版面上。占多宽这件事归 <see cref="SimpleShellFlow.PaneRevealWidth"/>（判据读得到），这里只画。</summary>
    private void ApplyPaneLayout(bool animate = false)
    {
        LeftPane.Width = _leftWidth;
        RightPane.Width = _rightWidth;
        RevealTo(LeftPaneReveal, SimpleShellFlow.PaneRevealWidth(_leftWidth, _leftOpen), _leftOpen, animate);
        RevealTo(RightPaneReveal, SimpleShellFlow.PaneRevealWidth(_rightWidth, _rightOpen), _rightOpen, animate);
        // 竖柄跟着栏走：栏收起就没有"这一格的边"可拖了（想再开走顶栏那颗钮）
        LeftSplitThumb.Visibility = _leftOpen ? Visibility.Visible : Visibility.Collapsed;
        RightSplitThumb.Visibility = _rightOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>弹入/弹出一帧一帧走：过渡打在 Reveal 那层（两侧列宽是 Auto，跟着它走），
    /// 栏本体钉在目标宽上被裁边——所以内容不重排，只是从屏幕外一点点推进来（用户 2026-09-21 ①：
    /// 「应该是从屏幕外弹进画面的感觉」）。收起时等动画走完再藏，免得半路 Visibility 把它掐断。</summary>
    /// <summary>弹入/弹出一帧一帧走：过渡打在 Reveal 这层（两侧列宽是 Auto，跟着它走），
    /// 栏本体钉在目标宽上贴边对齐——内容不重排，是一点点从屏幕边缘外被露出来（用户 2026-09-21 ①：
    /// 「应该是从屏幕外弹进画面的感觉」）。裁边只在动画这几帧开：停住时关掉，白卡那圈投影才是完整的。</summary>
    private static void RevealTo(Border reveal, double target, bool open, bool animate)
    {
        if (!animate || !reveal.IsLoaded || Math.Abs(reveal.Width - target) < 0.5)
        {
            reveal.Width = target;
            reveal.ClipToBounds = false;      // 停着的时候不裁：白卡那圈投影是完整的（用户 2026-09-21 圈出来"外框被切平"）
            reveal.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (open) reveal.Visibility = Visibility.Visible;
        reveal.ClipToBounds = true;           // 只在弹入/弹出这几帧里裁边——内容不重排，是一点点被露出来
        var anim = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            reveal.ClipToBounds = false;
            if (!open) reveal.Visibility = Visibility.Collapsed;
        };
        reveal.BeginAnimation(FrameworkElement.WidthProperty, anim);
        reveal.Width = target;      // 本地值也落到目标：动画收手后不会弹回旧数
    }

    /// <summary>预览那一格尺寸变了（开关栏、拖竖柄、改窗口）→ 报视口宽 + 走专业版同一条自适应
    /// （<see cref="MainViewModel.FitTo"/>，留 24 像素边）。壳窗压根没接这条线，纸才会被裁一半。</summary>
    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _vm.SetPreviewViewport(PreviewHost.ActualWidth);
        if (_autoFit) FitNow();
    }

    /// <summary>纸要"看得全也看得出台面"：`FitTo` 自己只留 24 像素，用户 2026-09-21 说再小一点点、
    /// 纸张边框要多看见一些——这里替壳窗多要一份边（专业版那条 24 一个字不动，两版各有各的口味）。</summary>
    private const double PaperFitMargin = 56;

    private void FitNow()
        => _vm.FitTo(PreviewHost.ActualWidth - PaperFitMargin, PreviewHost.ActualHeight - PaperFitMargin);

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        _autoFit = true;
        FitNow();
    }

    /// <summary>手动 −/＋ 就是"这一下我自己定"：自动显示全就此让位，点「适应」按回去。</summary>
    private void OnManualZoomClick(object sender, RoutedEventArgs e) => _autoFit = false;

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
