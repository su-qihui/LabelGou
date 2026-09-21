using System.Windows;
using Microsoft.Win32;

namespace LabelGou.App.Services;

/// <summary>
/// 简洁版壳窗的调色板选取（第 97 棒，用户：「添加深色模式适配系统」）：读系统那个深浅色开关，
/// 要深色就往壳窗的资源共享里再压一份 <c>SimpleTokens.Dark.xaml</c>。
/// <para><strong>只管简洁版</strong>：专业版吃的是 <c>App.xaml</c> 那套全局样式，动它等于动一代界面
/// （风险不对等，用户当场点头的范围也是简洁版）。</para>
/// <para><strong>只在构造时读一次</strong>：系统里改了深浅，要下次启动才生效——不做注册表监听，
/// 这条代价讲给他了，别在这里偷偷加个 timer 养出"实时换肤"的期望。</para>
/// </summary>
public static class SimpleTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>系统的 <c>AppsUseLightTheme</c>：0 = 深色，1 = 浅色，<c>null</c> = 读不到（组策略锁了、系统太老）。</summary>
    public static int? ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") as int?;
        }
        catch (Exception)
        {
            // 读不到配色不是故障：按浅色开就是了，别拦人干活
            return null;
        }
    }

    /// <summary>按判据给这一扇窗换皮。浅色是 XAML 里本来就合并好的默认，所以只有深色才多压一份。</summary>
    public static void ApplyInto(Window shell)
    {
        if (!SimpleShellFlow.PreferDark(ReadAppsUseLightTheme())) return;
        shell.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            // 绝对 pack URI 带程序集名：XAML 里那条相对路径是编译器替你改写过才生效的，
            // 代码里递相对 Uri 会按**当前进程**的资源目录去找——测试进程里那就是 LabelGou.App.Tests，当场报"找不到资源"。
            Source = new Uri($"pack://application:,,,/{typeof(SimpleTheme).Assembly.GetName().Name}" +
                             ";component/Themes/SimpleTokens.Dark.xaml", UriKind.Absolute),
        });
    }
}
