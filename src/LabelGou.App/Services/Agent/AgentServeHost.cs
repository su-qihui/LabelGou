using System;
using System.Collections.Generic;
using LabelGou.Core.Agent;

namespace LabelGou.App.Services.Agent;

/// <summary>
/// 外部接入那扇门的<strong>唯一持有者</strong>（第 87 棒）。
/// <para>为什么从面板里搬出来：第 86 棒把监听挂在 <c>AiChatPanel.OnInitialized</c> 上，而一个进程里可能出现
/// 两份面板（主窗那份 + 调试窗里「和模型对话…」又开一扇 <c>AiChatWindow</c>）——那会同时开两个端口、
/// 发两把令牌，界面上只看得见后起的那一句。收成一个静态宿主之后，"谁在听、听在哪、令牌是哪把"只有一个答案。</para>
/// <para>面板只登记自己当"工具面提供者"（后登记的算），退场时交还；设置窗要开要停都找这里，不绕回主窗。</para>
/// </summary>
internal static class AgentServeHost
{
    private static GuiMcpServer? _server;
    private static AiChatPanel? _panel;

    /// <summary>状态变了（开/停/换面板），界面那行字要跟着重画。</summary>
    public static event Action? Changed;

    public static bool IsServing => _server is not null;

    /// <summary>面板登记自己是当前这份工具面的来源。</summary>
    public static void Attach(AiChatPanel panel) => _panel = panel;

    /// <summary>面板退场：如果听的正是它，顺手把门关上（否则留一个连不上任何界面的洞）。</summary>
    public static void Detach(AiChatPanel panel)
    {
        if (!ReferenceEquals(_panel, panel)) return;
        Stop();
        _panel = null;
    }

    /// <summary>连接那一串（含一次性令牌）。没在听就是 null——<strong>不许凭空造一个看着像的</strong>。</summary>
    public static string? ConnectCommand => _server?.ConnectCommand;

    public static string? Token => _server?.Token;

    /// <summary>
    /// 开起来。<paramref name="settings"/> 决定写权限给不给；没有可用面板就回一句人话，不开半扇门。
    /// </summary>
    public static string Start(AgentSettings settings)
    {
        if (_server is not null) return $"已经在听了：{ConnectCommand}";
        var panel = _panel;
        if (panel is null) return "现在没有可驱动的界面（主窗的 AI 面板没开）：先回到主窗再开。";

        try
        {
            _server = new GuiMcpServer(panel.BuildToolCatalog(settings), AgentHostKind.WithUi,
                panel.RunOnUi, AppInfo.Version);
        }
        catch (Exception ex)
        {
            _server = null;
            return $"门没开成：{ex.GetType().Name} {ex.Message}";
        }
        Changed?.Invoke();
        return "外部接入已开。把这行贴到 Codex 那边（令牌只活这一次运行）：\n"
               + ConnectCommand + "\n令牌 " + Token + "（环境变量 LABELGOU_MCP_TOKEN 填它）";
    }

    public static string Stop()
    {
        if (_server is null) return "现在没在听。";
        var server = _server;
        _server = null;
        try { server.Dispose(); } catch { /* 关不掉也只是端口晚点回收 */ }
        Changed?.Invoke();
        return "外部接入已断开（令牌作废）。";
    }
}
