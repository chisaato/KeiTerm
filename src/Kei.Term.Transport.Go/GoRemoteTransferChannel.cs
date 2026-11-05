using Kei.Term.Core.Abstractions;

namespace Kei.Term.Transport.Go;

public sealed class GoRemoteTransferChannel : IRemoteTransferChannel
{
    private readonly Func<CancellationToken, Task<GoRemoteFileSystem>> _create;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly TimeProvider _time;
    private readonly ITimer _idle;
    private GoRemoteFileSystem? _fileSystem;
    private int _leases;
    private long _idleSince;
    private bool _disposed;
    internal GoRemoteTransferChannel(Func<CancellationToken, Task<GoRemoteFileSystem>> create, TimeProvider? time = null)
    {
        _create = create;
        _time = time ?? TimeProvider.System;
        _idle = _time.CreateTimer(_ => _ = CloseIdleAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    public async Task<IRemoteTransferLease> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_fileSystem?.IsConnected != true)
            {
                if (_leases != 0) throw new IOException("传输连接已断开，等待现有租约归还后重连");
                if (_fileSystem != null) await _fileSystem.DisposeAsync();
                _fileSystem = await _create(ct);
                try { await _fileSystem.ConnectAsync(ct); }
                catch { await _fileSystem.DisposeAsync(); _fileSystem = null; throw; }
            }
            _leases++;
            return new Lease(this, _fileSystem.Borrow());
        }
        finally { _gate.Release(); }
    }
    private async ValueTask ReturnAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _leases--;
            if (!_disposed && _leases == 0)
            {
                _idleSince = _time.GetTimestamp();
                _idle.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
            }
        }
        finally { _gate.Release(); }
    }
    private async Task CloseIdleAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_disposed && _leases == 0 && _fileSystem != null && _time.GetElapsedTime(_idleSince) >= TimeSpan.FromSeconds(60))
            {
                await _fileSystem.DisposeAsync();
                _fileSystem = null;
            }
        }
        catch (IOException) { }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _idle.Dispose();
            if (_fileSystem != null) await _fileSystem.DisposeAsync();
            _fileSystem = null;
        }
        finally { _gate.Release(); }
    }
    private sealed class Lease(GoRemoteTransferChannel owner, IRemoteFileSystem fileSystem) : IRemoteTransferLease
    {
        private int _returned;
        public IRemoteFileSystem FileSystem { get; } = fileSystem;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _returned, 1) == 0 ? owner.ReturnAsync() : ValueTask.CompletedTask;
    }
}
