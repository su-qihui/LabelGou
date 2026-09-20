namespace LabelGou.Core.Agent;

/// <summary>
/// 一次请求到底等多久。<strong>全项目只这一处算术</strong>。
/// <para>口径与第 27 棒定的一样：<c>0 = 不设限</c>（原来那个 180 秒硬顶已作废——读一张大表两分钟很正常，
/// 到点切断只会让老板重问一遍）；填了数就当上限，但低于 10 秒没意义，兜到 10。</para>
/// <para>主仓里出站（外部 agent 子进程）与 HTTP 通道共用这一份。<strong>本 Win7 变体不装外部 Agent</strong>
/// （它要求本机跑 Node ≥16 的 codex，而 Node 官方自 13 起不支持 Win7），所以下游只剩 HTTP 通道一个消费者；
/// 文件仍留在原位、口径仍只这一处，这样每次并主仓时这一格不用重新对。</para>
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
