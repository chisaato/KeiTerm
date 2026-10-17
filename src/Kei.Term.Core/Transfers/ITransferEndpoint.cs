namespace Kei.Term.Core.Transfers;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

// 一条连接上的专用传输端点。SFTP 包住传输通道；SCP 包住单独的文件系统，不能和浏览共用。
public interface ITransferEndpoint : IAsyncDisposable
{
    FileTransferProtocol Protocol { get; }

    Task<IRemoteTransferLease> AcquireAsync(CancellationToken ct = default);

    // 取这个端点上的 exec。只开放 SFTP 子系统、或文件系统本身没有 shell 时返回 null。
    Task<IRemoteShell?> AcquireShellAsync(CancellationToken ct = default)
        => Task.FromResult<IRemoteShell?>(null);
}

public sealed class ChannelTransferEndpoint(IRemoteTransferChannel channel) : ITransferEndpoint
{
    public FileTransferProtocol Protocol => FileTransferProtocol.Sftp;

    public Task<IRemoteTransferLease> AcquireAsync(CancellationToken ct = default)
        => channel.AcquireAsync(ct);

    public Task<IRemoteShell?> AcquireShellAsync(CancellationToken ct = default)
        => channel.AcquireShellAsync(ct);

    public ValueTask DisposeAsync() => channel.DisposeAsync();
}

// 工厂返回的文件系统尚未连接。Acquire 时才连接，连接与关闭串行，重复释放无害。
public sealed class FileSystemTransferEndpoint : ITransferEndpoint
{
    private readonly IRemoteFileSystem _fileSystem;
    private readonly bool _owns;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private int _disposed;

    public FileSystemTransferEndpoint(IRemoteFileSystem fileSystem, FileTransferProtocol protocol, bool owns)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        Protocol = protocol;
        _owns = owns;
    }

    public FileTransferProtocol Protocol { get; }

    public async Task<IRemoteTransferLease> AcquireAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_fileSystem.IsConnected)
            {
                await _fileSystem.ConnectAsync(ct).ConfigureAwait(false);
            }

            return new SharedLease(_fileSystem);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<IRemoteShell?> AcquireShellAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_fileSystem.IsConnected)
            {
                await _fileSystem.ConnectAsync(ct).ConfigureAwait(false);
            }

            // SCP 的文件系统自己就是 shell。浏览用的 SFTP 文件系统没有 exec，这里返回 null。
            return _fileSystem as IRemoteShell;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_owns) return;
            try
            {
                await _fileSystem.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭失败也要把端点标成已释放，避免下一次 Acquire 再连上。
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private sealed class SharedLease(IRemoteFileSystem fileSystem) : IRemoteTransferLease
    {
        public IRemoteFileSystem FileSystem => fileSystem;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
