using System.Reflection;

namespace LabelGou.App.Services;

/// <summary>
/// 版本号与产品名的唯一出口：标题栏、「关于」对话框、导出件里的 producer 都从这里取。
/// <para>§二 早就写了「全部取程序集版本，改一处即可」，而上一版把 <c>v0.6.0</c> 硬编码在
/// MainWindow.xaml 的标题与 AboutText 里各一份 —— 版本升到 0.7.0 时那两处不会跟着动，
/// 用户看到的还是旧号，于是又一条「改了没变化」。</para>
/// </summary>
public static class AppInfo
{
    /// <summary>产品版本（如 <c>0.7.0</c>）。取 InformationalVersion，并去掉 SDK 附带的源码修订号。</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>标题栏那一句（含五步向导提示），与「关于」用同一个版本号。</summary>
    public static string WindowTitle =>
        $"LabelGou · 唛头标签助手  v{Version}（五步向导：导入数据 → 连接字段 → 选模板 → 拼版编号 → 核对与输出）";

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var head = plus > 0 ? informational[..plus] : informational;
            if (head.Trim().Length > 0) return head.Trim();
        }
        var named = assembly.GetName().Version;
        return named is null ? "0.0.0" : $"{named.Major}.{named.Minor}.{named.Build}";
    }
}
