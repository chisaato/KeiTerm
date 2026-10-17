namespace Kei.Term.Core.Transfers;

using System.Security.Cryptography;

// 比对本地文件和远端文件的哈希。本地用流式计算，可取消；远端执行工具集生成的命令。
public interface ITransferHashVerifier
{
    Task<string> HashLocalAsync(string path, CancellationToken cancellationToken = default);

    Task<string> HashRemoteAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class TransferHashVerifier(TransferHashAlgorithm algorithm, IRemoteShell shell, IRemoteToolset toolset) : ITransferHashVerifier
{
    public TransferHashAlgorithm Algorithm { get; } = algorithm;

    public async Task<string> HashLocalAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = Algorithm switch
        {
            TransferHashAlgorithm.Sha256 => await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            TransferHashAlgorithm.Sha1 => await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            TransferHashAlgorithm.Md5 => await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(Algorithm), Algorithm, "没有对应的本地哈希算法。"),
        };
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<string> HashRemoteAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(toolset);
        RemoteCommandResult result = await shell.RunAsync(toolset.HashCommand(Algorithm, path), cancellationToken).ConfigureAwait(false);
        if (result.ExitStatus != 0 || !string.IsNullOrEmpty(result.ExitSignal))
        {
            throw new TransferHashCommandException("远端哈希命令失败。");
        }

        if (!toolset.TryParseHash(Algorithm, result.Output, out string hex))
        {
            throw new TransferHashCommandException("远端哈希输出无法解析。");
        }

        return hex;
    }
}

// 两端哈希不一致。部分文件是坏数据，调用方必须删掉它，并且不能拿这次的源快照续传。
public sealed class TransferHashMismatchException : Exception
{
    public TransferHashMismatchException(string localHex, string remoteHex)
        : base("完整性校验不一致。")
    {
        LocalHex = localHex;
        RemoteHex = remoteHex;
    }

    public string LocalHex { get; }

    public string RemoteHex { get; }
}

// 远端哈希命令没有正常给出结果。数据本身未必是坏的，部分文件按续传开关保留。
public sealed class TransferHashCommandException : Exception
{
    public TransferHashCommandException(string message)
        : base(message)
    {
    }
}
