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
        PlaceBesideOwner(win, owner);
        // 关窗 = 收回。先改状态再 Close，避免 Closed 回调再走一遍 Dock。
        win.Closed += (_, _) => Dock();
        _float = win;
        IsDetached = true;
        win.Show();
        StateChanged?.Invoke();
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
