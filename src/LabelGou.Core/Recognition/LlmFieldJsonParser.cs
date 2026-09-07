using System.Text.Json;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>LLM 响应解析结果。</summary>
public sealed class LlmParseResult
{
    public List<FieldCandidate> Candidates { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>白名单外被丢弃的键数（D15）。</summary>
    public int DroppedKeys { get; set; }

    /// <summary>实际解析掉的那段 JSON 文本，日志与排障要看它。</summary>
    public string? Payload { get; set; }
}

/// <summary>
/// 解析大模型给的字段 JSON（定案 D15：模型只准出这种受约束的键值对，不准出坐标）。
/// <para>宽容度的边界要说清：结构上宽容（散文里抠 JSON、键名大小写、值是数字也收），
/// 语义上严格——<strong>白名单外的键一律丢弃并计数</strong>，绝不"顺手"接受一个不认识的字段。</para>
/// </summary>
public static class LlmFieldJsonParser
{
    /// <summary>一次响应里最多接受多少个字段，防呆。</summary>
    public const int MaxFields = 40;

    /// <summary>模型无自报置信度，统一按这个起步，之后由证据匹配升降（见 <see cref="CrossValidator"/>）。</summary>
    public const double BaseConfidence = 0.7;

    /// <summary>
    /// 从两个候选文本里挑一个当答案。
    /// <para>本机实测：Ollama 跑 <c>qwen3-vl:4b</c> 时 <c>response</c> 字段是<strong>空的</strong>，
    /// JSON 全在 <c>thinking</c> 里。所以调用方必须按「主答案 → 思考文本」的顺序问一遍，
    /// 否则会得出"模型什么都没返回"的错误结论。</para>
    /// </summary>
    public static string? PickBestJson(string? primary, string? fallback)
    {
        if (LooksLikeObject(primary)) return primary;
        if (LooksLikeObject(fallback)) return fallback;
        return primary;
    }

    private static bool LooksLikeObject(string? text)
        => !string.IsNullOrWhiteSpace(text) && text.IndexOf('{') >= 0 && text.LastIndexOf('}') > text.IndexOf('{');

    public static LlmParseResult Parse(string? response, string? sourceName = null)
    {
        var result = new LlmParseResult();
        var payload = ExtractObject(response);
        if (payload is null)
        {
            result.Warnings.Add(string.IsNullOrWhiteSpace(response)
                ? "模型没有返回内容（服务没起、超时，或它只输出了思考过程）。"
                : $"模型返回的内容里找不到 JSON 对象，已忽略。开头是：{Head(response)}");
            return result;
        }

        result.Payload = payload;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            result.Warnings.Add($"模型给的 JSON 格式不对，整份已忽略：{ex.Message}");
            return result;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                result.Warnings.Add("模型给的不是 JSON 对象（可能是数组或标量），已忽略。");
                return result;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (result.Candidates.Count >= MaxFields)
                {
                    result.Warnings.Add($"字段数已达上限 {MaxFields}，其余不再采纳。");
                    break;
                }

                if (!MarkFieldCatalog.TryParseKey(prop.Name, out var field))
                {
                    result.DroppedKeys++;
                    continue;   // 白名单外的键：D15 明确要求丢弃，不做"猜一个最像的字段"
                }

                if (field == MarkFieldKey.Logo) continue;   // 图片字段不从文本值采纳

                var (value, note) = ScalarOf(prop.Value);
                if (value is null)
                {
                    if (note is not null) result.Warnings.Add($"「{MarkFieldCatalog.Get(field).ChineseName}」被跳过：{note}");
                    continue;   // null = 模型认为图上没有，这是有效回答，不是告警
                }

                result.Candidates.Add(new FieldCandidate
                {
                    Field = field,
                    RawValue = value,
                    Origin = ValueOrigin.AiLlm,
                    Confidence = BaseConfidence,
                    Note = note,
                });
            }
        }

        if (result.DroppedKeys > 0)
            result.Warnings.Add($"模型给了 {result.DroppedKeys} 个白名单外的键，已丢弃（唛头字段清单是唯一真源）。");
        if (result.Candidates.Count == 0 && result.Warnings.Count == 0)
            result.Warnings.Add("模型的 JSON 里没有任何可用字段值。");
        if (sourceName is not null && result.Candidates.Count > 0)
        {
            foreach (var candidate in result.Candidates)
                if (candidate.Note is null) candidate.Note = $"来源：{sourceName}";
        }

        return result;
    }

    /// <summary>散文里抠 JSON：从第一个 <c>{</c> 配到最后一个 <c>}</c>，容忍模型加客套话或代码围栏。</summary>
    private static string? ExtractObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }

    private static string Head(string? text)
    {
        var one = TextNormalizer.Squeeze(text ?? string.Empty);
        return one.Length <= 40 ? one : one[..40] + "…";
    }

    /// <summary>把 JSON 值收成字符串。<c>(null, null)</c> 表示「模型说没有」，<c>(null, 说明)</c> 表示跳过并告警。</summary>
    private static (string? Value, string? Note) ScalarOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => (null, null),
        JsonValueKind.False => (null, "值是 false，不是可印内容"),
        JsonValueKind.True => (null, "值是 true，不是可印内容"),
        JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString())
            ? (null, null)
            : (TextNormalizer.Squeeze(element.GetString()), null),
        JsonValueKind.Number => (element.GetRawText(), null),
        _ => (null, "值不是单个文字或数字（模型给成了对象/数组）"),
    };
}
