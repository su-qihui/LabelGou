using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;

namespace LabelGou.App.Services.Recognition;

/// <summary>
/// <see cref="OllamaVisionClient"/> 的 OpenAI 兼容协议分支 —— 阿里云百炼、DeepSeek、vLLM、
/// 以及 Ollama 自己的 <c>/v1</c> 都走这一条。
/// <para>为什么要这条：本机 2.3B 视觉模型实测 <b>243 秒/张</b>，用户明确说慢，要求支持云端。
/// 但云端不是把地址一填就完：① 密钥必须能走环境变量（<c>%APPDATA%\LabelGou\recognition.json</c> 是明文）；
/// ② 端点不是 127.0.0.1 时界面必须标「数据会离开这台电脑」（§五-11 红线，工厂订单不能悄悄出网）；
/// ③ DeepSeek 的 <c>deepseek-chat</c> <b>不支持图片</b>，它只能接本地 OCR 认出的文字行，
/// 所以这里给了两条入口，而不是假装什么模型都能吃图。</para>
/// </summary>
public static partial class OllamaVisionClient
{
    /// <summary>
    /// 拼 OpenAI 兼容协议的请求体。基地址带不带 <c>/v1</c> 都行：百炼给的是
    /// <c>.../compatible-mode/v1</c>，DeepSeek 给的是 <c>https://api.deepseek.com</c>，
    /// 再重复插一段 <c>/v1</c> 就会 404。
    /// </summary>
    public static string OpenAiUrl(string endpoint, string tail)
    {
        var base0 = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (base0.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || base0.Contains("/v1/", StringComparison.OrdinalIgnoreCase)) return $"{base0}/{tail}";
        return $"{base0}/v1/{tail}";
    }

    /// <summary>
    /// 拉这个服务上的模型清单（两家都能列：Ollama 的 <c>/api/tags</c> 与 OpenAI 兼容的 <c>/models</c>）。
    /// <para>为什么要这个：用户说得很直接——百炼不只有 qwen-vl，qwen3 的 flash / max 各档都能用，
    /// 该做的是把列表拉下来让他选，而不是我们替他锁一个型号。列不出来时返回原因，
    /// 界面上仍可以手填模型名（有些网关不实现 /models）。</para>
    /// </summary>
    public static async Task<(IReadOnlyList<string> Ids, string? Error)> ListModelsAsync(
        RecognitionSettings settings,
        CancellationToken cancel = default,
        HttpMessageHandler? handler = null)
    {
        var empty = Array.Empty<string>();
        var openAi = settings.Provider == RecognitionSettings.Providers.OpenAi;
        if (string.IsNullOrWhiteSpace(settings.Endpoint)) return (empty, "没有填服务地址。");
        string? key = null;
        if (openAi)
        {
            key = settings.ResolveApiKey();
            if (key is null)
                return (empty, $"云端需要密钥（填设置里的 apiKey，或设环境变量 {settings.ApiKeyEnvVar}）。");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 30)));
            var url = openAi ? OpenAiUrl(settings.Endpoint, "models") : settings.Endpoint.TrimEnd('/') + "/api/tags";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await ClientFor(handler).SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (empty, $"服务返回 {(int)response.StatusCode}：{Brief(body)}");

            var ids = new List<string>();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (openAi && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { } s)
                        ids.Add(s);
            }
            else if (!openAi && root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { } s)
                        ids.Add(s);
            }

            return ids.Count > 0
                ? (ids, null)
                : (empty, "服务返回了空清单（可能这个端点不支持列模型），在模型名里手填即可。");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return (empty, "拉模型列表超时。");
        }
        catch (Exception ex)
        {
            return (empty, $"拉模型列表失败：{ex.Message}");
        }
    }

    /// <summary>把 OCR 文字行拼成给纯文本模型的证据块（定案 D11：OCR 行当证据池，模型只做整理）。</summary>
    public static string OcrLinesPrompt(RecognizedText text)
    {
        var keys = string.Join(", ", MarkFieldCatalog.Mappable.Select(d => d.Key.ToString()));
        var sb = new StringBuilder();
        sb.Append("本地 OCR 从一张外贸纸箱唛头上认出下面这些文字行（可能有错字、断行、顺序错乱）。\n")
          .Append("请只依据这些行，输出一个 JSON 对象，键只能取自：").Append(keys).Append("。\n")
          .Append("值必须逐字来自这些行（可以拼接同一字段被拆开的行）；行里没有出现过的信息一律填 null；")
          .Append("不要猜测、不要补全、不要翻译，不要输出坐标或版式。\n")
          .Append("OCR 行：\n");
        foreach (var line in text.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            sb.Append(line.Text.Trim()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 纯文本模型（DeepSeek 这类）用的入口：不吃图，只把 OCR 行整理成字段 JSON。
    /// <para>OCR 一行都没认出来时直接失败返回，不发请求——让模型凭空气编一份字段出来，
    /// 比识别不出来危险得多（这些值最终要印到纸箱上）。</para>
    /// </summary>
    public static Task<ModelOutcome> AskFieldsFromOcrLinesAsync(
        RecognitionSettings settings,
        RecognizedText? text,
        CancellationToken cancel = default,
        HttpMessageHandler? handler = null)
    {
        if (text is null || text.Lines.Count == 0)
            return Task.FromResult(new ModelOutcome { Error = "本地 OCR 没认出任何文字行，纯文本模型这一路没有依据可用。" });
        return AskOpenAiAsync(settings, OcrLinesPrompt(text), imageBase64: null, cancel, handler);
    }

    /// <summary>云端探活：列模型清单看那个名字在不在。百炼/DeepSeek 都实现了 /models。</summary>
    private static async Task<(bool Found, string Reason)> ProbeOpenAiAsync(
        RecognitionSettings settings,
        CancellationToken cancel,
        HttpMessageHandler? handler)
    {
        if (settings.ResolveApiKey() is null)
            return (false, $"云端没有 API 密钥（填设置里的 apiKey，或设环境变量 {settings.ApiKeyEnvVar}）。");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 30)));
            using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiUrl(settings.Endpoint, "models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolveApiKey());

            using var response = await ClientFor(handler).SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (false, $"云端返回 {(int)response.StatusCode}：{Brief(body)}");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return (false, "云端没返回模型清单（data 数组），可能这个端点不是 OpenAI 兼容协议。");

            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var x) ? x.GetString() : null;
                if (string.Equals(id, settings.Model, StringComparison.OrdinalIgnoreCase))
                    return (true, $"云端模型 {settings.Model} 可用");
                if (id is not null && id.StartsWith(settings.Model, StringComparison.OrdinalIgnoreCase))
                    return (true, $"云端有 {id}，按前缀认作你要的 {settings.Model}");
            }
            return (false, $"这个云端账号里没有模型 {settings.Model}（在设置里换成清单里有的名字）。");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return (false, "连云端超时，检查网络或把超时调大。");
        }
        catch (Exception ex)
        {
            return (false, $"连不上云端：{ex.Message}");
        }
    }

    private static async Task<ModelOutcome> AskFieldsViaOpenAiAsync(
        RecognitionSettings settings,
        string imagePath,
        string prompt,
        CancellationToken cancel,
        HttpMessageHandler? handler)
    {
        if (!settings.ModelAcceptsImages)
        {
            // 明确拒绝，而不是把图发过去让云端报错：DeepSeek 这类模型收到 image_url 会直接 400，
            // 用户看到的就是一句看不懂的接口错误，还以为是我们坏了。
            return new ModelOutcome
            {
                Error = $"{settings.Model} 不支持图片。这种模型请在设置里保持「只看文字」，"
                    + "由本地 OCR 认出行后再交给它整理（这条入口已经接好）。",
            };
        }
        string image64;
        try
        {
            image64 = Convert.ToBase64String(File.ReadAllBytes(imagePath));
        }
        catch (Exception ex)
        {
            return new ModelOutcome { Error = $"读图片失败：{ex.Message}" };
        }
        return await AskOpenAiAsync(settings, prompt, image64, cancel, handler).ConfigureAwait(false);
    }

    /// <summary>发一次 chat/completions。<paramref name="imageBase64"/> 为 null 时就是纯文本请求。</summary>
    private static async Task<ModelOutcome> AskOpenAiAsync(
        RecognitionSettings settings,
        string prompt,
        string? imageBase64,
        CancellationToken cancel,
        HttpMessageHandler? handler)
    {
        var key = settings.ResolveApiKey();
        if (key is null)
            return new ModelOutcome { Error = $"云端没有 API 密钥（填设置里的 apiKey，或设环境变量 {settings.ApiKeyEnvVar}）。" };

        var content = new List<object?>
        {
            new Dictionary<string, object?> { ["type"] = "text", ["text"] = prompt },
        };
        if (imageBase64 is not null)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    // 云端只认 data URL 或公网链接；我们没有公网地址，所以走 base64 内联。
                    ["url"] = $"data:image/png;base64,{imageBase64}",
                },
            });
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["temperature"] = 0,
            ["messages"] = new object?[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = content },
            },
            // 不是所有云端都认这个键（百炼认、有些网关不认），所以解析端不依赖它，仍然从文本里抠 JSON。
            ["response_format"] = new Dictionary<string, object?> { ["type"] = "json_object" },
        });

        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)));
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiUrl(settings.Endpoint, "chat/completions"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await ClientFor(handler).SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
                return new ModelOutcome { Error = $"云端返回 {(int)response.StatusCode}：{Brief(body)}", Elapsed = sw.Elapsed };

            string? answer = null, serverError = null;
            using (var doc = JsonDocument.Parse(body))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var e))
                    serverError = e.TryGetProperty("message", out var em) ? em.GetString() : e.ToString();
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("message", out var msg)
                            && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        {
                            answer = c.GetString();
                        }
                        break;
                    }
                }
            }

            var json = LlmFieldJsonParser.PickBestJson(answer, null);
            if (serverError is not null)
                return new ModelOutcome { Error = serverError, Elapsed = sw.Elapsed, RawResponse = answer };
            if (string.IsNullOrWhiteSpace(json))
            {
                return new ModelOutcome
                {
                    Error = "云端没有给出可用内容（choices 里的 content 是空的）。",
                    Elapsed = sw.Elapsed,
                    RawResponse = answer,
                };
            }
            return new ModelOutcome { Json = json, Elapsed = sw.Elapsed, RawResponse = answer };
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            sw.Stop();
            return new ModelOutcome { Error = $"云端响应超时（超过 {settings.TimeoutSeconds} 秒）。", Elapsed = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ModelOutcome { Error = $"调用云端失败：{ex.Message}", Elapsed = sw.Elapsed };
        }
    }

    private static string Brief(string body) => body.Length > 200 ? body[..200] : body;
}
