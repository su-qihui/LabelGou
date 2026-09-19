using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LabelGou.Core.Agent;

namespace LabelGou.App.Services.Agent;

/// <summary>
/// 开着的这个窗口给外部 agent 听的那一扇本地门（第 86 棒）。
/// <para>为什么在 GUI 进程里而不是另起一个头less 进程：老板要的是"它在动<strong>我眼前这一份</strong>"——
/// 表、模板、纸规、核对状态都只活在界面里，另起一个进程读磁盘就看不见这些了。
/// 代价由这一层认：<strong>所有工具调用都编组回 UI 线程</strong>执行（VM 与 WPF 集合是线程亲和的，§五-91）。</para>
/// <para>三道锁：只听 <c>127.0.0.1</c>（非回环来源直接断）、每会话一把一次性令牌（只存内存、不落盘）、
/// 工具面本身（<see cref="LabelGouToolCatalog"/>：写要老板勾过，出纸永远不给）。</para>
/// <para><c>TcpListener</c> 而非 <c>HttpListener</c>：理由与实测见 <see cref="McpHttpFrame"/>。
/// 传输这一层薄到不值得写判据——判据都住在 <see cref="McpHttpFrame"/>、<see cref="McpServerSession"/>、
/// <see cref="AgentAuth"/> 与 <see cref="LabelGouToolCatalog"/> 那四处。</para>
/// </summary>
internal sealed class GuiMcpServer : IDisposable
{
    private readonly McpServerSession _session;
    private readonly Func<Func<AgentToolResult>, AgentToolResult> _marshal;
    private readonly TcpListener _listener;
    private readonly Thread _loop;
    private readonly object _gate = new();
    private volatile bool _running;

    public GuiMcpServer(
        LabelGouToolCatalog catalog,
        AgentHostKind host,
        Func<Func<AgentToolResult>, AgentToolResult> marshal,
        string serverVersion)
    {
        _marshal = marshal;
        _session = new McpServerSession(catalog.Build(host), host,
            (name, args) => _marshal(() => catalog.Call(name, args)), "LabelGou", serverVersion);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Token = AgentAuth.NewToken();
        _running = true;
        _loop = new Thread(AcceptLoop) { IsBackground = true, Name = "labelgou-mcp" };
        _loop.Start();
    }

    public int Port { get; }

    /// <summary>本次会话那一把。<strong>不写盘</strong>：关掉软件就作废，免得留下一个人人能连的常开洞。</summary>
    public string Token { get; }

    /// <summary>给界面上那颗"复制连接命令"用的（老板不用自己拼）。</summary>
    public string ConnectCommand
        => $"codex mcp add labelgou --url http://127.0.0.1:{Port}/mcp --bearer-token-env-var LABELGOU_MCP_TOKEN";

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                return;     // Stop()/Dispose() 把监听关了，这是正常退场
            }

            try
            {
                using (client) Handle(client);
            }
            catch (Exception)
            {
                // 一条连接上的任何意外都只是关掉它。这一层不能因为对面发了怪东西就把服务整倒。
            }
        }
    }

    private void Handle(TcpClient client)
    {
        // 来源必须是回环：绑在 127.0.0.1 上本来别人连不到，这一道是防"以后有人把绑定改了"而判据没跟上。
        if (client.Client.RemoteEndPoint is not IPEndPoint remote || !AgentAuth.AllowsRemote(remote.Address.ToString()))
            return;

        var stream = client.GetStream();
        var headers = ReadHeaders(stream);
        if (headers is null) return;

        var request = McpHttpFrame.ParseRequest(headers, ReadBody(stream, McpHttpFrame.ContentLengthOf(headers)));
        if (request is null)
        {
            Write(stream, McpHttpFrame.BuildResponse(400, """{"error":"这一串我读不懂"}"""));
            return;
        }
        if (!request.Path.StartsWith(McpHttpFrame.Endpoint, StringComparison.Ordinal))
        {
            Write(stream, McpHttpFrame.BuildNotFound());
            return;
        }
        if (!AgentAuth.TokenMatches(request.Authorization, Token))
        {
            Write(stream, McpHttpFrame.BuildUnauthorized());
            return;
        }

        string? reply;
        lock (_gate) reply = _session.HandleLine(request.Body);

        // 通知不回体（HTTP 上也没有语义可回）：给一个空 result，204 会被部分客户端当成失败。
        Write(stream, McpHttpFrame.BuildOk(reply ?? "{}"));
    }

    private static string? ReadHeaders(NetworkStream stream)
    {
        var sb = new StringBuilder();
        int previous = -1, current;
        while ((current = stream.ReadByte()) >= 0)
        {
            sb.Append((char)current);
            if (previous == '\n' && (char)current == '\n') return sb.ToString();  // 空行 = 头完了
            if (sb.Length > 64_000) return null;                                   // 没完没了的头：断掉
            previous = current;
        }
        return null;
    }

    private static string ReadBody(NetworkStream stream, int length)
    {
        if (length <= 0) return string.Empty;
        var bytes = new byte[length];
        var read = 0;
        while (read < length)
        {
            var got = stream.Read(bytes, read, length - read);
            if (got <= 0) break;
            read += got;
        }
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    private static void Write(NetworkStream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
        stream.Flush();
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { /* 已经停了 */ }
        try { _loop.Join(500); } catch { /* 线程收尾失败不该挡住退出 */ }
    }
}
