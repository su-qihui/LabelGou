using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.Services.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 自由对话通道（<see cref="OllamaVisionClient.ChatAsync"/> 与 <see cref="AiChatHistory"/>）的钉子 —— 第 10 棒。
/// <para>为什么单开一条通道并单独钉住：此前只有「抽字段」那一条（固定提示词 + <c>response_format=json_object</c>
/// + 出来必过 <c>LlmFieldJsonParser</c>），拿它聊天会得到一句「没解析出 JSON」，而模型其实好好答了。
/// 用户要的是「能和 AI 沟通去调整」，那就必须有一条<strong>不解析、不结构化、不落库</strong>的路。</para>
/// <para>这里全部走假 handler，不真连公网（真连的 A/B 证据在 <c>_probe\b10-net\</c>）。</para>
/// </summary>
public class AiChatTests
{
    /// <summary>只记请求、按脚本回话的假端点。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        public readonly List<string> Bodies = new();
        public string Reply = @"{""choices"":[{""message"":{""content"":""绑件号(本箱)，别绑总件数""}}]}";
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Urls.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Reply, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static RecognitionSettings Cloud()
    {
        return new RecognitionSettings
        {
            Provider = RecognitionSettings.Providers.OpenAi,
            Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Model = "qwen-vl-max",
            ApiKey = "sk-test-key",
        };
    }

    private static RecognitionSettings Local()
    {
        return new RecognitionSettings
        {
            Provider = RecognitionSettings.Providers.Ollama,
            Endpoint = "http://127.0.0.1:11434",
            Model = "qwen3-vl:4b",
        };
    }

    private static AiChatTurn Turn(string role, string text) => new AiChatTurn(role, text);

    /// <summary>WPF 对象线程亲和，必须在 STA 线程上造（同其它几个测试里的 <c>OnSta</c> 写法）。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static JsonElement Payload(string body) => JsonDocument.Parse(body).RootElement;

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + 1, System.StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    [Fact]
    public async Task 多轮历史整份发出去且不强要JSON()
    {
        var stub = new StubHandler();
        var turns = new List<AiChatTurn>
        {
            Turn(AiChatTurn.System, "你是唛头助手"),
            Turn(AiChatTurn.User, "第四行该绑哪个字段"),
            Turn(AiChatTurn.Assistant, "绑件号(本箱)"),
            Turn(AiChatTurn.User, "那总件数呢"),
        };

        var outcome = await OllamaVisionClient.ChatAsync(Cloud(), turns, null, CancellationToken.None, stub);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("绑件号(本箱)，别绑总件数", outcome.Text);          // 原话交回，不做二次解析
        var messages = Payload(stub.Bodies[0]).GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());                     // 少发一轮模型就接不上话
        var roles = new List<string>();
        foreach (var m in messages.EnumerateArray()) roles.Add(m.GetProperty("role").GetString()!);
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, roles);
        Assert.Equal(JsonValueKind.String, messages[3].GetProperty("content").ValueKind);
        Assert.False(stub.Bodies[0].Contains("response_format", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task 附图只挂在最后一条用户消息上()
    {
        var stub = new StubHandler();
        var turns = new List<AiChatTurn>
        {
            Turn(AiChatTurn.User, "第一张怎么看"),
            Turn(AiChatTurn.Assistant, "照原样印"),
            Turn(AiChatTurn.User, "这张呢"),
        };

        await OllamaVisionClient.ChatAsync(
            Cloud(), turns, ("AmplyTe3BaseNkNvbnRlbnQ=", "image/jpeg"), CancellationToken.None, stub);

        var body = stub.Bodies[0];
        // 图不能每轮重发：一张 2000px 的图 base64 上去就是几百 KB，多轮就是几 MB
        Assert.Equal(1, CountOf(body, "data:image/jpeg;base64,"));    // 这才是真保证：图只有一份
        Assert.Equal(2, CountOf(body, "image_url"));                  // 一份图在 JSON 里占两处："type":"image_url" 与 "image_url":{…}
        var messages = Payload(body).GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages[2].GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.String, messages[0].GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task 本机Ollama走api_chat并取回message_content()
    {
        var stub = new StubHandler { Reply = @"{""message"":{""role"":""assistant"",""content"":""行高给到 38pt 就放得下""}}" };

        var outcome = await OllamaVisionClient.ChatAsync(
            Local(), new List<AiChatTurn> { Turn(AiChatTurn.User, "这行放不下怎么办") },
            null, CancellationToken.None, stub);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("行高给到 38pt 就放得下", outcome.Text);
        Assert.True(stub.Urls[0].EndsWith("/api/chat", System.StringComparison.Ordinal), stub.Urls[0]);
        Assert.False(Payload(stub.Bodies[0]).TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task 服务报错时把状态码与原话一起给出()
    {
        var stub = new StubHandler
        {
            Status = HttpStatusCode.BadRequest,
            Reply = @"{""error"":{""message"":""model not found: qwen3.8-flash""}}",
        };

        var outcome = await OllamaVisionClient.ChatAsync(
            Cloud(), new List<AiChatTurn> { Turn(AiChatTurn.User, "在吗") }, null, CancellationToken.None, stub);

        Assert.False(outcome.Ok);
        Assert.Contains("400", outcome.Error!, System.StringComparison.Ordinal);
        Assert.Contains("model not found", outcome.Error!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task 用户取消时正常返回不抛异常()
    {
        // 第 23 棒：旧过滤器把用户取消的 OCE 放出去，面板的 async void 接不住，
        // 点「停止」必弹 App 级错误框。取消必须变成「不 Ok 也不报错」的正常结果。
        using var cts = new CancellationTokenSource();
        var stub = new CancellingHandler(cts);

        var outcome = await OllamaVisionClient.ChatAsync(
            Cloud(), new List<AiChatTurn> { Turn(AiChatTurn.User, "在吗") }, null, cts.Token, stub);

        Assert.False(outcome.Ok);
        Assert.Null(outcome.Error);        // 不是错误：面板那局「已停止这一轮…」靠这个接手
        Assert.Null(outcome.Text);
    }

    /// <summary>收到请求就取消 token——模拟用户在请求在飞时点了「停止」。</summary>
    private sealed class CancellingHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }
    }

    [Fact]
    public async Task 没填模型时一个请求也不发()
    {
        var stub = new StubHandler();
        var settings = new RecognitionSettings
        {
            Provider = RecognitionSettings.Providers.OpenAi,
            Endpoint = "https://x.example/v1",
            Model = "",
        };

        var outcome = await OllamaVisionClient.ChatAsync(
            settings, new List<AiChatTurn> { Turn(AiChatTurn.User, "在吗") }, null, CancellationToken.None, stub);

        Assert.False(outcome.Ok);
        Assert.Empty(stub.Urls);        // 白跑一次 HTTP 就等于把用户的话发出去了
        Assert.Contains("模型", outcome.Error!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 对话窗口能真建起来()
    {
        // 窗口全代码构建，建不起来就是控件树写错；不依赖 App.xaml 资源（§五-71 那个坑在这里不适用）。
        // 断言必须留在 STA 线程里做：跨线程读 Window 的属性会抛
        // “The calling thread cannot access this object because a different thread owns it”（实测踩过）。
        var probe = OnSta(() =>
        {
            var w = new AiChatWindow();
            return (Built: true, HasContent: w.Content is not null, Title: w.Title);
        });

        Assert.True(probe.Built);
        Assert.True(probe.HasContent);                        // 控件树真装上了
        Assert.Contains("对话", probe.Title, System.StringComparison.Ordinal);
    }

    [Fact]
    public void 上下文只带最近若干轮但system永远在第一位()
    {
        var turns = new List<AiChatTurn> { Turn(AiChatTurn.System, "身份说明") };
        for (var i = 1; i <= 25; i++) turns.Add(Turn(AiChatTurn.User, "第 " + i + " 问"));

        var sent = AiChatHistory.BuildForRequest(turns);

        Assert.Equal(AiChatHistory.MaxTurns + 1, sent.Count);           // +1 是那条 system
        Assert.Equal("身份说明", sent[0].Text);
        Assert.Equal("第 25 问", sent[sent.Count - 1].Text);            // 刚说的话必须在，丢的只能是最早的
        Assert.Equal("第 6 问", sent[1].Text);
        Assert.Equal(5, AiChatHistory.DroppedTurns(turns));             // 省略了几轮要能报出来，界面得说实话
    }

    [Fact]
    public void 单条过长会被截断并说明()
    {
        var longText = new string('字', AiChatHistory.MaxCharsPerTurn + 500);
        var turns = new List<AiChatTurn> { Turn(AiChatTurn.User, longText) };

        var sent = AiChatHistory.BuildForRequest(turns);

        Assert.Contains("已截断", sent[0].Text, System.StringComparison.Ordinal);
        Assert.True(sent[0].Text.Length < longText.Length);
    }
}
