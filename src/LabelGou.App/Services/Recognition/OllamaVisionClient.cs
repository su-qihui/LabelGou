using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;

namespace LabelGou.App.Services.Recognition;

/// <summary>一次模型调用的结果。失败时 <see cref="Error"/> 是一句人话，不抛堆栈。</summary>
public sealed class ModelOutcome
{
    /// <summary>挑出来当答案的那段 JSON（<c>response</c> 空时会退到 <c>thinking</c>）。</summary>
    public string? Json { get; init; }

    public string? Error { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>原始 response 与 thinking 都留一份，事后能查出模型到底说了什么。</summary>
    public string? RawResponse { get; init; }

    public string? RawThinking { get; init; }

    public bool Ok => Error is null && Json is not null;
}

/// <summary>
/// 本机 Ollama（或任意 OpenAI 兼容端点）的视觉模型通道 —— M6 的第二通道（定案 D11）。
/// <para>只用 <see cref="HttpClient"/> 走 HTTP，不引任何 SDK；<b>默认端点是 127.0.0.1</b>，
/// 填成公网地址时界面会打「数据会离开这台电脑」的标记（§五-11）。</para>
/// <para>本机实测两件事写在这里，免得下次又当成 bug 查：① <c>qwen3-vl:4b</c> 约 <b>34 秒/张</b>，
/// 冷启动加载模型另算，所以超时默认给到 180 秒；② 它把 JSON 放在 <b>thinking</b> 字段里，
/// <c>response</c> 是空的——只读 response 会误判成"模型什么都没返回"。</para>
/// </summary>
public static partial class OllamaVisionClient
{
    /// <summary>
    /// 共享客户端。handler 必须是 <see cref="DualStackConnect"/> 那个双栈回退的：
    /// 本机实测百炼的 DNS 把 IPv6 排在前面，而这台机器没有 IPv6 出口，
    /// 用默认 handler 会先撞黑洞地址、一路挂到超时，界面只能报「拉模型列表超时」（第 10 棒）。
    /// </summary>
    private static readonly HttpClient Http =
        new(DualStackConnect.NewHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// 取一个 HTTP 客户端。<paramref name="handler"/> 是单测钩子：想验“<c>response</c> 空而
    /// <c>thinking</c> 有值”这种真形状，不需要真的养一个 Ollama 进程。
    /// 共享客户端负责连接复用，不能随手 dispose；临时那个交给 GC，单测里不留残余。
    /// 超时一律给无限，按请求用 <see cref="CancellationTokenSource.CancelAfter"/> 控，两者才能各自独立。
    /// </summary>
    private static HttpClient ClientFor(HttpMessageHandler? handler)
        => handler is null ? Http : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// 抽取提示词。<b>字段清单从 <see cref="MarkFieldCatalog"/> 生成</b>，
    /// 这样以后加字段只改目录一处，提示词不会悄悄落后于代码。
    /// </summary>
    public static string FieldPrompt()
    {
        var keys = string.Join(", ", MarkFieldCatalog.Mappable.Select(d => d.Key.ToString()));
        return "这是外贸纸箱唛头图片。只输出一个 JSON 对象，键只能取自：" + keys + "。"
            + "值必须逐字来自图中可见文字；图上没有或看不清的键必须填 null；"
            + "不要猜测、不要补全、不要翻译，不要输出坐标或版式。";
    }

    /// <summary>模型在不在。不在就直接降级为纯 OCR，而不是让用户等 34 秒然后报错。</summary>
    public static async Task<(bool Found, string Reason)> ProbeModelAsync(
        RecognitionSettings settings,
        CancellationToken cancel = default,
        HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model))
            return (false, "没有填模型端点或模型名。");

        // 云端（OpenAI 兼容协议）走另一套探活：它没有 /api/tags，只有 /models。
        if (settings.Provider == RecognitionSettings.Providers.OpenAi)
            return await ProbeOpenAiAsync(settings, cancel, handler).ConfigureAwait(false);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 30)));

            var http = ClientFor(handler);
            var url = settings.Endpoint.TrimEnd('/') + "/api/tags";
            using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (false, $"模型服务返回 {(int)response.StatusCode}，可能不是 Ollama 的接口。");

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return (false, "模型服务没返回模型清单。");

            foreach (var model in models.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.Equals(name, settings.Model, StringComparison.OrdinalIgnoreCase))
                    return (true, $"模型 {settings.Model} 可用");
            }

            return (false, $"这台电脑的模型服务里没有 {settings.Model}（先用 ollama pull 拉一个，或在设置里改成已有的模型）。");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return (false, "连接模型服务超时，可能它没在运行（ollama serve）。");
        }
        catch (HttpRequestException ex)
        {
            return (false, DualStackConnect.Talk(ex));
        }
        catch (Exception ex)
        {
            return (false, $"连不上模型服务：{ex.Message}");
        }
    }

    /// <summary>问一次：把图片交给模型，换回字段 JSON。</summary>
    public static async Task<ModelOutcome> AskFieldsAsync(
        RecognitionSettings settings,
        string imagePath,
        CancellationToken cancel = default,
        HttpMessageHandler? handler = null)
    {
        if (!File.Exists(imagePath)) return new ModelOutcome { Error = "要识别的图片不存在。" };

        // 同一个门面，按协议分流：调用方（RecognitionService）不需要知道对端是 Ollama 还是百炼。
        if (settings.Provider == RecognitionSettings.Providers.OpenAi)
            return await AskFieldsViaOpenAiAsync(settings, imagePath, FieldPrompt(), cancel, handler).ConfigureAwait(false);

        string payload;
        try
        {
            // 本机 ollama 不花流量，但一张十几 MB 的原图照样把等待时间拉长：同一份降采样口径（见 ImageForModel）
            var (image64, _) = ImageForModel.FromFile(imagePath);
            payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["model"] = settings.Model,
                ["prompt"] = FieldPrompt(),
                ["images"] = new[] { image64 },
                ["stream"] = false,
                ["format"] = "json",
                ["options"] = new Dictionary<string, object?> { ["temperature"] = 0 },
            });
        }
        catch (Exception ex)
        {
            return new ModelOutcome { Error = $"读图片失败：{ex.Message}" };
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)));

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var url = settings.Endpoint.TrimEnd('/') + "/api/generate";
            var http = ClientFor(handler);
            using var response = await http.PostAsync(url, content, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var brief = body.Length > 200 ? body[..200] : body;
                return new ModelOutcome { Error = $"模型服务返回 {(int)response.StatusCode}：{brief}", Elapsed = sw.Elapsed };
            }

            string? answer = null;
            string? thinking = null;
            string? serverError = null;
            using (var doc = JsonDocument.Parse(body))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("response", out var r)) answer = r.GetString();
                if (root.TryGetProperty("thinking", out var t)) thinking = t.GetString();
                if (root.TryGetProperty("error", out var e)) serverError = e.GetString();
            }

            var json = LlmFieldJsonParser.PickBestJson(answer, thinking);
            if (serverError is not null)
                return new ModelOutcome { Error = serverError, Elapsed = sw.Elapsed, RawResponse = answer, RawThinking = thinking };

            if (string.IsNullOrWhiteSpace(json))
            {
                return new ModelOutcome
                {
                    Error = "模型没有给出可用内容（response 与 thinking 都是空的）。",
                    Elapsed = sw.Elapsed,
                    RawResponse = answer,
                    RawThinking = thinking,
                };
            }

            return new ModelOutcome
            {
                Json = json,
                Elapsed = sw.Elapsed,
                RawResponse = answer,
                RawThinking = thinking,
            };
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            sw.Stop();
            return new ModelOutcome { Error = $"模型响应超时（超过 {settings.TimeoutSeconds} 秒）。", Elapsed = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ModelOutcome { Error = $"调用模型失败：{ex.Message}", Elapsed = sw.Elapsed };
        }
    }
}
