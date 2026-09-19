using System.Text.Json;

namespace LabelGou.Core.Agent;

/// <summary>外部 runtime 事件流里认得的几种事。</summary>
public enum AgentEventKind
{
    /// <summary>不认识的一律归这档：安静跳过，<strong>绝不抛</strong>。它以后加一种事件就把我们读表打断，不值得。</summary>
    Ignored,
    SessionStarted,
    TurnStarted,
    /// <summary>它的思考内容。</summary>
    Reasoning,
    /// <summary>它给的正文（我们要的那份 JSON 在这里）。</summary>
    Message,
    /// <summary>非致命告警（例："Model metadata for X not found"）。</summary>
    Notice,
    /// <summary>这一轮失败。</summary>
    Failed,
    /// <summary>收尾（带 token 账）。</summary>
    Completed,
}

/// <summary>
/// 外部 runtime 吐的一行 JSONL → 一条能上屏的事。
/// <para>事件名是<strong>量出来的</strong>，不是照记忆写的：见 <c>labelgou-other\_probe\agent-mcp\README.md</c> §3，
/// 本机 codex-cli 0.144.4 实测给出 <c>thread.started</c> / <c>turn.started</c> /
/// <c>item.completed</c>（<c>item.type</c> 为 <c>reasoning</c>｜<c>agent_message</c>｜<c>error</c>）/
/// <c>error</c> / <c>turn.completed</c> / <c>turn.failed</c>。</para>
/// <para><strong>只认 <c>item.completed</c> 的内容</strong>：<c>item.started</c> 与 <c>item.updated</c> 是"还在长"的中间态，
/// 把它们也摊上屏就成了同一句话越写越长的重影。</para>
/// <para>顺带一条实测事实：exec 这一路的 <c>reasoning</c> 是<strong>整段完成时才来</strong>，不是逐片流。
/// 所以外部 agent 那一路做不到第 31 棒那种"边想边滚"，界面只能给「它在想…（已等 N 秒）」+ 思考整段落 + 进度行；
/// 别在文档或提示语里承诺流式。</para>
/// </summary>
public sealed record AgentEvent(AgentEventKind Kind, string Text)
{
    public bool IsFailure => Kind == AgentEventKind.Failed;

    /// <summary>有内容值得摊给人看吗（空文本的 started 类事件不摊，免得刷一排空行）。</summary>
    public bool HasVisibleText => Text.Length > 0;

    public static AgentEvent Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new(AgentEventKind.Ignored, string.Empty);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new(AgentEventKind.Ignored, string.Empty);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !TryString(doc.RootElement, "type", out var type))
                return new(AgentEventKind.Ignored, string.Empty);

            switch (type)
            {
                case "thread.started":
                case "turn.started":
                    return new(AgentEventKind.Ignored, string.Empty);

                case "turn.completed":
                    return new(AgentEventKind.Completed, string.Empty);

                case "turn.failed":
                    return new(AgentEventKind.Failed, NestedMessage(doc.RootElement, "error") ?? "这一轮没跑成");

                case "error":
                    return new(AgentEventKind.Notice, TryString(doc.RootElement, "message", out var em) ? em : "它报了一句错");

                case "item.completed":
                    return Item(doc.RootElement);

                default:
                    return new(AgentEventKind.Ignored, string.Empty);
            }
        }
    }

    /// <summary>一条完成的事件项：<c>item.type</c> 决定它是思考、正文，还是只当作告警。</summary>
    private static AgentEvent Item(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return new(AgentEventKind.Ignored, string.Empty);
        if (!TryString(item, "type", out var itemType)) return new(AgentEventKind.Ignored, string.Empty);

        var text = TryString(item, "text", out var t) ? t : string.Empty;
        return itemType switch
        {
            "reasoning" => new(AgentEventKind.Reasoning, text),
            "agent_message" => new(AgentEventKind.Message, text),
            "error" => new(AgentEventKind.Notice, text),
            _ => new(AgentEventKind.Ignored, string.Empty),
        };
    }

    private static string? NestedMessage(JsonElement root, string property)
        => root.TryGetProperty(property, out var nested) && nested.ValueKind == JsonValueKind.Object
            && TryString(nested, "message", out var msg)
                ? msg
            : null;

    private static bool TryString(JsonElement parent, string property, out string value)
    {
        if (parent.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String)
        {
            value = child.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
