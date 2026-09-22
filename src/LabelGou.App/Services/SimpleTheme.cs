using System.Linq;
using System.Windows;

namespace LabelGou.App.Services;

/// <summary>
/// 简洁版壳窗的调色板切换（第 97 棒起，第 98 棒改成他自己点）：要深色就往壳窗的资源共享里压一份
/// <c>SimpleTokens.Dark.xaml</c>，要浅色把那一份摘掉——<strong>加得也减得</strong>，所以顶栏那颗钮点一下当场换皮。
/// <para><strong>只管简洁版</strong>：专业版吃的是 <c>App.xaml</c> 那套全局样式，动它等于动一代界面。</para>
/// <para><strong>不读系统配色</strong>：第 98 棒用户原话「不要把系统深浅模式变成默认了」。上一版这里读注册表
/// <c>AppsUseLightTheme</c>，机器设了深色软件就跟着黑——现在唯一的来源是他点的那一颗（默认浅色）。</para>
/// </summary>
public static class SimpleTheme
{
    /// <summary>深色调色板那份字典的地址（<see cref="DarkDictionary"/> 靠它认出"哪一份是我压进来的"）。</summary>
    private static Uri DarkPaletteUri { get; } = new Uri(
        $"pack://application:,,,/{typeof(SimpleTheme).Assembly.GetName().Name}" +
        ";component/Themes/SimpleTokens.Dark.xaml", UriKind.Absolute);

    /// <summary>这一扇窗现在是不是深那一身。</summary>
    public static bool IsDarkApplied(Window shell) => DarkDictionary(shell) is not null;

    /// <summary>
    /// 把窗换到那一身。幂等的：已经在那一身就一格不动，所以壳窗构造时照存过的值调一次、
    /// 之后每次点钮再调一次，走的是同一条路。
    /// </summary>
    public static void ApplyInto(Window shell, bool dark)
    {
        var current = DarkDictionary(shell);
        if (dark && current is null)
        {
            shell.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DarkPaletteUri });
        }
        else if (!dark && current is not null)
        {
            shell.Resources.MergedDictionaries.Remove(current);
        }
    }

    /// <summary>绝对 pack URI 带程序集名：XAML 里那条相对路径是编译器替你改写过才生效的，
    /// 代码里递相对 Uri 会按<strong>当前进程</strong>的资源目录去找——测试进程里那就是 LabelGou.App.Tests，当场报"找不到资源"（第 97 棒抓的）。</summary>
    private static ResourceDictionary? DarkDictionary(Window shell)
        => shell.Resources.MergedDictionaries.FirstOrDefault(d => d.Source is { } s && s.Equals(DarkPaletteUri));
}
