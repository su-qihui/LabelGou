namespace LabelGou.App.Services.Recognition;

/// <summary>
/// 一条对话消息。<see cref="Role"/> 用协议原词：<c>system</c> / <c>user</c> / <c>assistant</c>。
/// </summary>
/// <param name="Role">角色。</param>
/// <param name="Text">这一轮的正文（模型回什么就存什么，不做二次解析）。</param>
public sealed record AiChatTurn(string Role, string Text)
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>一次对话请求的结果。失败时 <see cref="Error"/> 是一句人话；<b>不解析字段 JSON</b>。</summary>
public sealed class ChatOutcome
{
    public string? Text { get; init; }

    public string? Error { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>模型/服务原话，出错时也留着，方便当场判断是端点错了还是模型在编。</summary>
    public string? Raw { get; init; }

    /// <summary>
    /// 模型的**思考内容**（第 31 棒）。
    /// <para>为什么要它：用户 2026-09-10 实测要求「AI 再思考时可以选择展开或者关闭思考内容」，
    /// 而且那时他正在判断"AI 排版为什么这么差"——<strong>思考过程就是判断它有没有读懂这张表的唯一材料</strong>。
    /// 不流式时它只在回答回来后才拿得到（对"以为卡住了"没用），所以这一条是配着流式一起加的。</para>
    /// </summary>
    public string? Reasoning { get; init; }

    public bool Ok => Error is null && Text is not null;
}

/// <summary>
/// 对话历史的裁剪 —— 纯函数，可单测。
/// <para>为什么要它：多轮对话每发一次都要把历史整份重发，用户在窗口里聊上几十轮，
/// 请求体会无界增长（云端按 token 收费，本机模型会把上下文窗口挤爆然后<strong>悄悄忘掉前面</strong>）。
/// 与其让模型静默失忆，不如我们明确丢最早的几轮，并在界面上说一句「已省略前 N 轮」。</para>
/// </summary>
public static class AiChatHistory
{
    /// <summary>最多带多少轮（不含开头那条 system）。20 轮足够一次调版式的来回沟通。</summary>
    public const int MaxTurns = 20;

    /// <summary>单条最长字符数：一条粘贴进来的整页表格能把上下文一次占满，超了就截。</summary>
    public const int MaxCharsPerTurn = 4000;

    /// <summary>
    /// 组装真正要发出去的 messages：保留开头 <c>system</c>，其余按时间顺序取最近
    /// <see cref="MaxTurns"/> 条，每条截到 <see cref="MaxCharsPerTurn"/>。
    /// </summary>
    public static IReadOnlyList<AiChatTurn> BuildForRequest(IReadOnlyList<AiChatTurn> turns)
    {
        var kept = new List<AiChatTurn>();
        var rest = turns;

        // 只认第一条 system（我们的用法里最多一条；多条时其余按普通轮处理）
        if (turns.Count > 0 && turns[0].Role == AiChatTurn.System)
        {
            kept.Add(Clamp(turns[0]));
            rest = turns.Skip(1).ToArray();
        }

        var meaningful = rest.Where(t => t.Text.Length > 0).ToList();
        // 只丢最早的，最近的必须全带上 —— 用户在对话窗里刚说的话被吃掉，比多花点 token 危险得多。
        // net6 的 List<T> 不支持 Range 索引（^MaxTurns..），用 GetRange 取最后 MaxTurns 条，语义等价。
        var window = meaningful.Count > MaxTurns ? meaningful.GetRange(meaningful.Count - MaxTurns, MaxTurns) : meaningful;
        kept.AddRange(window.Select(Clamp));
        return kept;
    }

    /// <summary>因为裁剪被丢掉的轮数（界面要老实告诉用户「省略了前 N 轮」，不要假装历史完整）。</summary>
    public static int DroppedTurns(IReadOnlyList<AiChatTurn> turns)
    {
        var rest = turns.Count > 0 && turns[0].Role == AiChatTurn.System ? turns.Skip(1) : turns;
        return Math.Max(0, rest.Count(t => t.Text.Length > 0) - MaxTurns);
    }

    private static AiChatTurn Clamp(AiChatTurn turn) =>
        turn.Text.Length <= MaxCharsPerTurn
            ? turn
            : turn with { Text = turn.Text[..MaxCharsPerTurn] + "…（这条太长，已截断）" };
}
