using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LabelGou.App.Services.Recognition;

namespace LabelGou.App.Services;

/// <summary>
/// 「模型（AI）设置与调试」窗口 —— 全代码构建，不走 XAML（少一处出错的地方）。
/// <para>为什么要有这个窗口：上一轮我只在代码里加了云端协议分支和一份手册，界面上一个入口都没有，
/// 用户打开软件什么都点不到，于是问「你哪里接模型，哪里调试」。能力藏在代码里不算交付。</para>
/// <para>这里刻意只做两件事：<b>选通道</b>与<b>当场试一次给他看</b>（探活 + 真识别一张图，
/// 把原文与耗时摊出来）。不藏成功学：模型答什么就显示什么，慢就多慢。</para>
/// </summary>
public sealed class AiDebugWindow : Window
{
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB3, 0x26, 0x1E));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));

    private readonly ComboBox _channel = new() { Margin = new Thickness(0, 2, 0, 10) };
    private readonly TextBox _endpoint = new() { Margin = new Thickness(0, 2, 0, 10) };
    private readonly TextBox _model = new() { Margin = new Thickness(0, 2, 0, 10) };
    private readonly PasswordBox _apiKey = new() { Margin = new Thickness(0, 2, 0, 10) };
    private readonly CheckBox _acceptsImages = new()
    {
        Content = "这个模型能看图（DeepSeek 的 deepseek-chat 不支持，取消勾选后它会只整理本地 OCR 认出的文字行）",
        Margin = new Thickness(0, 2, 0, 12),
    };
    private readonly TextBlock _networkNotice = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _log = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly ScrollViewer _logScroll;

    private RecognitionSettings _settings = RecognitionSettings.Load();

    public AiDebugWindow()
    {
        Title = "模型（AI）设置与调试";
        Width = 720;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Padding = new Thickness(14);

        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = "通道", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        foreach (var item in ChannelItems()) _channel.Items.Add(item.Name);
        _channel.SelectionChanged += (_, _) => ApplySelection();
        form.Children.Add(_channel);

        form.Children.Add(Labeled("服务地址 endpoint（本机 Ollama 填 http://127.0.0.1:11434）", _endpoint));
        form.Children.Add(Labeled("模型名 model", _model));
        form.Children.Add(Labeled("API 密钥（留空则读环境变量 LABELGOU_LLM_KEY，推荐这种）", _apiKey));
        form.Children.Add(_acceptsImages);
        form.Children.Add(_networkNotice);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(new Button { Content = "探测模型在不在", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = true });
        ((Button)buttons.Children[0]).Click += async (_, _) => await ProbeAsync();
        var ask = new Button { Content = "拿一张图真问一次（调试用）", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        ask.Click += async (_, _) => await AskOnceAsync();
        buttons.Children.Add(ask);
        var save = new Button { Content = "保存并关闭", Padding = new Thickness(12, 6, 12, 6) };
        save.Click += (_, _) => { Persist(); Close(); };
        buttons.Children.Add(save);
        form.Children.Add(buttons);

        form.Children.Add(new TextBlock { Text = "结果（模型原话，不修饰）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        _logScroll = new ScrollViewer { Content = _log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(form, 0);
        Grid.SetRow(_logScroll, 1);
        root.Children.Add(form);
        root.Children.Add(_logScroll);
        Content = root;

        SelectInitialChannel();
        RefreshNotice();
    }

    private sealed record ChannelItem(string Name, RecognitionSettings Values);

    private static List<ChannelItem> ChannelItems()
    {
        var items = new List<ChannelItem>
        {
            new("本机 Ollama（默认，数据不出这台电脑）", new RecognitionSettings()),
        };
        foreach (var preset in RecognitionSettings.CloudPresets)
        {
            var s = new RecognitionSettings();
            s.ApplyPreset(preset);
            items.Add(new ChannelItem(preset.Name, s));
        }
        items.Add(new ChannelItem("自定义 OpenAI 兼容端点（vLLM 等）", new RecognitionSettings
        {
            Provider = RecognitionSettings.Providers.OpenAi,
            Endpoint = "http://127.0.0.1:8000/v1",
            Model = "qwen2-vl",
        }));
        return items;
    }

    private static StackPanel Labeled(string label, Control input)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(input);
        return panel;
    }

    private void SelectInitialChannel()
    {
        var items = ChannelItems();
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Values.Endpoint, _settings.Endpoint, StringComparison.OrdinalIgnoreCase)
                && items[i].Values.Provider == _settings.Provider)
            {
                _channel.SelectedIndex = i;
                return;
            }
        }
        _channel.SelectedIndex = items.Count - 1;   // 认不出来就当自定义，把已存的值原样摊给他
        _endpoint.Text = _settings.Endpoint;
        _model.Text = _settings.Model;
        _acceptsImages.IsChecked = _settings.ModelAcceptsImages;
    }

    private void ApplySelection()
    {
        if (_channel.SelectedIndex < 0) return;
        var values = ChannelItems()[_channel.SelectedIndex].Values;
        _endpoint.Text = values.Endpoint;
        _model.Text = values.Model;
        _acceptsImages.IsChecked = values.ModelAcceptsImages;
        RefreshNotice();
    }

    private void RefreshNotice()
    {
        var outOfNetwork = !LooksLocal(_endpoint.Text) && !string.IsNullOrWhiteSpace(_endpoint.Text);
        _networkNotice.Text = outOfNetwork
            ? "⚠ 这个地址不是本机：一旦识别，唛头图片与上面的文字会离开这台电脑。工厂订单外传请你自己确认可不可以。"
            : "这个地址只连本机，数据不出这台电脑。";
        _networkNotice.Foreground = outOfNetwork ? WarnBrush : OkBrush;
    }

    private static bool LooksLocal(string? endpoint)
        => Uri.TryCreate((endpoint ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
        && (uri.Host is "127.0.0.1" or "localhost" or "0.0.0.0" or "::1");

    private RecognitionSettings Collect()
    {
        var index = Math.Max(0, _channel.SelectedIndex);
        var values = ChannelItems()[index].Values;
        _settings.Provider = values.Provider;
        _settings.Endpoint = _endpoint.Text.Trim();
        _settings.Model = _model.Text.Trim();
        _settings.ApiKey = string.IsNullOrWhiteSpace(_apiKey.Password) ? _settings.ApiKey : _apiKey.Password;
        _settings.ModelAcceptsImages = _acceptsImages.IsChecked == true;
        _settings.UseVisionModel = true;
        return _settings;
    }

    private void Persist()
    {
        Collect().Save();
        WriteLine($"已写入 {RecognitionSettings.FilePath}");
    }

    private async Task ProbeAsync()
    {
        var settings = Collect();
        WriteLine($"探测 {settings.Endpoint} 上的 {settings.Model} …");
        var sw = Stopwatch.StartNew();
        var (found, reason) = await OllamaVisionClient.ProbeModelAsync(settings);
        WriteLine($"{(found ? "可用" : "不可用")}（{sw.Elapsed.TotalSeconds:F1} 秒）：{reason}");
    }

    private async Task AskOnceAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "挑一张真样张（截图 / 照片 / CDR 导出的图）",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        var settings = Collect();
        if (!settings.ModelAcceptsImages)
        {
            WriteLine("这个模型不吃图，「拿一张图真问一次」用不了；它只在识别单据时接本地 OCR 的文字行。");
            return;
        }
        WriteLine($"问 {settings.Model}：{Path.GetFileName(dialog.FileName)}（可能要几十秒，别急）…");
        var outcome = await OllamaVisionClient.AskFieldsAsync(settings, dialog.FileName);
        if (!outcome.Ok)
        {
            WriteLine($"失败（{outcome.Elapsed.TotalSeconds:F1} 秒）：{outcome.Error}");
            return;
        }
        WriteLine($"成功（{outcome.Elapsed.TotalSeconds:F1} 秒），模型原话：");
        WriteLine(outcome.Json ?? "(空)");
    }

    private void WriteLine(string message)
    {
        _log.Text = $"[{DateTime.Now:HH:mm:ss}] {message}\n{_log.Text}";
        _logScroll.ScrollToTop();
    }
}
