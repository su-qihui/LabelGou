using System.Windows;
using System.Windows.Input;
using LabelGou.Core.Docking;

namespace LabelGou.App.Services;

/// <summary>
/// 「按住握把拖」这一件事的全部机械：**跟手移动 + 落点预览 + 松手吸附**。
/// <para>为什么单独一个类（M7 第 17 棒第二版）：用户 2026-09-09 第三次纠正要的是
/// 「把这个 AI 窗口长按拖动可以拆下来，然后拉到右侧可以吸附」。判据（离边多少算吸附、
/// 两条边都够得着时听谁的、右栏能给多宽）全在 <see cref="DockSnap"/>（Core、可单测）；
/// 这里只剩「拿光标位置、挪窗口、开关系两个预览」——所以它能被读成一段手势流程，而不是一堆 if。</para>
/// <para><strong>为什么捕获挂在主窗口上而不是握把上</strong>：拖的过程里这块内容会被搬进新的浮动窗口
/// （换了一个视觉树、甚至换了一个 HWND），捕获在握把上会跟着一起被搬走；捕获在主窗这个
/// <see cref="Window"/> 上，鼠标消息就一路归它收，光标跑到浮动窗口外面也照样能收到 Move/Up。</para>
/// <para><strong>坐标口径</strong>：一律换成<strong>屏幕 DIP</strong> 再算（<see cref="DockSnap"/> 的入参就是这个单位）。
/// <c>PointToScreen</c> 给的是物理像素，150% 缩放的屏上不换算就会偏 1.5 倍。
/// 已知边界：跨窗口换算假定两块窗口 DPI 相同（本机系统 DPI 感知，单屏/同缩放多屏成立）；
/// 真要支持每屏不同缩放得走 <c>PerMonitorV2</c> + 逐窗换算，代价记进 §十。</para>
/// </summary>
public sealed class PanelDragController
{
    private readonly Window _owner;
    private readonly DetachablePanel _panel;
    private readonly UIElement _grip;
    private readonly FrameworkElement _previewRight;
    private readonly FrameworkElement _previewBottom;
    private readonly Func<double> _splitRegionWidthDip;
    private readonly Func<double> _desiredRightWidthDip;

    private bool _armed;                          // 左键按下了，还没到起拖阈值
    private Point _pressDip;                      // 按下那一刻的光标（屏幕 DIP）
    private Vector _grabOffset;                   // 光标相对那块面板左上角的偏移（屏幕 DIP）
    private Vector _chrome;                       // 浮动窗「窗口左上角 → 客户区左上角」的厚度
    private bool _chromeMeasured;

    /// <summary>现在正拖着吗。</summary>
    public bool IsDragging { get; private set; }

    /// <summary>拖动过程中当前算出来的落点（松手时就按它处置；没在拖时是 <see cref="DockSite.Float"/>）。</summary>
    public DockSite PendingSite { get; private set; } = DockSite.Float;

    /// <summary>
    /// <param name="owner">主窗口：吸附目标，也是鼠标捕获的宿主。</param>
    /// <param name="panel">那块面板的搬移器（本类只调它的 FloatAt / Dock，不自己碰 ContentControl）。</param>
    /// <param name="grip">握把元素（跟着面板走，拆出去之后也还在）。</param>
    /// <param name="previewRight">右缘落点预览（一块虚线带，宽度按真会给出的那一档画）。</param>
    /// <param name="previewBottom">下缘落点预览。</param>
    /// <param name="splitRegionWidthDip">「预览 + 右栏」这一整块现在有多宽（DIP）——判右栏挤不挤得下用。</param>
    /// <param name="desiredRightWidthDip">右栏想要多宽：预览不许画一个给不出来的宽度。</param>
    public PanelDragController(
        Window owner, DetachablePanel panel, UIElement grip,
        FrameworkElement previewRight, FrameworkElement previewBottom,
        Func<double> splitRegionWidthDip, Func<double> desiredRightWidthDip)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _grip = grip ?? throw new ArgumentNullException(nameof(_grip));
        _previewRight = previewRight ?? throw new ArgumentNullException(nameof(previewRight));
        _previewBottom = previewBottom ?? throw new ArgumentNullException(nameof(previewBottom));
        _splitRegionWidthDip = splitRegionWidthDip ?? throw new ArgumentNullException(nameof(splitRegionWidthDip));
        _desiredRightWidthDip = desiredRightWidthDip ?? throw new ArgumentNullException(nameof(desiredRightWidthDip));

        _grip.PreviewMouseLeftButtonDown += OnGripDown;
        _grip.MouseMove += OnGripMouseMove;                       // 起拖之前（内容还在主窗里）先由握把自己收 Move
        _grip.MouseLeftButtonUp += OnOwnerMouseUp;                // 捕获万一没保住（见 BeginDrag 里那句重捕），松手也得收摊
        _owner.MouseMove += OnOwnerMouseMove;                     // 起拖之后一律看主窗（捕获在它身上）
        _owner.MouseLeftButtonUp += OnOwnerMouseUp;
        HidePreviews();
    }

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _armed = true;
        _pressDip = CursorDip(e);
        // 捕获在主窗上：拖的过程里这块内容会被搬进新窗口，捕获不能跟着搬。
        _owner.CaptureMouse();
        // 不 e.Handled：握把上没有别的可点东西，但别把 WPF 的鼠标基础状态搞乱。
    }

    private void OnGripMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _armed = false; return; }
        MaybeBeginDrag(e);
    }

    private void OnOwnerMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _armed = false; return; }
        if (!MaybeBeginDrag(e)) return;
        if (!IsDragging) return;

        var cursor = CursorDip(e);
        MoveFloatUnder(cursor);
        UpdatePending(cursor);
    }

    /// <summary>还没到阈值就什么都不做（返回 false = 还没起拖）。</summary>
    private bool MaybeBeginDrag(MouseEventArgs e)
    {
        if (!IsDragging && _armed)
        {
            var cursor = CursorDip(e);
            var dx = Math.Abs(cursor.X - _pressDip.X);
            var dy = Math.Abs(cursor.Y - _pressDip.Y);
            // 阈值用系统那个最小拖动距离：不做真「长按计时器」。桌面端「按住即拖」比「按住 400ms 才准拖」
            // 更符合手感，而用户说的「长按」在鼠标语境里就是「按住不放」（口径与理由留痕在 §八 登记行）。
            if (dx < SystemParameters.MinimumHorizontalDragDistance
                && dy < SystemParameters.MinimumVerticalDragDistance) return false;
            BeginDrag(cursor);
        }
        return IsDragging;
    }

    private void BeginDrag(Point cursor)
    {
        _grabOffset = CursorMinusContentOrigin(cursor);
        if (!_panel.IsDetached)
        {
            // 先按「没有窗框」估一个位置拆出去，Show 之后量到真实窗框厚度再补一次（见 MoveFloatUnder）。
            _chromeMeasured = false;
            _panel.FloatAt((cursor.X - _grabOffset.X, cursor.Y - _grabOffset.Y));
        }
        if (_panel.FloatingWindow is null) { _armed = false; return; }
        // 刚 Show 出一个新 HWND 时 WPF 有可能把捕获丢回去，这里再抢一次；抢不到也不影响流程（握把自己也会报 Up）。
        _owner.CaptureMouse();
        IsDragging = true;
        _armed = false;
    }

    /// <summary>把浮动窗挪到「面板左上角正好在光标减抓握偏移」处——这就是"跟手"。</summary>
    private void MoveFloatUnder(Point cursor)
    {
        if (_panel.FloatingWindow is not { } win) return;
        if (!_chromeMeasured)
        {
            _chrome = ChromeThickness(win);
            _chromeMeasured = true;
        }
        var want = new Point(cursor.X - _grabOffset.X, cursor.Y - _grabOffset.Y);
        win.Left = want.X - _chrome.X;
        win.Top = want.Y - _chrome.Y;
    }

    private void OnOwnerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_armed && !IsDragging) return;
        _armed = false;
        var site = PendingSite;
        IsDragging = false;
        PendingSite = DockSite.Float;
        HidePreviews();
        _owner.ReleaseMouseCapture();
        if (site == DockSite.Float) return;           // 停在半空：继续浮着，这是用户自己选的位置
        if (!_panel.Dock(site))
        {
            // 泊位没登记成功（例如右栏被别的逻辑关了）：说实话继续浮动，不静默把人丢到另一个泊位。
            AppLog.Warning($"拖到 {site} 但那个泊位不在，AI 面板继续浮动");
        }
    }

    private void UpdatePending(Point cursor)
    {
        var site = DockSnap.Decide(cursor.X, cursor.Y, _owner.Left, _owner.Top, _owner.ActualWidth, _owner.ActualHeight);
        var width = 0d;
        if (site == DockSite.Right)
        {
            width = DockSnap.ClampRightColumnDip(_desiredRightWidthDip(), _splitRegionWidthDip());
            // 右栏挤不下就不许报「可以吸到这里」：预览不画，松手也不吸（宁可维持浮动，不画一个做不到的承诺）。
            if (width <= 0) site = DockSite.Float;
        }
        PendingSite = site;
        _previewRight.Visibility = site == DockSite.Right ? Visibility.Visible : Visibility.Collapsed;
        _previewBottom.Visibility = site == DockSite.Bottom ? Visibility.Visible : Visibility.Collapsed;
        if (site == DockSite.Right) _previewRight.Width = width;
    }

    private void HidePreviews()
    {
        _previewRight.Visibility = Visibility.Collapsed;
        _previewBottom.Visibility = Visibility.Collapsed;
    }

    /// <summary>光标相对「那块面板左上角」的偏移：拖出去之后要让同一个抓点一直粘在手指下。</summary>
    private Vector CursorMinusContentOrigin(Point cursor)
    {
        var origin = ContentOriginDip();
        return new Vector(cursor.X - origin.X, cursor.Y - origin.Y);
    }

    private Point ContentOriginDip()
    {
        var content = _panel.Content;
        var win = Window.GetWindow(content) ?? _owner;
        var inRoot = content.TransformToVisual(win).Transform(new Point(0, 0));
        return ToScreenDip(win, inRoot);
    }

    /// <summary>窗口「左上角」与「客户区左上角」的差（标题栏 + 边框厚度，DIP）。</summary>
    private static Vector ChromeThickness(Window win)
    {
        var client = ToScreenDip(win, new Point(0, 0));
        return new Vector(client.X - win.Left, client.Y - win.Top);
    }

    private Point CursorDip(MouseEventArgs e) => ToScreenDip(_owner, e.GetPosition(_owner));

    /// <summary>某个窗口内的点 → 屏幕 DIP（<c>PointToScreen</c> 出来是物理像素，必须按该窗 DPI 换算）。</summary>
    private static Point ToScreenDip(Window win, Point inWindowRoot)
    {
        var device = win.PointToScreen(inWindowRoot);
        var scale = DpiOf(win);
        return new Point(DockSnap.DeviceToDip(device.X, scale), DockSnap.DeviceToDip(device.Y, scale));
    }

    private static double DpiOf(Window win)
        => PresentationSource.FromVisual(win)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
}
