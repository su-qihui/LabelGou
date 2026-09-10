using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LabelGou.App.Services.Recognition;
using LabelGou.Core.Data;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;
using LabelGou.Core.Templates;
using Microsoft.Win32;

namespace LabelGou.App.Services;

/// <summary>一张准备发给模型的图（已降采样与编码）。</summary>
/// <param name="Base64">不含 data: 前缀的 base64。</param>
/// <param name="MimeType">真实 MIME（写错会被严格的云端拒收）。</param>
/// <param name="Name">给人看的那一句（如「贴在原表第 3 行、第 12 列那一格起，88 KB」），日志与界面用。</param>
public sealed record AiChatImage(string Base64, string MimeType, string Name);

/// <summary>
/// 要 AI 排版时得先知道「这张表里真有什么、标签多大」——这些只有主界面知道，所以由它端过来。
/// <para><paramref name="Columns"/> 与 <paramref name="Portrait"/> 是第 15 棒加的：<strong>整张表</strong>的画像（含没连上字段的列）。
/// 只递已连字段会让「先做了自动绑定」把 AI 的视野锁死在绑对的那几列上（用户 2026-09-08 点出的根因）。</para>
/// <para><paramref name="SheetImages"/> 与 <paramref name="TemplateHasArtwork"/> 是第 20 棒加的：表里贴的效果照片
/// 与当前模板自带的底稿。第 20 棒曾定「没参照就不得造」，<strong>第 38 棒撤了那道闸</strong>（三道闸口径）：
/// 现在没参照也发请求，但面板得知道有没有参照——要声明的是「这一版是猜的」，不是不许排。</para>
/// <para><paramref name="CellFormats"/> 是第 39 棒加的：表里逐格量到的字号/粗体/居中。
/// <strong>它不发出去</strong>——发给模型的是画像里那段大白话；这一份是软件自己留着<strong>算</strong>的，
/// 模型回提案时由 <c>RowFormatEvidence</c> 照它把标签上每行字该多大定下来，不再用模型填的数。
/// CSV 与读不出格式的场合是 null（那就照旧用模型填的兜底）。</para>
/// </summary>
public sealed record AiLayoutContext(
    IReadOnlyList<(string Key, string Name, string Sample)> Fields,
    double WidthMm,
    double HeightMm,
    string? Note,
    IReadOnlyList<ColumnPortrait>? Columns = null,
    string? Portrait = null,
    IReadOnlyList<AiChatImage>? SheetImages = null,
    bool TemplateHasArtwork = false,
    IReadOnlyList<string>? SheetSpecNames = null,
    int RawRowCount = 0,
    int CurrentHeaderRow = 0,
    IReadOnlyList<CellFormat>? CellFormats = null)
{
    /// <summary>能问的东西有没有：已连字段与整表画像一个都没才算真的没得可给（第 15 棒：不能再把「没连上字段」当门槛）。</summary>
    public bool HasAnythingToAsk => (Fields is { Count: > 0 }) || !string.IsNullOrWhiteSpace(Portrait);

    /// <summary>
    /// 这张表到底有没有可对照的实物长相：表里贴的图、当前模板的底稿/图片元素、或用户这一条附的照片。
    /// <para>第 20~37 棒它是硬闸（三样都没就不发请求）；<strong>第 38 棒降为声明用的事实</strong>：
    /// 没参照照样排，但面板与提示词都要据此明说「这一版是猜的」，不许拿猜的装成照过参照排的。</para>
    /// </summary>
    public bool HasVisualReference(bool attachedPhoto) =>
        SheetImages is { Count: > 0 } || TemplateHasArtwork || attachedPhoto;
}

/// <summary>
/// 「AI 助手」面板：和模型聊 + 让 AI 出一版排版 + 按这版去打印。
/// <para>为什么抽成面板（M7 第 11 棒）：第 10 棒把对话做成了独立窗口，用户当天的话是
/// 「这个 AI 界面不应该藏起来，应该显示出来」——<strong>飘在外面的窗口对主流程来说仍然是藏</strong>。
/// 现在它是主窗口右侧第三个常驻页签，而独立窗口只是同一个面板换个壳（<see cref="AiChatWindow"/>），
/// <strong>不开第二份聊天代码</strong>。</para>
/// <para>约束口径（第 38 棒重分类后的三道闸，旧「必须人点用这个」的红线已于第 33 棒翻为直接落地+逐步撤回）：
/// AI 出的版式要过<strong>出纸闸</strong>（<see cref="RowLayoutSpec.Build"/> 排不出返 null、<see cref="TemplateValidator"/> 有 Error 拒入库、
/// 全空版拒落地），落地后靠<strong>可退闸</strong>兑底（逐步撤回）；打印仍走既有的 ⑤ 那一条命令与复核闸门，
/// 这里不开第二条出纸路（§五-22）。</para>
/// </summary>
public sealed class AiChatPanel : UserControl
{
    private static readonly Brush WarnBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E)));
    private static readonly Brush OkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)));

    /// <summary>
    /// 静态画刷<strong>必须冻结</strong>：Freezable 有线程亲和，未冻结的静态画刷被两个 STA 线程同时用时，
    /// WPF 会在 <c>Freezable.AddContextToList</c> 里抛 <c>IndexOutOfRangeException</c>——
    /// 而且崩在**别人的构造函数**里，从堆栈上根本看不出跟这条画刷有关系。
    /// <para>2026-09-10 阶段 29 第 1 棒真踩到：<c>AiAssistantPanelTests</c> 单跑 11/11 绿，
    /// 与新增的测试类并行跑就随机崩在 <c>AiChatPanel..ctor</c>（新坑 §五-127）。
    /// 同一条规矩 <c>RenderRules</c> / <c>TemplateEditorControl</c> / <c>LabelRenderer</c> 早就有了，这里漏了。</para>
    /// </summary>
    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    private readonly TextBlock _channelLine = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    // 用户 2026-09-09 拿截图圈这里：「<strong>AI 回复出来的窗口这么小？</strong>」。
    // 真相不是没地方放，而是这一块的 chrome（通道行 + 红字警告 + 两排按钮 + 输入框 + 脚注）
    // 全占固定高度，而对话区是唯一那个 Star 行 —— 面板一矮，被挤到只剩一条缝的就是它。
    // 所以：① 对话区给了 MinHeight，再矮也不许它变成一条缝；② 上面那些零碎从 7 行压到 4 行。
    private readonly TextBox _transcript = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        // 自己不再滚：整块内容由 _contentScroll 统一滚（第 33 棒），两层滚动条会让人不知道该拖哪一条。
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        // 横向留着：WPF 的 Wrap **不会在"单词"中间断行**，不可断的长 token（端点 URL、报错里的地址）会超出可视宽度
        // 再也看不到。Wrap + 横向 Auto 只为这种行给滚动条，正常换行时它不出现（用户 2026-09-10：「滚也不行」）。
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 13,
        MinHeight = 160,
        Margin = new Thickness(0, 0, 0, 6),
    };

    /// <summary>
    /// 中间那一整块内容（对话 + 思考 + 问题 + 改动卡）的滚动区（第 33 棒重做）。
    /// <para>新内容进来要把它带进视野：对话行滚到底、改动卡片直接 BringIntoView——
    /// 否则"它明明回了"和"它就是没看见"永远分不清（用户报了三次的就是这件事）。</para>
    /// </summary>
    private ScrollViewer? _contentScroll;
    private readonly TextBox _input = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Height = 52,
        Margin = new Thickness(0, 4, 0, 4),
        ToolTip = "Ctrl+Enter 发送；Enter 换行。聊天上下文最多带最近 " + AiChatHistory.MaxTurns +
                  " 轮（更早的会省略并在对话里说明），单条最长 " + AiChatHistory.MaxCharsPerTurn +
                  " 字。「让 AI 出一版排版」用的是同一条通道，不另设密钥。",
    };
    private readonly TextBlock _attachment = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    /// <summary>
    /// 等回来的那一句（含已等秒数）。用户 2026-09-09 圈的第一个问题：
    /// 「把图片/表格给 AI 时没有思考过程或等待结果的 UI，会以为卡了」。
    /// <para>所以这一行不是装饰：没动静的那几十秒里，人唯一能怀疑的就是程序死了或图发丢了。</para>
    /// </summary>
    private readonly TextBlock _waitLine = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = OkBrush,
        Visibility = Visibility.Collapsed,
    };

    private readonly System.Windows.Threading.DispatcherTimer _waitTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // ───────── 思考过程（第 31 棒：用户要"边想边滚、可展开可关"） ─────────
    // 为什么它是面板的一部分而不是另开一个窗：他当时正在追"AI 排版为什么这么差"，
    // 思考过程就是判断"它到底有没有读懂这张表"的唯一材料——和等待那一行是同一件事的两面。

    private readonly StackPanel _think = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _thinkTitle = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Button _thinkToggle = new() { Content = "收起", FontSize = 11, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBox _thinkBox = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,   // 思考里会出现长 token，理由同对话区
        FontSize = 12,
        Height = 108,
        Margin = new Thickness(0, 2, 0, 4),
    };

    /// <summary>攒 150ms 再刷一次思考框：思考片是几个字一片来的，逐片写控件会把 UI 线程刷爆。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _thinkTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly StringBuilder _thinkPending = new();
    private bool _thinkCollapsed;

    /// <summary>逐条问题那一块：一条一行，行尾挂 ❌/✅ 两颗小按钮（第 22 棒）。</summary>
    private readonly StackPanel _questions = new() { Visibility = Visibility.Collapsed };

    /// <summary>
    /// 逐条「改哪里 + 原值 → 新值 + ✅/❌」的改动卡（阶段 29 第 1 棒）。
    /// <para>与 <see cref="_questions"/> 是两件事：那份是<em>模型拿不准、要人二选一</em>的问题；
    /// 这份是<em>它打算动手的每一处改动</em>，人逐条放行。</para>
    /// </summary>
    private readonly StackPanel _changes = new() { Visibility = Visibility.Collapsed };

    /// <summary>
    /// **老板拍过板的决定**（第 35 棒）：一条一件，跨轮累积，每一轮请求都带上。
    /// <para>为什么单独存一份而不是只用会话历史：提案与排版那两条路**原来一个字的上下文都不带**，
    /// 于是他答完问题、模型下一轮看到的还是原来那张表那句话——"选择了也是无效的"就是这么来的。
    /// 单独成节还省 token，也不会被闲聊稀释。</para>
    /// </summary>
    private readonly List<string> _decisions = new();

    /// <summary>这一轮提案里他已经答了几条问题（答满就自动重出一版）。</summary>
    private int _questionsAnswered;

    /// <summary>连续自动重跑了几轮（人手点按钮会清零；封顶见 <see cref="MaxAutoReruns"/>）。</summary>
    private int _autoReruns;

    /// <summary>自动重跑的轮次上限：每轮一两分钟 + 一份 token，不许无限自动烧。</summary>
    private const int MaxAutoReruns = 3;

    /// <summary>给单测看：老板拍过的决定（"决定真的回灌给模型了"那条链的证据）。</summary>
    public IReadOnlyList<string> Decisions => _decisions;

    private DateTime _waitSince;
    private string _waitingFor = "AI";
    private readonly Button _send = new() { Content = "发送（Ctrl+Enter）", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _stop = new() { Content = "停止", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _attach = new() { Content = "附上图片…", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _detach = new() { Content = "去掉图", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _askProposal = new() { Content = "读这张表并提案", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _askLayout = new() { Content = "让 AI 出一版排版", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _applyLayout = new() { Content = "用这个（存成我的模板并选中）", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _print = new() { Content = "按这版去打印", Padding = new Thickness(12, 6, 12, 6) };

    private readonly List<AiChatTurn> _turns = new();

    /// <summary>本次要附的图（base64 + 真实 MIME）：只随下一条消息发出去，发完就清，不每轮重发。</summary>
    private (string Base64, string MimeType, string Name)? _image;

    /// <summary>一次最多发几张图（云端按时长与流量收费，而且一张表贴十几张时只有靠前那几张才是本次的样张）。</summary>
    public const int MaxImagesPerRequest = 4;

    /// <summary>AI 刚出的那版方案，等人点头。没点「用这个」之前它不落盘、不进模板库、不进预览。</summary>
    private RowLayoutSpec? _pending;

    /// <summary>
    /// 整份提案（表头行/合计行/版式/纸规），同样等人点头才落地（第 21 棒）。
    /// <para>与 <see cref="_pending"/> 分开存：一份只是模板，另一份还会重切用户的表，
    /// 两者共用一个按钮但不能共用一个待落地对象（错落地一个就是印错货）。</para>
    /// </summary>
    private AiSheetProposal? _pendingProposal;

    /// <summary>上一次问出去时递了哪份表画像：解析回来时要拿它对 <c>{{col:列名}}</c> 折算真表头。</summary>
    private IReadOnlyList<ColumnPortrait>? _lastColumns;

    /// <summary>上一次问出去时那张表的<strong>原表</strong>行数（行号越界的判据）。0 = 还没问过。</summary>
    private int _lastRawRowCount;

    /// <summary>上一次递出去的纸规清单（提案只能从这份里点名）。空 = 还没问过。</summary>
    private IReadOnlyList<string> _lastSpecNames = Array.Empty<string>();

    /// <summary>
    /// 上一次问出去时那张表逐格量到的字号/粗体/居中（第 39 棒）。
    /// <para>为什么要缓存而不是解析时再读一次文件：提案回来那一刻可能是几十秒以后，用户可能已经换了表
    /// （换了表这份就该作废，跟着 <c>_lastColumns</c> 一起刷新才对得上）。null = CSV 或量不到。</para>
    /// </summary>
    private IReadOnlyList<CellFormat>? _lastCellFormats;

    private CancellationTokenSource? _running;
    private RecognitionSettings _settings = RecognitionSettings.Load();

    /// <summary>要 AI 排版前问主界面要素材（连好的字段与标签尺寸）。没挂上时按钮会说实话，不假装能排。</summary>
    public Func<AiLayoutContext?>? GetLayoutContext { get; set; }

    /// <summary>人点了「用这个」才落地：交给主窗口存成用户模板并选中，返回一句结果话。</summary>
    public Func<RowLayoutSpec, (bool Ok, string Message)>? ApplyLayout { get; set; }

    /// <summary>
    /// 人点头后把整份提案交给主窗口落地（重切这张表 + 存版式 + 换纸规）。没挂上时按钮不亮（第 21 棒）。
    /// </summary>
    public Func<AiSheetProposal, (bool Ok, string Message)>? ApplyProposal { get; set; }

    /// <summary>
    /// 只落地「那一条问题」：第 22 棒——用户要的是一行问题配 ❌/✅ 两个按钮，
    /// 而不是一屏文字提醒让他再去别处点（原话：「改之后更乱了」）。
    /// </summary>
    public Func<AiSheetProposal, AiSheetQuestion, bool, (bool Ok, string Message)>? ApplyQuestion { get; set; }

    /// <summary>
    /// 软件**此刻**的状态快照（阶段 29 第 1 棒）：画「原值 → 新值」必须先知道"原来是什么"。
    /// <para>没挂上时改动卡不出现，退回第 21~28 棒那种"只说会改成什么"的文字清单（不静默丢信息）。</para>
    /// </summary>
    public Func<AiChangeContext?>? GetChangeContext { get; set; }

    /// <summary>
    /// 撤回最近一次 AI 改动（第 33 棒：用户改的架构是"AI 判完直接落地 + 可撤回"，
    /// 撤回粒度他选的是**逐步**——可连点，退到上一次读表、再上一次）。
    /// </summary>
    public Func<(bool Ok, string Message)>? UndoAiChange { get; set; }

    /// <summary>手上还有没有可撤回的（撤回那颗按钮据此亮不亮）。</summary>
    public Func<bool>? CanUndoAiChange { get; set; }

    /// <summary>最近一步叫什么（按钮文案说清"退回哪一步"）。</summary>
    public Func<string>? AiUndoLabel { get; set; }

    /// <summary>软件自己数「几枚标签、几张纸」（预览那行用真数，不用模型报的数）。</summary>
    public Func<string?, (int Labels, int Sheets)?>? OutputCounter { get; set; }

    /// <summary>「按这版去打印」= 跳到 ⑤ 并触发既有打印命令。这里不自己开第二条出纸路。</summary>
    public Action? GoPrint { get; set; }

    /// <summary>
    /// 数据的「代数」（MainViewModel.DataGeneration）：发请求前记一份、回来时对一遍，
    /// 不等就作废那轮结果——等待期间换文件/换表/换模板后，旧提案落在新数据上会剔错行（第 23 棒）。
    /// </summary>
    public Func<int>? GetDataGeneration { get; set; }

    /// <summary>当前会话历史（单测要看"回答注入上下文"那条链；主窗口不看它）。</summary>
    public IReadOnlyList<AiChatTurn> Turns => _turns;

    /// <summary>下面三个只读状态给单测与主窗口看：面板能不能发、手上有没有待确认的方案、现在写了什么。</summary>
    public bool IsBusy => _running is not null;

    public bool HasPendingLayout => _pending is not null;

    public string Transcript => _transcript.Text;

    /// <summary>
    /// 顶部那条<strong>拖拽握把</strong>（M7 第 17 棒第二版，用户 2026-09-09 第三次纠正：
    /// 「把这个 AI 窗口长按拖动可以拆下来，然后拉到右侧可以吸附」）。
    /// <para>为什么它是面板的一部分而不是主窗口的一块 chrome：这块内容会在
    /// 底部泊位 / 右栏泊位 / 浮动窗口之间搬（<see cref="DetachablePanel"/>），握把长在面板上，
    /// <strong>拆出去之后才有地方抓回来</strong>——挂在主窗标题栏上的那种把手一拆就没了。</para>
    /// <para>主窗口负责往它身上接鼠标事件（<c>PanelDragController</c>）；这里只把它做出来、不自己搬自己。</para>
    /// </summary>
    public Border DragGrip { get; private set; } = null!;

    /// <summary>握把那一行：一个拖拽把手该有的样子（≡ 图标 + 一句怎么用 + 十字移动光标）。</summary>
    private static Border BuildDragGrip()
    {
        var grip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF7)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD3, 0xDA)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.SizeAll,
            ToolTip = "按住这一条拖动：拖出主窗口 = 拆成独立窗口（跟着手走）；" +
                      "拖到主窗右缘 = 停靠成右侧一栏（预览在左、AI 在右）；拖到主窗下缘 = 回到底部原位。\n" +
                      "不想用拖的：右上角那个按钮与「视图」菜单是同一个动作。",
        };
        // 上下两行而不是一行横排：右栏那一档只有 420 DIP 宽（第 19 棒的新默认），横排这一句会被裁断，
        // 而被裁掉的恰好是后半句「拖到下缘 = 回底部」——那正是他唯一还没试过的落点（本棒真图上量到的）。
        var line = new StackPanel { Orientation = Orientation.Vertical };
        line.Children.Add(new TextBlock
        {
            Text = "≡ AI 助手",
            FontWeight = FontWeights.SemiBold,
        });
        line.Children.Add(new TextBlock
        {
            Text = "按住这里拖：拖出主窗 = 独立窗口 · 拖到右缘 = 吸成右栏 · 拖到下缘 = 回底部",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        grip.Child = line;
        return grip;
    }

    public AiChatPanel()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 0：拖拽握把（拆窗 / 吸附的唯一手势入口）
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 1：通道行
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });    // 2：内容区（对话 + 思考 + 问题 + 卡片，整块可滚）
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 3：空（内容区吃掉了它，保留行号免得下面几行的号要全改）
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 4：一排按钮（聊天 + 排版）
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 5：输入框与一行脚注

        DragGrip = BuildDragGrip();
        Grid.SetRow(DragGrip, 0);
        root.Children.Add(DragGrip);

        var openSettings = new Button { Content = "打开通道设置…", Padding = new Thickness(10, 4, 10, 4) };
        openSettings.Click += (_, _) =>
        {
            new AiDebugWindow { Owner = Window.GetWindow(this) }.ShowDialog();
            RefreshChannel();     // 设置窗里改了通道/密钥，回来那一行得马上是真的，不能等用户点发送
        };
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(openSettings, Dock.Right);
        header.Children.Add(openSettings);
        header.Children.Add(_channelLine);
        Grid.SetRow(header, 1);
        root.Children.Add(header);

        // 红线那句话从两行压成一行 + ToolTip：它是本面板最不该被误删的一句实话（聊与问不会自动改唛头），
        // 但用户圈的是「回复区太小」——那就不该拿两句加粗红字去占对话区的位置。
        var notice = new TextBlock
        {
            Text = "聊与问都不会自动改唛头：AI 排的版要点「用这个」才进模板库，没经你核对的值不进打印。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Foreground = WarnBrush,
            ToolTip = "AI 排的版要你先点「用这个」才会进模板库，中间还有一道校验拦着；" +
                      "没经你核对的值一律不进打印。打印走的还是 ⑤ 那一条命令与复核闸门。",
        };

        // 对话区不单独挂了：它和下面那块（思考/问题/卡片）**同住一个滚动区**，见下面 _contentScroll。

        Grid.SetRow(_waitLine, 3);
        // 等的那一句、附图那一行、逐条问题同一格堆着：窄栏里多一行固定高就是从对话区扣一块，StackPanel 只在需要时占高。
        // 思考那一块的标题行：一句话 + 一颗「收起/展开」（用户要的"可以选择展开或者关闭"）。
        var thinkHead = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        thinkHead.Children.Add(_thinkTitle);
        thinkHead.Children.Add(_thinkToggle);
        _think.Children.Add(thinkHead);
        _think.Children.Add(_thinkBox);
        _thinkToggle.Click += (_, _) => ToggleThinking();
        _thinkTimer.Tick += (_, _) => FlushThinking();

        var status = new StackPanel();
        status.Children.Add(_waitLine);
        status.Children.Add(_think);
        status.Children.Add(_questions);
        status.Children.Add(_changes);
        status.Children.Add(_attachment);

        // 第 32 棒：这一块（思考 + 问题 + 改动卡 + 附图状态）是会长的，以前无界增长，
        // 把下面那一排按钮、输入框与脚注**顶出可视区**，而面板本身不滚 —— 用户 2026-09-10 的原话是
        // 「输出结果存在下面遮挡看不到」。修法：给它**封顶 + 自己滚**（上限跟面板高度走，见下面的 SizeChanged），
        // 让对话区（唯一的 Star 行）去吸收剩余高度 —— 这样按钮与输入框**仍然钉在底部**。
        // 为什么不做"整页滚"：那会把输入框也滚走，手感更差；"上划能看见"这个目的，这一块自己滚已经达到。
        // ───────── 内容区：对话 + 思考 + 问题 + 卡片，**整块一个滚动**（第 33 棒重做） ─────────
        // 用户 2026-09-10 报了三次「上面的内容被下面的 UI 遮挡、看不到」：
        // 前两次我都在局部打补丁（① 横向裁切 → 修窗口重夹；② 给状态块封顶 + 自己滚），**都没解决根本**。
        // 根因是这一块的结构：对话区是唯一的 Star 行，而下面那几行（按钮/输入框/脚注）是 Auto——
        // 一旦内容比面板高，Star 行就被压到 MinHeight，多出来的部分被裁掉，而**面板本身不会滚**。
        // 现在改成聊天软件那种形状：**中间一整块内容自己滚，底部那一排（按钮 + 输入框）钉住不动**。
        // 于是"看不到的东西"在物理上不存在了——凡是有内容的都在这个滚动区里。
        var content = new StackPanel();
        content.Children.Add(_transcript);
        content.Children.Add(status);
        _contentScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,   // 横向不滚：内容自己换行（第 31 棒那条教训）
            Content = content,
        };
        Grid.SetRow(_contentScroll, 2);
        Grid.SetRowSpan(_contentScroll, 2);      // 顺带吃掉原来"附图状态"那一行的位置
        root.Children.Add(_contentScroll);

        // 两排按钮合成一排 WrapPanel：窄的时候自己换行，不再固定吃掉两行高。
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        _send.Click += async (_, _) => await SendAsync();
        _stop.Click += (_, _) => _running?.Cancel();
        _attach.Click += (_, _) => PickImage();
        _detach.Click += (_, _) => { _image = null; ShowAttachment(); };
        var clear = new Button { Content = "清空会话", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) => { _turns.Clear(); _transcript.Clear(); Append("会话已清空。"); };
        var copy = new Button { Content = "复制全部", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => { if (_transcript.Text.Length > 0) Clipboard.SetText(_transcript.Text); };
        _askProposal.Click += async (_, _) => await AskProposalAsync();
        _askLayout.Click += async (_, _) => await AskLayoutAsync();
        _applyLayout.Click += (_, _) => ApplyPending();
        _print.Click += (_, _) => PrintNow();
        // 排版那三件与聊天那六件之间插一条竖线：它们不是一类动作（一个是问，一个是拿结果落地）。
        buttons.Children.Add(_send);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_attach);
        buttons.Children.Add(_detach);
        buttons.Children.Add(clear);
        buttons.Children.Add(copy);
        buttons.Children.Add(new Border { Width = 1, Background = Brushes.LightGray, Margin = new Thickness(0, 4, 8, 4) });
        buttons.Children.Add(_askProposal);
        buttons.Children.Add(_askLayout);
        buttons.Children.Add(_applyLayout);
        buttons.Children.Add(_print);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        var bottom = new StackPanel();
        Grid.SetRow(bottom, 5);
        bottom.Children.Add(notice);
        bottom.Children.Add(_input);
        bottom.Children.Add(new TextBlock
        {
            // 长的那段说明已经进了输入框的 ToolTip，这里只留一行真正需要抬头看一眼的。
            Text = "Ctrl+Enter 发送，Enter 换行（悬停在输入框上有完整口径）。",
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 11,
            Foreground = Brushes.Gray,
        });
        root.Children.Add(bottom);

        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                await SendAsync();
            }
        };

        Content = root;
        _waitTimer.Tick += (_, _) => UpdateWaitLine();
        RefreshChannel();
    }

    /// <summary>通道那一行：每次要发东西前都重读一次设置，用户在设置窗里改了这里立刻看得见。</summary>
    public void RefreshChannel()
    {
        _settings = RecognitionSettings.Load();
        var s = _settings;
        var key = $"{s.ApiKeySource}{(s.SavedKeyStatus == SecretStore.Status.Unreadable ? "（存过但这台机解不开，请重填）" : string.Empty)}";
        // 显示真生效值（第 26 棒）；第 27 棒起超时可「不设限」，并把思考档上屏——用户得看得见自己拧到哪一档。
        var limit = s.EffectiveTimeoutSeconds == 0 ? "不限" : $"{s.EffectiveTimeoutSeconds}s";
        var thinking = s.Thinking == RecognitionSettings.ThinkingAuto ? string.Empty : $"　{s.Thinking}";
        _channelLine.Text = $"通道：{s.Provider}　端点：{ShortEndpoint(s.Endpoint)}　模型：{s.Model}（{limit}{thinking}）　" +
                            $"{(s.Provider == RecognitionSettings.Providers.OpenAi ? "订单数据会离开这台电脑" : "本机，不出网")}　{key}";
        // 完整端点进 ToolTip：上屏那截只是主域名（理由见 ShortEndpoint），一个字都不丢。
        _channelLine.ToolTip = string.IsNullOrWhiteSpace(s.Endpoint) ? null : "完整端点：" + s.Endpoint;
        _channelLine.Foreground = s.Provider == RecognitionSettings.Providers.OpenAi ? WarnBrush : OkBrush;
    }

    /// <summary>
    /// 端点上屏只显示主域名：长 URL 在换行文本框里是一个**不可断的"单词"**，会把那一行撑出可视宽度、
    /// 横向也滚不到——用户 2026-09-10 截图里被裁掉的那一截正是它。
    /// <para>完整值一律进 ToolTip，信息不丢；认不出是 URL 的就按长度截断。</para>
    /// </summary>
    private static string ShortEndpoint(string? endpoint)
    {
        var text = endpoint?.Trim() ?? string.Empty;
        if (text.Length == 0) return "（没填）";
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0) return uri.Host;
        return text.Length <= 28 ? text : text[..28] + "…";
    }

    private void ShowAttachment() =>
        _attachment.Text = _image is { } img
            ? $"已附图：{img.Name}（只随下一条消息发出去）"
            : "未附图。要让它看唛头样张就点「附上图片…」。";

    private void PickImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选一张要给模型看的图",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            // 与识别通道同一份降采样口径：长边 2000px 内 + 真实 MIME，手机原图整张 base64 上去就是超时
            var (b64, mime) = ImageForModel.FromFile(dialog.FileName);
            _image = (b64, mime, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            Append($"读图失败：{ex.Message}");
        }
        ShowAttachment();
    }

    private void Append(string text)
    {
        // 任何一条新话都算「上一条提示不再是那句了」：不然用户聊完一轮再撞同一个坑，
        // 会被下面的去重误判成「已经提醒过了」而真的静默。
        _noticeLine = null;
        _noticeHits = 0;
        AppendRaw(text);
    }

    private string? _noticeLine;
    private int _noticeHits;

    /// <summary>
    /// 「挡下没发请求」这类提示：同一句只占一行，再撞只把次数加上去。
    /// <para>用户 2026-09-08 拿 TOP 那张截图圈了这里：连点四次「让 AI 出一版排版」就刷出四行
    /// 一模一样的话，看着像四个不同的问题。但不能只去重不吭声（那更像没点到），所以留一行 + 报次数。</para>
    /// </summary>
    private void AppendNotice(string text)
    {
        if (string.Equals(_noticeLine, text, StringComparison.Ordinal))
        {
            _noticeHits++;
            ReplaceLastLine(NoticeText());
            return;
        }
        _noticeLine = text;
        _noticeHits = 1;
        AppendRaw(NoticeText());
    }

    private string NoticeText() => _noticeLine is null ? string.Empty
        : _noticeHits > 1
            ? $"{_noticeLine}（同一句已挡 {_noticeHits} 次——点了没反应就是被它挡的，不是没收到）"
            : _noticeLine;

    private void AppendRaw(string text)
    {
        _transcript.AppendText(text + Environment.NewLine);
        ScrollTranscriptToEnd();
    }

    /// <summary>把最后一行换掉（只给 <see cref="AppendNotice"/> 用：同一句只占一行，次数就地更新）。</summary>
    private void ReplaceLastLine(string line)
    {
        var all = _transcript.Text.TrimEnd('\r', '\n');
        var cut = all.LastIndexOf('\n');
        _transcript.Text = (cut < 0 ? string.Empty : all.Substring(0, cut + 1)) + line + Environment.NewLine;
        ScrollTranscriptToEnd();
    }

    /// <summary>
    /// 让**刚追加的那一行**露出来（第 33 棒）。
    /// <para>为什么不是 <c>_contentScroll.ScrollToEnd()</c>：整块内容里对话区在上面、思考/卡片在下面，
    /// "滚到最底"看到的是卡片那一块，刚写的那行反而在视野上方之外。
    /// 所以按"对话区底边那一点"去 BringIntoView——这才是聊天软件里"新消息自己滚出来"的手感。</para>
    /// <para>文本框自己不再滚（<c>VerticalScrollBarVisibility=Disabled</c>），它被外层无限高测量，
    /// 于是它会长到全文高度，这一句就是唯一在动的滚动。</para>
    /// </summary>
    private void ScrollTranscriptToEnd()
    {
        if (_transcript.ActualHeight <= 0) return;
        _transcript.BringIntoView(new Rect(0, Math.Max(0, _transcript.ActualHeight - 1), 1, 1));
    }

    private void SetBusy(bool busy)
    {
        _send.IsEnabled = !busy;
        _stop.IsEnabled = busy;
        _input.IsEnabled = !busy;
        _askLayout.IsEnabled = !busy;
        _askProposal.IsEnabled = !busy && GetLayoutContext?.Invoke() is not null;
        if (busy)
        {
            _waitSince = DateTime.Now;
            _waitLine.Visibility = Visibility.Visible;
            _waitTimer.Start();
            UpdateWaitLine();
        }
        else
        {
            _waitTimer.Stop();
            _waitLine.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>这一轮在等什么（发文字 / 读这张表 / 出一版模板），秒数那句就挂谁的名字。</summary>
    private void SetBusy(bool busy, string waitingFor)
    {
        _waitingFor = waitingFor;
        SetBusy(busy);
    }

    private void UpdateWaitLine()
    {
        var secs = Math.Max(0, (int)(DateTime.Now - _waitSince).TotalSeconds);
        _waitLine.Text = $"● {_waitingFor}，已经等了 {secs} 秒…"
            + (secs < 45
                ? "（云端一般几秒到半分钟；本机模型要一两分钟。这段时间里你的表、模板和数据一个字都没动）"
                : "（等得有点久了：可能图太大、模型在冷启动，或通道超时。点「停止」不会改动任何东西，可以再试一次或少附几张图）");
    }

    /// <summary>
    /// 开场给模型的身份说明：把 <see cref="MarkFieldCatalog"/> 的真字段清单与「逐字取值」的规矩带上。
    /// <para>为什么从目录生成而不是手写一段话：字段清单改了，提示词不能悄悄落后。</para>
    /// </summary>
    private static AiChatTurn SystemTurn()
    {
        var sb = new StringBuilder();
        sb.Append("你是外贸纸箱唛头标签助手 LabelGou 的顾问。用户在给纸箱印唛头（箱号、件号、毛净重、体积、")
          .Append("外箱尺寸、原产地、收货人等），软件里的字段清单如下：\n");
        foreach (var d in MarkFieldCatalog.Mappable)
            sb.Append("  - ").Append(d.Key).Append("：").Append(d.ChineseName)
              .Append("（常见表头写法：").Append(string.Join(" / ", d.Aliases.Take(4))).Append("）\n");
        sb.Append("回答要求：简短、给可操作的步骤；涉及数值时提醒用户必须以原始单据为准、要人工核对；")
          .Append("不要编造图上或表里没有的数据，也不要输出毫米坐标。\n")
          .Append("你能调的东西包括：表头在第几行（或这张表没表头）、哪几行是合计行该剔除、小标签尺寸、行式版式、整张纸的纸规与每页枚数——用户 2026-09-09 已把这几样的调整权交给你，但要他点「用这个」才落地。\n")
          .Append("三件必须主动提醒的事，碰到相关场景就开口，不要等用户问：\n")
          .Append("  ① 模板里写死的文字（不是 {{字段}} 那类空位）会跟着模板跑到别家客户头上，换客户时必须逐字核对；\n")
          .Append("  ② 没有表头的工厂表，首行会被当成表头吃掉，那一行就少印一张；要用户确认「表头在第几行/确实没表头」；\n")
          .Append("  ③ 表底部的「合计/TOTAL/小计」行会被当成一条真实货物排进版面，多出一张没意义的唛头。\n")
          .Append("参照优先：看得到效果图、模板截图或底稿时，一律照参照排；表里有把标签字拄抄一遍的那一块，它就是模板。\n")
          .Append("真没有任何参照时，你可以凭表里的列猜一版，但开头必须说清「这一版是无参照猜的」，并提醒用户发一张样张再照排（第 38 棒改的旧红线：从「不许猜」改成「猜了必须自报」）。\n")
          .Append("注意：软件是中文界面，请一律用中文回答。");
        return new AiChatTurn(AiChatTurn.System, sb.ToString());
    }

    private async Task SendAsync()
    {
        if (_running is not null) return;             // 一轮没结束不接第二轮，避免两条回答交错
        var question = _input.Text.Trim();
        if (question.Length == 0)
        {
            Append("先说要问什么。");
            return;
        }
        RefreshChannel();

        _input.Clear();
        _turns.Add(new AiChatTurn(AiChatTurn.User, question));
        Append($"我：{question}");

        var dropped = AiChatHistory.DroppedTurns(_turns);
        if (dropped > 0) Append($"（上下文只带最近 {AiChatHistory.MaxTurns} 轮，已省略前 {dropped} 轮）");

        var payload = new List<AiChatTurn> { SystemTurn() };
        payload.AddRange(AiChatHistory.BuildForRequest(_turns));
        var image = _image;
        _image = null;                                // 图只随这一条发，别每轮重发一遍
        ShowAttachment();

        _running = new CancellationTokenSource();
        SetBusy(true, "AI 在看你这句话");
        try
        {
            var outcome = await SendStreamingAsync(
                payload,
                image is { } one ? new List<(string Base64, string MimeType)> { (one.Base64, one.MimeType) } : null,
                _running.Token);
            if (outcome.Ok)
            {
                _turns.Add(new AiChatTurn(AiChatTurn.Assistant, outcome.Text!));
                Append($"模型（{outcome.Elapsed.TotalSeconds:F1} 秒）：{outcome.Text}");
            }
            else if (_running.IsCancellationRequested)
            {
                // 取消不是失败（§五-27）：不写 ERROR，也不假装模型回了话。
                Append("已停止这一轮，模型的话没有回来，你的问题还留在上面。");
            }
            else
            {
                Append($"没问到回答：{outcome.Error}");
                if (!string.IsNullOrWhiteSpace(outcome.Raw)) Append($"（服务原话：{outcome.Raw}）");
            }
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetBusy(false);
        }
    }

    /// <summary>
    /// 「让 AI 出一版排版」：把这张表真连上的字段与标签尺寸发给模型，要它只回一份 <see cref="RowLayoutSpec"/> 的 JSON。
    /// <para>这条不混进聊天历史：它是一次结构请求，塞进上下文会把后面几轮聊天的口径带偏。</para>
    /// </summary>
    public async Task AskLayoutAsync()
    {
        if (_running is not null) return;
        var ctx = GetLayoutContext?.Invoke();
        if (ctx is null)
        {
            Append("现在问不了：先走到 ① 导入数据、② 连上字段，AI 才知道这张表里真有什么。");
            return;
        }
        if (!ctx.HasAnythingToAsk)
        {
            AppendNotice("一个字段都没连上，这张表我也拿不到原样（没得可给），AI 排出来的版会是空格子。先回 ② 连接字段（可以点「自动推荐」）；如果点了推荐还是这句，那张表很可能根本没有表头行。");
            return;
        }
        if (ctx.Fields.Count == 0)
            AppendNotice("一个字段都没连上，那就把整张表原样交给它（含没连上的列），让它照样张排——软件不替你猜哪列是什么。");

        // 第 38 棒：「无参照就不发这一趟请求」那道硬闸撤了（用户拍板：只拦三道闸，其余放）。
        // 判据：猜出来的版看得见（预览+只读清单）、退得回（逐步撤回），不赔钱，不配被事前拦；
        // 而拦着它只会制造第 26 棒那种死路（重读一百次也出不来版式）。不拦不等于不声明：
        // 面板这里说给老板听，提示词里说给模型听，两头都得知道这一版是猜的。
        var attached = _image;
        var hasReference = ctx.HasVisualReference(attached is not null);
        if (!hasReference)
            AppendNotice("没有任何参照（表里没贴效果图、模板不带底稿、这条也没附图）——我先凭表里的列名和内容猜一版。"
                + "这一版是猜的不是对照：不满意点「↩ 撤回这一步」；要排准就附上样张图，我再照图出一版。");

        var images = new List<AiChatImage>(MaxImagesPerRequest);
        if (attached is { } shot) images.Add(new AiChatImage(shot.Base64, shot.MimeType, shot.Name));
        var skippedImages = 0;
        foreach (var img in ctx.SheetImages ?? Array.Empty<AiChatImage>())
        {
            if (images.Count >= MaxImagesPerRequest) { skippedImages++; continue; }
            images.Add(img);
        }

        RefreshChannel();
        ClearPending();

        var prompt = RowLayoutPrompt.Build(ctx.Fields, ctx.WidthMm, ctx.HeightMm, ctx.Note, ctx.Portrait, _decisions,
                hasVisualReference: hasReference)
            + (images.Count == 0 ? string.Empty : $"\n本条随附 {images.Count} 张图：它们是这张表里贴的效果照片/模板截图，或用户拍的样张。" +
              "版式必须照图上的行序与字面排，图上没有的行不要造，图与表格文字冲突时以图为准。");
        _lastColumns = ctx.Columns;
        var payload = new List<AiChatTurn>
        {
            new(AiChatTurn.System, "你是唛头行式版式生成器。只输出一个 JSON 对象，不要解释文字、不要 Markdown 围栏、不要毫米坐标。"),
            new(AiChatTurn.User, prompt),
        };
        _image = null;
        ShowAttachment();

        Append($"让 AI 出一版：{ctx.Fields.Count} 个已连字段"
               + (ctx.Columns is { Count: > 0 } cols
                   ? $"、整张表 {cols.Count} 列全给了它（含没连上的）"
                   : "（没拿到整张表，只给了已连字段）")
               + $"、标签 {ctx.WidthMm:0.#}×{ctx.HeightMm:0.#} mm" +
               (images.Count == 0 ? "（没附图，它只能按表里的东西排）"
                                  : $"（附图 {images.Count} 张：表里贴的样张一起发了" +
                                    (skippedImages > 0 ? $"，另有 {skippedImages} 张没发" : string.Empty) + "）") + "…");

        _running = new CancellationTokenSource();
        var generation = GetDataGeneration?.Invoke() ?? -1;
        SetBusy(true, "AI 在照着这张表出一版模板");
        try
        {
            var outcome = await SendStreamingAsync(
                payload, images.Count == 0 ? null : images.Select(x => (x.Base64, x.MimeType)).ToList(), _running.Token);
            if (!outcome.Ok)
            {
                if (_running.IsCancellationRequested) Append("已停止，版式没回来，当前用的模板没被改动。");
                else
                {
                    Append($"没拿到方案：{outcome.Error}");
                    if (!string.IsNullOrWhiteSpace(outcome.Raw)) Append($"（服务原话：{outcome.Raw}）");
                }
                return;
            }

            if (GetDataGeneration is { } readGen && readGen() != generation)
            {
                Append("等待期间表或模板换过了——这一版是照着旧的东西排的，作废（你的数据一个字没动）。要新样子就再点一次。");
                return;
            }
            FeedLayoutAnswer(outcome.Text, outcome.Elapsed.TotalSeconds);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetBusy(false);
        }
    }

    /// <summary>
    /// 把模型原文走一遍「解析 → 骨架 → 校验」，结果摊在面板上等用户点头。
    /// <para>为什么单独拆一个公开方法：整条确认路（包括「不点头就不落地」这条红线）
    /// 得能在不联网的情况下测——只测 HTTP 那一层等于没测用户真正会碰到的东西。</para>
    /// </summary>
    public void FeedLayoutAnswer(string? modelText, double seconds = 0)
    {
        ClearPending();

        var proposal = RowLayoutJsonParser.Parse(modelText, _lastColumns);
        foreach (var note in proposal.Notes) Append($"（已修正：{note}）");
        if (!proposal.HasSpec)
        {
            Append("这份方案不能用：" + string.Join("；", proposal.Errors));
            Append("你现在的模板没被改动。可以点「附上图片…」把厂商样张带上再问一次。");
            return;
        }

        var spec = proposal.Spec!;
        var template = spec.Build();
        if (template is null)
        {
            Append($"这份方案排不进 {spec.WidthMm:0.#}×{spec.HeightMm:0.#} mm：留白与行距把版面吃光了。你现在的模板没被改动。");
            return;
        }
        var issues = TemplateValidator.Validate(template);
        if (issues.HasError())
        {
            Append("校验拦下了这份方案（所以它不会进模板库）：" +
                   string.Join("；", issues.Where(i => i.Severity == IssueLevel.Error).Select(i => i.Message)));
            return;
        }

        _pending = spec;
        Append($"模型给了一版 {spec.Rows.Count} 行的方案（{seconds:F1} 秒），标签 {spec.WidthMm:0.#}×{spec.HeightMm:0.#} mm：");
        for (var i = 0; i < spec.Rows.Count; i++)
        {
            var r = spec.Rows[i];
            Append($"  第 {i + 1} 行 [{(r.Stretch ? "撑满大字" : $"{r.SizePt:0.#}pt")}][{(r.Align == HorizontalAlign.Center ? "居中" : r.Align == HorizontalAlign.Right ? "右" : "左")}] {r.Content}");
        }
        foreach (var warn in issues.Where(i => i.Severity == IssueLevel.Warning))
            Append($"（提醒：{warn.Message}）");
        Append("看一眼：字段对不对、哪行该大该小。点「用这个」才会存成你的模板并选中；不点就什么都不变。");
        _applyLayout.Content = "用这个（存成我的模板并选中）";
        _applyLayout.IsEnabled = true;
    }

    /// <summary>
    /// 面板发出去的请求**统一走这里**（第 31 棒）：流式 + 思考过程实时上屏。
    /// <para>为什么收口成一处：三条路（自由聊天 / 出一版排版 / 读表提案）都要"边想边看"，
    /// 各写一遍就等于三份流式纪律（停止、超时、收尾打点），迟早漏一份。</para>
    /// <para>正文片段刻意不逐字上屏：提案那一枪回的是 JSON，逐字写进对话区纯属噪音——
    /// 正文仍由各条调用方在拿到整段之后按自己的格式呈现。这里只把**思考**摊给人看。</para>
    /// </summary>
    private async Task<ChatOutcome> SendStreamingAsync(
        List<AiChatTurn> payload, List<(string Base64, string MimeType)>? images, CancellationToken token)
    {
        BeginThinking();
        // Progress<T> 在构造它的线程上捕获同步上下文——这里就是 UI 线程，所以片段回来时会回到 UI 线程。
        var reasoning = new Progress<string>(AppendThinking);
        var outcome = await OllamaVisionClient.ChatWithImagesStreamAsync(
            _settings, payload, images, reasoning, onContent: null, token);
        EndThinking(outcome);
        return outcome;
    }

    /// <summary>开始一次流式请求：清空思考框、把它摆出来（**默认展开**——用户要的就是"看它在想什么"）。</summary>
    private void BeginThinking()
    {
        lock (_thinkPending) _thinkPending.Clear();
        _thinkBox.Clear();
        _thinkTitle.Text = "它的思考过程（正在想…）";
        _thinkBox.Visibility = _thinkCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _think.Visibility = Visibility.Visible;
        _thinkTimer.Start();
    }

    /// <summary>
    /// 思考片段到达（**从请求那条线程回来**）：只攒着，不碰控件。
    /// <para>攒 150ms 再刷的理由见 <see cref="_thinkTimer"/>：一次读表两分钟、思考片上千片，
    /// 逐片 AppendText + ScrollToEnd 会把 UI 线程刷爆（那是"界面卡住"的另一种成因）。</para>
    /// </summary>
    private void AppendThinking(string piece)
    {
        lock (_thinkPending) _thinkPending.Append(piece);
    }

    private void FlushThinking()
    {
        string pending;
        lock (_thinkPending)
        {
            if (_thinkPending.Length == 0) return;
            pending = _thinkPending.ToString();
            _thinkPending.Clear();
        }
        _thinkBox.AppendText(pending);
        _thinkBox.ScrollToEnd();
    }

    /// <summary>请求结束：停表、把最后一片刷完，并如实说清这次到底有没有思考内容。</summary>
    private void EndThinking(ChatOutcome outcome)
    {
        _thinkTimer.Stop();
        FlushThinking();
        _thinkTitle.Text = outcome.Reasoning is { Length: > 0 } reason
            ? $"它的思考过程（{reason.Length} 字 · 点右边可以收起）"
            : _thinkBox.Text.Length > 0
                ? "它的思考过程（已结束）"
                : "这次没有思考内容（模型不吐思考，或通道设置里思考档关着）";
    }

    /// <summary>收起 / 展开思考框（用户 2026-09-10 要的"可以选择展开或者关闭"）。</summary>
    private void ToggleThinking()
    {
        _thinkCollapsed = !_thinkCollapsed;
        _thinkBox.Visibility = _thinkCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _thinkToggle.Content = _thinkCollapsed ? "展开" : "收起";
    }

    /// <summary>
    /// 「读这张表并提案」：把整张表（画像 + 贴图 + 纸规清单 + 原表行数）一次交给模型，
    /// 要它回一份「这张表该怎么切、这张纸该怎么摆」的 JSON 提案（第 21 棒）。
    /// <para>与 <see cref="AskLayoutAsync"/> 的分工：那条只要一版模板；这条管切表与选纸——
    /// 用户 2026-09-09 判定这些判断该由看得到整张表的 AI 做，而不是由程序写死规则去猜。</para>
    /// </summary>
    public async Task AskProposalAsync()
    {
        // 人手点的那颗（工具栏 / "下一步"按钮）：自动轮次清零——他自己发起的，就该重新开始算。
        _autoReruns = 0;
        await RunProposalAsync();
    }

    /// <summary>
    /// **带着老板的决定再跑一轮**（第 35 棒：他要的"你收到信息后就知道怎么调整怎么做了 → 自动把排版拍出来"）。
    /// <para>只在他把**这一轮的问题都答完**时才自动跑（答一半就跑等于白花一两分钟）；轮次有上限。</para>
    /// </summary>
    private async Task AskProposalFollowUpAsync()
    {
        if (_autoReruns >= MaxAutoReruns)
        {
            Append($"已经自动重出了 {MaxAutoReruns} 版，先停手（每版要一两分钟、也算一份钱，不自动烧）。"
                 + "想接着试就点上面那颗「让 AI 重出方案（排版 + 绑定列）」。");
            return;
        }
        _autoReruns++;
        Append($"你把这一轮的问题都答完了 —— 带着你的决定重出第 {_autoReruns} 版（不满意可以撤回）。");
        await RunProposalAsync();
    }

    private async Task RunProposalAsync()
    {
        if (_running is not null) return;
        var ctx = GetLayoutContext?.Invoke();
        if (ctx is null)
        {
            Append("现在问不了：先走到 ① 导入数据，AI 才知道这张表里真有什么。");
            return;
        }
        if (!ctx.HasAnythingToAsk)
        {
            AppendNotice("这张表我什么都没拿到（没连字段也没画像），问它也是白问。");
            return;
        }
        RefreshChannel();
        ClearPending();

        var attached = _image;
        var images = new List<AiChatImage>(MaxImagesPerRequest);
        if (attached is { } shot) images.Add(new AiChatImage(shot.Base64, shot.MimeType, shot.Name));
        foreach (var img in ctx.SheetImages ?? Array.Empty<AiChatImage>())
        {
            if (images.Count >= MaxImagesPerRequest) break;
            images.Add(img);
        }
        _image = null;
        ShowAttachment();

        _lastColumns = ctx.Columns;
        _lastRawRowCount = ctx.RawRowCount;
        _lastSpecNames = ctx.SheetSpecNames ?? Array.Empty<string>();
        _lastCellFormats = ctx.CellFormats;

        var prompt = AiSheetProposalPrompt.Build(
            ctx.Portrait ?? "（没拿到整张表画像，只有已连字段）",
            _lastSpecNames, ctx.RawRowCount, ctx.CurrentHeaderRow,
            $"{ctx.WidthMm:0.#}×{ctx.HeightMm:0.#} mm", images.Count, _decisions);
        var payload = new List<AiChatTurn>
        {
            new(AiChatTurn.System, AiSheetProposalPrompt.SystemText),
            new(AiChatTurn.User, prompt),
        };
        Append($"开始读这张表：表里 {_lastRawRowCount} 行，这台机器上有 {_lastSpecNames.Count} 张纸可选，"
               + (images.Count == 0 ? "没带图（那它只能看字）。" : $"带上 {images.Count} 张图。"));

        _running = new CancellationTokenSource();
        var generation = GetDataGeneration?.Invoke() ?? -1;
        SetBusy(true, "AI 在读这张表（行多的表会慢一点）");
        try
        {
            var outcome = await SendStreamingAsync(
                payload, images.Count == 0 ? null : images.Select(x => (x.Base64, x.MimeType)).ToList(), _running.Token);
            if (!outcome.Ok)
            {
                if (_running.IsCancellationRequested) Append("已停止，提案没回来，表与纸规都没动。");
                else
                {
                    Append($"没拿到提案：{outcome.Error}");
                    if (!string.IsNullOrWhiteSpace(outcome.Raw)) Append($"（服务原话：{outcome.Raw}）");
                }
                return;
            }
            if (GetDataGeneration is { } readGen && readGen() != generation)
            {
                Append("等待期间表或模板换过了——这份提案报的是旧表的行号，落在新表上会剔错行，作废（你的数据一个字没动）。要新表的提案就再点一次。");
                return;
            }
            FeedProposalAnswer(outcome.Text, outcome.Elapsed.TotalSeconds);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetBusy(false);
        }
    }

    /// <summary>
    /// 把提案逐条摊在面板上等用户点头（表头行、合计行、版式、纸规各一条，谁不对可以只拒一份）。
    /// <para>跟版式那条一样拆成公开方法：这条「不点头就不改用户的表」的红线要能在不联网的情况下测。</para>
    /// </summary>
    public void FeedProposalAnswer(string? modelText, double seconds = 0)
    {
        ClearPending();
        // 没问过就直喂（单测这条路）时不知道原表行数，那就用 int.MaxValue 让边界检查空转，
        // 而不是编一个看起来很真的行数。
        var rawRows = _lastRawRowCount > 0 ? _lastRawRowCount : int.MaxValue;
        // 第 39 棒：把量到的格式一起交进去——字号/粗细/居中由软件算，不再用模型填的那几个数。
        var proposal = AiSheetProposal.Parse(modelText, _lastColumns, rawRows, _lastSpecNames, _lastCellFormats);
        _questionsAnswered = 0;      // 新一轮的问题从 0 数起（第 35 棒：答满才自动重出）
        foreach (var note in proposal.Notes) Append($"（已修正：{note}）");
        if (proposal.Errors.Count > 0)
        {
            Append("这次没采纳它的方案：" + string.Join("；", proposal.Errors));
            Append("表、模板与纸规都保持原样。");
            ClearChangesBlock();
            ShowNextSteps(applied: false);
            return;
        }
        if (proposal.IsEmpty)
        {
            // 第 34 棒：这句以前是「它没给出任何可执行的改动（可能只回了话）」——技术话，而且**是死路**
            // （用户 2026-09-10 的原话：「思考完回答啥也没做……那不应该出现下一步的按键让 AI 来排版和绑定列吗」）。
            Append("它这次没给可落地的改动（只说了话，没给能改的项）。表、模板与纸规都保持原样。");
            ClearChangesBlock();
            ShowNextSteps(applied: false);
            return;
        }
        var items = proposal.DescribeItems(rawRows);
        var explain = proposal.Explain();
        // 那五行是用户逐字定的口径（表格有效数据 / 纸张 / 模版 / 张数 / 预览）；
        // DescribeItems 那份「会改这几件事」从阶段 29 起只在改动清单出不来时兜底，不让两遍都打。
        var count = OutputCounter?.Invoke(proposal.Readout.QtyColumn);
        foreach (var line in proposal.SummaryLines(count?.Labels, count?.Sheets)) Append(line);
        // 第 32 棒：版式**逐行核对**（这一行到底填哪一列）。判据是它自己报的绑定 + 真表头。
        var rowCheck = proposal.DescribeLayoutRows(_lastColumns);
        if (rowCheck.Count > 0)
        {
            Append("这个版式每一行填什么（对着表核一遍，带 ⚠ 的对不上）：");
            foreach (var row in rowCheck) Append("　· " + row);
        }
        // ── 第 33 棒：**AI 判完直接落地**（用户改的架构：能自动判的直接进预览，靠"可撤回"兜底） ──
        // 以前是逐条 ✅/❌ 卡，他明确要换掉（"直接注入预览及其他"）。四类一起落：切表 / 绑定 / 版式 / 纸规。
        if (ApplyProposal is { } autoApply)
        {
            var (ok, message) = autoApply(proposal);
            Append((ok ? "已直接落到预览（不满意可以在下面撤回）：" : "没能落地：") + message);
        }
        else
            Append("（这个面板没接上落地入口，下面只能看。）");

        var shownAsList = ShowAppliedChanges(proposal);
        if (!shownAsList && items.Count > 0 && proposal.Questions.Count == 0)
            foreach (var item in items) Append("　· " + item);

        // 问题留在最后：**只有要他拍板的才问他**（能自动判的已经落了，不拿"确认"去烦他）。
        ShowQuestions(proposal);

        // 第 31 棒：它对这张表的**判断逐条**摆出来。用户 2026-09-10 截图里那段「它的说法」读着混乱——
        // 根因是提示词逼它"一句说完"（五件事挤成一句），不是显示写错了。现在一条一件，人一行行扫。
        if (proposal.Facts.Count > 0)
        {
            Append("它对这张表的判断（一条一件）：");
            foreach (var fact in proposal.Facts) Append("　· " + fact);
        }
        if (explain.Count > 0)
        {
            Append("它提醒（不采纳也能用，但你得知道）：");
            foreach (var e in explain) Append("　· " + e);
        }
        // 软件自己改过什么单独收尾：以前它跟改动清单挤在一起，同一句会印两遍（用户圈图那屏就是这个）。
        foreach (var n in proposal.Notes) Append("（软件这边：" + n + "）");
        _pendingProposal = proposal;
        // 第 33 棒：整份已经自动落过了，这颗按钮改成"再落一次"没有意义 —— 收起来不用。
        _applyLayout.Content = "重落一次（一般不用点）";
        _applyLayout.IsEnabled = false;
        // 第 34 棒：**任何一条路都收在"下一步"**（哪怕它这次什么都没动）。
        ShowNextSteps(applied: shownAsList);
    }

    /// <summary>清掉「它改了什么」那一块（提案没回来可落地的东西时用，免得留着上一轮的清单）。</summary>
    private void ClearChangesBlock()
    {
        _changes.Children.Clear();
        _changes.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// **下一步那颗按钮**（第 34 棒）。
    /// <para>用户 2026-09-10 的原话：「*号后删不删也不问，那**即使是它觉得没问题**，
    /// 那不应该出现下一步的按键让 AI 来排版和绑定列吗」——之前提案回来没有可落地的东西时，
    /// 面板只剩一句技术话，人被晾在死路上。现在**任何结局都收在这里**：
    /// 重出方案（带上他刚答的、接着上下文再来一次）或只让它排一版版式。</para>
    /// </summary>
    private void ShowNextSteps(bool applied)
    {
        _changes.Visibility = Visibility.Visible;
        _changes.Children.Add(new TextBlock
        {
            Text = applied ? "下一步（不满意就撤回，或者让它重出一版）：" : "下一步（它没动手，那就再来一次）：",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 2),
        });
        var again = new Button
        {
            Content = "让 AI 重出方案（排版 + 绑定列）",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            ToolTip = "把整张表再交给它一次：切表 + 绑定列 + 版式 + 纸规一起出；你刚答过的问题也在上下文里",
        };
        again.Click += async (_, _) => await AskProposalAsync();
        var layoutOnly = new Button
        {
            Content = "只让它排一版版式",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
        };
        layoutOnly.Click += async (_, _) => await AskLayoutAsync();
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(again);
        row.Children.Add(layoutOnly);
        _changes.Children.Add(row);
        _changes.BringIntoView();
    }

    /// <summary>
    /// 把「要人拍一下」的那几条挂成一行一句 + ❌/✅ 两颗小按钮（第 22 棒真正要的东西）。
    /// <para>点一颗只落那一条，落完两颗都置灰并把选了哪个标在行尾——
    /// 不然人记不住刚才点的是哪边，又变成一屏看不出结论的文字。</para>
    /// </summary>
    private void ShowQuestions(AiSheetProposal proposal)
    {
        _questions.Children.Clear();
        if (proposal.Questions.Count == 0 || ApplyQuestion is null)
        {
            _questions.Visibility = Visibility.Collapsed;
            return;
        }
        _questions.Visibility = Visibility.Visible;
        for (var i = 0; i < proposal.Questions.Count; i++)
        {
            var q = proposal.Questions[i];
            // 一问一块：问题文字独占一行，按钮组另起一行右对齐。
            // 旧版用 DockPanel 把按钮钉右侧——右栏窄形态下问题折行把行拉高，
            // 两颗按钮被拉成占半屏的 giant 灰块（用户 2026-09-10 实拍）；竖排让两种形态同一套版式。
            var block = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            block.Children.Add(new TextBlock
            {
                Text = $"⚠️{i + 1}. {q.Text}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            var answer = new TextBlock
            {
                FontSize = 11,
                Foreground = OkBrush,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var yes = new Button
            {
                Content = "✅ " + q.YesLabel,
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(4, 0, 0, 0),
            };
            var no = new Button
            {
                Content = "❌ " + q.NoLabel,
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
            };
            // WrapPanel：窄栏放不下就自然换行，按钮永远只占自己那一行的高度。
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
            actions.Children.Add(no);
            actions.Children.Add(yes);
            actions.Children.Add(answer);
            block.Children.Add(actions);
            no.Click += (_, _) => AnswerQuestion(proposal, q, false, answer, no, yes);
            yes.Click += (_, _) => AnswerQuestion(proposal, q, true, answer, no, yes);
            _questions.Children.Add(block);
        }
    }

    /// <summary>
    /// 把「它改了什么」摊成**只读**清单（第 33 棒：逐条 ✅ 卡停用）。
    /// <para>为什么还留着：用户要的是"能自动判的直接落地"，但**看得见**这条不许丢——
    /// 落了地就得告诉他改了哪几处、从什么改成什么，并给一颗「撤回这一步」。
    /// 安全感从"事前逐条点头"换成了"事后看得见 + 退得回"（那是他 2026-09-10 定的架构）。</para>
    /// <para>返回 false = 清单出不来（没接落地入口 / 这次没有变化），调用方退回旧文字清单，不静默丢信息。</para>
    /// </summary>
    private bool ShowAppliedChanges(AiSheetProposal proposal)
    {
        _changes.Children.Clear();
        if (ApplyProposal is null)
        {
            _changes.Visibility = Visibility.Collapsed;
            return false;
        }
        var changes = proposal.DescribeChanges(GetChangeContext?.Invoke());
        if (changes.Count == 0)
        {
            _changes.Visibility = Visibility.Collapsed;
            return false;
        }
        _changes.Visibility = Visibility.Visible;
        _changes.Children.Add(new TextBlock
        {
            Text = $"它改了这 {changes.Count} 处（已经直接生效了；点「详情」看改的是什么）：",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 2),
        });
        foreach (var change in changes)
        {
            var block = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            var detail = new TextBlock
            {
                Text = change.DiffText,
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 1, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            var toggle = new Button
            {
                Content = "详情",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
            };
            toggle.Click += (_, _) =>
            {
                var show = detail.Visibility != Visibility.Visible;
                detail.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = show ? "收起" : "详情";
            };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = "· " + change.Target,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 0);
            Grid.SetColumn(toggle, 1);
            head.Children.Add(title);
            head.Children.Add(toggle);
            block.Children.Add(head);
            block.Children.Add(detail);
            _changes.Children.Add(block);
        }

        // 撤回：**逐步**可连点（用户选的粒度）。文案说清"退回哪一步"，免得人不知道自己退掉了什么。
        if (UndoAiChange is { } undo && (CanUndoAiChange?.Invoke() ?? false))
        {
            var undoResult = new TextBlock
            {
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = Brushes.Gray,
            };
            var label = AiUndoLabel?.Invoke() ?? string.Empty;
            var undoButton = new Button
            {
                Content = "↩ " + (label.Length > 0 ? label : "撤回 AI 的这一步"),
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "把 AI 这一次动手的改动退回去（可以连着点，一步步往回退）",
            };
            undoButton.Click += (_, _) =>
            {
                var (ok, message) = undo();
                undoResult.Text = (ok ? "" : "没撤成：") + message;
                var still = CanUndoAiChange?.Invoke() ?? false;
                undoButton.IsEnabled = still;
                if (!still) undoButton.Content = "没有更早的 AI 改动了";
                else undoButton.Content = "↩ " + (AiUndoLabel?.Invoke() ?? "撤回 AI 的这一步");
            };
            _changes.Children.Add(undoButton);
            _changes.Children.Add(undoResult);
        }
        _changes.BringIntoView();
        return true;
    }

    /// <summary>
    /// 人拍了一条问题：先落地，再**把这条问答注入上下文**（第 33 棒）。
    /// <para>用户的原话是「选择后将回答注入思考」——他的选择必须让 AI 下一轮知道，
    /// 不然它下一次读表还会照原来那套判断重来一遍（那就是"答了跟没答一样"）。
    /// 注入方式：追加一问一答进对话历史，下一轮请求自动带上；纸面上不给它刷屏（历史不进对话区）。</para>
    /// </summary>
    private void AnswerQuestion(AiSheetProposal proposal, AiSheetQuestion q, bool yes, TextBlock answer, Button no, Button yesButton)
    {
        if (ApplyQuestion is not { } apply)
        {
            Append("这个面板没接上「逐条落地」入口（只有主窗口里的 AI 页签能这么点）。");
            return;
        }
        no.IsEnabled = false;
        yesButton.IsEnabled = false;
        answer.Text = (yes ? "✅ " : "❌ ") + (yes ? q.YesLabel : q.NoLabel);
        var (ok, message) = apply(proposal, q, yes);
        Append((ok ? "已办：" : "没办成：") + message);
        if (!ok) return;

        // 第 35 棒：他的决定要**真的回到模型手里**——下一轮请求会带上 _decisions 那一节。
        // 以前只塞会话历史，而提案/排版那两条路**根本不读历史**，所以他答了等于没答（他自己说的
        // 「提出问题选择了也是无效的」就是这么来的）。
        var choice = yes ? q.YesLabel : q.NoLabel;
        _decisions.Add($"{q.Text} → {choice}");
        _turns.Add(new AiChatTurn(AiChatTurn.User, $"【我对你这一问的决定】{q.Text} → {choice}"));
        _questionsAnswered++;

        // 这一轮的问题都答完了 → 带着他的决定**自动重出一版**（他要的"你收到信息后就知道怎么调整
        // 怎么做了 → 自动把排版拍出来"）。只答一半不跑，免得白花一两分钟。
        if (proposal.Questions.Count > 0 && _questionsAnswered >= proposal.Questions.Count)
            _ = AskProposalFollowUpAsync();
    }

    /// <summary>清掉待确认的东西，并把「用这个」那颗按钮的文案还回去（两种提案共用一颗按钮，文案不能错）。</summary>
    private void ClearPending()
    {
        _pending = null;
        _pendingProposal = null;
        _questions.Children.Clear();
        _questions.Visibility = Visibility.Collapsed;
        _changes.Children.Clear();
        _changes.Visibility = Visibility.Collapsed;
        _applyLayout.IsEnabled = false;
        _applyLayout.Content = "用这个（存成我的模板并选中）";
    }

    /// <summary>用户点了「用这个」才落地。存不存得进模板库由 <see cref="TemplateStore"/> 的校验说了算，这里不放宽。</summary>
    public void ApplyPending()
    {
        // 先试提案：它还会重切用户的表与换纸规，不能与「只存一版模板」那一路走同一个口子静默降级。
        if (_pendingProposal is { } pendingProposal)
        {
            if (ApplyProposal is not { } applyProposal)
            {
                Append("这个面板没接上提案落地入口（提案要改表与纸规，只有主窗口的「AI 助手」页签里能用）。");
                return;
            }
            var (okAll, msgAll) = applyProposal(pendingProposal);
            if (okAll)
            {
                _pendingProposal = null;
                _applyLayout.IsEnabled = false;
                _applyLayout.Content = "用这个（存成我的模板并选中）";
            }
            Append((okAll ? "已落地：" : "没落地：") + msgAll);
            return;
        }
        if (_pending is not { } spec)
        {
            Append("手上没有待确认的方案，先点「让 AI 出一版排版」。");
            return;
        }
        if (ApplyLayout is not { } apply)
        {
            Append("这个面板没接上模板库（只在主窗口的「AI 助手」页签里能用）。");
            return;
        }
        var (ok, message) = apply(spec);
        if (ok)
        {
            // 落地一次就收手：再点一次不该把同一个方案再存一遍（按钮同时置灰，双保险）。
            _pending = null;
            _applyLayout.IsEnabled = false;
        }
        Append((ok ? "已落地：" : "没落地：") + message);
    }

    /// <summary>「按这版去打印」= 跳到 ⑤ 并触发那条既有打印命令；未核对的字段照样被复核闸门拦着。</summary>
    public void PrintNow()
    {
        if (GoPrint is not { } go)
        {
            Append("这个面板没接上打印（只在主窗口的「AI 助手」页签里能用）。");
            return;
        }
        if (_pending is not null)
        {
            Append("先说一声：你点的那版还没落地，现在打印用的是当前选中的模板。");
            return;
        }
        go();
    }
}
