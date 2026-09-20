using System.Windows;
using System.Windows.Threading;
using LabelGou.App.Printing;
using LabelGou.App.Services;

namespace LabelGou.App;

/// <summary>应用程序入口。</summary>
public partial class App : Application
{
    /// <summary>
    /// 启动时装好三层异常兜底并把过程写进日志：
    /// 打印店机器上窗口一闪而过、操作员描述不清问题，日志是唯一取证手段；
    /// 同时避免未处理异常直接弹系统错误框把程序带走。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        System.Threading.Thread.CurrentThread.CurrentUICulture =
            new System.Globalization.CultureInfo("zh-CN");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLog.Info($"LabelGou 启动，版本 {GetType().Assembly.GetName().Version}，日志目录 {AppLog.DirectoryPath}");

        // 第 84 棒：上次要是被强杀/断电，OnExit 根本没跑，这台打印机的默认还留在被改过的状态——先补还，再告诉他
        var leftover = PrinterDefaultsGuard.RecoverLeftoversAtStartup();
        if (leftover is not null)
        {
            Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(this.MainWindow, leftover, "LabelGou",
                MessageBoxButton.OK, MessageBoxImage.Information)), DispatcherPriority.ApplicationIdle);
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // ⑤ 步那三格与「打印首选项…」改的是这台打印机在本机的用户默认（别的软件也吃它），用完得还回去
        var failed = PrinterDefaultsGuard.RestoreAllOnExit();
        if (failed is not null) AppLog.Info(failed);
        // 导入时暂存的那份表副本也在这儿清掉（第 90 棒②）：它只为"这一次正在做的表"存在。
        // 换文件时已经清过一轮，这一句管的是"关掉软件"那一头；忘了清也不出事，下次启动导入会顶掉它。
        LabelGou.Core.Data.ImportCache.Clear();
        AppLog.Info($"退出，代码 {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("UI 线程未处理异常", e.Exception);
        MessageBox.Show(
            "程序遇到一个问题，已经记进日志。\n\n" + e.Exception.Message +
            "\n\n日志位置：" + AppLog.DirectoryPath,
            "LabelGou",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        // 标记已处理：不弹系统错误框、不闪退，让用户能继续把手头这批标签印完
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => AppLog.Error("后台线程未处理异常", e.ExceptionObject as Exception);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("任务异常未被观察", e.Exception);
        e.SetObserved();
    }
}
