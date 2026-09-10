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
    public static Task<ChatOutcome> ChatAsync(
        RecognitionSettings settings,
        IReadOnlyList<AiChatTurn> turns,
        (string Base64, string MimeType)? image = null,
        CancellationToken cancel = default,
        HttpMessageHandler? handler = null)
        => ChatWithImagesAsync(settings, turns,
            image is null ? null : new[] { image.Value }, cancel, handler);

    /// <summary>
    /// 多图版（第 20 棒）：一张表右侧常贴两三张不同客户/不同面的样张，
    /// 只挑一张发过去等于让模型看半边拼图，它还当自己看全了。
    /// </summary>
    public static async Task<ChatOutcome> ChatWithImagesAsync(
        RecognitionSettings settings,
        IReadOnlyList<AiChatTurn> turns,
        IReadOnlyList<(string Base64, string MimeType)>? images,
        CancellationToken cancel = default,
        HttpMessageHandler? handler = null)
    {
        if (turns.Count == 0) return new ChatOutcome { Error = "没有要问的话。" };
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model))
            return new ChatOutcome { Error = "没有填服务地址或模型名（先在「模型（AI）设置与调试」里选好）。" };

        return settings.Provider == RecognitionSettings.Providers.OpenAi
            ? await ChatOpenAiAsync(settings, turns, images, cancel, handler).ConfigureAwait(false)
            : await ChatOllamaAsync(settings, turns, images, cancel, handler).ConfigureAwait(false);
    }

    /// <summary>OpenAI 兼容协议：messages 里每条一个 role，带图时最后一条 user 的 content 是数组。</summary>
    private static async Task<ChatOutcome> ChatOpenAiAsync(
        RecognitionSettings settings, IReadOnlyList<AiChatTurn> turns,
        IReadOnlyList<(string Base64, string MimeType)>? images, CancellationToken cancel, HttpMessageHandler? handler)
    {
        var key = settings.ResolveApiKey();
        if (key is null)
            return new ChatOutcome { Error = settings.MissingKeyHint };

        var lastUser = -1;
        if (images is { Count: > 0 })
            for (var i = turns.Count - 1; i >= 0; i--)
                if (turns[i].Role == AiChatTurn.User) { lastUser = i; break; }

        var messages = new List<object?>(turns.Count);
        for (var i = 0; i < turns.Count; i++)
        {
            if (i == lastUser)
            {
                var content = new List<object?>
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = turns[i].Text },
                };
                // 先文字后图：云端按内容顺序读，把「这是你看到的表」那句摆在像素前面
                foreach (var (b64, mime) in images!)
                    content.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new Dictionary<string, object?> { ["url"] = $"data:{mime};base64,{b64}" },
                    });
                messages.Add(new Dictionary<string, object?> { ["role"] = turns[i].Role, ["content"] = content });
            }
            else
            {
                messages.Add(new Dictionary<string, object?> { ["role"] = turns[i].Role, ["content"] = turns[i].Text });
            }
        }

        // 刻意不发 response_format、不带 temperature：这是聊天，不是抽字段，套上 json_object 模型就只能回 JSON。
        // 思考档（第 27 棒）：聊天/提案是最慢的一条路，用户可选关思考或降档换速度。
        var body = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
        };
        settings.ApplyThinkingTo(body);
        var payload = JsonSerializer.Serialize(body);

        return await PostChatAsync(settings, OpenAiUrl(settings.Endpoint, "chat/completions"), payload,
            extractOpenAi, $"Bearer {key}", cancel, handler).ConfigureAwait(false);
    }

    /// <summary>本机 Ollama：<c>/api/chat</c> 收 messages，回 <c>message.content</c>。</summary>
    private static async Task<ChatOutcome> ChatOllamaAsync(
        RecognitionSettings settings, IReadOnlyList<AiChatTurn> turns,
        IReadOnlyList<(string Base64, string MimeType)>? images, CancellationToken cancel, HttpMessageHandler? handler)
    {
        var lastUser = -1;
        if (images is { Count: > 0 })
            for (var i = turns.Count - 1; i >= 0; i--)
                if (turns[i].Role == AiChatTurn.User) { lastUser = i; break; }

        var messages = new List<object?>(turns.Count);
        for (var i = 0; i < turns.Count; i++)
        {
            var msg = new Dictionary<string, object?> { ["role"] = turns[i].Role, ["content"] = turns[i].Text };
            // Ollama 的图挂在消息上（本来就是一个数组），不走 data URL
            if (i == lastUser) msg["images"] = images!.Select(x => x.Base64).ToArray();
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
        // 超时口径（第 27 棒）：0 = 不设限，只靠手动停止——用户实测 180 秒硬顶会拦下真在算的云端请求，当日改口径。
        var seconds = settings.EffectiveTimeoutSeconds;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        var sw = Stopwatch.StartNew();
        ChatOutcome outcome;
        AppLog.Info($"AI 聊天/提案请求发出 → {host}（请求体约 {payload.Length / 1024} KB，{(seconds == 0 ? "不设时限" : $"上限 {seconds} 秒")}）");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            if (seconds > 0) timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
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
                outcome = new ChatOutcome { Error = $"服务返回 {(int)response.StatusCode}：{Brief(body)}", Elapsed = sw.Elapsed, Raw = body };
            else
            {
                var (text, serverError) = extract(body);
                outcome = serverError is not null
                    ? new ChatOutcome { Error = serverError, Elapsed = sw.Elapsed, Raw = text }
                    : string.IsNullOrWhiteSpace(text)
                        ? new ChatOutcome { Error = "模型没回话（返回里没有 content）。", Elapsed = sw.Elapsed, Raw = body }
                        : new ChatOutcome { Text = text, Elapsed = sw.Elapsed, Raw = text };
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            sw.Stop();
            // 用户点了「停止」：正常返回（不报错），调用方那句「已停止这一轮…」才有机会接手。
            // 旧过滤器把用户取消的 OCE 放出去，面板的 async void 没人接，直接弹 App 级错误框（第 23 棒）。
            outcome = new ChatOutcome { Elapsed = sw.Elapsed };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            outcome = new ChatOutcome
            {
                Error = $"{seconds} 秒内没等到 {host} 的回答。嫌慢就去「打开通道设置…」把思考档调到 low 或关思考，"
                      + "也可以把超时填 0＝不设限（只靠手动停止）；想再试就人手再点一次，不自动重试。",
                Elapsed = sw.Elapsed,
            };
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            outcome = new ChatOutcome { Error = DualStackConnect.Talk(ex), Elapsed = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            outcome = new ChatOutcome { Error = $"对话请求失败：{ex.Message}", Elapsed = sw.Elapsed };
        }
        // 收尾必收账（第 25 棒）：旧版这条链路一条日志不打，卡死了无从分诊「没发出」还是「没回来」。
        if (outcome.Error is null)
            AppLog.Info(cancel.IsCancellationRequested
                ? $"AI 聊天/提案被手动停止：{host} 跑了 {outcome.Elapsed.TotalSeconds:0.0} 秒。"
                : $"AI 聊天/提案完成：{host} 用了 {outcome.Elapsed.TotalSeconds:0.0} 秒，回了约 {(outcome.Text?.Length ?? 0) / 1024} KB。");
        else
            AppLog.Info($"AI 聊天/提案未成：{host} 用了 {outcome.Elapsed.TotalSeconds:0.0} 秒 —— {outcome.Error}");
        return outcome;
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
