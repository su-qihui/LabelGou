using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LabelGou.App.Services.Recognition;
using LabelGou.App.ViewModels;
using LabelGou.Core.Docking;
using LabelGou.Core.Editing;
using LabelGou.Core.Templates;

namespace LabelGou.App;

/// <summary>主窗口：左侧五步向导（导入数据 → 连接字段 → 选模板 → 拼版编号 → 核对与输出，一次只露一步），
/// 右侧按毫米真实尺寸预览单标签与整版。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        // 标题里的版本号从程序集版本来（§二：改一处即可）。上一版把 v0.6.0 硬编码在 XAML 与 AboutText 里各一份，
        // 版本升上去这两处不会跟着动，用户看到的还是旧号——又是一条「改了没变化」。
        Title = Services.AppInfo.WindowTitle;
        DataContext = _viewModel;
        _viewModel.ErrorRaised += OnErrorRaised;

        // M3：打印/导出前的复核闸门（§七-11）——问人的事留给窗口，VM 不直接弹框
        _viewModel.ConfirmGate = text => MessageBox.Show(this, text, "LabelGou 打印前复核",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        // 整版控件靠回调取标签版面（Func 没法在 XAML 里绑），按页临时算，不预先展开几百页
        SheetView.LayoutProvider = index => _viewModel.Sheet.LayoutFor(index);

        // AI 面板在代码里建、放进右侧宿主 AiHost（第 11 棒把它从菜单搬进来常驻）。
        // 为什么不在 XAML 里直接挂：拆窗要把同一个实例在宿主与浮动窗之间搬，写在页签里就得先摘干净，
        // 交给 DetachablePanel 统一做这件事更清楚（也绝不会变成第二套 AI 面板）。
        var ai = new Services.AiChatPanel { Margin = new Thickness(0, 4, 0, 0) };
        AiPanel = ai;
        // 面板自己不知道模板库与打印在哪，三件事由这里递给它。
        WireAi(ai);

        // 第 30 棒：AI 模式下导入完表，由 VM 喊一声让**主面板**去读（VM 不认识面板，走事件）。
        // 只在这里订阅一次：浮动窗用的是同一个面板类，跟着 WireAi 一起订阅就会两个面板同时发请求
        // （用户要付两次模型钱、还会看到两份卡片）。
        _viewModel.AiReadRequested += () =>
        {
            if (_viewModel.IsAiMode) _ = AiPanel.AskProposalAsync();
        };

        // 只拆 AI 这一块（用户 2026-09-08 的第二版要求：「我把 AI 窗口拆下来和排版拿来对照」）：
        // 预览留在主窗，AI 在窗外，两边同时看得见——上一版把整块（预览 + AI）一起搬走是理解错了。
        _aiPanel = new Services.DetachablePanel(AiHost, ai, "LabelGou · AI 助手")
        {
            // 用户 2026-09-09：拆出来的窗永远从 900×700 贴着主窗开，拉到多大、摆在哪下次又没了——现在记住。
            ReadGeometry = _viewModel.LoadAiFloatGeometry,
            WriteGeometry = (l, t, w, h) => _viewModel.SaveAiFloatGeometry(l, t, w, h),
        };
        // 第二个泊位（用户 2026-09-09 第三次纠正：「拉到右侧可以吸附」）：右侧那一栏。
        // 只是多登记一个宿主，内容仍只有一个实例；拖拽与按钮走的都是同一套 Float / Dock。
        _aiPanel.AddDockSite(DockSite.Right, AiHostRight);
        _aiPanel.StateChanged += SyncAiPanelState;
        // 按住顶部那条握把拖 = 拆出（跟手）/ 拖到右缘或下缘 = 吸附。判据在 Core 的 DockSnap，这里只接线。
        _aiDrag = new Services.PanelDragController(this, _aiPanel, ai.DragGrip,
            DockPreviewRight, DockPreviewBottom, SplitRegionWidthDip, DesiredRightWidthDip);
        // 上次停在哪个泊位就摆回哪个（浮动不记：启动不该莫名多开一个窗口）。第 19 棒改了两道：
        // ① 「没记过」的默认从底部那一行换成右栏（他要的默认是左向导 / 中预览 / 右 AI）；
        // ② 右栏上次是窄条不再拦着把 AI 放回右栏——收窄就是 AI 收起来的家（内容不给看，列还在）。
        //   旧状态文件里那种「AI 在底部 + 右栏窄条」的矛盾组合由 ReconcileRightPane 搬回右栏并展开。
        (_leftPane, _rightPane) = _viewModel.LoadPaneModes();
        Services.AppLog.Info($"两栏形态接回上次那份：左={_leftPane}，右={_rightPane}");
        var recorded = _viewModel.LoadAiDock();
        var (startSite, startRightPane) = DockSnap.ReconcileRightPane(recorded.Site, _rightPane);
        if (startSite != recorded.Site || startRightPane != _rightPane)
        {
            Services.AppLog.Info($"上次那份状态里「AI 在 {recorded.Site}」与「右栏 {_rightPane}」在新规矩下互相矛盾（没人住的右栏不该留窄条）：本次按 AI 在右栏、展开处理");
            _rightPane = startRightPane;
            _viewModel.SavePaneModes(_leftPane, _rightPane);
        }
        if (startSite == DockSite.Right) _aiPanel.Dock(DockSite.Right);
        SyncAiPanelState();
        // 第 31 棒：窗口一改尺寸就**重新夹两栏的宽度**（只算宽度，不写盘——写盘那件事仍归 Closing 与 StateChanged）。
        // 修的是一个真 bug（用户 2026-09-10 截图）：右栏宽度原来只在「切换停靠状态」时算一次，
        // 于是「宽窗口下开着右栏、再把窗口拖窄」时列宽不跟着收 → 左栏 + 预览下限 + 右栏 之和超过窗口宽
        // → **最右边那截被窗口边缘裁掉，而窗口本身不横向滚动，内容就再也够不着**
        // （用户的描述正是「上面的字被挤出去看不见、往上滚也不行」）。
        // 夹法不用另写一套：ApplyPaneModes 里的 ClampRightColumnDip 早就把「预览下限」算进去了，
        // 缺的只是"窗口变了要重算一次"这个触发；放不下时它会自动退回窄条（AI 收起来的家），
        // 窗口再拉宽又按 _rightWidthDip 复原。
        SizeChanged += (_, _) => ApplyPaneModes();
        // 分隔条拖到哪、下次就开多宽：只在关窗那一次写盘，不跟着拖动每像素写。
        Closing += (_, _) => _viewModel.SaveAiDock(_aiPanel?.LastDockedSite ?? DockSite.Right,
            RightColumnWidthWorthRemembering());

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

    private Services.DetachablePanel? _aiPanel;
    private Services.PanelDragController? _aiDrag;

    /// <summary>
    /// 右栏上次给过多少宽（分隔条拖完、或下次吸附时读回来）。0 = 还没给过。
    /// <para>为什么另存一份而不直接读 <c>AiRightCol.Width</c>：AI 不在右栏时那一列必须真的收回 0 宽
    /// （列宽是预留空间，藏着 GroupBox 不藏列），所以那个值会被抹掉，不能当记忆用。</para>
    /// </summary>
    private double _rightWidthDip;

    /// <summary>AI 面板本体（通道设置窗关回来要刷新它那一行）。</summary>
    internal Services.AiChatPanel AiPanel { get; private set; }

    /// <summary>AI 那块的搬移器（标题栏按钮、原位提示、菜单都走这里，不开第二个入口）。</summary>
    internal Services.DetachablePanel? AiPanelHost => _aiPanel;

    /// <summary>
    /// AI 挂在主窗里时的默认高度：拆走时这一行让给预览，收回来按这个数复原。
    /// <para>2026-09-09 从 330 抬到 420：面板自己的硬需求（对话区 MinHeight 160 + 通道行 + 按钮一排 +
    /// 输入框 + 两行说明）就超过 330，于是被挤掉的是对话区——那就是用户圈的「AI 回复出来的窗口这么小」。</para>
    /// </summary>
    internal const double DockedAiHeight = 420;

    /// <summary>AI 挂在主窗里时那一行的下限：低于这个数对话区又开始被压，所以不许拖那么矮。</summary>
    internal const double DockedAiMinHeight = 260;

    private void OnDetachAiClick(object sender, RoutedEventArgs e) => _aiPanel?.Toggle();

    /// <summary>右栏那一栏里的手动退回入口：与拖到下缘同一个 <see cref="DetachablePanel.Dock(DockSite)"/>，不开第二套。</summary>
    private void OnAiDockBottomClick(object sender, RoutedEventArgs e) => _aiPanel?.Dock(DockSite.Bottom);

    /// <summary>
    /// 「恢复自动猜切法」：把 AI 定过的表头行与剔除行全部清掉，重读一次当前文件（第 21 棒）。
    /// <para>这条入口必须存在：不然用户点错一次「用这个」，就只能重选一次文件才能改回来。</para>
    /// </summary>
    private void OnResetSheetCutClick(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = _viewModel.ResetSheetChoice();
        if (!ok) _viewModel.ReportStatus("没能恢复自动切法：" + msg);
        Services.AppLog.Info((ok ? "切法恢复自动：" : "切法恢复失败：") + msg);
    }

    /// <summary>「预览 + 右栏」这一整块现在有多宽（DIP）：判右栏挤不挤得下的唯一口径。
    /// <para>构造期 ActualWidth 还是 0，那时拿屏幕工作区估一下——不然启动时会被判成「挤不下」而永久停在底部。</para></summary>
    private double SplitRegionWidthDip()
    {
        var total = RootColumns.ActualWidth > 0 ? RootColumns.ActualWidth : SystemParameters.WorkArea.Width;
        return Math.Max(0, total - WizardCol.ActualWidth - DockSnap.SplitterDip);
    }

    /// <summary>右栏想要多宽：先看现在这一栏（用户刚拖过分隔条），再看本窗上次给过的，再看上次记的，都没有就用默认那档。</summary>
    private double DesiredRightWidthDip()
    {
        if (AiRightCol.ActualWidth >= DockSnap.MinRightColumnDip) return AiRightCol.ActualWidth;
        if (_rightWidthDip >= DockSnap.MinRightColumnDip) return _rightWidthDip;
        var saved = _viewModel.LoadAiDock().RightWidth;
        return saved > 0 ? saved : DockSnap.DefaultRightColumnDip;
    }

    /// <summary>关窗时右栏那个宽度值不值得存下来：只有「展开且 AI 真住在里面」的那一栏宽度才算数。
    /// <para>不收这条口，窄条那一档的 44 会被当成上次记的右栏宽度存走，下次展开就开成一根柱子。</para></summary>
    private double RightColumnWidthWorthRemembering()
        => _rightPane == PaneMode.Open && _aiPanel?.Site == DockSite.Right && AiRightCol.ActualWidth >= DockSnap.MinRightColumnDip
            ? AiRightCol.ActualWidth
            : _rightWidthDip;

    /// <summary>拆/收/换泊位之后同步：两块宿主谁可见、行与列的宽、按钮文字、原位那句实话。</summary>
    private void SyncAiPanelState()
    {
        if (_aiPanel is not { } panel) return;
        var site = panel.Site;
        var detached = site == DockSite.Float;

        AiBox.Visibility = site == DockSite.Bottom ? Visibility.Visible : Visibility.Collapsed;
        DetachedHint.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;
        // AiRightBox 的可见性不在这里写：它跟「右栏给多宽」是同一件事，归 ApplyPaneModes 一处管（两处各写一半 = 谁后跑谁赢）。

        // AI 不挂在下面那一行时，那一行的高度必须真让给预览（Height 与 MinHeight 成对改，§五-107）。
        AiRow.Height = site == DockSite.Bottom ? new GridLength(DockedAiHeight) : GridLength.Auto;
        AiRow.MinHeight = site == DockSite.Bottom ? DockedAiMinHeight : 0;

        // AI 住在右栏时什么时候该强制展开、什么时候该把窄条留着，只看一件事：它是刚搬进来的，还是本来就在。
        // 从别的泊位搬进来（拖到右缘吸上、菜单选展开）= 他要看它，那窄条得收掉；
        // 本来就在右栏（启动接回上次那份）= 窄条就是它收起来的家，不许碰；
        // 搬去底部行或飘在窗外 = 那根窄条没主人了，复位成展开（下次吸回来是开着的）。
        // 第 19 棒：窄条只可能是「AI 收起来的家」，AI 不在家时不留一根空窄条。
        if (site != DockSite.Right) _rightPane = PaneMode.Open;
        else if (_lastAiSite is { } prev && prev != DockSite.Right) _rightPane = PaneMode.Open;
        _lastAiSite = site;

        // 两列的宽与两条分隔条全在 ApplyPaneModes 里算：那里同时看「泊位」和「栏形态」两个输入，
        // 两处各写一半迟早打架（谁后跑谁赢，界面上就是一栏忽宽忽窄）。
        ApplyPaneModes();

        DetachAiButton.Content = detached ? "收回主窗口 ⇤" : "把 AI 拆成独立窗口 ⇱";
        // 原位那句话要说的是真让出去的那一块，而不是固定写死「这一行」。
        DetachedHintText.Text = detached
            ? (panel.LastDockedSite == DockSite.Right
                ? "AI 助手已拆成独立窗口（标题「LabelGou · AI 助手」），右侧那一栏的宽度已还给预览。\n" +
                  "按住它顶部那条握把拖到主窗下缘就吸回底部那一行，拖到右缘又吸回这里；关掉窗口也会收回原位。"
                : "AI 助手已拆成独立窗口（标题「LabelGou · AI 助手」），下面这一行的高度已还给预览。\n" +
                  "按住它顶部那条握把拖到主窗右缘，就能把 AI 吸成右侧一栏，与预览并排对照；拖回下缘或关掉窗口都会收回这里。")
            : "";
        _viewModel.SaveAiDock(panel.LastDockedSite, _rightWidthDip);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    // ===== 左栏三态（展开 / 窄条 / 关掉）与右栏两态（展开 / 窄条）：用户 2026-09-09 两段录屏 + 第 19 棒纠正 =====

    /// <summary>左栏展开时的默认宽度（XAML 里那一个 470；收回来按它复原）。</summary>
    internal const double DefaultLeftPaneWidthDip = 470;

    /// <summary>左栏展开时的下限：不许拖成一条缝；收窄/关掉时这个下限必须一起抹掉。</summary>
    internal const double LeftPaneOpenMinWidthDip = 320;

    private PaneMode _leftPane = PaneMode.Open;
    private PaneMode _rightPane = PaneMode.Open;
    private double _leftOpenWidthDip = DefaultLeftPaneWidthDip;

    /// <summary>上一次同步时 AI 停在哪个泊位（null = 还没同步过 = 启动那一次）。
    /// <para>为什么只记这一个：「栏该不该从窄条强制展开」取决于是不是刚搬进来，而启动那次不许强制（否则存不住收起态）。</para></summary>
    private DockSite? _lastAiSite;

    /// <summary>
    /// 把两栏的形态落到列宽、分隔条与窄条上（三态的宽度一律问 <see cref="DockSnap.WidthForPane"/>）。
    /// <para><strong>为什么 MinWidth 也要跟着改</strong>：<c>ColumnDefinition.MinWidth</c> 会把列钉住，
    /// 光把 Width 设成 44 或 0 是收不起来的（左栏那个 320 下限就是干这个的）——不收下限 = 假收窄。</para>
    /// <para><strong>为什么右栏要听两个输入</strong>：「AI 住在哪个泊位」与「右栏这一态给多宽」是两件事：
    /// AI 飘在窗外或在底部那一行时右栏没内容可摆（给 0，也不留一根空窄条），而 AI 住在右栏时
    /// 展开与收窄都只改这一栏的宽，不搬内容（第 19 棒）。</para>
    /// </summary>
    private void ApplyPaneModes()
    {
        var leftOpen = _leftPane == PaneMode.Open;
        WizardCol.MinWidth = leftOpen ? LeftPaneOpenMinWidthDip : 0;
        WizardCol.Width = new GridLength(DockSnap.WidthForPane(_leftPane, _leftOpenWidthDip));
        WizardBody.Visibility = leftOpen ? Visibility.Visible : Visibility.Collapsed;
        WizardRail.Visibility = _leftPane == PaneMode.Narrow ? Visibility.Visible : Visibility.Collapsed;
        WizardSplitter.Visibility = leftOpen ? Visibility.Visible : Visibility.Collapsed;
        WizardSplitCol.Width = new GridLength(leftOpen ? DockSnap.SplitterDip : 0);

        // AI 住在右栏时这一栏才存在，且听两个输入：「它住在哪」与「它收没收起来」。
        // 第 19 棒改的关键一条：收窄不再把 AI 踢回底部那一行——那正是他要的「用完收到右侧」，
        // 内容留在这一栏里只是不给看（而列宽真的收成了 44，预览真拿到了那块地方）。
        var aiHomeIsRight = _aiPanel?.Site == DockSite.Right;
        var rightOpen = aiHomeIsRight && _rightPane == PaneMode.Open;
        var rightWidth = 0d;
        if (rightOpen)
        {
            // 夹一道：地方不够就是 0，那时宁可不撑这一栏也不把预览挤没（判据在 DockSnap）。
            rightWidth = DockSnap.ClampRightColumnDip(DesiredRightWidthDip(), SplitRegionWidthDip());
            if (rightWidth > 0) _rightWidthDip = rightWidth;
        }
        else if (aiHomeIsRight)
        {
            rightWidth = DockSnap.WidthForPane(PaneMode.Narrow, 0);    // 窄条那一档不关心展开宽是多少
        }
        // AI 不住这一栏时剩下的宽度必须真收：列宽是「预留空间」，只藏 GroupBox 不藏列会把预览挤短一截。
        AiRightCol.Width = new GridLength(rightWidth);
        AiRightBox.Visibility = rightOpen ? Visibility.Visible : Visibility.Collapsed;
        AiRightRail.Visibility = aiHomeIsRight && !rightOpen ? Visibility.Visible : Visibility.Collapsed;
        // 分隔条只在展开态给：窄条那一档没什么可调的（拖它只会把一根窄条拖宽，那不是收起）。
        AiRightSplitter.Visibility = rightOpen ? Visibility.Visible : Visibility.Collapsed;
        AiRightSplitCol.Width = new GridLength(rightOpen ? DockSnap.SplitterDip : 0);
    }

    /// <summary>
    /// 换某一栏的形态：先改状态，再落列宽并写盘。
    /// <para>展开右栏时顺手把 AI 吸回来（菜单那一句写的就是「展开（AI 就在这一栏）」）；
    /// 但地方挤不出合法右栏时不兑这个诺：只改形态，并把实话放进状态栏。</para>
    /// <para><strong>收窄不搬 AI</strong>（第 19 棒）：上一版这里是 <c>Dock(DockSite.Bottom)</c>，
    /// 而那条「AI 不许住在一个被收掉的列里」是我自己发明的规矩，用户 2026-09-09 直接否了——
    /// 他要的就是「用完可以收起到右侧」，把内容踢回底部等于把这个动作做废。</para>
    /// </summary>
    private void SetPaneMode(bool left, PaneMode mode)
    {
        if (left)
        {
            // 收之前先把用户拖出来的那一档宽度抄走，展开时原样还回去（不然每次都跳回 470）。
            if (mode != PaneMode.Open && WizardCol.ActualWidth >= LeftPaneOpenMinWidthDip)
                _leftOpenWidthDip = WizardCol.ActualWidth;
            _leftPane = mode;
        }
        else
        {
            // 右栏没有「完全关闭」这一档：AI 的家不收 = 关掉它住的那一栏，所以这一档根本不给它。
            if (mode == PaneMode.Closed) mode = PaneMode.Open;
            _rightPane = mode;
            if (mode == PaneMode.Open && _aiPanel is { } p && p.Site != DockSite.Right)
            {
                if (DockSnap.CanDockRight(SplitRegionWidthDip())) p.Dock(DockSite.Right);
                else _viewModel.ReportStatus("右侧现在挤不出合法的一栏（预览最少要留 360）：AI 先留在原来的地方。");
            }
        }
        ApplyPaneModes();
        _viewModel.SavePaneModes(_leftPane, _rightPane);
        Services.AppLog.Info($"两栏形态改为：左={_leftPane}，右={_rightPane}");
    }

    private void OnLeftPaneCollapseClick(object sender, RoutedEventArgs e) => SetPaneMode(true, DockSnap.CollapseStep(_leftPane));

    private void OnLeftPaneExpandClick(object sender, RoutedEventArgs e) => SetPaneMode(true, PaneMode.Open);

    private void OnLeftPaneOpenClick(object sender, RoutedEventArgs e) => SetPaneMode(true, PaneMode.Open);

    private void OnLeftPaneNarrowClick(object sender, RoutedEventArgs e) => SetPaneMode(true, PaneMode.Narrow);

    private void OnLeftPaneCloseClick(object sender, RoutedEventArgs e) => SetPaneMode(true, PaneMode.Closed);

    private void OnRightPaneCollapseClick(object sender, RoutedEventArgs e) => SetPaneMode(false, DockSnap.CollapseStep(_rightPane, mayClose: false));

    private void OnRightPaneExpandClick(object sender, RoutedEventArgs e) => SetPaneMode(false, PaneMode.Open);

    private void OnRightPaneOpenClick(object sender, RoutedEventArgs e) => SetPaneMode(false, PaneMode.Open);

    private void OnRightPaneNarrowClick(object sender, RoutedEventArgs e) => SetPaneMode(false, PaneMode.Narrow);

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
            // 画布照"会印出来的那条标签"画（含本行箱数这类推算量）——递表里第一行会让 Ctns 那行整条不显示（第 65 棒③）。
            PreviewRecord = _viewModel.EditorPreviewRecord,
        };
        var window = new TemplateEditorWindow(vm) { Owner = this };
        window.Saved += saved => _viewModel.ReloadTemplates(saved.Id);
        window.SavedAsCopy += saved => _viewModel.ReloadTemplates(saved.Id);
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

    /// <summary>
    /// ② 区「整批固定值…」：表里没这一列、但整批共用一个值（厂商表的客户名 BOLAROM 就属于这种）。
    /// <para>没数据时不开窗：里面会是十九个空行，开了也没意义。</para>
    /// </summary>
    private void OnEditFixedValuesClick(object sender, RoutedEventArgs e)
    {
        var rows = _viewModel.BuildFixedValueRows();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "先在① 打开工厂发来的表格，再填整批固定值。", "整批固定值",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new FixedValuesWindow(rows) { Owner = this };
        if (dialog.ShowDialog() == true) _viewModel.ApplyFixedValues(rows);
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

    // ---------- M5：从底稿导入（C 类模板） ----------

    /// <summary>
    /// 三态入口：文件框同时收 <c>.svg</c> 与 <c>.cdr</c>。
    /// <c>.svg</c> → 完整解析（保真矢量底图 + 可绑文字）；
    /// <c>.cdr</c> → 只抽内嵌预览图当不可打印的参考底图，并告诉人怎么拿保真版。
    /// </summary>
    private void OnImportBackgroundClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 CorelDRAW / Illustrator 导出的底稿",
            Filter = "底稿|*.svg;*.cdr|SVG 矢量底稿（推荐）|*.svg|CorelDRAW 底稿|*.cdr",
        };
        if (dialog.ShowDialog(this) != true) return;

        var (viewModel, error) = TemplateImportViewModel.Open(dialog.FileName, _viewModel.Templates);
        if (viewModel is null)
        {
            MessageBox.Show(this, error ?? "这份底稿导不进来。", "从底稿导入",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new TemplateImportWindow(viewModel) { Owner = this };
        window.ShowDialog();                              // 存好了窗口自己置 DialogResult=true 并关闭
        if (window.ResultTemplate is null) return;        // 取消，或确实没过了校验（原因已写在窗口里）

        var template = window.ResultTemplate;
        _viewModel.ReloadTemplates(template.Id);
        Services.AppLog.Info($"底稿导入完成并选中：{template.Name}（{template.Elements.Count} 个元素）");
    }

    // ---------- M6：智能识别单据（图片 / Word） ----------

    private bool _recognizing;

    /// <summary>
    /// 模型设置与调试入口：上一轮云端支持只写在代码层、界面上一个入口都没有，
    /// 用户直接问「哪里接模型、哪里调试」。这个窗口里能选通道（本机 Ollama / 阿里云百炼 /
    /// DeepSeek / 自定义端点）、探测模型在不在、并拿一张真图当场问一次，把模型原话与耗时摊出来。
    /// </summary>
    private void OnAiSettingsClick(object sender, RoutedEventArgs e)
    {
        new Services.AiDebugWindow { Owner = this }.ShowDialog();
        AiPanel.RefreshChannel();     // 常驻页签上的通道行跟着改，不留旧话
    }

    private Services.AiChatWindow? _chatWindow;

    /// <summary>
    /// 「和模型对话」入口（第 10 棒）：用户要的是能问、能来回调，而不是又一条固定提示词的抽字段。
    /// <para>故意用非模态：这个窗口的用法就是一边看预览一边问，模态会把预览与 ②③ 面板全部挡住；
    /// 但同时只开一个（再点一次只把它带回前台），避免两个窗口各自一份历史、用户分不清哪份在发。</para>
    /// </summary>
    private void OnAiChatClick(object sender, RoutedEventArgs e)
    {
        if (_chatWindow is { IsVisible: true })
        {
            _chatWindow.Activate();
            return;
        }
        _chatWindow = new Services.AiChatWindow { Owner = this };
        WireAi(_chatWindow.Panel);        // 同一个面板，能力递法也与页签里那份一致
        _chatWindow.Closed += (_, _) => _chatWindow = null;
        _chatWindow.Show();
    }

    /// <summary>
    /// 把主界面的能力递给 AI 面板：排版要问「这张表真连了哪些字段」，落地要过模板库自己的校验，
    /// 打印复用 ⑤ 那条命令——面板里不建第二份模板库、不开第二套出纸路（§五-22）。
    /// </summary>
    private void WireAi(Services.AiChatPanel panel)
    {
        panel.GetLayoutContext = () =>
        {
            var template = _viewModel.SelectedTemplate?.Template;
            if (template is null) return null;
            var fields = _viewModel.FieldRows.Where(r => r.Mapped)
                .Select(r => (r.FieldKey, r.DisplayName, r.SampleValue)).ToList();
            // 已连字段只是一半：没连上的列也要摊给模型看，否则它只能按我们的猜测排（用户 2026-09-08 圈的 TOP/郑小姐两条）。
            var portrait = _viewModel.BuildTablePortrait();
            // 表里贴的那张效果图/模板截图也一起递过去（第 20 棒）：它才是「这枚唛头该长什么样」的唯一说明。
            var sheetImages = new List<Services.AiChatImage>();
            foreach (var img in _viewModel.SheetImages)
            {
                if (sheetImages.Count >= Services.AiChatPanel.MaxImagesPerRequest) break;
                try
                {
                    var (b64, mime) = Services.Recognition.ImageForModel.Encode(img.Bytes, img.MimeType);
                    sheetImages.Add(new Services.AiChatImage(b64, mime, img.Describe()));
                }
                catch (Exception ex)
                {
                    // 一张图编码失败不能挡整次请求（EMF/WMF 这类 Office 矢量图 WPF 解不了就是这条）
                    System.Diagnostics.Debug.WriteLine($"[WireAi] 表内贴图发不出去，已跳过：{img.FileName} {ex.Message}");
                }
            }
            var hasArtwork = template.Elements.Any(e => e.Kind is LabelGou.Core.Templates.ElementKind.Image
                                                        or LabelGou.Core.Templates.ElementKind.Vector);
            return new Services.AiLayoutContext(fields, template.WidthMm, template.HeightMm, _viewModel.StatusMessage,
                portrait?.Columns, portrait?.Portrait, sheetImages, hasArtwork,
                // 提案那一枪要的东西：真有的纸规清单（它只能从这份里选）、原表行数（行号边界）、
                // 软件目前猜的表头行（告诉它现在错在哪，它才知道要不要改）。
                _viewModel.SheetSpecNames, _viewModel.RawRowCount, _viewModel.DetectedHeaderRow,
                // 第 39 棒：逐格量到的字号/粗体/居中。这一份**不发出去**，是软件自己留着算标签字号的
                // （模型回提案时由 RowFormatEvidence 照它改 spec），所以它不在提示词里出现。
                portrait?.CellFormats);
        };
        panel.ApplyLayout = ApplyAiLayout;
        panel.ApplyProposal = ApplyAiProposal;
        // 第 23 棒：AI 请求在飞期间换文件/表/模板，旧结果落地前按代数对一遍，不等就作废。
        panel.GetDataGeneration = () => _viewModel.DataGeneration;
        // 第 40 棒：这里原来还接了一条 panel.ApplyQuestion = ApplyAiQuestion（点一条问题就改一处活表），
        // 两阶段拆分后删了——读表阶段的答复只改那份提案，排版阶段整份落地，中间不许动活表。
        // 阶段 29 第 1 棒：改动卡要先说清「原来是什么」，所以把软件此刻的状态单拎一份给它。
        // 拿不到的字段一律留 null（Core 那边会显示成"还没定"），**不许在这里补一个看起来很像的值**。
        panel.GetChangeContext = () =>
        {
            var choice = _viewModel.CurrentChoice;
            var excluded = (choice.ExcludedRawRows ?? Array.Empty<int>())
                .OrderBy(i => i).Select(i => i + 1).ToList();     // 卡上给人看的是 Excel 口径（1 起）
            var template = _viewModel.SelectedTemplate?.Template;
            // 字段绑定（第 30 棒）：每个字段此刻连在哪一列。值给**列标题**——卡上"原值"那一格就是它。
            var bindings = new Dictionary<LabelGou.Core.Marks.MarkFieldKey, string>();
            foreach (var row in _viewModel.FieldRows)
            {
                if (!row.Mapped) continue;
                if (!Enum.TryParse<LabelGou.Core.Marks.MarkFieldKey>(row.FieldKey, out var key)) continue;
                var label = row.Columns.FirstOrDefault(c => c.Index == row.ColumnIndex)?.Label;
                if (!string.IsNullOrWhiteSpace(label)) bindings[key] = label;
            }
            return new LabelGou.Core.Recognition.AiChangeContext(
                _viewModel.RawRowCount,
                _viewModel.DetectedHeaderRow > 0 ? _viewModel.DetectedHeaderRow : null,
                choice.HasHeader,
                excluded.Count == 0 ? null : excluded,
                _viewModel.SelectedTemplate?.Name,
                template?.WidthMm,
                template?.HeightMm,
                _viewModel.Sheet.SelectedSheetOption?.Spec.Name,
                bindings.Count == 0 ? null : bindings);
        };
        // 第 33 棒：能自动判的直接落地、靠"可撤回"兜底 —— AI 要动手之前由这一份压快照，
        // 面板上那颗「撤回」按一下退一步（可连点）。快照只活在内存里（跨会话撤回没意义）。
        panel.UndoAiChange = () => _viewModel.UndoLastAiChange();
        panel.CanUndoAiChange = () => _viewModel.CanUndoAiChange;
        panel.AiUndoLabel = () => _viewModel.AiUndoLabel;
        // 「预览:31个模板,155张」那一句的数由软件自己数（按 AI 点的那一列逐行加），不信模型报的总数。
        panel.OutputCounter = qtyColumn => _viewModel.CountOutput(qtyColumn);
        panel.GoPrint = PrintFromAi;
    }

    /// <summary>用户点了「用这个」才走到这里。存不存得进模板库仍由 <see cref="TemplateStore"/> 的校验说了算。</summary>
    private (bool Ok, string Message) ApplyAiLayout(RowLayoutSpec spec)
    {
        var template = spec.Build();
        if (template is null)
            return (false, "这份方案排不进这块标签（留白与行距把版面吃光了），当前模板没被动过。");
        // 出纸闸（第 38 棒 · 三道闸之一，销第 36 棒欠账 #2）：一个要素都印不出的空版不配上预览。
        if (!_viewModel.TemplatePrintsAnything(template))
            return (false, "这一版排出来是空的（模板里的占位符在当前数据里一个都取不到值），我没往上放——当前模板没被动过。");
        var (saved, fileName, issues) = _viewModel.Templates.Save(template);
        if (!saved)
            return (false, "校验拦下了，没入库：" + string.Join("；",
                issues.Where(i => i.Severity == IssueLevel.Error).Select(i => i.Message)));
        _viewModel.ReloadTemplates(template.Id);
        Services.AppLog.Info($"AI 出的版式经用户确认存为模板：{template.Name}（{fileName}）");
        return (true, $"已存成我的模板「{template.Name}」（{fileName}）并选中，预览已跟着换。要改细节走 ③ 编辑模板。");
    }

    /// <summary>
    /// 落地**整份提案**：重切这张表 → 落字段绑定 → 存这版模板 → 换那张纸。
    /// <para>第 33 棒起这条不再等用户逐条点头（他改了架构：<b>能自动判的直接生效，靠"可撤回"兜底</b>），
    /// 所以**字段绑定也进了这里**——以前绑定是单独的逐条 ✅ 卡，现在四类一起落。</para>
    /// <para>各件独立报成败，不假装「全成才算成功」：切表会被 Core 拒掉（那张表保住），
    /// 版式进不了模板库是校验的事，纸规点名不对就保持现状——把它们包成一个布尔值反而隐掉了
    /// 用户真正需要看的那一句。</para>
    /// </summary>
    private (bool Ok, string Message) ApplyAiProposal(LabelGou.Core.Recognition.AiSheetProposal proposal)
    {
        var lines = new List<string>();
        var anyOk = false;

        // 第 33 棒：**动手之前压快照**（撤回粒度"逐步"，一次 AI 动手 = 一步）。
        // 压在这里而不是面板里：快照这件事归主窗口管，面板只管"要不要落地"。
        _viewModel.BeginAiChange("读表提案（整份）");

        var next = _viewModel.ChoiceFrom(proposal);
        if (!SameCut(_viewModel.CurrentChoice, next))
        {
            var (ok, msg) = _viewModel.ApplySheetChoice(next);
            lines.Add((ok ? "切表✓ " : "切表✗（表保持原样）") + msg);
            anyOk |= ok;
        }
        // 绑定要紧跟在切表后面：切表那一步会重读源文件，字段行是按"当前方案"重建的，
        // 先落绑定就会被它盖掉（第 33 棒撤回那边是同一个顺序理由）。
        if (proposal.Mappings.Count > 0)
        {
            var bound = 0;
            foreach (var mapping in proposal.Mappings)
                if (_viewModel.BindField(mapping.Field, mapping.ColumnIndex).Ok) bound++;
            lines.Add($"字段绑定{(bound > 0 ? "✓" : "✗")} 落了 {bound} 项"
                + (bound < proposal.Mappings.Count ? $"（它报了 {proposal.Mappings.Count} 项，其余没对上）" : string.Empty));
            anyOk |= bound > 0;
        }
        if (proposal.Layout is { } spec)
        {
            var (ok, msg) = ApplyAiLayout(spec);
            lines.Add((ok ? "版式✓ " : "版式✗ ") + msg);
            anyOk |= ok;
        }
        if (proposal.SheetSpecName is { } name)
        {
            var msg = _viewModel.Sheet.SelectSheetSpecByName(name);
            lines.Add(msg);
            anyOk |= msg.StartsWith("纸规已切到", StringComparison.Ordinal);
        }
        // 张数列接到拼版上（第 40 棒补）：必须排在切表与字段绑定都落完之后——切表会重建展开列候选并把展开列复位，
        // 早接会被它冲掉。Readout.QtyColumn 来自模型报的 qtyColumn 或老板对「qty-column」那条答的「是」。
        if (proposal.Readout.QtyColumn is { Length: > 0 } qtyHeader)
        {
            var qtyMsg = _viewModel.Sheet.ApplyQtyColumn(qtyHeader);
            if (qtyMsg.Length > 0)
            {
                lines.Add(qtyMsg);
                anyOk |= qtyMsg.StartsWith("已按", StringComparison.Ordinal);
            }
        }
        foreach (var w in proposal.Warnings) lines.Add("⚠ " + w);
        if (lines.Count == 0) return (false, "这份提案里没有可落地的改动（它什么都没提）。当前表、模板与纸规都没动。");
        Services.AppLog.Info("AI 提案落地（第 33 棒起自动落地，可撤回）：" + string.Join(" / ", lines));
        return (anyOk, string.Join("\n", lines));
    }

    /// <summary>两份切法是否等价（等价就别白重读一次文件，也不要假装"改了其实没改"）。合计行兜底也是一项（第 24 棒）。</summary>
    private static bool SameCut(LabelGou.Core.Data.SheetLayoutChoice a, LabelGou.Core.Data.SheetLayoutChoice b)
    {
        var ra = a.ExcludedRawRows ?? Array.Empty<int>();
        var rb = b.ExcludedRawRows ?? Array.Empty<int>();
        return a.HasHeader == b.HasHeader && a.HeaderRowIndex == b.HeaderRowIndex
            && a.SkipSummaryRows == b.SkipSummaryRows
            && ra.SequenceEqual(rb);
    }

    /// <summary>「按这版去打印」：跳到 ⑤ 并触发那条既有命令；未核对字段照样被复核闸门拦着。</summary>
    private void PrintFromAi()
    {
        _viewModel.StepIndex = 4;
        // 第 23 棒：Mvvm.Execute 不查 CanExecute——任务在跑或没有整版时这里会硬闯进 RunJob。
        if (!_viewModel.Export.PrintCommand.CanExecute(null))
        {
            Services.AppLog.Info("AI 面板「按这版去打印」没发出去：打印命令自己挡了（没有可输出的整版，或已有任务在跑）。");
            return;
        }
        _viewModel.Export.PrintCommand.Execute(null);
    }

    /// <summary>
    /// 识别入口。三条现场约束决定了它的形状：
    /// ① 大模型一张约 34 秒，所以必须能中途取消（<see cref="RecognitionProgressWindow"/>）；
    /// ② 跑着的时候不许再点一次（<see cref="_recognizing"/> 重入锁，两张单子撞在一起只会互相盖结果）；
    /// ③ 识别结果不直接进数据源 —— 先过核对窗口逐条确认（定案 D13），没核完的不允许接进来。
    /// </summary>
    private async void OnRecognizeClick(object sender, RoutedEventArgs e)
    {
        if (_recognizing)
        {
            _viewModel.ReportStatus("识别还在跑，先等它结束或点取消。");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择工厂发来的单据（照片 / 截图 / Word）",
            Filter = RecognitionService.OpenFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        _recognizing = true;
        RecognitionRun? run = null;
        using var cts = new CancellationTokenSource();
        var progressWindow = new RecognitionProgressWindow { Owner = this };
        progressWindow.CancelRequested += () => cts.Cancel();

        try
        {
            var progress = new Progress<string>(message =>
            {
                progressWindow.Report(message);
                _viewModel.ReportStatus(message);
            });

            var task = RecognitionService.RunAsync(dialog.FileNames, RecognitionSettings.Load(), progress, cts.Token);
            // 跑完（或取消后停下来）就自己收窗，不靠用户去点——续接里只碰 UI，不抛新异常
            _ = task.ContinueWith(_ => progressWindow.Complete("识别停下来了，正在回主界面…"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            _ = task.ContinueWith(_ => progressWindow.Complete("识别已停下来。"),
                CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);

            progressWindow.ShowDialog();      // 用户提前关掉也只是触发取消，后台会自己停下来
            run = await task;
        }
        catch (Exception ex)
        {
            Services.AppLog.Error("识别流程异常", ex);
            MessageBox.Show(this, "识别没能跑完：" + ex.Message, "智能识别",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _recognizing = false;
            if (progressWindow.IsLoaded) progressWindow.Close();
        }

        if (run is null) return;
        if (run.Cancelled)
        {
            // 取消后不能再说「识别结束，正在打开核对窗口」：那是把半途而废当成跑完了。
            // 已经跑完的那几份仍交回核对窗口（下面的 Batches 不为空就照开），但文案得说实话。
            _viewModel.ReportStatus(run.Batches.Count > 0
                ? $"识别已取消：跑完的 {run.Batches.Count} 份还是送进核对窗口了，没跑完的那些不算。"
                : "识别已取消，没有可核对的结果。");
            if (run.Batches.Count == 0) return;
        }
        else if (run.Batches.Count == 0)
        {
            _viewModel.ReportStatus(run.Warnings.Count > 0 ? string.Join("；", run.Warnings) : "没有可识别的文件。");
            return;
        }

        var review = new RecognitionReviewViewModel(run);
        var reviewWindow = new RecognitionReviewWindow(review) { Owner = this };
        reviewWindow.ShowDialog();            // “导入这一份”才是提交动作；关窗不会撤销已经导入过的记录

        if (!reviewWindow.AnyImported)
        {
            _viewModel.ReportStatus("识别结果一份也没导入，当前数据源没变（未核对的东西不允许当数据用）。");
            return;
        }

        _viewModel.AdoptRecognizedRecords(reviewWindow.ImportedRecords, string.Join("、", review.ImportedNames));
        var leftover = reviewWindow.LeftoverSummary;
        if (leftover.Length > 0) _viewModel.ReportStatus($"已接入记录；另有 {leftover}。");
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

    /// <summary>
    /// 打开「批量管理模板」那一屏（用户 2026-09-10 点名要的多选删除）。
    /// <para>关掉之后刷一次模板下拉：他刚删掉的那几份若正是当前选中的那份，下拉得跟着换，
    /// 不能留着一个已经不存在的选中项。</para>
    /// </summary>
    private void OnManageTemplatesClick(object sender, RoutedEventArgs e)
    {
        new TemplateManagerWindow(_viewModel.Templates) { Owner = this }.ShowDialog();
        _viewModel.ReloadTemplates();
    }

    private void OnSheetFitClick(object sender, RoutedEventArgs e) => SheetFitNow();

    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 视口宽一律递给 VM：缩略一览按它分十格。这一句不能挂在「自动适应」那颗勾上 ——
        // 一览开着时那排按钮（含这颗勾）整排是藏起来的，挂上去格宽就永远不刷新（第 71 棒他报的"太小/不换行"）。
        _viewModel.SetPreviewViewport(PreviewHost.ActualWidth);
        if (AutoFitBox.IsChecked == true) FitNow();
    }

    /// <summary>
    /// 点缩略一览里的一格：跳到那一行并收起一览。
    /// <para>走鼠标事件而不是每格一条命令 —— 命令路由静默失败时表现就是"点了没反应"（第 71 棒他报的那条）。</para>
    /// </summary>
    private void OnRowThumbClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MainViewModel.RowThumb thumb })
            _viewModel.SelectRowThumb(thumb);
    }

    private void OnSheetHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SheetAutoFitBox.IsChecked == true) SheetFitNow();
    }

    private void FitNow() => _viewModel.FitTo(PreviewHost.ActualWidth, PreviewHost.ActualHeight);

    private void SheetFitNow() => _viewModel.FitSheetTo(SheetHost.ActualWidth, SheetHost.ActualHeight);

    private static string AboutText =>
        $"LabelGou · 唛头标签助手 v{Services.AppInfo.Version}（M7）\n\n" +
        "面向打印店 / 印刷厂的唛头标签自动化工具。\n" +
        "五步向导：Excel/CSV 导入 → 字段映射（含整批固定值）→ 套模板（内置的、自己拖的、从 CorelDRAW/Illustrator 导出的 SVG 底稿导入的、按行式骨架生成的）"
        + "→ 拼版编号（一页一枚 / 一页多枚、角线与套准十字、页边可跟随标签）→ 核对与输出。\n" +
        "预览 / 直连打印 / PDF / PNG / TIFF / 给 CorelDRAW 的 SVG 走同一套渲染与同一套判据；"
        + "还能把单据照片 / 截图 / Word 单据识别成记录（本地 OCR + 大模型双通道交叉校验）。\n\n" +
        "识别结果逐条人工核对过才算数据：没核完的字段会被标红，并且不给导入、不给打印；纸规报错的条数会写在打印 / 导出的确认框里。\n" +
        "模型通道（本机 Ollama / 云端）、端点、模型名、超时、本地 OCR 开关与密钥清除，都在「文件 → 模型（AI）设置与调试…」里改。\n" +
        "API 密钥不落盘：填了只在这次运行里有效；要长期用，把环境变量 LABELGOU_LLM_KEY 设上。\n" +
        "出问题先看「帮助 → 查看诊断日志…」，运行日志与被跳过的坏纸规文件名都记在那儿。\n" +
        "与店里的 CorelDRAW 对接：看安装目录下 tools\\cdr 的说明与批量导出宏。\n" +
        "后续里程碑：M8 打磨发布。\n\n" +
        "开发计划与进度详见 labelgou-word 目录下的文档。授权：MIT。";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        // 文本框里按纯 Ctrl+字母是编辑动作（Ctrl+I 斜体、Ctrl+T/Ctrl+O 那些），不该被全局快捷键抢走：
        // 上一版用 HasFlag(Control)，在备注框里敲字时一句「Ctrl+I」就把底稿导入窗口弹出来了。
        // 只挡「纯 Ctrl」这一类：Alt+←/→ 这种导航键在文本框里也不该抢回来。
        if (Keyboard.FocusedElement is TextBox or PasswordBox && modifiers == ModifierKeys.Control) return;

        // Ctrl+Shift+R：识别入口。Ctrl+O 已经被「打开数据文件」占了，不再抢一个键
        if (e.Key == Key.R && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OnRecognizeClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.O && modifiers == ModifierKeys.Control)
        {
            if (_viewModel.OpenFileCommand.CanExecute(null)) _viewModel.OpenFileCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P && modifiers == ModifierKeys.Control)
        {
            if (_viewModel.Export.PrintCommand.CanExecute(null)) _viewModel.Export.PrintCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.T && modifiers == ModifierKeys.Control)
        {
            OnEditTemplateClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.I && modifiers == ModifierKeys.Control)
        {
            OnImportBackgroundClick(this, new RoutedEventArgs());
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
