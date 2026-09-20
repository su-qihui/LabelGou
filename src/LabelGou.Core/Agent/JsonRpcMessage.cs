using System.Text.Json;

namespace LabelGou.Core.Agent;

/// <summary>一条 JSON-RPC 帧是哪一类。</summary>
public enum JsonRpcKind
{
    /// <summary>带 id 的请求，必须回一条。</summary>
    Request,

    /// <summary>不带 id 的通知，不许回。</summary>
    Notification,

    /// <summary>别人回给我们的响应（出站那一路用得上）。</summary>
    Response,

    /// <summary>读不成：一行脏数据不该把服务打断，调用方按协议错误回 -32700。</summary>
    Invalid,
}

/// <summary>
/// JSON-RPC 2.0 的一帧（MCP 在 stdio 上就是「一行一个 JSON」）。
/// <para><strong>只管编解码，不碰传输</strong>：谁递来字符串、谁把结果写走，它一概不知道。
/// 这样 Core 能把它单测钉死，而 App 那层换管道、换 socket 都不用动判据。</para>
/// <para>id 一律存<strong>原文</strong>（<see cref="IdRaw"/>）而不是转成数字：JSON-RPC 允许 id 是字符串或数字，
/// 而我们回包时必须把 id 原样带回去——转一次类型就可能把 <c>"7"</c> 回成 <c>7</c>，客户端认不出这条是谁的。</para>
/// </summary>
public sealed record JsonRpcMessage(
    JsonRpcKind Kind,
    string? IdRaw,
    string? Method,
    JsonElement Params,
    string? Error)
{
    // ── 协议里那几个码，MCP 客户端认的是数字，不是我们的措辞 ──
    public const int CodeParseError = -32700;
    public const int CodeInvalidRequest = -32600;
    public const int CodeMethodNotFound = -32601;
    public const int CodeInvalidParams = -32602;
    public const int CodeServerNotInitialized = -32002;
    public const int CodeInternalError = -32603;

    /// <summary>有就带 id，没有就是通知——回错了会让客户端等到超时。</summary>
    public bool WantsReply => Kind == JsonRpcKind.Request;

    /// <summary>
    /// 解一行。畸形 JSON、缺 jsonrpc、缺 method 统统归 <see cref="JsonRpcKind.Invalid"/> 并把原因写进
    /// <see cref="Error"/>，<strong>绝不抛</strong>：一条脏帧拖垮整个会话，外面那个 agent 只会看到"连不上"。
    /// </summary>
    public static JsonRpcMessage Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return new JsonRpcMessage(JsonRpcKind.Invalid, null, null, default, "空行");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return new JsonRpcMessage(JsonRpcKind.Invalid, null, null, default, $"不是合法 JSON：{ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new JsonRpcMessage(JsonRpcKind.Invalid, null, null, default, "一帧必须是 JSON 对象");
            if (!ReadCommon(doc.RootElement, out var idRaw, out var method, out var @params, out var reason, out var isResponse))
                return new JsonRpcMessage(JsonRpcKind.Invalid, idRaw, method, Detach(@params), reason);

            if (isResponse) return new JsonRpcMessage(JsonRpcKind.Response, idRaw, null, Detach(@params), null);
            if (method is null)
                return new JsonRpcMessage(JsonRpcKind.Invalid, idRaw, null, Detach(@params), "少了 method");
            if (idRaw is null) return new JsonRpcMessage(JsonRpcKind.Notification, idRaw, method, Detach(@params), null);
            return new JsonRpcMessage(JsonRpcKind.Request, idRaw, method, Detach(@params), null);
        }
    }

    /// <summary>通知：没有 id，所以也不会有回复。</summary>
    public static string WriteNotification(string method, string paramsJson = "{}")
        => $"{{\"jsonrpc\":\"2.0\",\"method\":{Quote(method)},\"params\":{ParamsOrEmpty(paramsJson)}}}";

    /// <summary>请求（出站那一路用：我们向 agent 的 app-server 问东西时）。</summary>
    public static string WriteRequest(string idRaw, string method, string paramsJson = "{}")
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{IdLiteral(idRaw)},\"method\":{Quote(method)},\"params\":{ParamsOrEmpty(paramsJson)}}}";

    /// <summary>成功回复。id 用原文回填，理由见类型注释。</summary>
    public static string WriteResult(string idRaw, string resultJson)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{IdLiteral(idRaw)},\"result\":{ParamsOrEmpty(resultJson)}}}";

    /// <summary>失败回复。message 会原样发给对面，所以里面不许有路径、密钥、堆栈。</summary>
    public static string WriteError(string idRaw, int code, string message)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{IdLiteral(idRaw)},\"error\":{{\"code\":{code},\"message\":{Quote(message)}}}}}";

    /// <summary>
    /// 这条链上<strong>唯一</strong>的 JSON 写法：中文原样上路。
    /// <para>默认的 <c>System.Text.Json</c> 会把非 ASCII 转成 <c>\u8FD9\u5F20</c>——那是合法 JSON，
    /// 但对面的提示词与人肉读日志时都成了天书，而 §五-160 那条教训就是"现场只剩一堆读不懂的字节"。
    /// 只在这一个地方开这个口子，别在调用侧各配一份 options。</para>
    /// <para><c>TypeInfoResolver</c> <strong>必须显式给</strong>：<c>JsonNode.ToJsonString(options)</c> 会直接要求它，
    /// 而这份 options 若先被 <c>JsonSerializer.Serialize</c> 用过一次才会补上默认解析器——
    /// 于是"只在某条测试先跑时才炸"。红检当场抓到过一次，别拿"我这边跑是绿的"当证据。</para>
    /// </summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>把字符串塞成 JSON 字面量（引号与换行都转掉，别让一句人话把帧打断）。</summary>
    public static string Quote(string text) => JsonSerializer.Serialize(text, Wire);

    private static string ParamsOrEmpty(string json)
        => string.IsNullOrWhiteSpace(json) ? "{}" : json;

    private static string IdLiteral(string? idRaw)
    {
        if (string.IsNullOrWhiteSpace(idRaw)) return "null";
        // 数字 id 就按数字回，字符串 id 就带引号回——两种都是合法 JSON，但转错了客户端对不上号。
        var trimmed = idRaw.Trim();
        return long.TryParse(trimmed, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : Quote(trimmed);
    }

    private static bool ReadCommon(
        JsonElement root,
        out string? idRaw,
        out string? method,
        out JsonElement @params,
        out string? reason,
        out bool isResponse)
    {
        idRaw = null;
        method = null;
        @params = default;
        reason = null;
        isResponse = false;

        if (!root.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String
            || version.GetString() != "2.0")
        {
            reason = "jsonrpc 必须是 \"2.0\"";
            return false;
        }

        if (root.TryGetProperty("id", out var id) && id.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            idRaw = IdText(id);

        if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
            method = m.GetString();

        root.TryGetProperty("params", out @params);

        // 有 result 或有 error 且没有 method：那是回给我们的响应。
        var hasResult = root.TryGetProperty("result", out _);
        var hasError = root.TryGetProperty("error", out _);
        isResponse = (hasResult || hasError) && method is null;

        if (!isResponse && idRaw is null && method is null)
        {
            reason = "既不是请求也不是响应（method 与 result/error 都没有）";
            return false;
        }
        return true;
    }

    private static string IdText(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString() ?? string.Empty,
        JsonValueKind.Number => id.GetRawText(),
        _ => id.GetRawText(),
    };

    /// <summary>
    /// 把 <c>JsonElement</c> 从那份 <c>JsonDocument</c> 上摘下来。
    /// <para><see cref="Parse"/> 一进来就把文档 dispose 了（不这么做就要么漏、要么把生命周期摊给调用方），
    /// 而 <c>JsonElement</c> 只是那文档上的一个指针——直接把指针交出去，调用方一读就是
    /// <c>ObjectDisposedException: Cannot access a disposed object 'JsonDocument'</c>。
    /// 一帧里只有 <c>params</c> 需要带走，所以在这儿 Clone 一次；<c>default</c>（没带 params）不用克隆。</para>
    /// </summary>
    private static JsonElement Detach(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined ? element : element.Clone();
}
