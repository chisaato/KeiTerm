namespace Kei.Term.Core.Services;

// 意外断开后的重连裁决。等待秒数固定为 1, 2, 4, 8, 16, 30，第 6 次仍失败就停止。
public static class ReconnectSchedule
{
    public const int MaxAttempts = 6;

    public static readonly int[] DelaysSeconds = [1, 2, 4, 8, 16, 30];

    // attempt 从 1 计。超出 6 次或非法次数返回 null，表示不再排程。
    public static int? DelaySeconds(int attempt)
        => attempt is >= 1 and <= MaxAttempts ? DelaysSeconds[attempt - 1] : null;
}

public readonly record struct ReconnectFacts(
    bool Enabled,
    bool UserDisconnected,
    bool AuthCancelled,
    bool HostKeyRejected,
    bool InteractiveRequired,
    int NextAttempt);

public readonly record struct ReconnectDecision(bool ShouldReconnect, int? DelaySeconds, int Attempt);

public static class ReconnectPolicy
{
    public static ReconnectDecision Decide(ReconnectFacts facts)
    {
        if (!facts.Enabled
            || facts.UserDisconnected
            || facts.AuthCancelled
            || facts.HostKeyRejected
            || facts.InteractiveRequired)
        {
            return new ReconnectDecision(false, null, facts.NextAttempt);
        }

        int? delay = ReconnectSchedule.DelaySeconds(facts.NextAttempt);
        return delay is int seconds
            ? new ReconnectDecision(true, seconds, facts.NextAttempt)
            : new ReconnectDecision(false, null, facts.NextAttempt);
    }
}

public static class ReconnectMessages
{
    public static string Waiting(int seconds, int attempt)
        => $"连接已断开，{seconds} 秒后重连（第 {attempt}/{ReconnectSchedule.MaxAttempts} 次）";

    public const string Reconnected = "已重新连接";
}
