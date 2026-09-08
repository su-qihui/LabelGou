using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LabelGou.App.Services.Recognition;
using LabelGou.Core.Marks;
using Microsoft.Win32;

namespace LabelGou.App.Services;

/// <summary>
/// 「和模型对话」窗口 —— 全代码构建（与 <see cref="AiDebugWindow"/> 同一风格，少一处出错的地方）。
/// <para>为什么单独开一个窗口（第 10 棒）：用户的话是「没有 AI 对话窗口，没法和 AI 沟通去调整」。
/// 此前界面上只有<strong>抽字段</strong>那一条入口（固定提示词 + 强制 JSON），
/// 想问「这四行该怎么排」「这个字段名一般写什么」根本没有地方问，能力藏在 <c>ChatAsync</c> 里等于没有。</para>
/// <para>红线写死在界面上：<b>这里聊出来的东西不会自动改唛头</b>——不写模板、不写毫米坐标、不落库
/// （§五-10 / §七-11）。要落到版式，用户自己走 ② 连接字段与 ③ 选模板；模型说什么都不算已确认。</para>
/// </summary>
public sealed class AiChatWindow : Window
{
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));

    private readonly TextBlock _channelLine = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _transcript = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 13,
        Margin = new Thickness(0, 0, 0, 8),
    };
    private readonly TextBox _input = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Height = 68,
        Margin = new Thickness(0, 0, 0, 6),
    };
    private readonly TextBlock _attachment = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _send = new() { Content = "发送（Ctrl+Enter）", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _stop = new() { Content = "停止", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _attach = new() { Content = "附上图片…", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _detach = new() { Content = "去掉图", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };

    private readonly List<AiChatTurn> _turns = new();

    /// <summary>本次要附的图（base64 + 真实 MIME）：只随下一条用户消息发出去，发完就清，不每轮重发。</summary>
    private (string Base64, string MimeType, string Name)? _image;

    private CancellationTokenSource? _running;
    private RecognitionSettings _settings = RecognitionSettings.Load();

    public AiChatWindow()
    {
        Title = "和模型对话（问它怎么调）";
        Width = 780;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Padding = new Thickness(14);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var openSettings = new Button { Content = "打开通道设置…", Padding = new Thickness(10, 4, 10, 4) };
        openSettings.Click += (_, _) => new AiDebugWindow { Owner = this }.ShowDialog();
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(openSettings, Dock.Right);
        header.Children.Add(openSettings);
        header.Children.Add(_channelLine);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var notice = new TextBlock
        {
            Text = "这里只是问与聊：模型说的话不会自动改唛头，也不碰毫米坐标与模板。" +
                   "要落到版式，请自己走 ② 连接字段与 ③ 选模板；没经你核对的值一律不进打印。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Foreground = WarnBrush,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(notice, 1);
        root.Children.Add(notice);

        Grid.SetRow(_transcript, 2);
        root.Children.Add(_transcript);

        Grid.SetRow(_attachment, 3);
        root.Children.Add(_attachment);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _send.Click += async (_, _) => await SendAsync();
        _stop.Click += (_, _) => _running?.Cancel();
        _attach.Click += (_, _) => PickImage();
        _detach.Click += (_, _) => { _image = null; ShowAttachment(); };
        var clear = new Button { Content = "清空会话", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) => { _turns.Clear(); _transcript.Clear(); Append("会话已清空。"); };
        var copy = new Button { Content = "复制全部", Padding = new Thickness(12, 6, 12, 6) };
        copy.Click += (_, _) => { if (_transcript.Text.Length > 0) Clipboard.SetText(_transcript.Text); };
        buttons.Children.Add(_send);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_attach);
        buttons.Children.Add(_detach);
        buttons.Children.Add(clear);
        buttons.Children.Add(copy);

        var bottom = new StackPanel();
        Grid.SetRow(bottom, 4);
        bottom.Children.Add(_input);
        bottom.Children.Add(buttons);
        bottom.Children.Add(new TextBlock
        {
            Text = "Ctrl+Enter 发送；Enter 换行。上下文最多带最近 " + AiChatHistory.MaxTurns +
                   " 轮（更早的会省略并在对话里说明），单条最长 " + AiChatHistory.MaxCharsPerTurn + " 字。",
            TextWrapping = TextWrapping.Wrap,
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
        RefreshChannelLine();
        Loaded += (_, _) => _input.Focus();
    }

    private void RefreshChannelLine()
    {
        _settings = RecognitionSettings.Load();
        var s = _settings;
        var key = s.ResolveApiKey() is null ? "没有密钥" : "密钥已备（本次有效）";
        _channelLine.Text = $"通道：{s.Provider}　端点：{s.Endpoint}　模型：{s.Model}（{s.TimeoutSeconds}s）　{s.Provider switch { RecognitionSettings.Providers.OpenAi => "订单数据会离开这台电脑", _ => "本机，不出网" }}　{key}";
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
        if (dialog.ShowDialog(this) != true) return;
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
        _transcript.AppendText(text + Environment.NewLine);
        _transcript.ScrollToEnd();
    }

    /// <summary>
    /// 开场给模型的身份说明：把 <see cref="MarkFieldCatalog"/> 的真字段清单与「逐字取值」的规矩带上。
    /// <para>为什么从目录生成而不是手写一段话：字段清单改了，提示词不能悄悄落后（同 <c>FieldPrompt()</c> 的理由）。</para>
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
        RefreshChannelLine();

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
        SetBusy(true);
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

    private void SetBusy(bool busy)
    {
        _send.IsEnabled = !busy;
        _stop.IsEnabled = busy;
        _input.IsEnabled = !busy;
    }
}
