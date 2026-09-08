using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LabelGou.App.Export;
using LabelGou.App.Services;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「AI 助手」面板的钉子（M7 第 11 棒）。用户原话：<b>「这个 AI 界面不应该藏起来，应该显示出来」</b>。
/// <para>所以第一条测试钉的不是行为而是<strong>拓扑</strong>：AI 必须在主窗口里常驻、有自己的那块地方。
/// 上一棒的教训就是「能力藏在代码里等于没有」——<c>ChatAsync</c> 早就写好了，界面上没入口，用户照样用不了。</para>
/// <para><strong>2026-09-08 第 16 棒第二版</strong>：AI 不再是预览的一个页签。用户原话
/// 「<strong>我把 AI 窗口拆下来和排版拿来对照</strong>」——只拆 AI 那一块，预览留在主窗；
/// 所以这里多钉一条反向：XAML 里不得再出现 <c>&lt;TabItem Header="AI 助手"&gt;</c>。</para>
/// <para>其余几条钉住那条确认路的红线：<b>AI 给的方案没经人点头前一个字都不落地</b>，脏方案连递都不递，
/// 落地只递一次。这里不联网（真网络证据在 <c>_probe\b10-net\</c>），走 <see cref="AiChatPanel.FeedLayoutAnswer"/>
/// 把「解析 → 骨架 → 校验 → 等点头」整条跑完——只测 HTTP 那一层等于没测用户真正会碰的东西。</para>
/// <para>WPF 对象的断言一律留在 STA 线程里做（§五-91）。</para>
/// </summary>
public class AiAssistantPanelTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"找不到源文件 {Path.Combine(parts)}（从 {AppContext.BaseDirectory} 向上查找）");
    }

    private static IEnumerable<Button> Buttons(DependencyObject node)
    {
        if (node is Button button) yield return button;
        // UIElementCollection 只给非泛型枚举器，拿索引用才能直接当 DependencyObject 使
        if (node is Panel panel)
        {
            for (var i = 0; i < panel.Children.Count; i++)
                foreach (var found in Buttons(panel.Children[i])) yield return found;
        }
        if (node is ContentControl content && content.Content is DependencyObject inner)
            foreach (var found in Buttons(inner)) yield return found;
    }

    [Fact]
    public void AI助手常驻主窗口自己的那块不再当预览的页签()
    {
        var xaml = RepoFile("src", "LabelGou.App", "MainWindow.xaml");
        var code = RepoFile("src", "LabelGou.App", "MainWindow.xaml.cs");

        // 常驻：有自己的宿主与标题，不是藏在菜单里
        Assert.Contains("Text=\"AI 助手\"", xaml);
        Assert.Contains("x:Name=\"AiHost\"", xaml);
        // 拆窗只搬这一块：预览留在主窗，两边才能对照
        Assert.Contains("x:Name=\"DetachAiButton\"", xaml);
        Assert.Contains("new Services.DetachablePanel(AiHost, ai", code);
        Assert.Contains("WireAi(ai)", code);                                              // 接上了能力，不是摆个空壳
        // 反向：AI 当页签就永远需要切回去看效果，而这正是用户要消除的那一步
        Assert.DoesNotContain("<TabItem Header=\"AI 助手\">", xaml);
    }

    [Fact]
    public void 独立对话窗与页签共用同一份实现()
    {
        var probe = OnSta(() =>
        {
            var window = new AiChatWindow();
            return (hasPanel: window.Panel is not null,
                    sameInstance: ReferenceEquals(window.Content, window.Panel));
        });

        Assert.True(probe.hasPanel);
        Assert.True(probe.sameInstance);        // 两份聊天实现迟早会漂，这里不给它第二次机会
    }

    [Fact]
    public void 面板上排版与打印两个动作都摆在明面()
    {
        var labels = OnSta(() => Buttons(new AiChatPanel()).Select(b => b.Content as string ?? string.Empty).ToList());

        Assert.Contains("发送（Ctrl+Enter）", labels);
        Assert.Contains("让 AI 出一版排版", labels);
        Assert.Contains("用这个（存成我的模板并选中）", labels);
        Assert.Contains("按这版去打印", labels);
    }

    [Fact]
    public void 面板里没有摆在不存在的行上的控件()
    {
        // 写这段时真实踩过：根 Grid 只声明了 6 行，输入框那一格设到第 7 行，
        // 编译不报错、单测不报错，只有眼睛看得到（输入框与按钮行重叠）。
        var offenders = OnSta(() =>
        {
            var grid = Assert.IsType<Grid>(new AiChatPanel().Content);
            return grid.Children.Cast<UIElement>()
                .Where(c => Grid.GetRow(c) >= grid.RowDefinitions.Count)
                .Select(c => $"{c.GetType().Name}@row{Grid.GetRow(c)}")
                .ToList();
        });

        Assert.Empty(offenders);
    }

    [Fact]
    public void 方案没经人点头前一个字节都不落地()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel();
            var applied = 0;
            panel.ApplyLayout = _ => { applied++; return (true, "ok"); };
            panel.FeedLayoutAnswer("""{"rows":[{"content":"NO. {{CartonNo}} / {{CartonTotal}}","sizePt":20}]}""");
            return (pending: panel.HasPendingLayout, applied, text: panel.Transcript);
        });

        Assert.True(probe.pending);                                     // 方案在手，等人点头
        Assert.Equal(0, probe.applied);                                 // 但没人点头之前什么都不落地
        Assert.Contains("点「用这个」才会存成你的模板", probe.text);
    }

    [Fact]
    public void 点用这个才交出去而且只交一次()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel();
            var got = new List<string>();
            panel.ApplyLayout = spec => { got.Add(spec.Rows[0].Content); return (true, "已存成我的模板"); };
            panel.FeedLayoutAnswer("""{"rows":[{"content":"MADE IN {{Origin}}","sizePt":18}]}""");
            panel.ApplyPending();
            panel.ApplyPending();                                       // 手快点两下
            return (count: got.Count, content: got.FirstOrDefault(), pending: panel.HasPendingLayout, text: panel.Transcript);
        });

        Assert.Equal(1, probe.count);
        Assert.Equal("MADE IN {{Origin}}", probe.content);
        Assert.False(probe.pending);
        Assert.Contains("已落地：已存成我的模板", probe.text);
    }

    [Fact]
    public void 脏方案不递到模板库()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel();
            var called = 0;
            panel.ApplyLayout = _ => { called++; return (true, "不该被调用"); };
            panel.FeedLayoutAnswer("抱歉，这个我做不了。");
            panel.ApplyPending();
            return (called, pending: panel.HasPendingLayout, text: panel.Transcript);
        });

        Assert.Equal(0, probe.called);
        Assert.False(probe.pending);
        Assert.Contains("这份方案不能用", probe.text);
        Assert.Contains("你现在的模板没被改动", probe.text);
    }

    [Fact]
    public void 没连上字段时不去烦模型也不留半个请求()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel();
            panel.GetLayoutContext = () => new AiLayoutContext(new List<(string Key, string Name, string Sample)>(), 140, 100, null);
            panel.AskLayoutAsync().GetAwaiter().GetResult();
            return (text: panel.Transcript, busy: panel.IsBusy);
        });

        Assert.Contains("一个字段都没连上", probe.text);
        Assert.False(probe.busy);
    }

    [Fact]
    public void 同一句挡下提示连撞四次只占一行并把次数报出来()
    {
        var transcript = OnSta(() =>
        {
            var panel = new AiChatPanel();
            panel.GetLayoutContext = () => new AiLayoutContext(new List<(string Key, string Name, string Sample)>(), 140, 100, null);
            for (var i = 0; i < 4; i++) panel.AskLayoutAsync().GetAwaiter().GetResult();
            return panel.Transcript;
        });

        // 用户 2026-09-08 拿 TOP 那张截图圈的就是这里：上一版刷四行一模一样的话，看着像四个不同的问题。
        Assert.Equal(1, transcript.Split('\n').Count(l => l.Contains("一个字段都没连上")));
        // 但不能只去重不吭声（那更像没点到），所以下次撞上去要把次数报在同一行里。
        Assert.Contains("已挡 4 次", transcript);
    }

    [Fact]
    public void 没接上模板库与打印时说实话而不是假装能用()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel();
            panel.FeedLayoutAnswer("""{"rows":[{"content":"{{ItemNo}}","sizePt":16}]}""");
            panel.ApplyPending();
            panel.PrintNow();
            return panel.Transcript;
        });

        Assert.Contains("这个面板没接上模板库", probe);
        Assert.Contains("这个面板没接上打印", probe);
    }

    [Fact]
    public void 待确认的方案没落地时打印前会提醒一句()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel();
            var printed = 0;
            panel.GoPrint = () => printed++;
            panel.FeedLayoutAnswer("""{"rows":[{"content":"{{ItemNo}}","sizePt":16}]}""");
            panel.PrintNow();                       // 手上还压着一版没点头的方案
            return (printed, text: panel.Transcript);
        });

        Assert.Equal(0, probe.printed);
        Assert.Contains("还没落地", probe.text);
    }
}
