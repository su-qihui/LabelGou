using System.Text.Json;

namespace LabelGou.Core.Agent;

/// <summary>一颗工具跑完给客户端看的结果。</summary>
/// <param name="Ok">假的那条会以 <c>isError:true</c> 回出去，而不是当协议错误——客户端要能读出"它答了，但办不成"。</param>
/// <param name="Text">给人/给模型看的正文。<strong>不许塞路径、堆栈、密钥</strong>。</param>
public sealed record AgentToolResult(bool Ok, string Text);

/// <summary>
/// MCP 会话状态机（<c>initialize</c> / <c>tools/list</c> / <c>tools/call</c> / <c>ping</c>）。
/// <para>只管协议，不管传输：谁递来一行、谁把回包写走，它一概不知道——所以 Core 能把它整个单测掉，
/// 而 App 那边换管道、换 socket 都不用动判据（同 <see cref="JsonRpcMessage"/> 的分工理由）。</para>
/// <para>可见性只有一条规则：<strong>对这个宿主不可见的工具 = 根本不存在</strong>。
/// <c>NeverExposed</c> 那几颗（打印、导出、存模板）连"没这颗工具"以外都不多说，
/// 免得对面靠错误信息摸出"原来有个 print 被藏起来了"。</para>
/// </summary>
public sealed class McpServerSession
{
    private readonly AgentToolRegistry _registry;
    private readonly AgentHostKind _host;
    private readonly Func<string, JsonElement, AgentToolResult> _callTool;
    private readonly string _serverName;
    private readonly string _serverVersion;
    private bool _initialized;

    public McpServerSession(
        AgentToolRegistry registry,
        AgentHostKind host,
        Func<string, JsonElement, AgentToolResult> callTool,
        string serverName,
        string serverVersion)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _callTool = callTool ?? throw new ArgumentNullException(nameof(callTool));
        _host = host;
        _serverName = serverName;
        _serverVersion = serverVersion;
    }

    /// <summary>走到 <c>initialize</c> 了吗（给测试与日志看）。</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// 吃一行，回一行。<c>null</c> = 这条是通知，不许回（回了会让对面把回复当请求继续处理）。
    /// </summary>
    public string? HandleLine(string line)
    {
        var message = JsonRpcMessage.Parse(line);
        if (message.Kind == JsonRpcKind.Invalid)
            return JsonRpcMessage.WriteError(message.IdRaw ?? string.Empty, JsonRpcMessage.CodeParseError,
                "这一帧我读不懂，按 JSON-RPC 2.0 一行一个对象发");

        // 回给我们的响应（我们这侧是服务端）：没有要转发的对象，静默丢掉。
        if (message.Kind == JsonRpcKind.Response) return null;
        if (message.Method is null) return null;

        if (message.Kind == JsonRpcKind.Notification)
            return null;    // 通知一概不回；initialized 也不给它解锁任何东西——只认 initialize 那条请求（见 Initialize）

        var id = message.IdRaw ?? string.Empty;
        if (message.Method == "initialize") return Initialize(id, message.Params);
        if (message.Method == "ping") return JsonRpcMessage.WriteResult(id, "{}");

        if (!_initialized)
            return JsonRpcMessage.WriteError(id, JsonRpcMessage.CodeServerNotInitialized,
                "还没 initialize，先按协议握手");

        return message.Method switch
        {
            "tools/list" => JsonRpcMessage.WriteResult(id, _registry.ToolsListJson(_host)),
            "tools/call" => CallTool(id, message.Params),
            _ => JsonRpcMessage.WriteError(id, JsonRpcMessage.CodeMethodNotFound, $"不接这个 method：{message.Method}"),
        };
    }

    private string Initialize(string id, JsonElement @params)
    {
        // 只认这条请求作为握手：对面单发一条 notifications/initialized 想跳过握手是骗不开的。
        _initialized = true;
        // 对面报它支持哪个协议版本就原样回哪个：这里的严格性只会让版本对不上时直接连不上，
        // 而我们没有第二家客户端可以验证——握手能过、工具面受控才是我们要的那件事。
        var requested = TryText(@params, "protocolVersion", out var version) ? version : "2024-11-05";
        return JsonRpcMessage.WriteResult(id,
            $"{{\"protocolVersion\":{JsonRpcMessage.Quote(requested)}," +
            $"\"capabilities\":{{\"tools\":{{}}}}," +
            $"\"serverInfo\":{{\"name\":{JsonRpcMessage.Quote(_serverName)},\"version\":{JsonRpcMessage.Quote(_serverVersion)}}}}}");
    }

    private string CallTool(string id, JsonElement @params)
    {
        if (!TryText(@params, "name", out var name))
            return JsonRpcMessage.WriteError(id, JsonRpcMessage.CodeInvalidParams, "tools/call 少了 name");

        if (!_registry.TryGet(name, out var spec) || !AgentToolRegistry.VisibleTo(spec, _host))
            return JsonRpcMessage.WriteError(id, JsonRpcMessage.CodeInvalidParams, $"没有这颗工具：{name}");

        @params.TryGetProperty("arguments", out var arguments);
        AgentToolResult result;
        try
        {
            result = _callTool(name, arguments);
        }
        catch (Exception)
        {
            // 堆栈与路径都不许顺着协议漏给对面；现场解释留在我们自己的日志里。
            return JsonRpcMessage.WriteError(id, JsonRpcMessage.CodeInternalError, "这颗工具这次没跑成");
        }

        return JsonRpcMessage.WriteResult(id,
            $"{{\"content\":[{{\"type\":\"text\",\"text\":{JsonRpcMessage.Quote(result.Text)}}}],\"isError\":{(result.Ok ? "false" : "true")}}}");
    }

    private static bool TryText(JsonElement parent, string property, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out var child)
            && child.ValueKind == JsonValueKind.String)
        {
            value = child.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
