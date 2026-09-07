using System.Windows;

namespace LabelGou.App;

/// <summary>
/// 识别进度窗（M6）：一张说明当前在干什么的窗口 + 一个真能停下来的取消按钮。
/// <para>存在的唯一理由是耗时：本地 OCR 几十毫秒，大模型 34 秒/张，一批五张三分多钟。
/// 没有取消入口的话，用户只能结束进程，还会以为程序死了。</para>
/// <para>刻意不做 ViewModel：这里没有任何判定逻辑，只有两行字和一个按钮。</para>
/// </summary>
public sealed partial class RecognitionProgressWindow : Window
{
    /// <summary>用户点了取消（或者用 ESC 关了窗）。</summary>
    public event Action? CancelRequested;

    public RecognitionProgressWindow()
    {
        InitializeComponent();
    }

    /// <summary>刷新进度文字。调用方用 <see cref="Progress{T}"/> 投递，天然回到 UI 线程。</summary>
    public void Report(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Report(message));
            return;
        }
        StatusText.Text = message;
    }

    /// <summary>识别结束：停掉跑马灯、收掉取消按钮，让窗口自己关掉。</summary>
    public void Complete(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Complete(message));
            return;
        }

        StatusText.Text = message;
        Bar.IsIndeterminate = false;
        CancelButton.IsEnabled = false;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;      // 取消中……别让人连点
        StatusText.Text = "正在取消……已完成的文档仍然有效。";
        CancelRequested?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelRequested?.Invoke();          // 右上角 X 与 ESC 也算取消，别留一个跑不完的后台任务
        base.OnClosed(e);
    }
}
