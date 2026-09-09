using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LabelGou.Core.Docking;

namespace LabelGou.App.Services;

/// <summary>
/// 把一块界面内容在「主窗口里的某个泊位」与「独立浮动窗口」之间整体搬移：拆得下来，也拼得回去。
/// <para><strong>为什么要有它</strong>（M7 第 16 棒，用户 2026-09-08 的原话）：
/// 「AI 这个窗口做出来后要切换回去才能再看到效果」——右侧那块是三个互斥页签
/// （单标签 / 整版拼版 / AI 助手），AI 落地一版就得切页签才看得见结果。
/// 拆成独立窗口后两边同时可见，"切回去"这一步就没了；用户还要求「这个窗口也可以拼到别处」，
/// 所以收回必须和拆出一样是一下的事。</para>
/// <para><strong>为什么第 17 棒第二版给它加了泊位（<see cref="DockSite"/>）</strong>：用户 2026-09-09 第三次纠正
/// 「把这个 AI 窗口长按拖动可以拆下来，然后拉到右侧可以吸附」——「吸到右侧」意味着<strong>家里不止一个位置</strong>
/// （右下角那一行 + 右侧那一栏），所以这里从「一个 home」改成「一组 home + 当前停在哪个」，
/// 拖拽与按钮都只调这里的 <see cref="Float"/> / <see cref="Dock(DockSite)"/>，不开第二套搬移实现。</para>
/// <para><strong>为什么是"搬"而不是"复制一份"</strong>：WPF 里一个元素只能有一个视觉父，
/// 复制一份等于做第二套预览与第二套 AI 面板（两处状态早晚对不上，§五-22 那一类）。
/// 所以这里先把内容从原宿主摘干净（<see cref="ContentControl.Content"/> 置 null），
/// 再挂到浮动窗口上——<strong>全程只有一个实例</strong>，页签选中项、滚动位置、会话内容都不丢。</para>
/// <para><strong>关掉浮动窗口 = 自动搬回上次那个泊位</strong>：内容跟着窗口一起消失是不可接受的降级
/// （那等于把用户的会话与预览扔掉），所以 <see cref="Window.Closed"/> 里收回。</para>
/// </summary>
public sealed class DetachablePanel
{
    private readonly Dictionary<DockSite, ContentControl> _homes = new();
    private readonly UIElement _content;
    private readonly string _title;
    private readonly FontFamily? _fontFamily;
    private readonly double _fontSize = SystemFonts.MessageFontSize;
    private Window? _float;

    /// <summary>浮动窗口的默认尺寸（首次拆出且状态里没记过时用；之后由用户拖）。</summary>
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

    /// <param name="home">内容默认的泊位（主窗口里那个 ContentControl；本棒之前它是唯一的家）。</param>
    /// <param name="content">要搬走的那块内容（一个 UIElement 实例，不许两处持有）。</param>
    /// <param name="title">浮动窗口标题。</param>
    public DetachablePanel(ContentControl home, UIElement content, string title)
    {
        ArgumentNullException.ThrowIfNull(home);
        _content = content ?? throw new ArgumentNullException(nameof(_content));
        _title = title ?? string.Empty;
        // 浮动窗的字体沿用家里那一份（不递就会退回系统默认，拆出去前后一个字都不变才叫「搬」而不是「重建」）。
        _fontFamily = home.FontFamily;
        _fontSize = home.FontSize;
        AddDockSite(DefaultSite, home);
    }
    
    /// <summary>构造时递进来的那个泊位（第 16 棒第二版以来的右下角那一行）。</summary>
    public const DockSite DefaultSite = DockSite.Bottom;
    
    /// <summary>现在内容在哪个泊位（飘着时是 <see cref="DockSite.Float"/>）。</summary>
    public DockSite Site { get; private set; } = DefaultSite;
    
    /// <summary>上次停靠的泊位：关窗、点「收回」时按它复原，不会把右栏停靠的人扔回底部。</summary>
    public DockSite LastDockedSite { get; private set; } = DefaultSite;
    
    /// <summary>某个泊位上的宿主（没登记过返回 null）。给主窗口同步可见性与行列宽用。</summary>
    public ContentControl? HostAt(DockSite site) => _homes.GetValueOrDefault(site);
    
    /// <summary>
    /// 登记一个泊位。<see cref="DockSite.Float"/> 不是泊位（那是浮动窗口），递进来直接拒。
    /// <para>同一个泊位登记两次当场抛：那意味着界面上有两块地方自称是 AI 的家，早晚对不上。</para>
    /// </summary>
    public DetachablePanel AddDockSite(DockSite site, ContentControl host)
    {
        if (site == DockSite.Float) throw new ArgumentException("浮动不是泊位，别往这里登记", nameof(site));
        ArgumentNullException.ThrowIfNull(host);
        if (_homes.ContainsKey(site)) throw new InvalidOperationException($"泊位 {site} 已经登记过了");
        _homes[site] = host;
        // 只有「现在就该停在这个泊位」的那一个才拿到内容：后登记的泊位必须留着空，
        // 否则同一块内容会被两个宿主同时指着（那正是这个类存在的理由）。
        if (Site == site && host.Content is null) host.Content = _content;
        return this;
    }
    
    /// <summary>现在是在外面飘着，还是在家里。</summary>
    public bool IsDetached => Site == DockSite.Float;

    /// <summary>当前那个浮动窗口（没拆出去时 null）。给测试与"聚焦已有窗口"用。</summary>
    public Window? FloatingWindow => _float;

    /// <summary>被搬的那块内容本体（拖拽控制器要靠它量「面板左上角在屏幕哪儿」，好让抓点粘在光标下）。</summary>
    public UIElement Content => _content;

    /// <summary>浮动窗的基准标题（拖拽控制器靠它往上拼「→ 松手吸成右栏」那句预告，落点消失时再擦掉）。</summary>
    public string Title => _title;

    /// <summary>拆出去 / 收回时通知宿主（宿主拿它切换按钮文字与原位提示）。</summary>
    public event Action? StateChanged;

    /// <summary>点一下那个按钮：飘着就收回上次那个泊位，在家就拆出去。</summary>
    public void Toggle()
    {
        if (IsDetached) Dock();
        else Float();
    }

    /// <summary>把内容搬到一个新的独立窗口里并显示出来（沿用上次记住的位置，没记过就贴主窗）。已经飘着时幂等。</summary>
    public void Float() => FloatAt(null);

    /// <summary>
    /// 拆出去，并且**由调用方指定落在屏幕哪**（拖拽拆出用：窗口左上角 = 光标位置 − 抓握偏移，
    /// 于是那块面板原地跟着手走，而不是先跳到默认位置再被拖走）。递 null = 走 <see cref="Float"/> 那套默认摆位。
    /// </summary>
    public void FloatAt((double Left, double Top)? atScreenDip, (double Width, double Height)? sizeDip = null)
    {
        if (IsDetached) return;

        var home = HostAt(Site);
        var owner = home is null ? null : Window.GetWindow(home);
        // 只拿「已经上过屏」的主窗当 Owner：WPF 不许把 Owner 设给一个从未 Show 过的窗（当场抛
        // InvalidOperationException，本轮单测真撞上过），而这种窗上拆窗本来也无从发生——宁可不认这个主窗。
        if (owner is not null && !owner.IsLoaded) owner = null;
        // 先把家里那份引用摘干净再挂过去：不摘的话家里与浮动窗口同时指着一块内容（单测
        // 「拆出去后家里不再持有」就是钉这一条）。WPF 会不会当场抛「已是另一元素逻辑子」取决于
        // 当时是否已建立视觉父（本机实测：未渲染的 ContentControl 不抛）——**不赌它，一律先摘**。
        ClearAllHomes();

        var win = new Window
        {
            Title = _title,
            Owner = owner,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = sizeDip?.Width ?? DefaultFloatWidth,
            Height = sizeDip?.Height ?? DefaultFloatHeight,
            MinWidth = 520,
            MinHeight = 420,
            FontFamily = _fontFamily,
            FontSize = _fontSize,
            Content = _content,
        };
        if (atScreenDip is { } spot)
        {
            win.Left = spot.Left;
            win.Top = spot.Top;
        }
        // 先试上次记住的那份；拿不到（没记过、或小到不像话、或不在任何屏幕里）才贴主窗开默认尺寸。
        else if (!RestoreGeometry(win)) PlaceBesideOwner(win, owner);
        // 关窗 = 收回。先改状态再 Close，避免 Closed 回调再走一遍 Dock。
        win.Closed += (_, _) => Dock();
        _float = win;
        Site = DockSite.Float;
        win.Show();
        StateChanged?.Invoke();
    }

    /// <summary>把内容从所有泊位上摘干净（拆出去与换泊位都得先做这一步，否则两处同时指一块内容）。</summary>
    private void ClearAllHomes()
    {
        foreach (var host in _homes.Values) host.Content = null;
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

    /// <summary>把内容搬回上次停靠的泊位（浮动窗口随之关闭）。没拆出去时什么都不做（幂等）。</summary>
    public void Dock() => Dock(LastDockedSite);

    /// <summary>
    /// 把内容搬到指定泊位并关掉浮动窗。
    /// <para>返回 false = 那个泊位根本没登记（或正停在一个没登记的位子上）。<strong>调用方必须据此退回浮动</strong>，
    /// 不许静默把人丢到另一个泊位去——那等于替他做了选择。</para>
    /// </summary>
    public bool Dock(DockSite site)
    {
        var target = HostAt(site);
        if (target is null) return false;
        if (!IsDetached && Site == site) return true;      // 已经在那儿了（幂等）

        var previous = Site;
        Site = site;                                        // 先改状态再 Close，避免 Closed 回调再走一遍 Dock
        LastDockedSite = site;
        if (previous == DockSite.Float)
        {
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
        }
        ClearAllHomes();
        target.Content = _content;
        StateChanged?.Invoke();
        return true;
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
