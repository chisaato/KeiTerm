namespace Kei.Term.Ssh.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Kei.Term.Core.Abstractions;

// 每个连接一条专用 SFTP 传输连接（ADR-0002）。
// 建立过程串行；多个租约共享这条连接上的 SftpClient，各自打开独立文件流
// （SSH.NET SftpSession 按请求编号多路复用，单个流不可跨线程共用）。
// 空闲 = 没有未归还租约。空闲满 IdleTimeout 后关闭，下次 Acquire 再懒连接。
// 客户端掉线后下次 Acquire 重连。Dispose 取消正在进行的拨号，并让未归还租约的后续操作失败。
internal sealed class SftpTransferConnection : IRemoteTransferChannel
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task<ITransferClient>> _connect;
    private readonly Func<ITransferClient, IRemoteFileSystem>? _createFileSystem;
    private readonly TimeProvider _time;
    private readonly TimeSpan _idleTimeout;
    private readonly IAsyncDisposable? _owned;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private ITransferClient? _client;
    private ITimer? _idleTimer;
    private int _leases;
    private int _idleEpoch;
    private int _connectCount;
    private int _disposed;

    internal SftpTransferConnection(
        Func<CancellationToken, Task<ITransferClient>> connect,
        Func<ITransferClient, IRemoteFileSystem>? createFileSystem,
        TimeProvider time,
        IAsyncDisposable? owned,
        ILogger? logger,
        TimeSpan? idleTimeout = null)
    {
        _connect = connect;
        _createFileSystem = createFileSystem;
        _time = time;
        _owned = owned;
        _logger = logger ?? NullLogger.Instance;
        _idleTimeout = idleTimeout ?? IdleTimeout;
    }

    internal static SftpTransferConnection Create(SshDialer dialer, ILogger logger)
    {
        return new SftpTransferConnection(
            async ct =>
            {
                SftpClient client = await dialer.ConnectClientAsync(info => new SftpClient(info), ct);
                return new SftpTransferClient(client);
            },
            createFileSystem: null,
            TimeProvider.System,
            dialer,
            logger);
    }

    // 成功建立过的次数。集成测试用来确认多条流没有各开一条连接。
    internal int ConnectCount => Volatile.Read(ref _connectCount);

    public async Task<IRemoteTransferLease> AcquireAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            // 有租约就不再算空闲；作废已经排上的关闭回调
            CancelIdleTimer();

            if (_client is not { IsConnected: true })
            {
                bool reconnect = _client != null;
                DisposeClient();
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                _client = await _connect(linked.Token);
                if (IsDisposed)
                {
                    DisposeClient();
                    throw new ObjectDisposedException(GetType().Name);
                }

                if (!_client.IsConnected)
                {
                    DisposeClient();
                    throw new IOException("专用传输连接未能建立");
                }

                Interlocked.Increment(ref _connectCount);
                _logger.LogInformation(reconnect ? "专用传输连接已重连" : "专用传输连接已建立");
            }

            _leases++;
            IRemoteFileSystem fileSystem = CreateLeaseFileSystem(_client);
            return new Lease(this, fileSystem);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // 先取消拨号，再等闸门，避免 Acquire 握着闸门卡在 Connect 上时这里死等
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try
        {
            CancelIdleTimer();
            DisposeClient();
        }
        finally
        {
            _gate.Release();
        }

        if (_owned != null)
        {
            await _owned.DisposeAsync();
        }

        _lifetime.Dispose();
        _logger.LogInformation("专用传输连接已释放");
    }

    private IRemoteFileSystem CreateLeaseFileSystem(ITransferClient client)
    {
        if (_createFileSystem != null)
        {
            return _createFileSystem(client);
        }

        SftpTransferClient sftp = (SftpTransferClient)client;
        return new SftpRemoteFileSystem(sftp.Client, () => LeaseFailure(sftp), _logger);
    }

    private Exception? LeaseFailure(SftpTransferClient client)
    {
        if (IsDisposed)
        {
            return new IOException("传输连接已关闭");
        }

        if (!client.IsConnected)
        {
            return new IOException("传输连接已断开");
        }

        return null;
    }

    private async Task ReleaseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_leases > 0)
            {
                _leases--;
            }

            if (IsDisposed || _leases != 0)
            {
                return;
            }

            ArmIdleTimer();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ArmIdleTimer()
    {
        CancelIdleTimer();
        int epoch = _idleEpoch;
        _idleTimer = _time.CreateTimer(
            static state =>
            {
                (SftpTransferConnection self, int captured) = ((SftpTransferConnection, int))state!;
                self.CloseIfStillIdle(captured);
            },
            (this, epoch),
            _idleTimeout,
            Timeout.InfiniteTimeSpan);
    }

    // 同步关闭，便于 TimeProvider 在测试里推进时间后立即观察到断开。
    // 计时器不会在持有闸门时触发：拨号前已取消空闲计时，dueTime 也不是 0。
    private void CloseIfStillIdle(int epoch)
    {
        if (IsDisposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (IsDisposed || epoch != _idleEpoch || _leases != 0 || _client is not { IsConnected: true })
            {
                return;
            }

            DisposeClient();
            _logger.LogInformation("专用传输连接空闲 {IdleSeconds} 秒，已关闭", _idleTimeout.TotalSeconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CancelIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        _idleEpoch++;
    }

    private void DisposeClient()
    {
        ITransferClient? client = _client;
        _client = null;
        if (client == null)
        {
            return;
        }

        try
        {
            client.Dispose();
        }
        catch
        {
            // 释放路径防御
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private sealed class Lease(SftpTransferConnection owner, IRemoteFileSystem fileSystem) : IRemoteTransferLease
    {
        private int _released;

        public IRemoteFileSystem FileSystem { get; } = fileSystem;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return ValueTask.CompletedTask;
            }

            return new ValueTask(owner.ReleaseAsync());
        }
    }
}

internal interface ITransferClient : IDisposable
{
    bool IsConnected { get; }
}

internal sealed class SftpTransferClient(SftpClient client) : ITransferClient
{
    public SftpClient Client { get; } = client;

    public bool IsConnected
    {
        get
        {
            try
            {
                return Client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (IsConnected)
            {
                Client.Disconnect();
            }
        }
        catch
        {
            // 已经断开
        }

        Client.Dispose();
    }
}
