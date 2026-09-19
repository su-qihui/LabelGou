namespace LabelGou.Core.Agent;

/// <summary>
/// 一次请求到底等多久。<strong>全项目只这一处算术</strong>。
/// <para>口径与第 27 棒定的一样：<c>0 = 不设限</c>（原来那个 180 秒硬顶已作废——读一张大表两分钟很正常，
/// 到点切断只会让老板重问一遍）；填了数就当上限，但低于 10 秒没意义，兜到 10。</para>
/// <para>出站（外部 agent 子进程）与原来的 HTTP 通道共用这一份：两边各写一遍，迟早一边认 0 一边认 ∞。</para>
/// </summary>
public static class TimeoutPolicy
{
    /// <summary>低于这个秒数没意义（模型冷启动加载就要几十秒），按它兜底。</summary>
    public const int FloorSeconds = 10;

    /// <summary>用户填的那格 → 真生效的秒数（0=不设限）。</summary>
    public static int Effective(int requestedSeconds)
        => requestedSeconds <= 0 ? 0 : Math.Max(FloorSeconds, requestedSeconds);

    /// <summary>0 那档要说人话，别在界面上写「0 秒」。</summary>
    public static string Describe(int effectiveSeconds)
        => effectiveSeconds <= 0 ? "不设限（一直等到它答，或你点停止）" : $"{effectiveSeconds} 秒";
}
