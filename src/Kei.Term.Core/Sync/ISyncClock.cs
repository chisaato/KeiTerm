namespace Kei.Term.Core.Sync;

// 调度只依赖可替换的时钟，测试用手动时钟推进，不碰系统定时器。
public interface ISyncClock
{
    DateTime UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemSyncClock : ISyncClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => delay == Timeout.InfiniteTimeSpan
            ? Task.Delay(Timeout.Infinite, cancellationToken)
            : Task.Delay(delay, cancellationToken);
}
