using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LabelGou.App.Services.Recognition;

/// <summary>
/// <see cref="OllamaVisionClient"/> 的<b>自由对话</b>分支（第 10 棒）。
/// <para>为什么要单独一条：用户问「没有 AI 对话窗口，没法和 AI 沟通去调整」——现有的两条入口
/// （<c>AskFieldsAsync</c> 与 <c>AskFieldsFromOcrLinesAsync</c>）都是<strong>抽字段</strong>：
/// 固定提示词、<c>response_format=json_object</c>、出来还要过 <see cref="LabelGou.Core.Recognition.LlmFieldJsonParser"/>。
/// 拿它聊天会得到一句「没解析出 JSON」，而模型其实好好答了。所以这里单开一条：
/// <b>不解析 JSON、不带 response_format、不落库、不碰毫米</b>，只把原话交回界面（§五-10 / §七-11 的红线）。</para>
/// <para>多轮：OpenAI 兼容与 Ollama 都是把整份 messages 重发一遍，历史由
/// <see cref="AiChatHistory.BuildForRequest"/> 裁过，不在这儿管。</para>
/// </summary>
public static partial class OllamaVisionClient
{
    /// <param name="settings">通道设置（端点/模型/密钥/超时），与识别用的是同一份，不另建配置。</param>
    /// <param name="turns">对话历史，顺序即时间顺序；<c>system</c> 只认第一条。</param>
    /// <param name="image">本次要附的图（base64 + 真实 MIME），只挂在最后一条 user 上。</param>
    public static async Task<ChatOutcome> ChatAsync(
        RecognitionSettings settings,
        IReadOnlyList<AiChatTurn> turns,
        (string Base64, string MimeType)? image = null,
        CancellationToken cancel = default,
        HttpMessageHandler? handler = null)
    {
        if (turns.Count == 0) return new ChatOutcome { Error = "没有要问的话。" };
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model))
            return new ChatOutcome { Error = "没有填服务地址或模型名（先在「模型（AI）设置与调试」里选好）。" };

        return settings.Provider == RecognitionSettings.Providers.OpenAi
            ? await ChatOpenAiAsync(settings, turns, image, cancel, handler).ConfigureAwait(false)
            : await ChatOllamaAsync(settings, turns, image, cancel, handler).ConfigureAwait(false);
    }

    /// <summary>OpenAI 兼容协议：messages 里每条一个 role，带图时最后一条 user 的 content 是数组。</summary>
    private static async Task<ChatOutcome> ChatOpenAiAsync(
        RecognitionSettings settings, IReadOnlyList<AiChatTurn> turns,
        (string Base64, string MimeType)? image, CancellationToken cancel, HttpMessageHandler? handler)
    {
        var key = settings.ResolveApiKey();
        if (key is null)
            return new ChatOutcome { Error = $"云端没有 API 密钥（填设置里的 apiKey，或设环境变量 {settings.ApiKeyEnvVar}）。" };

        var lastUser = -1;
        if (image is not null)
            for (var i = turns.Count - 1; i >= 0; i--)
                if (turns[i].Role == AiChatTurn.User) { lastUser = i; break; }

        var messages = new List<object?>(turns.Count);
        for (var i = 0; i < turns.Count; i++)
        {
            if (i == lastUser)
            {
                var (b64, mime) = image!.Value;
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = turns[i].Role,
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["type"] = "text", ["text"] = turns[i].Text },
                        new Dictionary<string, object?>
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object?> { ["url"] = $"data:{mime};base64,{b64}" },
                        },
                    },
                });
            }
            else
            {
                messages.Add(new Dictionary<string, object?> { ["role"] = turns[i].Role, ["content"] = turns[i].Text });
            }
        }

        // 刻意不发 response_format、不发 temperature：这是聊天，不是抽字段，套上 json_object 模型就只能回 JSON。
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
        });

        return await PostChatAsync(settings, OpenAiUrl(settings.Endpoint, "chat/completions"), payload,
            extractOpenAi, $"Bearer {key}", cancel, handler).ConfigureAwait(false);
    }

    /// <summary>本机 Ollama：<c>/api/chat</c> 收 messages，回 <c>message.content</c>。</summary>
    private static async Task<ChatOutcome> ChatOllamaAsync(
        RecognitionSettings settings, IReadOnlyList<AiChatTurn> turns,
        (string Base64, string MimeType)? image, CancellationToken cancel, HttpMessageHandler? handler)
    {
        var lastUser = -1;
        if (image is not null)
            for (var i = turns.Count - 1; i >= 0; i--)
                if (turns[i].Role == AiChatTurn.User) { lastUser = i; break; }

        var messages = new List<object?>(turns.Count);
        for (var i = 0; i < turns.Count; i++)
        {
            var msg = new Dictionary<string, object?> { ["role"] = turns[i].Role, ["content"] = turns[i].Text };
            if (i == lastUser) msg["images"] = new[] { image!.Value.Base64 };   // Ollama 的图挂在消息上，不走 data URL
            messages.Add(msg);
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
            ["stream"] = false,
        });

        return await PostChatAsync(settings, settings.Endpoint.TrimEnd('/') + "/api/chat", payload,
            extractOllama, authorization: null, cancel, handler).ConfigureAwait(false);
    }

    private static async Task<ChatOutcome> PostChatAsync(
        RecognitionSettings settings, string url, string payload,
        Func<string, (string? Text, string? ServerError)> extract,
        string? authorization, CancellationToken cancel, HttpMessageHandler? handler)
    {
        var seconds = Math.Max(10, settings.TimeoutSeconds);
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            if (authorization is not null)
                request.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var response = await ClientFor(handler).SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
                return new ChatOutcome { Error = $"服务返回 {(int)response.StatusCode}：{Brief(body)}", Elapsed = sw.Elapsed, Raw = body };

            var (text, serverError) = extract(body);
            if (serverError is not null)
                return new ChatOutcome { Error = serverError, Elapsed = sw.Elapsed, Raw = text };
            if (string.IsNullOrWhiteSpace(text))
                return new ChatOutcome { Error = "模型没回话（返回里没有 content）。", Elapsed = sw.Elapsed, Raw = body };
            return new ChatOutcome { Text = text, Elapsed = sw.Elapsed, Raw = text };
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            sw.Stop();
            return new ChatOutcome { Error = $"{seconds} 秒内没等到 {url} 的回答（云端慢就把超时调大）。", Elapsed = sw.Elapsed };
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return new ChatOutcome { Error = DualStackConnect.Talk(ex), Elapsed = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ChatOutcome { Error = $"对话请求失败：{ex.Message}", Elapsed = sw.Elapsed };
        }
    }

    private static (string? Text, string? ServerError) extractOpenAi(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? err = root.TryGetProperty("error", out var e)
                ? (e.TryGetProperty("message", out var em) ? em.GetString() : e.ToString())
                : null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        return (c.GetString(), err);
                    break;
                }
            return (null, err);
        }
        catch (JsonException)
        {
            return (null, "服务返回的不是 JSON（地址可能不是 OpenAI 兼容端点）。");
        }
    }

    private static (string? Text, string? ServerError) extractOllama(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (root.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                return (c.GetString(), err);
            return (null, err);
        }
        catch (JsonException)
        {
            return (null, "服务返回的不是 JSON（地址可能不是 Ollama）。");
        }
    }
}
