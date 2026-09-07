using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using LabelGou.App.Services.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 云端通道（OpenAI 兼容协议：阿里云百炼、DeepSeek、vLLM）的钉子。
/// <para>为什么要有这一档：本机 2.3B 视觉模型实测 243 秒/张，用户明确要求支持云端。
/// 但云端有三条不能靠人记住的规矩，所以全部钉成测试：① 密钥优先环境变量（设置文件是明文）；
/// ② 端点不是 127.0.0.1 就必须被认成"数据出网"；③ 不支持图片的模型（DeepSeek 的 deepseek-chat）
/// 绝不能被偷偷发图——那会换来一句用户看不懂的 400。</para>
/// <para>这里全部走 <see cref="StubHandler"/>，不真连公网。</para>
/// </summary>
public class CloudChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "labelgou-cloud-" + Guid.NewGuid().ToString("N")[..8]);

    public CloudChannelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    private static RecognitionSettings Cloud(string provider = RecognitionSettings.Providers.OpenAi) => new()
    {
        Provider = provider,
        Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1",
        Model = "qwen-vl-max",
        ApiKey = "sk-test-key",
    };

    private string MakePng()
    {
        var path = Path.Combine(_dir, "sample.png");
        // 1x1 像素的合法 PNG：客户端只会把它 base64 内联，不关心内容
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        return path;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = new();
        public List<string?> AuthHeaders { get; } = new();
        public List<string> RequestBodies { get; } = new();
        public Func<HttpRequestMessage, string> Responder { get; set; } = _ =>
            """{"choices":[{"message":{"content":"{\"ItemNo\":\"olu830-35\"}"}}]}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Responder(request), Encoding.UTF8, "application/json"),
            };
        }
    }

    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions")]
    [InlineData("http://127.0.0.1:11434/v1", "http://127.0.0.1:11434/v1/models")]
    public void 基地址带不带v1都只补一次(string endpoint, string expected)
    {
        var tail = expected.EndsWith("models") ? "models" : "chat/completions";
        Assert.Equal(expected, OllamaVisionClient.OpenAiUrl(endpoint, tail));
    }

    [Fact]
    public async Task 云端请求走chatcompletions并带密钥和图片()
    {
        var handler = new StubHandler();
        var settings = Cloud();

        var outcome = await OllamaVisionClient.AskFieldsAsync(settings, MakePng(), default, handler);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("""{"ItemNo":"olu830-35"}""", outcome.Json);
        Assert.Single(handler.RequestUris);
        Assert.EndsWith("/v1/chat/completions", handler.RequestUris[0]);
        Assert.Equal("Bearer sk-test-key", handler.AuthHeaders[0]);
        Assert.Contains("image_url", handler.RequestBodies[0]);
        Assert.Contains("qwen-vl-max", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task 没有密钥时不发请求()
    {
        var handler = new StubHandler();
        var settings = Cloud();
        settings.ApiKey = null;

        var outcome = await OllamaVisionClient.AskFieldsAsync(settings, MakePng(), default, handler);

        Assert.False(outcome.Ok);
        Assert.Contains("API 密钥", outcome.Error);
        Assert.Empty(handler.RequestUris);   // 一个字节都不该发出去：没有密钥的请求只会浪费配额并留下日志
    }

    [Fact]
    public async Task 环境变量里的密钥优先于设置文件里的那个()
    {
        var envVar = "LABELGOU_CLOUD_TEST_KEY";
        Environment.SetEnvironmentVariable(envVar, "sk-from-env");
        try
        {
            var handler = new StubHandler();
            var settings = Cloud();
            settings.ApiKeyEnvVar = envVar;

            await OllamaVisionClient.AskFieldsAsync(settings, MakePng(), default, handler);

            Assert.Equal("Bearer sk-from-env", handler.AuthHeaders[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task 不支持图片的模型会被明确拒绝而不是发图过去()
    {
        var handler = new StubHandler();
        var settings = Cloud();
        settings.Model = "deepseek-chat";
        settings.ModelAcceptsImages = false;

        var outcome = await OllamaVisionClient.AskFieldsAsync(settings, MakePng(), default, handler);

        Assert.False(outcome.Ok);
        Assert.Contains("不支持图片", outcome.Error);
        Assert.Contains("OCR", outcome.Error);   // 要顺手告诉他该走哪条路
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public void 云端预设会把通道标成数据出网()
    {
        var settings = new RecognitionSettings();
        Assert.True(settings.StaysOnThisMachine);   // 默认指向 127.0.0.1

        var bailian = RecognitionSettings.CloudPresets.First(p => p.Name.Contains("DashScope"));
        settings.ApplyPreset(bailian);

        Assert.Equal(RecognitionSettings.Providers.OpenAi, settings.Provider);
        Assert.False(settings.StaysOnThisMachine);
        Assert.Contains("数据会离开这台电脑", settings.DescribeChannels());
        Assert.True(settings.ModelAcceptsImages);   // 百炼这个预设是看图的

        var deepseek = RecognitionSettings.CloudPresets.First(p => p.Name.Contains("DeepSeek"));
        settings.ApplyPreset(deepseek);
        Assert.False(settings.ModelAcceptsImages);  // DeepSeek 只能整理 OCR 文字
        Assert.Contains("不看图", settings.DescribeChannels());
    }

    [Theory]
    [InlineData("qwen-vl-max", true)]
    [InlineData("qwen3.5:2b", true)]        // 本机这个就带 vision 能力
    [InlineData("qwen3-max", true)]          // 认不出吃不吃图时默认吃图：发图只会换来一句可见的接口错误，比默默丢图好
    [InlineData("deepseek-chat", false)]
    [InlineData("deepseek-reasoner", false)]
    [InlineData("", true)]                  // 猜不出来时默认吃图：宁可在发图时报错，也不要默默丢图
    public void 按模型名猜吃不吃图(string modelId, bool expected)
        => Assert.Equal(expected, RecognitionSettings.GuessAcceptsImages(modelId));

    [Fact]
    public async Task 拉云端模型列表取data里的id()
    {
        var handler = new StubHandler
        {
            Responder = _ => """{"data":[{"id":"qwen-vl-max"},{"id":"qwen3-max"},{"id":"deepseek-chat"}]}""",
        };

        var (ids, error) = await OllamaVisionClient.ListModelsAsync(Cloud(), default, handler);

        Assert.Null(error);
        Assert.Equal(3, ids.Count);
        Assert.Contains("qwen3-max", ids);   // 用户要点名的那种型号必须在列表里能选到
        Assert.EndsWith("/v1/models", handler.RequestUris[0]);
        Assert.Equal("Bearer sk-test-key", handler.AuthHeaders[0]);
    }

    [Fact]
    public async Task 拉本机ollama模型列表走apitags不需要密钥()
    {
        var handler = new StubHandler
        {
            Responder = _ => """{"models":[{"name":"qwen3.5:2b"},{"name":"qwen3-vl:4b"}]}""",
        };
        var settings = new RecognitionSettings();   // 默认就是本机 ollama

        var (ids, error) = await OllamaVisionClient.ListModelsAsync(settings, default, handler);

        Assert.Null(error);
        Assert.Equal(new[] { "qwen3.5:2b", "qwen3-vl:4b" }, ids);
        Assert.EndsWith("/api/tags", handler.RequestUris[0]);
        Assert.Null(handler.AuthHeaders[0]);
    }

    [Fact]
    public async Task 云端没密钥时不拉列表并说清怎么填()
    {
        var handler = new StubHandler();
        var settings = Cloud();
        settings.ApiKey = null;

        var (ids, error) = await OllamaVisionClient.ListModelsAsync(settings, default, handler);

        Assert.Empty(ids);
        Assert.NotNull(error);
        Assert.Contains("密钥", error);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task 协议是ollama时仍然走原生接口不串味()
    {
        var handler = new StubHandler { Responder = _ => """{"response":"{\"ItemNo\":\"olu830-35\"}"}""" };
        var settings = Cloud(RecognitionSettings.Providers.Ollama);
        settings.Endpoint = "http://127.0.0.1:11434";
        settings.Model = "qwen3-vl:4b";

        var outcome = await OllamaVisionClient.AskFieldsAsync(settings, MakePng(), default, handler);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.EndsWith("/api/generate", handler.RequestUris[0]);
        Assert.Contains("\"images\"", handler.RequestBodies[0]);
        Assert.Null(handler.AuthHeaders[0]);   // 本机不需要密钥，也别把测试用的 key 发到 127.0.0.1 上
    }
}
