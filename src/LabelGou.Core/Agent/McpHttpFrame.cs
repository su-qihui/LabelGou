using System.Text;

namespace LabelGou.Core.Agent;

/// <summary>一个解开的 MCP HTTP 请求。</summary>
/// <param name="Method">POST / GET。</param>
/// <param name="Path">请求路径（我们只认一个端点，路径写错就 404）。</param>
/// <param name="Authorization">原样的授权头，交给 <see cref="AgentAuth"/> 判。</param>
/// <param name="Body">按 Content-Length 读出来的体。</param>
public sealed record McpHttpRequest(string Method, string Path, string? Authorization, string Body);

/// <summary>
/// 一段最小的 HTTP/1.1：<strong>只够读一个请求、写一个响应</strong>。
/// <para>为什么不用 <c>HttpListener</c>：它走内核的 HTTP.sys，URL 命名空间要预留——本机这次实测能绑，
/// 可这台是内置 Administrator，那次"通"证明不了标准用户也能绑（店里机器不一定是管理员账户）。
/// <c>TcpListener</c> 是纯 socket，没有这一层前提。取证见 <c>labelgou-other\_probe\agent-mcp\README.md</c> §2。</para>
/// <para>为什么不引 Kestrel：那要往一个 net8.0 + 零 NuGet 包的 App 里灌一坨 ASP.NET（实测官方 MCP SDK 一次拖进 16 个包）。
/// 我们只要"读一行请求 + 读 Content-Length 那么长的体 + 回一段 JSON"：MCP 的 <c>tools/list</c> 与
/// <c>tools/call</c> 都是一问一答，不需要服务端主动推，也就不需要 SSE。</para>
/// <para>解析与拼包都是纯函数—— socket 那层薄到不需要判据，判据全在这一页。</para>
/// </summary>
public static class McpHttpFrame
{
    /// <summary>单个体最多多大（一份画像 JSON 几十 KB，4 MB 是"对面开始乱发"的界）。</summary>
    public const int MaxBodyChars = 4_000_000;

    /// <summary>只接受这一个端点；别的路一律 404，不解释有什么路。</summary>
    public const string Endpoint = "/mcp";

    /// <summary>
    /// 解析请求头与体。首行不合法、体超长、行不全——都回 null，调用方直接 400 关掉，不做任何解释性回包。
    /// </summary>
    public static McpHttpRequest? ParseRequest(string headerBlock, string body)
    {
        if (string.IsNullOrWhiteSpace(headerBlock)) return null;
        var lines = headerBlock.Replace("\r\n", "\n").Split('\n');
        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        string? authorization = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (string.Equals(line[..colon].Trim(), "Authorization", StringComparison.OrdinalIgnoreCase))
                authorization = line[(colon + 1)..].Trim();
        }

        if ((body ?? string.Empty).Length > MaxBodyChars) return null;
        return new McpHttpRequest(parts[0], parts[1], authorization, body ?? string.Empty);
    }

    /// <summary>从请求头里取声明的体长（调用方按它读到那么多字节就停）。</summary>
    public static int ContentLengthOf(string headerBlock)
    {
        foreach (var raw in headerBlock.Replace("\r\n", "\n").Split('\n'))
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            if (!string.Equals(raw[..colon].Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(raw[(colon + 1)..].Trim(), out var length) && length >= 0)
                return Math.Min(length, MaxBodyChars);
        }
        return 0;
    }

    /// <summary>响应。<paramref name="json"/> 原样上路（长度按字节算，不是按字符算——中文一字数十字节）。</summary>
    public static string BuildResponse(int status, string json)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            _ => "Error",
        };
        return "HTTP/1.1 " + status + " " + reason + "\r\n"
               + "Content-Type: application/json\r\n"
               + "Content-Length: " + Encoding.UTF8.GetByteCount(json) + "\r\n"
               + "Cache-Control: no-store\r\n"
               + "Connection: close\r\n\r\n"
               + json;
    }

    public static string BuildOk(string json) => BuildResponse(200, json);

    /// <summary>错令牌 / 没令牌：一句人话，不多解释（解释越多越像在教对面怎么试）。</summary>
    public static string BuildUnauthorized() => BuildResponse(401, """{"error":"令牌不对或者没带令牌"}""");

    public static string BuildNotFound() => BuildResponse(404, """{"error":"这个端点不做这件事"}""");
}
