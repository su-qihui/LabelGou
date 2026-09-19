using LabelGou.Core.Agent;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 最小 HTTP 那一段的判据（第 86 棒）。
/// <para>socket 本身不测（薄到没有分支），但"从字节里认出方法与授权头""Content-Length 按字节还是按字符"
/// "超长体拒收"这几处一旦错了，症状是外面连得上却什么都读不到——那种问题最难查，所以先钉住。</para>
/// </summary>
public class McpHttpFrameTests
{
    const string PostHeaders = "POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer ABC\r\nContent-Length: 5\r\n\r\n";

    [Fact]
    public void 从请求头里认出方法与授权()
    {
        var request = McpHttpFrame.ParseRequest(PostHeaders, "hello");
        Assert.NotNull(request);
        Assert.Equal("POST", request!.Method);
        Assert.Equal("/mcp", request.Path);
        Assert.Equal("Bearer ABC", request.Authorization);
        Assert.Equal("hello", request.Body);
    }

    [Fact]
    public void 授权头大小写与写法都不挑()
        => Assert.Equal("ABC", McpHttpFrame.ParseRequest("POST /mcp HTTP/1.1\r\nauthorization: ABC\r\n\r\n", "")!.Authorization);

    [Fact]
    public void 首行不像请求就整个拒()
    {
        Assert.Null(McpHttpFrame.ParseRequest("\r\n", ""));
        Assert.Null(McpHttpFrame.ParseRequest("GET\r\n\r\n", ""));
        Assert.Null(McpHttpFrame.ParseRequest("", ""));
    }

    [Fact]
    public void 体超过上限当读不懂而不是撑内存()
        => Assert.Null(McpHttpFrame.ParseRequest("POST /mcp HTTP/1.1\r\n\r\n", new string('甲', McpHttpFrame.MaxBodyChars + 1)));

    [Fact]
    public void ContentLength没有就当没有体_有就照它读()
    {
        Assert.Equal(0, McpHttpFrame.ContentLengthOf("GET /mcp HTTP/1.1\r\n\r\n"));
        Assert.Equal(5, McpHttpFrame.ContentLengthOf(PostHeaders));
        Assert.Equal(1200, McpHttpFrame.ContentLengthOf("POST /mcp HTTP/1.1\r\ncontent-length: 1200\r\n\r\n"));
    }

    [Fact]
    public void ContentLength超过上限就夹到上限()
        => Assert.Equal(McpHttpFrame.MaxBodyChars,
            McpHttpFrame.ContentLengthOf("POST /mcp HTTP/1.1\r\nContent-Length: 99999999\r\n\r\n"));

    [Fact]
    public void 响应长度按字节算不是按字符算()
    {
        // 中文一字符 3 字节：按字符报长度，客户端会读一半就断——这是"连得上但拿不到"的经典形状。
        var json = """{"m":"这张表还没导入"}""";
        var response = McpHttpFrame.BuildResponse(200, json);
        Assert.Contains($"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(json)}", response);
        Assert.DoesNotContain($"Content-Length: {json.Length}\r\n", response);
    }

    [Fact]
    public void 令牌错了回401而不是解释一堆()
    {
        Assert.StartsWith("HTTP/1.1 401", McpHttpFrame.BuildUnauthorized());
        Assert.Contains("令牌", McpHttpFrame.BuildUnauthorized());
    }
}
