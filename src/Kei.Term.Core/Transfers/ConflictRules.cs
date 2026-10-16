namespace Kei.Term.Core.Transfers;

// 冲突判定用的源或目标事实。大小目前不参与比较，留给调用方核对，策略只看修改时间。
public readonly record struct TransferFileFacts(long Size, DateTimeOffset ModifiedUtc);

public static class ConflictRules
{
    public static readonly TimeSpan OlderTolerance = TimeSpan.FromSeconds(2);

    public static ConflictResolution Resolve(
        TransferConflictPolicy policy,
        TransferFileFacts source,
        TransferFileFacts target)
    {
        return policy switch
        {
            TransferConflictPolicy.Overwrite => ConflictResolution.Overwrite,
            TransferConflictPolicy.Skip => ConflictResolution.Skip,
            TransferConflictPolicy.AutoRename => ConflictResolution.Rename,
            TransferConflictPolicy.Ask => ConflictResolution.NeedsPrompt,
            TransferConflictPolicy.OverwriteIfOlder => ResolveIfOlder(source, target),
            // 损坏配置里的未定义枚举按询问处理，避免静默覆盖。
            _ => ConflictResolution.NeedsPrompt,
        };
    }

    private static ConflictResolution ResolveIfOlder(TransferFileFacts source, TransferFileFacts target)
    {
        // 比较绝对时间，不看时钟盘面。目标早于「源时间减 2 秒」才覆盖；正好 2 秒或更近都算相同，跳过。
        DateTimeOffset cutoff = source.ModifiedUtc - OlderTolerance;
        if (target.ModifiedUtc < cutoff)
        {
            return ConflictResolution.Overwrite;
        }

        return ConflictResolution.Skip;
    }
}
