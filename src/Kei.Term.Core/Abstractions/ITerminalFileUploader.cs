namespace Kei.Term.Core.Abstractions;

using Kei.Term.Core.Models;

public interface ITerminalFileUploader
{
    Task UploadAsync(
        ITerminalSession session,
        IReadOnlyList<string> paths,
        TerminalUploadProtocol protocol,
        string remoteDirectory,
        IProgress<TerminalUploadProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record TerminalUploadProgress(string FilePath, long BytesTransferred, long TotalBytes);

// 二进制协议独占当前终端通道，防止键盘输入、VT 查询应答和协议数据互相污染。
public interface ITerminalBinaryTransferSession
{
    Task<ITerminalBinaryChannel> AcquireBinaryChannelAsync(CancellationToken ct = default);
}

public interface ITerminalBinaryChannel : IAsyncDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    // 接收器已退出并消费完协议字节后，将缓冲中的 shell 输出交回终端。
    ValueTask CompleteAsync() => DisposeAsync();
}
