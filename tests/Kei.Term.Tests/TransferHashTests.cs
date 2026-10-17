using System.Security.Cryptography;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;

namespace Kei.Term.Tests;

public sealed class TransferHashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keiterm-hash-" + Guid.NewGuid().ToString("N"));

    public TransferHashTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task LocalHash_MatchesKnownVectors()
    {
        string path = Path.Combine(_root, "a.txt");
        await File.WriteAllBytesAsync(path, "a"u8.ToArray());
        ScriptShell shell = new(_ => new RemoteCommandResult("", "", 0));
        TransferHashVerifier sha256 = new(TransferHashAlgorithm.Sha256, shell, PosixToolset.Instance);
        TransferHashVerifier sha1 = new(TransferHashAlgorithm.Sha1, shell, PosixToolset.Instance);
        TransferHashVerifier md5 = new(TransferHashAlgorithm.Md5, shell, PosixToolset.Instance);

        Assert.Equal("ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb", await sha256.HashLocalAsync(path));
        Assert.Equal("86f7e437faa5a7fce15d1ddcb9eaeaea377667b8", await sha1.HashLocalAsync(path));
        Assert.Equal("0cc175b9c0f1b6a831c399e269772661", await md5.HashLocalAsync(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MatchingHash_RenamesPartial(bool upload)
    {
        byte[] content = "payload-hash"u8.ToArray();
        string hex = Hex(content);
        ByteRemote remote = new();
        string source = await PrepareSourceAsync(upload, remote, content);
        string destination = upload ? "/out" : Path.Combine(_root, "down");
        ScriptShell shell = new(command => command.Contains("sha256sum --", StringComparison.Ordinal)
            ? new RemoteCommandResult(hex + "  file\n", "", 0)
            : new RemoteCommandResult("", "no", 1));
        bool verified = false;

        await TransferAsync(upload, remote, source, destination, "final.bin", new TransferHashVerifier(TransferHashAlgorithm.Sha256, shell, PosixToolset.Instance), () => verified = true);

        Assert.True(verified);
        Assert.Contains(shell.Commands, command => command.Contains("sha256sum --", StringComparison.Ordinal));
        Assert.Equal(content, await ReadAsync(upload, remote, destination, "final.bin"));
        Assert.False(Exists(upload, remote, destination, "final.bin.keipart"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mismatch_DeletesPartialRegardlessOfResume(bool resume)
    {
        byte[] content = "payload-hash"u8.ToArray();
        ByteRemote remote = new();
        string source = await PrepareSourceAsync(upload: true, remote, content);
        string downSource = "/remote-src.bin";
        remote.Put(downSource, content);
        ScriptShell shell = new(_ => new RemoteCommandResult(new string('a', 64) + "  file\n", "", 0));
        TransferHashVerifier verifier = new(TransferHashAlgorithm.Sha256, shell, PosixToolset.Instance);
        FileTransferSettings settings = Settings(resume);

        TransferHashMismatchException upload = await Assert.ThrowsAsync<TransferHashMismatchException>(() =>
            new FileTransferExecutor().UploadAsync(Request(source, "/out", "a.bin", settings, verifier), remote));
        TransferHashMismatchException download = await Assert.ThrowsAsync<TransferHashMismatchException>(() =>
            new FileTransferExecutor().DownloadAsync(Request(downSource, Path.Combine(_root, "down"), "b.bin", settings, verifier), remote));

        Assert.NotEqual(upload.LocalHex, upload.RemoteHex);
        Assert.False(remote.Exists("/out/a.bin.keipart"));
        Assert.False(remote.Exists("/out/a.bin"));
        Assert.False(File.Exists(Path.Combine(_root, "down", "b.bin.keipart")));
        Assert.False(File.Exists(Path.Combine(_root, "down", "b.bin")));
        Assert.Equal(TransferFailureKind.Transient, DotNetTransferFailureClassifier.Instance.Classify(download));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteHashFailure_KeepsPartialOnlyWhenResumeIsOn(bool resume)
    {
        byte[] content = "payload-hash"u8.ToArray();
        ByteRemote remote = new();
        string source = await PrepareSourceAsync(upload: true, remote, content);
        ScriptShell rejected = new(_ => new RemoteCommandResult("", "missing", 127));
        ScriptShell garbage = new(_ => new RemoteCommandResult("not-a-hash\n", "", 0));
        FileTransferSettings settings = Settings(resume);

        TransferHashCommandException missing = await Assert.ThrowsAsync<TransferHashCommandException>(() =>
            new FileTransferExecutor().UploadAsync(
                Request(source, "/out", "a.bin", settings, new TransferHashVerifier(TransferHashAlgorithm.Sha256, rejected, PosixToolset.Instance)),
                remote));
        Assert.Equal(resume, remote.Exists("/out/a.bin.keipart"));
        Assert.Equal(TransferFailureKind.Transient, DotNetTransferFailureClassifier.Instance.Classify(missing));

        remote.Put("/remote-src.bin", content);
        await Assert.ThrowsAsync<TransferHashCommandException>(() =>
            new FileTransferExecutor().DownloadAsync(
                Request("/remote-src.bin", Path.Combine(_root, "down-" + resume), "b.bin", settings, new TransferHashVerifier(TransferHashAlgorithm.Sha256, garbage, PosixToolset.Instance)),
                remote));
        Assert.Equal(resume, File.Exists(Path.Combine(_root, "down-" + resume, "b.bin.keipart")));
    }

    [Fact]
    public async Task AfterCopyTamper_IsSeenByTheHash_AndDeletesThePartial()
    {
        byte[] content = "payload-hash"u8.ToArray();
        ByteRemote remote = new();
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, content);
        ScriptShell shell = new(_ =>
        {
            byte[] stored = remote.Read("/out/final.bin.keipart");
            string hex = Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant();
            return new RemoteCommandResult(hex + "  file\n", "", 0);
        });

        await Assert.ThrowsAsync<TransferHashMismatchException>(() => new FileTransferExecutor().UploadAsync(
            Request(source, "/out", "final.bin", Settings(resume: true), new TransferHashVerifier(TransferHashAlgorithm.Sha256, shell, PosixToolset.Instance), async _ =>
            {
                byte[] stored = remote.Read("/out/final.bin.keipart");
                stored[0] ^= 0xFF;
                remote.Put("/out/final.bin.keipart", stored);
                await Task.CompletedTask;
            }),
            remote));

        Assert.False(remote.Exists("/out/final.bin.keipart"));
        Assert.False(remote.Exists("/out/final.bin"));
    }

    [Fact]
    public async Task Queue_PublishesVerifyingBeforeCompletion()
    {
        byte[] content = "queue-ok"u8.ToArray();
        string source = Path.Combine(_root, "ok.bin");
        await File.WriteAllBytesAsync(source, content);
        TaskCompletionSource writing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource hashing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseHash = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HashFileSystem remote = new(command =>
        {
            if (command.Contains("sha256sum --", StringComparison.Ordinal))
            {
                hashing.TrySetResult();
                releaseHash.Task.GetAwaiter().GetResult();
                return new RemoteCommandResult(Hex(content) + "  file\n", "", 0);
            }

            if (command.StartsWith("uname", StringComparison.Ordinal) || command.Contains("sha256sum", StringComparison.Ordinal))
            {
                return new RemoteCommandResult(command.StartsWith("uname", StringComparison.Ordinal) ? "Linux\n" : "", "", 0);
            }

            return new RemoteCommandResult("", "", 1);
        })
        {
            OnWrite = () =>
            {
                writing.TrySetResult();
                releaseWrite.Task.GetAwaiter().GetResult();
            },
        };

        await using TransferQueue queue = Queue(remote, verify: true, retryCount: 0);
        List<QueuedTransferState> states = [];
        QueuedTransfer task = queue.Enqueue(Upload(source, "/out"));
        task.Updated += () =>
        {
            lock (states)
            {
                if (states.Count == 0 || states[^1] != task.State)
                {
                    states.Add(task.State);
                }
            }
        };
        await writing.Task;
        lock (states)
        {
            if (states.Count == 0 || states[^1] != QueuedTransferState.Transferring)
            {
                states.Add(QueuedTransferState.Transferring);
            }
        }

        releaseWrite.TrySetResult();
        await hashing.Task;
        lock (states)
        {
            Assert.Contains(QueuedTransferState.Transferring, states);
            Assert.Contains(QueuedTransferState.Verifying, states);
            Assert.True(states.IndexOf(QueuedTransferState.Transferring) < states.IndexOf(QueuedTransferState.Verifying));
        }

        releaseHash.TrySetResult();
        await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
        Assert.False(task.IsUnverified);
        Assert.Equal(content, remote.Read("/out/ok.bin"));
    }

    [Fact]
    public async Task Queue_NoAlgorithm_CompletesUnverifiedWithoutVerifying()
    {
        byte[] content = "plain"u8.ToArray();
        string source = Path.Combine(_root, "plain.bin");
        await File.WriteAllBytesAsync(source, content);
        HashFileSystem remote = new(command => command.StartsWith("uname", StringComparison.Ordinal)
            ? new RemoteCommandResult("", "no exec", 127)
            : new RemoteCommandResult(new string('b', 64) + "\n", "", 0));
        List<QueuedTransferState> states = [];
        await using TransferQueue queue = Queue(remote, verify: true, retryCount: 0);
        QueuedTransfer task = queue.Enqueue(Upload(source, "/out"));
        task.Updated += () =>
        {
            lock (states)
            {
                if (states.Count == 0 || states[^1] != task.State) states.Add(task.State);
            }
        };

        await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
        Assert.True(task.IsUnverified);
        Assert.DoesNotContain(QueuedTransferState.Verifying, states);
        Assert.DoesNotContain(remote.Commands, command => command.Contains("sha256sum --", StringComparison.Ordinal));
        Assert.Equal(content, remote.Read("/out/plain.bin"));
    }

    [Fact]
    public async Task Queue_SessionOverride_WinsOverGlobalSwitch()
    {
        byte[] content = "off"u8.ToArray();
        string source = Path.Combine(_root, "off.bin");
        await File.WriteAllBytesAsync(source, content);
        HashFileSystem remote = new(_ => new RemoteCommandResult("Linux\n", "", 0));
        await using TransferQueue disabled = Queue(remote, verify: true, retryCount: 0, verifyOverride: () => false);
        QueuedTransfer skipped = disabled.Enqueue(Upload(source, "/out"));
        await WaitUntilAsync(() => skipped.State == QueuedTransferState.Completed);
        Assert.False(skipped.IsUnverified);
        Assert.Empty(remote.Commands);

        HashFileSystem enabledRemote = new(command =>
        {
            if (command.Contains("sha256sum --", StringComparison.Ordinal))
            {
                return new RemoteCommandResult(Hex(content) + "\n", "", 0);
            }

            if (command.StartsWith("uname", StringComparison.Ordinal) || command.Contains("sha256sum", StringComparison.Ordinal))
            {
                return new RemoteCommandResult(command.StartsWith("uname", StringComparison.Ordinal) ? "Linux\n" : "", "", 0);
            }

            return new RemoteCommandResult("", "", 1);
        });
        await using TransferQueue enabled = Queue(enabledRemote, verify: false, retryCount: 0, verifyOverride: () => true);
        QueuedTransfer task = enabled.Enqueue(Upload(source, "/out"));
        await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
        Assert.False(task.IsUnverified);
        Assert.Contains(enabledRemote.Commands, command => command.Contains("sha256sum --", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Queue_MismatchRetry_RestartsFromZeroInsteadOfPlantedPartial()
    {
        byte[] content = "abcdef"u8.ToArray();
        string source = Path.Combine(_root, "retry.bin");
        await File.WriteAllBytesAsync(source, content);
        int hashes = 0;
        HashFileSystem remote = null!;
        remote = new HashFileSystem(command =>
        {
            if (!command.Contains("sha256sum --", StringComparison.Ordinal))
            {
                if (command.StartsWith("uname", StringComparison.Ordinal))
                {
                    return new RemoteCommandResult("Linux\n", "", 0);
                }

                return new RemoteCommandResult("", "", command.Contains("sha256sum", StringComparison.Ordinal) ? 0 : 1);
            }

            int n = Interlocked.Increment(ref hashes);
            if (n == 1)
            {
                return new RemoteCommandResult(new string('0', 64) + "\n", "", 0);
            }

            byte[] stored = remote.Read("/out/retry.bin.keipart");
            return new RemoteCommandResult(Hex(stored) + "\n", "", 0);
        });
        await using TransferQueue queue = Queue(remote, verify: true, retryCount: 1);
        QueuedTransfer task = queue.Enqueue(Upload(source, "/out"));
        task.Updated += () =>
        {
            if (task.State == QueuedTransferState.RetryWait)
            {
                remote.Put("/out/retry.bin.keipart", "XXX"u8.ToArray());
            }
        };

        await WaitUntilAsync(() => task.State == QueuedTransferState.Completed);
        Assert.Equal(content, remote.Read("/out/retry.bin"));
        Assert.Equal([0L, 0L], remote.WriteOffsets);
        Assert.False(task.IsUnverified);
        Assert.Equal(2, Volatile.Read(ref hashes));
    }

    private async Task<string> PrepareSourceAsync(bool upload, ByteRemote remote, byte[] content)
    {
        if (!upload)
        {
            remote.Put("/remote-src.bin", content);
            return "/remote-src.bin";
        }

        string path = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static Task TransferAsync(
        bool upload,
        ByteRemote remote,
        string source,
        string destination,
        string name,
        ITransferHashVerifier verifier,
        Action? onVerifying)
    {
        FileTransferRequest request = Request(source, destination, name, Settings(false), verifier, onVerifying: onVerifying);
        FileTransferExecutor executor = new();
        return upload ? executor.UploadAsync(request, remote) : executor.DownloadAsync(request, remote);
    }

    private static async Task<byte[]> ReadAsync(bool upload, ByteRemote remote, string destination, string name)
    {
        if (upload)
        {
            return remote.Read(destination.TrimEnd('/') + "/" + name);
        }

        return await File.ReadAllBytesAsync(Path.Combine(destination, name));
    }

    private static bool Exists(bool upload, ByteRemote remote, string destination, string name)
        => upload ? remote.Exists(destination.TrimEnd('/') + "/" + name) : File.Exists(Path.Combine(destination, name));

    private static FileTransferSettings Settings(bool resume) => new()
    {
        ResumeEnabled = resume,
        VerifyHashEnabled = true,
        HashAlgorithmOrder = [TransferHashAlgorithm.Sha256],
    };

    private static FileTransferRequest Request(
        string source,
        string destination,
        string name,
        FileTransferSettings settings,
        ITransferHashVerifier verifier,
        Func<CancellationToken, Task>? afterCopy = null,
        Action? onVerifying = null)
        => new(source, destination, name, settings, new TransferFileFacts(0, DateTimeOffset.UnixEpoch), Verifier: verifier, OnVerifying: onVerifying, AfterCopy: afterCopy);

    private static TransferQueue Queue(HashFileSystem remote, bool verify, int retryCount, Func<bool?>? verifyOverride = null)
    {
        FileSystemTransferEndpoint endpoint = new(remote, FileTransferProtocol.Sftp, owns: false);
        FileTransferSettings settings = new()
        {
            VerifyHashEnabled = verify,
            HashAlgorithmOrder = [TransferHashAlgorithm.Sha256],
            ArchiveFormatOrder = [],
            ResumeEnabled = true,
            AutoRetryEnabled = retryCount > 0,
            RetryCount = retryCount,
            BackoffMode = RetryBackoffMode.Fixed,
            InitialRetryIntervalSeconds = 0,
            MaxRetryIntervalSeconds = 0,
            DefaultConflictPolicy = TransferConflictPolicy.Overwrite,
        };
        return new TransferQueue(
            endpoint,
            () => settings,
            capabilities: new TransferCapabilityProvider(null, null, () => settings, endpoint.AcquireShellAsync),
            verifyHashOverride: verifyOverride);
    }

    private static TransferSubmission Upload(string path, string remoteDirectory)
    {
        FileInfo info = new(path);
        return new TransferSubmission(
            TransferDirection.Upload,
            info.Name,
            path,
            remoteDirectory,
            false,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc));
    }

    private static string Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(15, timeout.Token);
        }
    }

    private sealed class ScriptShell(Func<string, RemoteCommandResult> handle) : IRemoteShell
    {
        public List<string> Commands { get; } = [];

        public Task<RemoteCommandResult> RunAsync(string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(handle(command));
        }
    }

    private sealed class ByteRemote : IRemoteFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public bool SupportsResume => true;
        public bool IsConnected => true;
        public string WorkingDirectory => "/";

        public void Put(string path, byte[] data) => _files[path] = data.ToArray();

        public bool Exists(string path) => _files.ContainsKey(path);

        public byte[] Read(string path) => _files[path].ToArray();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
            => OpenReadAsync(path, 0, ct);

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default)
        {
            MemoryStream stream = new(_files[path].ToArray(), writable: false);
            stream.Position = offset;
            return Task.FromResult<Stream>(stream);
        }

        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
            => OpenWriteAsync(path, 0, ct);

        public Task<Stream> OpenWriteAsync(string path, long offset, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path, out byte[]? data) || offset == 0)
            {
                data = [];
                _files[path] = data;
            }

            return Task.FromResult<Stream>(new Sink(data, written => _files[path] = written));
        }

        public Task CommitWriteAsync(Stream stream, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            if (!_files.Remove(path))
            {
                throw new FileNotFoundException(path);
            }

            return Task.CompletedTask;
        }

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => RenameOverwriteAsync(oldPath, newPath, ct);

        public Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            byte[] data = _files[oldPath];
            _files.Remove(oldPath);
            _files[newPath] = data;
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path, out byte[]? data))
            {
                return Task.FromResult<RemoteFileItem?>(null);
            }

            return Task.FromResult<RemoteFileItem?>(new RemoteFileItem(Path.GetFileName(path), path, false, data.Length, DateTimeOffset.UnixEpoch, "-rw-r--r--"));
        }

        public Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Sink(byte[] data, Action<byte[]> commit) : MemoryStream
        {
            private byte[] _data = data.ToArray();

            public override void Write(byte[] buffer, int offset, int count)
            {
                int end = checked((int)(Position + count));
                if (_data.Length < end)
                {
                    Array.Resize(ref _data, end);
                }

                Buffer.BlockCopy(buffer, offset, _data, (int)Position, count);
                Position += count;
                commit(_data);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.ToArray(), 0, buffer.Length);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class HashFileSystem : IRemoteFileSystem, IRemoteShell
    {
        private readonly Func<string, RemoteCommandResult> _shell;
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly List<long> _offsets = [];

        public HashFileSystem(Func<string, RemoteCommandResult> shell) => _shell = shell;

        public Action? OnWrite { get; init; }
        public List<string> Commands { get; } = [];
        public IReadOnlyList<long> WriteOffsets => _offsets;
        public bool SupportsResume => true;
        public bool IsConnected => true;
        public string WorkingDirectory => "/";

        public void Put(string path, byte[] data) => _files[path] = data.ToArray();

        public byte[] Read(string path) => _files[path].ToArray();

        public Task<RemoteCommandResult> RunAsync(string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(_shell(command));
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => OpenReadAsync(path, 0, ct);

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default)
        {
            MemoryStream stream = new(_files[path].ToArray(), writable: false);
            stream.Position = offset;
            return Task.FromResult<Stream>(stream);
        }

        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => OpenWriteAsync(path, 0, ct);

        public Task<Stream> OpenWriteAsync(string path, long offset, CancellationToken ct = default)
        {
            _offsets.Add(offset);
            OnWrite?.Invoke();
            if (!_files.TryGetValue(path, out byte[]? existing) || offset == 0)
            {
                existing = [];
            }

            _files[path] = existing;
            return Task.FromResult<Stream>(new Sink(existing, offset, written => _files[path] = written));
        }

        public Task CommitWriteAsync(Stream stream, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => RenameOverwriteAsync(oldPath, newPath, ct);

        public Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            if (_files.Remove(oldPath, out byte[]? data))
            {
                _files[newPath] = data;
            }

            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path, out byte[]? data))
            {
                return Task.FromResult<RemoteFileItem?>(null);
            }

            return Task.FromResult<RemoteFileItem?>(new RemoteFileItem(Path.GetFileName(path), path, false, data.Length, DateTimeOffset.UnixEpoch, "-rw-r--r--"));
        }

        public Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Sink(byte[] data, long offset, Action<byte[]> commit) : MemoryStream
        {
            private byte[] _data = data.ToArray();
            private long _position = offset;

            public override bool CanSeek => true;
            public override long Position { get => _position; set => _position = value; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                int end = checked((int)(_position + count));
                if (_data.Length < end)
                {
                    Array.Resize(ref _data, end);
                }

                Buffer.BlockCopy(buffer, offset, _data, (int)_position, count);
                _position += count;
                commit(_data);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.ToArray(), 0, buffer.Length);
                return ValueTask.CompletedTask;
            }
        }
    }
}
