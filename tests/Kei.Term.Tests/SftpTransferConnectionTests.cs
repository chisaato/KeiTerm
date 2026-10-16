using Kei.Term.Core.Abstractions;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class SftpTransferConnectionTests
{
    [Fact]
    public void IdleTimeout_IsSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), SftpTransferConnection.IdleTimeout);
    }

    [Fact]
    public async Task FirstAcquireConnects_SecondAcquireReuses_IdleCloseThenReconnects()
    {
        await using Harness harness = Harness.Create();
        IRemoteTransferLease first = await harness.Channel.AcquireAsync();
        ProbeFileSystem firstFiles = Assert.IsType<ProbeFileSystem>(first.FileSystem);
        IRemoteTransferLease second = await harness.Channel.AcquireAsync();
        ProbeFileSystem secondFiles = Assert.IsType<ProbeFileSystem>(second.FileSystem);

        Assert.Equal(1, harness.Connects);
        Assert.Equal(firstFiles.Client.Id, secondFiles.Client.Id);
        Assert.Equal(1, harness.Channel.ConnectCount);

        await first.DisposeAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(firstFiles.Client.Disposed);

        await second.DisposeAsync();
        harness.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(firstFiles.Client.Disposed);
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(firstFiles.Client.Disposed);

        await using IRemoteTransferLease third = await harness.Channel.AcquireAsync();
        ProbeFileSystem thirdFiles = Assert.IsType<ProbeFileSystem>(third.FileSystem);
        Assert.Equal(2, harness.Connects);
        Assert.NotEqual(firstFiles.Client.Id, thirdFiles.Client.Id);
        Assert.True(thirdFiles.Client.IsConnected);
    }

    [Fact]
    public async Task DroppedClient_ReconnectsOnNextAcquire_AndDisposesTheOldOne()
    {
        await using Harness harness = Harness.Create();
        IRemoteTransferLease lease = await harness.Channel.AcquireAsync();
        ProbeFileSystem files = Assert.IsType<ProbeFileSystem>(lease.FileSystem);
        files.Client.IsConnected = false;
        await lease.DisposeAsync();

        await using IRemoteTransferLease again = await harness.Channel.AcquireAsync();
        ProbeFileSystem againFiles = Assert.IsType<ProbeFileSystem>(again.FileSystem);
        Assert.Equal(2, harness.Connects);
        Assert.True(files.Client.Disposed);
        Assert.NotEqual(files.Client.Id, againFiles.Client.Id);
    }

    [Fact]
    public async Task ConcurrentAcquire_EstablishesOneConnection()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int connects = 0;
        await using SftpTransferConnection channel = new(
            async ct =>
            {
                Interlocked.Increment(ref connects);
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new ProbeClient(1);
            },
            client => new ProbeFileSystem((ProbeClient)client),
            new ManualTimeProvider(),
            owned: null,
            logger: null);

        Task<IRemoteTransferLease> first = channel.AcquireAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<IRemoteTransferLease> second = channel.AcquireAsync();
        release.TrySetResult();
        IRemoteTransferLease[] leases = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, connects);
        Assert.Equal(1, channel.ConnectCount);
        Assert.Equal(
            Assert.IsType<ProbeFileSystem>(leases[0].FileSystem).Client.Id,
            Assert.IsType<ProbeFileSystem>(leases[1].FileSystem).Client.Id);
        await leases[0].DisposeAsync();
        await leases[1].DisposeAsync();
    }

    [Fact]
    public async Task HeldLease_DoesNotIdleClose()
    {
        await using Harness harness = Harness.Create();
        await using IRemoteTransferLease lease = await harness.Channel.AcquireAsync();
        ProbeFileSystem files = Assert.IsType<ProbeFileSystem>(lease.FileSystem);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(files.Client.Disposed);
        Assert.Equal(1, harness.Connects);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightConnect_AndFailsOutstandingLease()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using SftpTransferConnection connecting = new(
            async ct =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new ProbeClient(1);
            },
            client => new ProbeFileSystem((ProbeClient)client),
            new ManualTimeProvider(),
            owned: null,
            logger: null);
        Task pending = connecting.AcquireAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await connecting.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        await using Harness harness = Harness.Create();
        IRemoteTransferLease lease = await harness.Channel.AcquireAsync();
        await harness.Channel.DisposeAsync();
        IOException error = await Assert.ThrowsAsync<IOException>(() => lease.FileSystem.OpenReadAsync("/x"));
        Assert.Equal("传输连接已断开", error.Message);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Channel.AcquireAsync());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private int _connects;
        private int _nextId;

        public SftpTransferConnection Channel { get; }
        public ManualTimeProvider Clock { get; }
        public int Connects => Volatile.Read(ref _connects);

        private Harness(SftpTransferConnection channel, ManualTimeProvider clock)
        {
            Channel = channel;
            Clock = clock;
        }

        public static Harness Create()
        {
            ManualTimeProvider clock = new();
            Harness harness = null!;
            harness = new Harness(new SftpTransferConnection(
                ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref harness._connects);
                    return Task.FromResult<ITransferClient>(new ProbeClient(Interlocked.Increment(ref harness._nextId)));
                },
                client => new ProbeFileSystem((ProbeClient)client),
                clock,
                owned: null,
                logger: null), clock);
            return harness;
        }

        public ValueTask DisposeAsync() => Channel.DisposeAsync();
    }

    private sealed class ProbeClient(int id) : ITransferClient
    {
        public int Id { get; } = id;
        public bool IsConnected { get; set; } = true;
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            IsConnected = false;
        }
    }

    private sealed class ProbeFileSystem : IRemoteFileSystem
    {
        public ProbeFileSystem(ProbeClient client)
        {
            Client = client;
        }

        public ProbeClient Client { get; }
        public bool IsConnected => Client.IsConnected;
        public string WorkingDirectory => "/";

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Kei.Term.Core.Models.RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => throw Unused();

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        {
            if (!Client.IsConnected)
            {
                throw new IOException("传输连接已断开");
            }

            return Task.FromResult(Stream.Null);
        }

        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => throw Unused();
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => throw Unused();
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => throw Unused();
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => throw Unused();
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => throw Unused();
        public Task<Kei.Term.Core.Models.RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => throw Unused();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Exception Unused() => new InvalidOperationException("测试未使用此操作");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state, _utcNow + dueTime);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _utcNow += by;
            List<ManualTimer> due;
            lock (_gate)
            {
                due = _timers.Where(timer => timer.IsDue(_utcNow)).ToList();
            }

            foreach (ManualTimer timer in due)
            {
                timer.Fire();
            }
        }

        private void Forget(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private int _fired;

            public bool IsDue(DateTimeOffset now) => _fired == 0 && due <= now;

            public void Fire()
            {
                if (Interlocked.Exchange(ref _fired, 1) == 1)
                {
                    return;
                }

                callback(state);
                owner.Forget(this);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose()
            {
                Interlocked.Exchange(ref _fired, 1);
                owner.Forget(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
