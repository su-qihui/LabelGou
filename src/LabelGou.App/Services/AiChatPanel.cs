using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LabelGou.App.Services.Recognition;
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
/// 与当前模板自带的底稿。用户 2026-09-09 定的规矩：<strong>没参照就不得造</strong>，所以面板得先知道有没有参照。</para>
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
    int CurrentHeaderRow = 0)
{
    /// <summary>能问的东西有没有：已连字段与整表画像一个都没才算真的没得可给（第 15 棒：不能再把「没连上字段」当门槛）。</summary>
    public bool HasAnythingToAsk => (Fields is { Count: > 0 }) || !string.IsNullOrWhiteSpace(Portrait);

    /// <summary>
    /// 这张表到底有没有可对照的实物长相：表里贴的图、当前模板的底稿/图片元素、或用户这一条附的照片。
    /// <para>三样都没时 <see cref="AskLayoutAsync"/> 就不得把请求发出去：让模型凭列名造一版，
    /// 本质上是拿语法猜设计，错的东西会一路走到纸上。</para>
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
/// <para>红线仍然成立，只是多了一扇有人看着的门：AI 出的版式<strong>必须人点「用这个」</strong>才会存成模板，
/// 中间还要过 <see cref="RowLayoutSpec.Build"/>（排不出返 null）与 <see cref="TemplateValidator"/>（有 Error 拒入库）；
/// 打印走既有的 ⑤ 那一条命令与复核闸门，这里不开第二条出纸路（§五-22）。</para>
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
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 13,
        MinHeight = 160,
        Margin = new Thickness(0, 0, 0, 6),
    };
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

    /// <summary>逐条问题那一块：一条一行，行尾挂 ❌/✅ 两颗小按钮（第 22 棒）。</summary>
    private readonly StackPanel _questions = new() { Visibility = Visibility.Collapsed };

    /// <summary>
    /// 逐条「改哪里 + 原值 → 新值 + ✅/❌」的改动卡（阶段 29 第 1 棒）。
    /// <para>与 <see cref="_questions"/> 是两件事：那份是<em>模型拿不准、要人二选一</em>的问题；
    /// 这份是<em>它打算动手的每一处改动</em>，人逐条放行。</para>
    /// </summary>
    private readonly StackPanel _changes = new() { Visibility = Visibility.Collapsed };

    /// <summary>这批卡是按哪一代数据算出来的（点 ✅ 之前对一遍，不等就整批作废）。</summary>
    private int _changesGeneration = -1;
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
    /// 只落地改动卡上的**那一条**（阶段 29 第 1 棒）：人点 ✅ 走这里，点 ❌ 也要走（回一句"没动"）。
    /// <para>这是阶段 29 那条红线的落点——AI 的每一次写都要人点过 ✅，一步都不许自己落地。</para>
    /// </summary>
    public Func<AiSheetProposal, AiChange, bool, (bool Ok, string Message)>? ApplyChange { get; set; }

    /// <summary>软件自己数「几枚标签、几张纸」（预览那行用真数，不用模型报的数）。</summary>
    public Func<string?, (int Labels, int Sheets)?>? OutputCounter { get; set; }

    /// <summary>「按这版去打印」= 跳到 ⑤ 并触发既有打印命令。这里不自己开第二条出纸路。</summary>
    public Action? GoPrint { get; set; }

    /// <summary>
    /// 数据的「代数」（MainViewModel.DataGeneration）：发请求前记一份、回来时对一遍，
    /// 不等就作废那轮结果——等待期间换文件/换表/换模板后，旧提案落在新数据上会剔错行（第 23 棒）。
    /// </summary>
    public Func<int>? GetDataGeneration { get; set; }

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
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });    // 2：对话区（唯一可变的那块）
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 3：附图状态
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

        Grid.SetRow(_transcript, 2);
        root.Children.Add(_transcript);

        Grid.SetRow(_waitLine, 3);
        // 等的那一句、附图那一行、逐条问题同一格堆着：窄栏里多一行固定高就是从对话区扣一块，StackPanel 只在需要时占高。
        var status = new StackPanel();
        status.Children.Add(_waitLine);
        status.Children.Add(_questions);
        status.Children.Add(_changes);
        status.Children.Add(_attachment);
        Grid.SetRow(status, 3);
        root.Children.Add(status);

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
        _channelLine.Text = $"通道：{s.Provider}　端点：{s.Endpoint}　模型：{s.Model}（{limit}{thinking}）　" +
                            $"{(s.Provider == RecognitionSettings.Providers.OpenAi ? "订单数据会离开这台电脑" : "本机，不出网")}　{key}";
        _channelLine.Foreground = s.Provider == RecognitionSettings.Providers.OpenAi ? WarnBrush : OkBrush;
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
        _transcript.ScrollToEnd();
    }

    /// <summary>把最后一行换掉（只给 <see cref="AppendNotice"/> 用：同一句只占一行，次数就地更新）。</summary>
    private void ReplaceLastLine(string line)
    {
        var all = _transcript.Text.TrimEnd('\r', '\n');
        var cut = all.LastIndexOf('\n');
        _transcript.Text = (cut < 0 ? string.Empty : all.Substring(0, cut + 1)) + line + Environment.NewLine;
        _transcript.ScrollToEnd();
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
          .Append("还有一条红线：没看到效果图、模板截图或底稿时，不要凭列名编一版设计出来，先让用户给你一张参照。\n")
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
            var outcome = await OllamaVisionClient.ChatAsync(
                _settings, payload, image is { } shot ? (shot.Base64, shot.MimeType) : null,
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

        // 没有参照就不发这一趟请求（用户 2026-09-09 原话：「表格里没有效果图或者模版的时候，AI 识别到不应该直接制作，
        // 应该等人先做出模版导入照片然后再理解做出模版对照表格」）。拦在发送之前：
        // 一发就是几十秒与一笔钱，回来还是一版凭列名猜的样张。
        var attached = _image;
        if (!ctx.HasVisualReference(attached is not null))
        {
            AppendNotice("没有参照，我不出模板。这张表里没有贴效果图或模板截图，当前模板也不带底稿，你这一条也没附照片——" +
                "让我在这种条件下排版就是凭列名猜设计，猜错要重印。请先做其中一件：" +
                "① 在 ③ 步「导入底稿」把 CorelDRAW/AI 导出的 SVG 或 .cdr 递过来；" +
                "② 或点本面板「附上图片…」，把这枚唛头拍下来或截图给我。" +
                "参照到了我再对照整张表出模板（那一步不用改数据，只要给我看一眼）。现在你可以先把字段连好。");
            return;
        }

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

        var prompt = RowLayoutPrompt.Build(ctx.Fields, ctx.WidthMm, ctx.HeightMm, ctx.Note, ctx.Portrait)
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
            var outcome = await OllamaVisionClient.ChatWithImagesAsync(
                _settings, payload,
                images.Count == 0 ? null : images.Select(x => (x.Base64, x.MimeType)).ToList(), _running.Token);
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
    /// 「读这张表并提案」：把整张表（画像 + 贴图 + 纸规清单 + 原表行数）一次交给模型，
    /// 要它回一份「这张表该怎么切、这张纸该怎么摆」的 JSON 提案（第 21 棒）。
    /// <para>与 <see cref="AskLayoutAsync"/> 的分工：那条只要一版模板；这条管切表与选纸——
    /// 用户 2026-09-09 判定这些判断该由看得到整张表的 AI 做，而不是由程序写死规则去猜。</para>
    /// </summary>
    public async Task AskProposalAsync()
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

        var prompt = AiSheetProposalPrompt.Build(
            ctx.Portrait ?? "（没拿到整张表画像，只有已连字段）",
            _lastSpecNames, ctx.RawRowCount, ctx.CurrentHeaderRow,
            $"{ctx.WidthMm:0.#}×{ctx.HeightMm:0.#} mm", images.Count);
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
            var outcome = await OllamaVisionClient.ChatWithImagesAsync(
                _settings, payload,
                images.Count == 0 ? null : images.Select(x => (x.Base64, x.MimeType)).ToList(), _running.Token);
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
        var proposal = AiSheetProposal.Parse(modelText, _lastColumns, rawRows, _lastSpecNames);
        foreach (var note in proposal.Notes) Append($"（已修正：{note}）");
        if (proposal.Errors.Count > 0)
        {
            Append("这次没采纳它的方案：" + string.Join("；", proposal.Errors));
            Append("表、模板与纸规都保持原样。");
            return;
        }
        if (proposal.IsEmpty)
        {
            Append("它没给出任何可执行的改动（可能只回了话）。表、模板与纸规保持原样。");
            return;
        }
        var items = proposal.DescribeItems(rawRows);
        var explain = proposal.Explain();
        // 那五行是用户逐字定的口径（表格有效数据 / 纸张 / 模版 / 张数 / 预览）；
        // DescribeItems 那份「会改这几件事」从阶段 29 起只在改动卡出不来时兜底，不让两遍都打。
        var count = OutputCounter?.Invoke(proposal.Readout.QtyColumn);
        foreach (var line in proposal.SummaryLines(count?.Labels, count?.Sheets)) Append(line);
        ShowQuestions(proposal);
        var shownAsCards = ShowChanges(proposal);
        if (!shownAsCards && items.Count > 0 && proposal.Questions.Count == 0)
            foreach (var item in items) Append("　· " + item);
        if (explain.Count > 0)
        {
            Append("它提醒（不采纳也能用，但你得知道）：");
            foreach (var e in explain) Append("　· " + e);
        }
        // 软件自己改过什么单独收尾：以前它跟改动清单挤在一起，同一句会印两遍（用户圈图那屏就是这个）。
        foreach (var n in proposal.Notes) Append("（软件这边：" + n + "）");
        _pendingProposal = proposal;
        _applyLayout.Content = "这些全都要（一次落地）";
        _applyLayout.IsEnabled = ApplyProposal is not null;
        if (ApplyProposal is null)
            Append("（这个面板没接上提案落地入口，只能看。）");
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
    /// 把提案摊成逐条「改哪里 + 原值 → 新值 + ✅/❌」的改动卡（阶段 29 第 1 棒）。
    /// <para><strong>这是阶段 29 那条红线的界面落点</strong>：AI 的每一次写都要人点 ✅ 才落地。
    /// 用户 2026-09-10 的原话是「AI 会弹出「修改 xxxx ✅/❌」这样的 UI 来确认取消」，
    /// 而且他特意纠正过一版写法——<em>不能靠"人的改动一定对"来防冲突</em>，要让 AI 的每次写都可确认可取消。</para>
    /// <para>返回 false = 这张卡出不来（没上下文、没接落地入口，或压根没改动），调用方退回旧文字清单，
    /// <strong>不静默丢信息</strong>。</para>
    /// </summary>
    private bool ShowChanges(AiSheetProposal proposal)
    {
        _changes.Children.Clear();
        _changesGeneration = -1;
        if (ApplyChange is null)
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
        _changesGeneration = GetDataGeneration?.Invoke() ?? -1;
        _changes.Visibility = Visibility.Visible;
        _changes.Children.Add(new TextBlock
        {
            Text = $"它打算改这 {changes.Count} 处，逐条确认（点「采用」才动，点「取消」就不动）：",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 2),
        });
        foreach (var change in changes)
        {
            var block = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            block.Children.Add(new TextBlock
            {
                Text = "· " + change.Target,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            // 原值 → 新值单独一行、缩进、次要色：这一行才是人判断的依据（旧清单只有后半截）。
            block.Children.Add(new TextBlock
            {
                Text = change.DiffText,
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 1, 0, 0),
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
                Content = "✅ 采用",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(4, 0, 0, 0),
            };
            var no = new Button
            {
                Content = "❌ 取消",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
            };
            var actions = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 0),
            };
            actions.Children.Add(no);
            actions.Children.Add(yes);
            actions.Children.Add(answer);
            block.Children.Add(actions);
            no.Click += (_, _) => AnswerChange(proposal, change, false, answer, no, yes);
            yes.Click += (_, _) => AnswerChange(proposal, change, true, answer, no, yes);
            _changes.Children.Add(block);
        }
        return true;
    }

    /// <summary>
    /// 点一条改动卡：**先对数据代数**（这张卡还算不算数），再走落地。
    /// <para><strong>卡过期一律整批作废重出</strong>：它出方案那几十秒里人改了别处，卡上写的"原来是什么"
    /// 就已经不是现在了——拿旧原值往新状态上盖，等于静默改错东西（阶段 29 决议里点名的第二条）。</para>
    /// </summary>
    private void AnswerChange(AiSheetProposal proposal, AiChange change, bool yes,
        TextBlock answer, Button no, Button yesButton)
    {
        if (ApplyChange is not { } apply)
        {
            Append("这个面板没接上「逐条落地」入口（只有主窗口里的 AI 页签能这么点）。");
            return;
        }
        if (GetDataGeneration is { } generation && generation() != _changesGeneration)
        {
            Append("这批卡作废了：等它出方案的时候，表或模板换过了——卡上写的「原来是什么」已经不是现在。"
                 + "你的数据一个字没动；要新样子就再点一次「读这张表并提案」。");
            ClearPending();
            return;
        }
        no.IsEnabled = false;
        yesButton.IsEnabled = false;
        answer.Text = yes ? "✅ 采用" : "❌ 取消";
        var (ok, message) = apply(proposal, change, yes);
        Append((ok ? "已办：" : "没办成：") + message);
        // 落地成功后**重出剩余卡片**（第 30 棒），三件事一起解决：
        // ① 刚办完那一条因为"无变化"自己消失（无变化不出卡那条纪律顺带办了收尾）；
        // ② 其余卡片带着**新的原值**重出，不会出现"卡上写的原值已经过期"；
        // ③ 顺带把数据代数重新记一遍——落地会 BumpDataGeneration，不重记的话点第二张卡就被
        //    我们自己刚做的改动判成"卡过期"，人会觉得"怎么点一张就全废了"。
        if (yes && ok) ShowChanges(proposal);
    }

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
        _changesGeneration = -1;
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
