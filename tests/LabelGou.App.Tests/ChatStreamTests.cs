using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabelGou.App.Services.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「流式 + 思考内容」这一层的钉子（第 31 棒）。
/// <para>用户 2026-09-10 实测后明确要「AI 再思考时可以选择展开或者关闭思考内容」，并且选的是
/// **直接做流式（边想边滚）**而不是"事后才可看"。他当时正在追"AI 排版为什么这么差"——
/// 思考过程就是判断"它到底有没有读懂这张表"的唯一材料。</para>
/// <para>这里钉的是最容易静默失效的那一段：**SSE 分片的解析**。分片形状比"整段回一次"复杂得多——
/// <c>data:</c> 前缀、<c>[DONE]</c> 收尾、空行心跳、半截 JSON、"思考"与"正文"两个字段（百炼用
/// <c>delta.reasoning_content</c>、Ollama 用 <c>message.thinking</c>）。所以它抽成了纯函数，
/// 不用联网就能喂真分片文本；整条请求另有一条假端点的端到端钉子。真连公网的证据在 <c>_probe\</c>。</para>
/// </summary>
public class ChatStreamTests
{
    // ─────────────────────── 纯函数：一行 → 这一片有什么 ───────────────────────

    [Fact]
    public void 思考与正文是分开收的()
    {
        var think = OllamaVisionClient.ParseStreamLine(
            """data: {"choices":[{"delta":{"reasoning_content":"我先看表头"}}]}""", openAiCompatible: true);
        var text = OllamaVisionClient.ParseStreamLine(
            """data: {"choices":[{"delta":{"content":"第1列是货号"}}]}""", openAiCompatible: true);

        Assert.Equal("我先看表头", think.Reasoning);
        Assert.Null(think.Content);
        Assert.Equal("第1列是货号", text.Content);
        Assert.Null(text.Reasoning);
    }

    [Fact]
    public void DONE收尾_而空行与注释不算错()
    {
        var done = OllamaVisionClient.ParseStreamLine("data: [DONE]", openAiCompatible: true);
        Assert.True(done.Done);
        Assert.Null(done.Error);

        // 心跳、空行、SSE 注释都不是错——把它们当错会误报（各家都会夹带自己的心跳行）。
        foreach (var quiet in new[] { "", "   ", ": keep-alive", "event: ping" })
        {
            var piece = OllamaVisionClient.ParseStreamLine(quiet, openAiCompatible: true);
            Assert.Null(piece.Error);
            Assert.Null(piece.Content);
            Assert.Null(piece.Reasoning);
            Assert.False(piece.Done);
        }
    }

    [Fact]
    public void 半截JSON不当错_下一片照旧能收()
    {
        // 流被打断时最后一片可能是残的：不能因此把整次请求判死。
        var broken = OllamaVisionClient.ParseStreamLine(
            """data: {"choices":[{"delta":{"cont""", openAiCompatible: true);
        Assert.Null(broken.Error);

        var next = OllamaVisionClient.ParseStreamLine(
            """data: {"choices":[{"delta":{"content":"照旧收下"}}]}""", openAiCompatible: true);
        Assert.Equal("照旧收下", next.Content);
    }

    [Fact]
    public void 服务端夹带的错误要报出来()
    {
        var piece = OllamaVisionClient.ParseStreamLine(
            """data: {"error":{"message":"model not found"}}""", openAiCompatible: true);

        Assert.Equal("model not found", piece.Error);
    }

    [Fact]
    public void 带finish_reason的收尾片也要把正文一起收下()
    {
        // 真流式里最后一片常常"既带正文又带收尾标记"——只认收尾不认正文就会**悄悄少掉最后一截内容**。
        // 注意形状：finish_reason 是 **choice 的**属性，所以 delta 要先闭合（写错过一次：少一个 }，
        // 那种 JSON 是非法的，解析器会正确地整片丢掉——这条用例顺带把"非法片不当成内容"也钉住了）。
        var piece = OllamaVisionClient.ParseStreamLine(
            """data: {"choices":[{"delta":{"content":"[\"第1列是货号\"]}"},"finish_reason":"stop"}]}""",
            openAiCompatible: true);

        Assert.Equal("""["第1列是货号"]}""", piece.Content);
        Assert.True(piece.Done);
    }

    [Fact]
    public void 本机Ollama的逐行JSON也认_它用thinking字段()
    {
        var think = OllamaVisionClient.ParseStreamLine(
            """{"message":{"thinking":"先看列名"},"done":false}""", openAiCompatible: false);
        var text = OllamaVisionClient.ParseStreamLine(
            """{"message":{"content":"第2列是件数"},"done":false}""", openAiCompatible: false);
        var done = OllamaVisionClient.ParseStreamLine(
            """{"message":{"content":""},"done":true}""", openAiCompatible: false);

        Assert.Equal("先看列名", think.Reasoning);
        Assert.Equal("第2列是件数", text.Content);
        Assert.True(done.Done);
    }

    // ─────────────────────── 整条请求：假端点喂真分片 ───────────────────────

    /// <summary>同步收集片段。<strong>刻意不用 <c>Progress&lt;T&gt;</c></strong>——那个会异步投递，断言就不确定了。</summary>
    private sealed class Collector : IProgress<string>
    {
        public readonly StringBuilder All = new();
        public int Calls;
        public void Report(string value) { Calls++; All.Append(value); }
    }

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK ? "text/event-stream" : "text/plain"),
            };
        }
    }

    private static RecognitionSettings Cloud() => new()
    {
        Provider = RecognitionSettings.Providers.OpenAi,
        Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1",
        Model = "qwen3.8-flash",
        ApiKey = "sk-测试用的假密钥",
    };

    private static AiChatTurn[] Ask() => new[] { new AiChatTurn(AiChatTurn.User, "看这张表") };

    [Fact]
    public async Task 流式走通_思考与正文都拼回完整一份且请求带了stream()
    {
        // 两片思考、两片正文、一片收尾——照百炼 SSE 的真实形状写（含空行心跳）。
        const string sse = """
            data: {"choices":[{"delta":{"reasoning_content":"先看这张表的列名，"}}]}

            data: {"choices":[{"delta":{"reasoning_content":"再判哪几行是合计。"}}]}

            data: {"choices":[{"delta":{"content":"{\"facts\":"}}]}
            data: {"choices":[{"delta":{"content":"[\"第1列是货号\"]}"},"finish_reason":"stop"}]}
            data: [DONE]

            """;
        var handler = new StubHandler(sse);
        var reasoning = new Collector();
        var content = new Collector();

        var outcome = await OllamaVisionClient.ChatWithImagesStreamAsync(
            Cloud(), Ask(), images: null, reasoning, content, CancellationToken.None, handler);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("""{"facts":["第1列是货号"]}""", outcome.Text);
        Assert.Contains("先看这张表的列名", outcome.Reasoning);
        Assert.Contains("再判哪几行是合计", outcome.Reasoning);

        // 边到边报：思考与正文各报了 2 片——这正是"边想边滚"的判据（只在最后给一次就等于没流式）。
        Assert.Equal(2, reasoning.Calls);
        Assert.Equal(2, content.Calls);
        Assert.Equal(outcome.Reasoning, reasoning.All.ToString());

        // 请求体必须真的带 stream:true，否则服务端会整段回一次，流式就是空的。
        Assert.Contains("\"stream\":true", handler.LastBody);
    }

    [Fact]
    public async Task 非2xx时不假装成功_调用方据此可以退回非流式()
    {
        var handler = new StubHandler("stream not supported", HttpStatusCode.BadRequest);

        var outcome = await OllamaVisionClient.ChatWithImagesStreamAsync(
            Cloud(), Ask(), images: null, onReasoning: null, onContent: null, CancellationToken.None, handler);

        Assert.False(outcome.Ok);
        Assert.Contains("400", outcome.Error ?? string.Empty);
    }
}
