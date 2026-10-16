using Kei.Term.Core.Settings;

namespace Kei.Term.Core.Transfers;

// 数值会写入 JSON。0 是固定间隔，缺字段不能靠枚举默认值变成固定间隔。
public enum RetryBackoffMode
{
    Fixed = 0,
    Exponential = 1,
}

public static class RetryBackoff
{
    // attempt 从 1 计，表示第几次重试，不是首次传输。
    public static TimeSpan Delay(int attempt, FileTransferSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "重试次数从 1 开始。");
        }

        long initial = NonNegative(settings.InitialRetryIntervalSeconds);
        if (settings.BackoffMode == RetryBackoffMode.Fixed)
        {
            return TimeSpan.FromSeconds(initial);
        }

        long max = NonNegative(settings.MaxRetryIntervalSeconds);
        long scaled = MultiplyByPowerOfTwo(initial, attempt - 1);
        return TimeSpan.FromSeconds(Math.Min(scaled, max));
    }

    // attemptsSoFar 是已经失败的尝试次数，含首次。首次失败时为 1。
    // 重试次数为 3 时，1、2、3 返回 true，4 返回 false。无法判断与永久性都不重试。
    public static bool ShouldRetry(TransferFailureKind classification, int attemptsSoFar, FileTransferSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.AutoRetryEnabled || classification != TransferFailureKind.Transient)
        {
            return false;
        }

        if (settings.RetryCount < 1 || attemptsSoFar < 1)
        {
            return false;
        }

        return attemptsSoFar <= settings.RetryCount;
    }

    private static long NonNegative(int seconds)
        => seconds < 0 ? 0 : seconds;

    // initial * 2^shift。shift 很大时饱和，不抛溢出。
    private static long MultiplyByPowerOfTwo(long initial, int shift)
    {
        if (initial <= 0 || shift <= 0)
        {
            return initial;
        }

        if (shift >= 62)
        {
            return long.MaxValue;
        }

        long factor = 1L << shift;
        if (initial > long.MaxValue / factor)
        {
            return long.MaxValue;
        }

        return initial * factor;
    }
}
