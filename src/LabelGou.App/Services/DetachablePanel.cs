using System.Windows;
using System.Windows.Controls;

namespace LabelGou.App.Services;

/// <summary>
/// 把一块界面内容在「主窗口里」与「独立浮动窗口」之间整体搬移：拆得下来，也拼得回去。
/// <para><strong>为什么要有它</strong>（M7 第 16 棒，用户 2026-09-08 的原话）：
/// 「AI 这个窗口做出来后要切换回去才能再看到效果」——右侧那块是三个互斥页签
/// （单标签 / 整版拼版 / AI 助手），AI 落地一版就得切页签才看得见结果。
/// 拆成独立窗口后两边同时可见，"切回去"这一步就没了；用户还要求「这个窗口也可以拼到别处」，
/// 所以收回必须和拆出一样是一下的事。</para>
/// <para><strong>为什么是"搬"而不是"复制一份"</strong>：WPF 里一个元素只能有一个视觉父，
/// 复制一份等于做第二套预览与第二套 AI 面板（两处状态早晚对不上，§五-22 那一类）。
/// 所以这里先把内容从原宿主摘干净（<see cref="ContentControl.Content"/> 置 null），
/// 再挂到浮动窗口上——<strong>全程只有一个实例</strong>，页签选中项、滚动位置、会话内容都不丢。</para>
/// <para><strong>关掉浮动窗口 = 自动搬回主窗口</strong>：内容跟着窗口一起消失是不可接受的降级
/// （那等于把用户的会话与预览扔掉），所以 <see cref="Window.Closed"/> 里收回。</para>
/// </summary>
public sealed class DetachablePanel
{
    private readonly ContentControl _home;
    private readonly UIElement _content;
    private readonly string _title;
    private Window? _float;

    /// <summary>浮动窗口的默认宽度（首次拆出时用；之后由用户拖）。</summary>
    public const double DefaultFloatWidth = 900;

    /// <summary>浮动窗口的默认高度。</summary>
    public const double DefaultFloatHeight = 700;

    /// <summary>
    /// 可选：取上次记住的浮动窗口几何（全 0 / null 表示没记过）。
    /// <para>为什么需要它（用户 2026-09-09）：上一版的浮动窗永远从 900×700 贴着主窗开，
    /// 他拉到多大、摆在哪，下次拆窗又得重摆一次。</para>
    /// </summary>
    public Func<(double Left, double Top, double Width, double Height)?>? ReadGeometry { get; set; }

    /// <summary>可选：把浮动窗口几何写回状态。只在收回/关窗那一次写，不跟着拖动每像素写盘。</summary>
    public Action<double, double, double, double>? WriteGeometry { get; set; }

    /// <summary>当前飘着的那个窗口的几何（没拆出去时 null）。给单测与“收回前抄一份”用。</summary>
    public (double Left, double Top, double Width, double Height)? FloatingGeometry
        => _float is null ? null : (_float.Left, _float.Top, _float.Width, _float.Height);

    /// <param name="home">内容"家里"的位置：主窗口里那个 ContentControl。</param>
    /// <param name="content">要搬走的那块内容（一个 UIElement 实例，不许两处持有）。</param>
    /// <param name="title">浮动窗口标题。</param>
    public DetachablePanel(ContentControl home, UIElement content, string title)
    {
        _home = home ?? throw new ArgumentNullException(nameof(home));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _title = title ?? string.Empty;
        _home.Content = _content;
    }

    /// <summary>现在是在外面飘着，还是在家里。</summary>
    public bool IsDetached { get; private set; }

    /// <summary>当前那个浮动窗口（没拆出去时 null）。给测试与"聚焦已有窗口"用。</summary>
    public Window? FloatingWindow => _float;

    /// <summary>拆出去 / 收回时通知宿主（宿主拿它切换按钮文字与原位提示）。</summary>
    public event Action? StateChanged;

    /// <summary>点一下那个按钮：飘着就收回，在家就拆出去。</summary>
    public void Toggle()
    {
        if (IsDetached) Dock();
        else Float();
    }

    /// <summary>把内容搬到一个新的独立窗口里并显示出来。已经在外飘着时什么都不做（幂等）。</summary>
    public void Float()
    {
        if (IsDetached) return;

        var owner = Window.GetWindow(_home);
        // 先把家里那份引用摘干净再挂过去：不摘的话家里与浮动窗口同时指着一块内容（单测
        // 「拆出去后家里不再持有」就是钉这一条）。WPF 会不会当场抛「已是另一元素逻辑子」取决于
        // 当时是否已建立视觉父（本机实测：未渲染的 ContentControl 不抛）——**不赌它，一律先摘**。
        _home.Content = null;

        var win = new Window
        {
            Title = _title,
            Owner = owner,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = DefaultFloatWidth,
            Height = DefaultFloatHeight,
            MinWidth = 520,
            MinHeight = 420,
            FontFamily = _home.FontFamily,
            FontSize = _home.FontSize,
            Content = _content,
        };
        // 先试上次记住的那份；拿不到（没记过、或小到不像话、或不在任何屏幕里）才贴主窗开默认尺寸。
        if (!RestoreGeometry(win)) PlaceBesideOwner(win, owner);
        // 关窗 = 收回。先改状态再 Close，避免 Closed 回调再走一遍 Dock。
        win.Closed += (_, _) => Dock();
        _float = win;
        IsDetached = true;
        win.Show();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 把记住的位置与大小接回窗口。返回 false 表示没用上（调用方退回默认摆位）。
    /// <para>两道护栏：尺寸不得小于窗口自己的 MinWidth/MinHeight（旧状态里存个坏数不能让窗打不开）；
    /// 而且窗口左上角必须落在某块屏幕的工作区内（拔了副屏后不能把窗开到看不见的地方）。</para>
    /// </summary>
    private bool RestoreGeometry(Window win)
    {
        var saved = ReadGeometry?.Invoke();
        if (saved is not { } g) return false;
        if (g.Width < win.MinWidth || g.Height < win.MinHeight) return false;
        if (double.IsNaN(g.Left) || double.IsNaN(g.Top)) return false;
        if (!OnAnyScreen(g.Left, g.Top, g.Width, g.Height)) return false;
        win.Left = g.Left;
        win.Top = g.Top;
        win.Width = g.Width;
        win.Height = g.Height;
        return true;
    }

    /// <summary>窗口（左上角那一块）是不是还在屏幕范围内。虚拟桌面坐标可以为负，所以不能只比 &gt;= 0。
    /// <para>说清它有多强：WPF 只暴露主屏工作区（<see cref="System.Windows.SystemParameters.WorkArea"/>），
    /// 要逐屏精判得 P/Invoke <c>EnumDisplayMonitors</c>——为一条提示不值得。所以这一条只是
    /// 「挡掉坏数与拔副屏后跑得太远」的护栏（宽容区 6000 DIP，足够盖住一屏 5120 宽），不是逐屏判定。</para>
    /// </summary>
    private static bool OnAnyScreen(double left, double top, double width, double height)
    {
        var primary = System.Windows.SystemParameters.WorkArea;
        const double slack = 6000;
        var x = left + Math.Min(40, width / 2);       // 拿左上角那一块作代表，拖出屏外的窗角不算丢
        var y = top + Math.Min(40, height / 2);
        return x >= primary.Left - slack && x <= primary.Right + slack
               && y >= primary.Top - slack && y <= primary.Bottom + slack;
    }

    /// <summary>收回/关窗前把几何写回状态（只存得回来的那份，拿不到就什么都不做）。</summary>
    private void SaveGeometryQuietly(Window win)
    {
        if (WriteGeometry is not { } write) return;
        try
        {
            // 最大化/最小化时 Left/Top 是还原后的值，但 Width 是屏幕宽；那种状态下存下来会越存越偏，直接跳过。
            if (win.WindowState != WindowState.Normal) return;
            write(win.Left, win.Top, win.Width, win.Height);
        }
        catch (Exception)
        {
            // 记不住窗口的摆位是可接受的降级，不是故障：下次回到默认摆位就是了，不弹框。
        }
    }

    /// <summary>把内容搬回主窗口（浮动窗口随之关闭）。没拆出去时什么都不做（幂等）。</summary>
    public void Dock()
    {
        if (!IsDetached) return;

        IsDetached = false;
        var win = _float;
        _float = null;
        if (win is not null)
        {
            // 先把几何存下来（趁窗口还在），再把内容摘走：顺序反了就拿不到用户摆的那一份了。
            SaveGeometryQuietly(win);
            // 先把自己的内容摘走，窗口里就不能再留一份引用（否则销毁时可能连带把内容一起清掉）。
            win.Content = null;
            try { win.Close(); } catch (InvalidOperationException) { /* 已经在关闭流程里（例如 Closed 回调本身） */ }
        }
        _home.Content = _content;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 首次拆出时贴着主窗口右侧放；屏幕放不下就退回主窗口左上角偏一点，绝不飘到看不见的地方。
    /// </summary>
    private static void PlaceBesideOwner(Window win, Window? owner)
    {
        if (owner is null) return;     // 单测里没有主窗口，交给系统默认位置
        var left = owner.Left + owner.Width - win.Width / 3;
        var top = owner.Top + 24;
        var area = System.Windows.SystemParameters.WorkArea;
        win.Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - win.Width));
        win.Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - win.Height));
    }
}
