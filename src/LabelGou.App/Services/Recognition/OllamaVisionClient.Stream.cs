using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LabelGou.App.Services.Recognition;

/// <summary>
/// <see cref="OllamaVisionClient"/> 的**流式**分支（第 31 棒）。
/// <para><strong>为什么要它</strong>：用户 2026-09-10 实测后明确要求「AI 再思考时可以选择展开或者关闭思考内容」，
/// 而且他当时正在追"AI 排版为什么这么差"——<strong>思考过程是判断"它到底有没有读懂这张表"的唯一材料</strong>。
/// 不流式的话，思考内容只有等整段回答回来才拿得到，对"等了半天以为卡住了"毫无帮助（读一张表要一两分钟）。</para>
/// <para><strong>与那条老路的关系</strong>：老路（<c>PostChatAsync</c>）一个字不动，这条是并行的第二条；
/// 超时口径（0 = 不设限）、手动停止、错误分诊与收尾打点<strong>全部照抄同一套</strong>——
/// 第 25/26 棒那三条教训（超时硬顶拦下真在算的请求、连接池闲置死线、卡住了无从分诊）不许因为"换了条路"复发。</para>
/// <para>供应商差异：OpenAI 兼容端点是 SSE（<c>data: {...}</c> 行 + <c>data: [DONE]</c>）；
/// 本机 Ollama 是逐行 JSON（NDJSON，<c>done: true</c> 收尾）。两种都在
/// <see cref="ParseStreamLine"/> 里认，且那个函数是**纯函数**——分片形状的花样太多，不联网就钉不住它。</para>
/// </summary>
public static partial class OllamaVisionClient
{
    /// <summary>流式响应的**一行**解析结果。</summary>
    /// <param name="Reasoning">这一片里的思考内容（没有就是 null）。</param>
    /// <param name="Content">这一片里的正式回答（没有就是 null）。</param>
    /// <param name="Done">这一片说明流结束了。</param>
    /// <param name="Error">这一片里带回了服务端错误。</param>
    public readonly record struct StreamPiece(string? Reasoning, string? Content, bool Done, string? Error);

    /// <summary>
    /// 一行流式文本 → 这一片里有什么。
    /// <para><strong>为什么单独抽出来</strong>：SSE 的分片形状比"整段回一次"复杂得多——<c>data:</c> 前缀、
    /// <c>[DONE]</c> 收尾、空行心跳、半截 JSON、以及"思考"与"正文"分成两个字段（
    /// 百炼/DeepSeek 用 <c>delta.reasoning_content</c>，Ollama 用 <c>message.thinking</c>）。
    /// 这些只有真发一次请求才验得了的话，这条链就只能靠"看着像能用"。纯函数 + 真分片文本喂进去，才算钉住。</para>
    /// <para>认不出的一律返回空片（不算错）：各家会夹带自己的心跳与注释行，把它们当错会误报。</para>
    /// </summary>
    /// <param name="line">原始一行（含前缀，含可能的 \r）。</param>
    /// <param name="openAiCompatible">true = SSE（<c>data:</c>），false = Ollama 的逐行 JSON。</param>
    public static StreamPiece ParseStreamLine(string line, bool openAiCompatible)
    {
        var text = line?.Trim() ?? string.Empty;
        if (text.Length == 0) return default;
        if (text.StartsWith(":", StringComparison.Ordinal)) return default;          // SSE 注释/心跳

        if (openAiCompatible)
        {
            if (!text.StartsWith("data:", StringComparison.Ordinal)) return default;
            var payload = text[5..].Trim();
            if (payload.Length == 0) return default;
            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                return new StreamPiece(null, null, Done: true, Error: null);
            return ParseJsonPiece(payload, ollama: false);
        }

        return ParseJsonPiece(text, ollama: true);
    }

    private static StreamPiece ParseJsonPiece(string json, bool ollama)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;

            // 错误：两家的形状差不多，都在顶层 error 里（可能是字符串也可能是对象）。
            if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            {
                var message = err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString();
                if (!string.IsNullOrWhiteSpace(message)) return new StreamPiece(null, null, false, message);
            }

            string? reasoning = null;
            string? content = null;
            var done = false;

            if (ollama)
            {
                if (root.TryGetProperty("done", out var d) && d.ValueKind is JsonValueKind.True) done = true;
                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                {
                    reasoning = StringOf(msg, "thinking");
                    content = StringOf(msg, "content");
                }
            }
            else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.Object) continue;
                    // 绝大多数是 delta；少数服务最后一片回整条 message，一并收，不然会少内容。
                    foreach (var holder in new[] { "delta", "message" })
                    {
                        if (!choice.TryGetProperty(holder, out var el) || el.ValueKind != JsonValueKind.Object) continue;
                        reasoning ??= StringOf(el, "reasoning_content") ?? StringOf(el, "reasoning");
                        content ??= StringOf(el, "content");
                    }
                    if (choice.TryGetProperty("finish_reason", out var fr)
                        && fr.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(fr.GetString()))
                        done = true;
                    break;
                }
            }
            return new StreamPiece(reasoning, content, done, null);
        }
        catch (JsonException)
        {
            // 半截 JSON：不当错——SSE 一行一片，网络层已经把行拆干净了；真拆坏了下一片照旧能收。
            return default;
        }
    }

    private static string? StringOf(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>
    /// 流式发一次聊天/提案请求。<see cref="ChatWithImagesAsync"/> 的所有口径都适用，
    /// 区别只有两点：<b>思考内容与正文边到边报</b>（回调里给），以及**非 2xx 时可能还没开流**。
    /// </summary>
    /// <param name="onReasoning">思考片段到达时回调（在调用线程上直接调；界面那边自己保证切到 UI 线程）。</param>
    /// <param name="onContent">正文片段到达时回调。</param>
    public static async Task<ChatOutcome> ChatWithImagesStreamAsync(
        RecognitionSettings settings,
        IReadOnlyList<AiChatTurn> turns,
        IReadOnlyList<(string Base64, string MimeType)>? images,
        IProgress<string>? onReasoning,
        IProgress<string>? onContent = null,
        CancellationToken cancel = default,
        HttpMessageHandler? handler = null)
    {
        if (turns.Count == 0) return new ChatOutcome { Error = "没有要问的话。" };
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model))
            return new ChatOutcome { Error = "没有填服务地址或模型名（先在「模型（AI）设置与调试」里选好）。" };

        var openAi = settings.Provider == RecognitionSettings.Providers.OpenAi;
        string? key = null;
        if (openAi)
        {
            key = settings.ResolveApiKey();
            if (key is null) return new ChatOutcome { Error = settings.MissingKeyHint };
        }

        var url = openAi
            ? OpenAiUrl(settings.Endpoint, "chat/completions")
            : settings.Endpoint.TrimEnd('/') + "/api/chat";
        var body = openAi
            ? BuildOpenAiBody(settings, turns, images, stream: true)
            : BuildOllamaBody(settings, turns, images, stream: true);
        var payload = JsonSerializer.Serialize(body);

        // 超时口径与打点全部照抄 PostChatAsync（第 27 棒：0 = 不设限，只靠手动停止）。
        var seconds = settings.EffectiveTimeoutSeconds;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        var sw = Stopwatch.StartNew();
        ChatOutcome outcome;
        AppLog.Info($"AI 流式请求发出 → {host}（请求体约 {payload.Length / 1024} KB，"
                    + (seconds == 0 ? "不设时限" : $"上限 {seconds} 秒") + "）");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            if (seconds > 0) timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            if (key is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

            // 关键一行：ResponseHeadersRead —— 不等整段回完，拿到响应头就开始读流。
            using var response = await ClientFor(handler)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                sw.Stop();
                AppLog.Info($"AI 流式请求被拒：{host} 返回 {(int)response.StatusCode}（这条会由调用方决定要不要退回非流式）");
                return new ChatOutcome
                {
                    Error = $"服务返回 {(int)response.StatusCode}：{Brief(errBody)}",
                    Elapsed = sw.Elapsed,
                    Raw = errBody,
                };
            }

            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var pieces = 0;
            while (true)
            {
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) break;
                var piece = ParseStreamLine(line, openAi);
                if (piece.Error is not null)
                {
                    sw.Stop();
                    AppLog.Info($"AI 流式请求中途报错：{host} 跑了 {sw.Elapsed.TotalSeconds:0.0} 秒 —— {piece.Error}");
                    return new ChatOutcome { Error = piece.Error, Elapsed = sw.Elapsed, Raw = piece.Error };
                }
                if (piece.Reasoning is { Length: > 0 } r)
                {
                    reasoning.Append(r);
                    SafeReport(onReasoning, r);
                    pieces++;
                }
                if (piece.Content is { Length: > 0 } c)
                {
                    content.Append(c);
                    SafeReport(onContent, c);
                    pieces++;
                }
                if (piece.Done && openAi) break;      // OpenAI 以 [DONE] 收尾（有些服务不发，靠 line==null 兜）
            }
            sw.Stop();
            var text = content.ToString();
            outcome = text.Length == 0
                ? new ChatOutcome
                {
                    Error = pieces == 0
                        ? "流回来了但一片内容都没有（地址可能不是流式兼容端点，或模型这次什么都没说）。"
                        : "模型只吐了思考没给正文。",
                    Elapsed = sw.Elapsed,
                    Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
                }
                : new ChatOutcome
                {
                    Text = text,
                    Elapsed = sw.Elapsed,
                    Raw = text,
                    Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
                };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            sw.Stop();
            // 用户点了「停止」：正常返回（不报错），调用方那句「已停止…」才有机会接手（第 23 棒钉过）。
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
            outcome = new ChatOutcome { Error = $"流式对话请求失败：{ex.Message}", Elapsed = sw.Elapsed };
        }

        // 收尾必收账（第 25 棒）：不流式那条早就打点了，流式这条没有的话，卡死时同样无从分诊。
        if (outcome.Error is null)
            AppLog.Info(cancel.IsCancellationRequested
                ? $"AI 流式请求被手动停止：{host} 跑了 {outcome.Elapsed.TotalSeconds:0.0} 秒。"
                : $"AI 流式请求完成：{host} 用了 {outcome.Elapsed.TotalSeconds:0.0} 秒，"
                  + $"回了约 {(outcome.Text?.Length ?? 0) / 1024} KB，思考约 {(outcome.Reasoning?.Length ?? 0) / 1024} KB。");
        else
            AppLog.Info($"AI 流式请求未成：{host} 用了 {outcome.Elapsed.TotalSeconds:0.0} 秒 —— {outcome.Error}");
        return outcome;
    }

    /// <summary>
    /// 回调里抛异常不许把整条流带崩：思考上屏是"顺便给人看"的东西，
    /// 界面那边真出了岔子也该是它自己报错，不该让这次请求白跑。
    /// </summary>
    private static void SafeReport(IProgress<string>? sink, string text)
    {
        if (sink is null) return;
        try { sink.Report(text); }
        catch (Exception ex) { AppLog.Warning("流式回调出错（已忽略，不影响本次结果）：" + ex.Message); }
    }
}
