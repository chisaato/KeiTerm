namespace Kei.Term.Core.Transfers;

// 远端操作系统。Windows 只留扩展点，本次探测不会把它报成可用平台。
public enum RemotePlatform
{
    Unknown = 0,
    Linux = 1,
    Darwin = 2,
    Windows = 3,
}

// 完整性校验算法。数值即设置文件里的存储值，未列出的算法视为禁用。
public enum TransferHashAlgorithm
{
    Sha256 = 0,
    Sha1 = 1,
    Md5 = 2,
}

// 目录归档格式。数值即设置文件里的存储值。
public enum TransferArchiveFormat
{
    Zstd = 0,
    Gzip = 1,
    Tar = 2,
}

// 一次能力探测的结果。列表按探测当时的配置顺序排列，首项是当时的最佳方案。
// 使用时仍要用当前配置顺序调用 Select，不能假定首项永远最优。
public sealed class TransferProbeResult
{
    public TransferProbeResult(
        RemotePlatform platform,
        IReadOnlyList<TransferHashAlgorithm> hashes,
        IReadOnlyList<TransferArchiveFormat> archives,
        DateTime probedAtUtc)
    {
        Platform = platform;
        Hashes = hashes.ToArray();
        Archives = archives.ToArray();
        ProbedAtUtc = NormalizeUtc(probedAtUtc);
    }

    public RemotePlatform Platform { get; }

    public IReadOnlyList<TransferHashAlgorithm> Hashes { get; }

    public IReadOnlyList<TransferArchiveFormat> Archives { get; }

    public DateTime ProbedAtUtc { get; }

    // 当前配置顺序里，第一个同时也出现在探测结果中的算法。都没有则 null。
    public TransferHashAlgorithm? SelectHash(IEnumerable<TransferHashAlgorithm> preference)
        => Select(preference, Hashes);

    public TransferArchiveFormat? SelectArchive(IEnumerable<TransferArchiveFormat> preference)
        => Select(preference, Archives);

    public static TransferProbeResult Unavailable(TimeProvider time)
        => new(RemotePlatform.Unknown, [], [], time.GetUtcNow().UtcDateTime);

    private static T? Select<T>(IEnumerable<T> preference, IReadOnlyList<T> available)
        where T : struct, Enum
    {
        foreach (T item in preference)
        {
            if (available.Contains(item))
            {
                return item;
            }
        }

        return null;
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        // 未标记的时间按写入约定视为 UTC，避免 ToUniversalTime 把它当地时再转一次。
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
