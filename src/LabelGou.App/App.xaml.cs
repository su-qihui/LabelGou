using System.Windows;
using System.Windows.Threading;
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
        // Win7 的 Schannel 默认可能不启用 TLS 1.2，而云端 AI 走 HTTPS 必须 TLS 1.2，
        // 不显式打开就会握手/证书校验失败；Win7 无 TLS 1.3，只加到 Tls12，Win10 上无副作用。
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;

        System.Threading.Thread.CurrentThread.CurrentUICulture =
            new System.Globalization.CultureInfo("zh-CN");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLog.Info($"LabelGou 启动，版本 {GetType().Assembly.GetName().Version}，日志目录 {AppLog.DirectoryPath}");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
