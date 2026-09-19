using System.IO;
using System.Windows;
using System.Windows.Controls;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.Services.Agent;
using LabelGou.Core.Agent;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「模型（AI）设置与调试」窗里那一块外部 Agent 的判据（第 87 棒）。
/// <para>用户报的不是 bug 而是<strong>看不见</strong>：第 86 棒把开关留在了 <c>agent.json</c> 里，
/// 界面上一个字都没有——"我并在界面上没看到类似接入 codex 等接口"。这一页钉的就是"看得见的东西真在、
/// 且点下去真改了那份文件"，别再回到<em>只有代码知道</em>的状态。</para>
/// <para>沿用第 50 棒那条纯布局判据的量法（偏移 + 宽度越不越出容器，不用 <c>DesiredSize</c>），
/// 但只量这一块——历史遗留的排布不归本棒管，混在一起红了也说不清是谁的。</para>
/// </summary>
public sealed class AgentSettingsWindowTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void 三颗勾点完真写进那份设置文件() => OnSta(() =>
    {
        var window = new AiDebugWindow();
        var path = Path.Combine(Path.GetTempPath(), $"lg-agent-{Guid.NewGuid():N}.json");
        try
        {
            window.UseExternalAgentBox.IsChecked = true;
            window.ServeClientsBox.IsChecked = true;
            window.AllowWriteToolsBox.IsChecked = false;
            window.SaveAgentSettingsTo(path);

            var (back, failure) = AgentSettings.LoadFrom(path);
            Assert.Null(failure);
            Assert.True(back.UseExternalAgent, "「让外部 agent 读表」那颗勾点了，文件里却是关的");
            Assert.True(back.ServeExternalClients, "「允许外部连入」那颗勾点了，文件里却是关的");
            Assert.False(back.AllowWriteToolsForExternalClient, "没勾的那颗不许被顺手写成开的");
        }
        finally
        {
            File.Delete(path);
        }
        return true;
    });

    [Fact]
    public void 没在听的时候不许凭空亮出一把令牌() => OnSta(() =>
    {
        Assert.False(AgentServeHost.IsServing, "这条判据要求此刻确实没在听（前面哪条测试没关门就是那里漏了）");
        var window = new AiDebugWindow();
        Assert.Equal(string.Empty, window.AgentConnectForTests);
        Assert.DoesNotContain("令牌", window.AgentConnectForTests);
        // 状态那一行要说实话：没在听就写"现在没在听"，别写一半像成功的话。
        Assert.Contains("现在没在听", window.AgentStatusForTests);
        return true;
    });

    [Fact]
    public void 窗口收到最窄时这一块没有控件被挤出去() => OnSta(() =>
    {
        var window = new AiDebugWindow();
        var root = (FrameworkElement)window.Content;
        // 用窗口的 MinWidth 量：他真会把窗子拖窄，那一版"按钮被边缘化"就是这么露出来的
        root.Measure(new Size(window.MinWidth, window.MinHeight));
        root.Arrange(new Rect(0, 0, window.MinWidth, window.MinHeight));
        window.UpdateLayout();

        Assert.True(root.ActualWidth > 200, $"根宽度没量到（{root.ActualWidth}）——夹具没生效，别拿这条当绿");
        var over = new List<string>();
        foreach (var box in window.AgentControlsForTests)
        {
            if (box is not { ActualWidth: > 0 }) continue;
            var right = box.TranslatePoint(new Point(box.ActualWidth, 0), root).X;
            if (right > root.ActualWidth + 0.5)
                over.Add($"{Describe(box)} 右缘 {right:0.#} > 容器 {root.ActualWidth:0.#}");
        }
        Assert.True(over.Count == 0, "这些控件被挤到窗口右边外面：\n" + string.Join("\n", over));
        return true;
    });

    [Fact]
    public void 没有可驱动的界面时那扇门不开()
    {
        // 面板没登记过（单测进程里就是没开主窗）→ 不许起监听，也不许留一个"看着像开了"的状态。
        Assert.False(AgentServeHost.IsServing);
        var line = AgentServeHost.Start(new AgentSettings { ServeExternalClients = true });
        Assert.Contains("没有可驱动的界面", line);
        Assert.False(AgentServeHost.IsServing);
        Assert.Null(AgentServeHost.ConnectCommand);
    }

    [Fact]
    public void 断开之后令牌与端口都不再对外()
    {
        Assert.Equal("现在没在听。", AgentServeHost.Stop());   // 没开过就点断开：说清而不是炸
        Assert.False(AgentServeHost.IsServing);
    }

    static string Describe(FrameworkElement element) => element switch
    {
        ContentControl c => $"「{c.Content}」",
        TextBlock t => $"状态文字「{t.Text[..Math.Min(12, t.Text.Length)]}…」",
        _ => element.GetType().Name,
    };
}
