using System.IO;
using System.Net;
using System.Net.Http;
using LabelGou.App.Services.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 27 棒：思考档参数到底发没发出去（假端点收请求体断言，不真连公网）。
/// <para>官方口径（百炼 2026-09-04 文档）：qwen3.8 系列 <c>reasoning_effort</c> 只认
/// low/medium/xhigh，关思考走 <c>enable_thinking:false</c>，两参数与 thinking_budget 混发会报错——
/// 所以每份请求体里这两个键最多出现一个，auto/陌生值时一个都不许出现。</para>
/// </summary>
public class B27ThinkingPayloadTests
{
    /// <summary>只记请求体、按形状回话的假端点（抄 AiChatTests 的 StubHandler 口径）。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        public string Reply = @"{""choices"":[{""message"":{""content"":""收到""}}]}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Reply, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static RecognitionSettings CloudSettings(string thinking) => new()
    {
        Provider = RecognitionSettings.Providers.OpenAi,
        Endpoint = "https://example.invalid/compatible-mode/v1",
        Model = "qwen3.8-flash",
        ApiKey = "test-key",
        Thinking = thinking,
    };

    [Theory]
    [InlineData("", null)]                    // 默认：一个字都不发，旧模型/别的网关不会因陌生键 400
    [InlineData("off", "enable_thinking\":false")]
    [InlineData("low", "reasoning_effort\":\"low")]
    [InlineData("medium", "reasoning_effort\":\"medium")]
    [InlineData("xhigh", "reasoning_effort\":\"xhigh")]
    [InlineData("high", null)]                // 陌生档位：不发——百炼会直接报错，不替它转译
    public async Task 聊天请求体按档位注入思考参数_一次只发一个(string thinking, string? expectedFragment)
    {
        var stub = new StubHandler();
        var outcome = await OllamaVisionClient.ChatAsync(
            CloudSettings(thinking),
            new[] { new AiChatTurn(AiChatTurn.User, "这张表怎么切") },
            handler: stub);

        Assert.True(outcome.Ok, outcome.Error);
        var body = Assert.Single(stub.Bodies);
        if (expectedFragment is null)
        {
            Assert.DoesNotContain("enable_thinking", body, StringComparison.Ordinal);
            Assert.DoesNotContain("reasoning_effort", body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expectedFragment, body, StringComparison.Ordinal);
            // 混发会报错：出现了本档的键，另一个就不许出现
            var otherKey = expectedFragment.StartsWith("enable_thinking", StringComparison.Ordinal)
                ? "reasoning_effort" : "enable_thinking";
            Assert.DoesNotContain(otherKey, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 关思考时只发enable_thinking_不碰reasoning_effort()
    {
        var stub = new StubHandler();
        await OllamaVisionClient.ChatAsync(
            CloudSettings("off"),
            new[] { new AiChatTurn(AiChatTurn.User, "这张表怎么切") },
            handler: stub);

        var body = Assert.Single(stub.Bodies);
        Assert.Contains("\"enable_thinking\":false", body, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_effort", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 云端抽字段那条路也带思考档()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), "b27-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        try
        {
            var stub = new StubHandler
            {
                Reply = @"{""choices"":[{""message"":{""content"":""{\""ItemNo\"":\""b5011\""}""}}]}",
            };
            var outcome = await OllamaVisionClient.AskFieldsAsync(
                CloudSettings("low"), imagePath, handler: stub);

            Assert.True(outcome.Ok, outcome.Error);
            var body = Assert.Single(stub.Bodies);
            Assert.Contains("\"reasoning_effort\":\"low\"", body, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(imagePath); } catch (IOException) { /* 临时文件清不掉不影响结论 */ }
        }
    }

    [Fact]
    public void 本机Ollama通道的思考档注入口不碰_那条路压根不调用注入()
    {
        // ApplyThinkingTo 是纯函数：off 只发 enable_thinking，档位只发 reasoning_effort。
        // 注入点只有 ChatOpenAiAsync 与 AskOpenAiAsync 两处（都在 OpenAI 兼容分支里），
        // Ollama 分支的 payload 构造不引用它——这里锁函数行为，分支归属靠代码审读。
        var off = new Dictionary<string, object?>();
        CloudSettings("off").ApplyThinkingTo(off);
        Assert.Equal(new Dictionary<string, object?> { ["enable_thinking"] = false }, off);

        var auto = new Dictionary<string, object?>();
        CloudSettings("").ApplyThinkingTo(auto);
        Assert.Empty(auto);
    }
}
