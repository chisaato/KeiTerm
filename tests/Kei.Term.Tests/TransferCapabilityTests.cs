using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Transfers;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public class TransferCapabilityTests
{
    [Fact]
    public void EffectiveOrders_DropDuplicatesUnknownsAndEmptyLists()
    {
        FileTransferSettings settings = new()
        {
            HashAlgorithmOrder =
            [
                TransferHashAlgorithm.Md5,
                TransferHashAlgorithm.Md5,
                (TransferHashAlgorithm)99,
                TransferHashAlgorithm.Sha256,
            ],
            ArchiveFormatOrder = [],
            ArchiveDirectoriesEnabled = true,
            VerifyHashEnabled = true,
        };

        Assert.Equal([TransferHashAlgorithm.Md5, TransferHashAlgorithm.Sha256], settings.EffectiveHashAlgorithmOrder);
        Assert.Empty(settings.EffectiveArchiveFormatOrder);
        Assert.True(settings.EffectiveVerifyHashEnabled);
        Assert.False(settings.EffectiveArchiveDirectoriesEnabled);
        Assert.Equal(4, settings.HashAlgorithmOrder.Count);

        FileTransferSettings defaults = new();
        Assert.Equal([TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Sha1, TransferHashAlgorithm.Md5], defaults.EffectiveHashAlgorithmOrder);
        Assert.Equal([TransferArchiveFormat.Zstd, TransferArchiveFormat.Gzip, TransferArchiveFormat.Tar], defaults.EffectiveArchiveFormatOrder);
        Assert.True(defaults.VerifyHashEnabled);
        Assert.False(defaults.ArchiveDirectoriesEnabled);
        Assert.True(defaults.EffectiveVerifyHashEnabled);
        Assert.False(defaults.EffectiveArchiveDirectoriesEnabled);

        defaults.VerifyHashEnabled = false;
        Assert.False(defaults.EffectiveVerifyHashEnabled);
        defaults.HashAlgorithmOrder = null!;
        Assert.Empty(defaults.EffectiveHashAlgorithmOrder);
    }

    [Fact]
    public async Task SettingsJson_MissingFieldsUseDefaults_AndUnknownNumbersAreDroppedOnRead()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-cap-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string missing = Path.Combine(directory, "missing.json");
            await File.WriteAllTextAsync(missing, """
                {
                  "FontSize": 18,
                  "FileTransfer": { "PollingIntervalSeconds": 9 }
                }
                """);
            JsonSettingsService missingReader = new(missing);
            FileTransferSettings loadedMissing = (await missingReader.LoadSettingsAsync()).FileTransfer;
            Assert.Equal(18, missingReader.Current.FontSize);
            Assert.Equal(9, loadedMissing.PollingIntervalSeconds);
            Assert.True(loadedMissing.VerifyHashEnabled);
            Assert.False(loadedMissing.ArchiveDirectoriesEnabled);
            Assert.Equal([TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Sha1, TransferHashAlgorithm.Md5], loadedMissing.EffectiveHashAlgorithmOrder);
            Assert.Equal([TransferArchiveFormat.Zstd, TransferArchiveFormat.Gzip, TransferArchiveFormat.Tar], loadedMissing.EffectiveArchiveFormatOrder);

            string custom = Path.Combine(directory, "custom.json");
            JsonSettingsService writer = new(custom);
            await writer.SaveSettingsAsync(new AppSettings
            {
                FileTransfer = new FileTransferSettings
                {
                    VerifyHashEnabled = false,
                    HashAlgorithmOrder = [TransferHashAlgorithm.Md5, TransferHashAlgorithm.Md5, TransferHashAlgorithm.Sha1],
                    ArchiveDirectoriesEnabled = true,
                    ArchiveFormatOrder = [TransferArchiveFormat.Tar, (TransferArchiveFormat)40, TransferArchiveFormat.Gzip],
                },
            });
            string saved = await File.ReadAllTextAsync(custom);
            Assert.Contains("\"HashAlgorithmOrder\"", saved, StringComparison.Ordinal);
            Assert.DoesNotContain("Sha256", saved, StringComparison.Ordinal);

            JsonSettingsService reader = new(custom);
            FileTransferSettings loaded = (await reader.LoadSettingsAsync()).FileTransfer;
            Assert.False(loaded.VerifyHashEnabled);
            Assert.True(loaded.ArchiveDirectoriesEnabled);
            Assert.Equal([TransferHashAlgorithm.Md5, TransferHashAlgorithm.Sha1], loaded.EffectiveHashAlgorithmOrder);
            Assert.Equal([TransferArchiveFormat.Tar, TransferArchiveFormat.Gzip], loaded.EffectiveArchiveFormatOrder);
            Assert.False(loaded.EffectiveVerifyHashEnabled);
            Assert.True(loaded.EffectiveArchiveDirectoriesEnabled);
            Assert.Contains((TransferArchiveFormat)40, loaded.ArchiveFormatOrder);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Select_UsesCurrentPreferenceNotStoredOrder()
    {
        TransferProbeResult result = new(
            RemotePlatform.Linux,
            [TransferHashAlgorithm.Sha1, TransferHashAlgorithm.Md5],
            [TransferArchiveFormat.Tar, TransferArchiveFormat.Gzip],
            new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(TransferHashAlgorithm.Sha1, result.SelectHash([TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Sha1, TransferHashAlgorithm.Md5]));
        Assert.Equal(TransferHashAlgorithm.Md5, result.SelectHash([TransferHashAlgorithm.Md5]));
        Assert.Null(result.SelectHash([TransferHashAlgorithm.Sha256]));
        Assert.Null(result.SelectHash([]));
        Assert.Equal(TransferArchiveFormat.Gzip, result.SelectArchive([TransferArchiveFormat.Zstd, TransferArchiveFormat.Gzip, TransferArchiveFormat.Tar]));
        Assert.Null(result.SelectArchive([TransferArchiveFormat.Zstd]));
    }

    [Fact]
    public void SessionOverrides_CloneCopiesTransferFlags()
    {
        SessionOverrides source = new() { VerifyHashEnabled = false, ArchiveDirectoriesEnabled = true };
        SessionOverrides clone = source.Clone();
        source.VerifyHashEnabled = true;
        Assert.False(clone.VerifyHashEnabled);
        Assert.True(clone.ArchiveDirectoriesEnabled);
        Assert.Null(new SessionOverrides().VerifyHashEnabled);
        Assert.Null(new SessionOverrides().ArchiveDirectoriesEnabled);
    }

    [Fact]
    public void PosixToolset_QuotesPathsAndParsesLeadingHex()
    {
        const string path = "a b'c -d";
        string quoted = "'a b'\\''c -d'";
        Assert.Equal(quoted, PosixShell.Quote(path));
        Assert.Equal(quoted, ScpRemoteFileSystem.ShellQuote(path));

        PosixToolset tools = PosixToolset.Instance;
        string hash = tools.HashCommand(TransferHashAlgorithm.Sha256, path);
        Assert.Contains("sha256sum -- " + quoted, hash, StringComparison.Ordinal);
        Assert.Contains("shasum -a 256 -- " + quoted, hash, StringComparison.Ordinal);
        string md5 = tools.HashCommand(TransferHashAlgorithm.Md5, path);
        Assert.Contains("md5sum -- " + quoted, md5, StringComparison.Ordinal);
        Assert.Contains("md5 -q -- " + quoted, md5, StringComparison.Ordinal);

        string sha256 = new string('A', 64);
        Assert.True(tools.TryParseHash(TransferHashAlgorithm.Sha256, sha256 + "  file name", out string parsed));
        Assert.Equal(sha256.ToLowerInvariant(), parsed);
        Assert.True(tools.TryParseHash(TransferHashAlgorithm.Sha256, "\\" + sha256.ToLowerInvariant() + " *file\n", out parsed));
        Assert.False(tools.TryParseHash(TransferHashAlgorithm.Sha256, new string('a', 40) + "  file", out _));
        Assert.False(tools.TryParseHash(TransferHashAlgorithm.Md5, "MD5 (file) = " + new string('a', 32), out _));
        Assert.True(tools.TryParseHash(TransferHashAlgorithm.Md5, new string('b', 32) + "\n", out parsed));
        Assert.Equal(new string('b', 32), parsed);

        string source = "/var/tmp/a b'c/-lead";
        string archive = "/tmp/it's odd.tzst";
        string packed = tools.PackCommand(TransferArchiveFormat.Zstd, source, archive);
        Assert.Contains("tar -C '/var/tmp/a b'\\''c' -cf \"$tmp\" -- '-lead'", packed, StringComparison.Ordinal);
        Assert.Contains("zstd -q -c \"$tmp\" > '/tmp/it'\\''s odd.tzst'", packed, StringComparison.Ordinal);
        Assert.Contains("mktemp", packed, StringComparison.Ordinal);
        Assert.Contains("||", packed, StringComparison.Ordinal);
        Assert.DoesNotContain(" | ", packed, StringComparison.Ordinal);

        string plain = tools.PackCommand(TransferArchiveFormat.Tar, source, "/tmp/out.tar");
        Assert.Equal("tar -C '/var/tmp/a b'\\''c' -cf '/tmp/out.tar' -- '-lead'", plain);
        Assert.DoesNotContain("mktemp", plain, StringComparison.Ordinal);

        string unpacked = tools.UnpackCommand(TransferArchiveFormat.Gzip, archive, "/dest/a b");
        Assert.Contains("gzip -dc '/tmp/it'\\''s odd.tzst'", unpacked, StringComparison.Ordinal);
        Assert.Contains("tar -C '/dest/a b' -xf \"$tmp\"", unpacked, StringComparison.Ordinal);
        Assert.Contains("||", unpacked, StringComparison.Ordinal);
        Assert.DoesNotContain(" | ", unpacked, StringComparison.Ordinal);

        Assert.Null(RemoteToolsets.For(RemotePlatform.Windows));
        Assert.Null(RemoteToolsets.For(RemotePlatform.Unknown));
        Assert.Same(PosixToolset.Instance, RemoteToolsets.For(RemotePlatform.Darwin));
        Assert.Equal(RemotePlatform.Linux, tools.ParsePlatform("Linux\n"));
        Assert.Equal(RemotePlatform.Darwin, tools.ParsePlatform("Darwin\r\n"));
        Assert.Equal(RemotePlatform.Unknown, tools.ParsePlatform("Windows_NT\n"));
    }

    [Fact]
    public async Task Darwin_DoesNotAdvertiseGnuDirectoryArchiveImplementation()
    {
        ScriptShell shell = new(command => new RemoteCommandResult(command == "uname -s" ? "Darwin\n" : "", "", 0));
        TransferProbeResult result = await TransferCapabilityProbe.ProbeAsync(shell, new FileTransferSettings());
        Assert.Equal(RemotePlatform.Darwin, result.Platform);
        Assert.NotEmpty(result.Hashes);
        Assert.Empty(result.Archives);
    }

    [Fact]
    public async Task Probe_FollowsSettingsOrder_AndTreatsExecFailureAsEmpty()
    {
        PosixToolset tools = PosixToolset.Instance;
        FileTransferSettings settings = new()
        {
            HashAlgorithmOrder = [TransferHashAlgorithm.Md5, TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Sha1],
            ArchiveFormatOrder = [TransferArchiveFormat.Zstd, TransferArchiveFormat.Gzip, TransferArchiveFormat.Tar],
        };
        DateTimeOffset now = new(2026, 10, 18, 3, 4, 5, TimeSpan.Zero);
        ScriptShell shell = new(command =>
        {
            if (command == tools.PlatformProbeCommand)
            {
                return new RemoteCommandResult("Linux\n", "", 0);
            }

            if (command == tools.HashProbeCommand(TransferHashAlgorithm.Md5)
                || command == tools.HashProbeCommand(TransferHashAlgorithm.Sha1)
                || command == tools.ArchiveProbeCommand(TransferArchiveFormat.Gzip)
                || command == tools.ArchiveProbeCommand(TransferArchiveFormat.Tar))
            {
                return new RemoteCommandResult("", "", 0);
            }

            return new RemoteCommandResult("", "missing", 1);
        });

        TransferProbeResult result = await TransferCapabilityProbe.ProbeAsync(shell, settings, new FixedClock(now));
        Assert.Equal(RemotePlatform.Linux, result.Platform);
        Assert.Equal([TransferHashAlgorithm.Md5, TransferHashAlgorithm.Sha1], result.Hashes);
        Assert.Equal([TransferArchiveFormat.Gzip, TransferArchiveFormat.Tar], result.Archives);
        Assert.Equal(now.UtcDateTime, result.ProbedAtUtc);
        Assert.Contains(shell.Commands, command => command == tools.HashProbeCommand(TransferHashAlgorithm.Sha256));
        Assert.Contains(shell.Commands, command => command == tools.ArchiveProbeCommand(TransferArchiveFormat.Zstd));

        ScriptShell darwin = new(command =>
        {
            if (command == tools.PlatformProbeCommand)
            {
                return new RemoteCommandResult("Darwin\n", "", 0);
            }

            if (command == tools.HashProbeCommand(TransferHashAlgorithm.Sha1))
            {
                return new RemoteCommandResult("", "", 0);
            }

            return new RemoteCommandResult("", "", 1);
        });
        TransferProbeResult mac = await TransferCapabilityProbe.ProbeAsync(
            darwin,
            new FileTransferSettings { HashAlgorithmOrder = [TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Sha1], ArchiveFormatOrder = [] },
            new FixedClock(now));
        Assert.Equal(RemotePlatform.Darwin, mac.Platform);
        Assert.Equal([TransferHashAlgorithm.Sha1], mac.Hashes);
        Assert.Empty(mac.Archives);

        ScriptShell windows = new(_ => new RemoteCommandResult("Windows_NT\n", "", 0));
        TransferProbeResult unknown = await TransferCapabilityProbe.ProbeAsync(windows, settings, new FixedClock(now));
        Assert.Equal(RemotePlatform.Unknown, unknown.Platform);
        Assert.Empty(unknown.Hashes);
        Assert.Empty(unknown.Archives);
        Assert.Equal([tools.PlatformProbeCommand], windows.Commands);

        ScriptShell rejected = new(_ => throw new IOException("exec refused"));
        TransferProbeResult empty = await TransferCapabilityProbe.ProbeAsync(rejected, settings, new FixedClock(now));
        Assert.Equal(RemotePlatform.Unknown, empty.Platform);
        Assert.Empty(empty.Hashes);

        ScriptShell failed = new(_ => new RemoteCommandResult("", "no", 127));
        TransferProbeResult failedResult = await TransferCapabilityProbe.ProbeAsync(failed, settings, new FixedClock(now));
        Assert.Equal(RemotePlatform.Unknown, failedResult.Platform);
        Assert.Empty(failedResult.Hashes);
    }

    [Fact]
    public async Task Provider_SharesOneProbe_RereadsRepository_AndKeepsUnsavedInMemory()
    {
        PosixToolset tools = PosixToolset.Instance;
        DateTimeOffset now = new(2026, 10, 18, 1, 2, 3, TimeSpan.Zero);
        CountingMetadata repository = new();
        HoldingShell shell = new(command => command == tools.PlatformProbeCommand
            ? new RemoteCommandResult("Linux\n", "", 0)
            : new RemoteCommandResult("", "", command.Contains("sha256sum", StringComparison.Ordinal) || command.Contains("gzip", StringComparison.Ordinal) ? 0 : 1));
        Guid sessionId = Guid.NewGuid();
        TransferCapabilityProvider provider = new(sessionId, repository, () => new FileTransferSettings
        {
            HashAlgorithmOrder = [TransferHashAlgorithm.Sha256],
            ArchiveFormatOrder = [TransferArchiveFormat.Gzip],
        }, _ => Task.FromResult<IRemoteShell?>(shell), new FixedClock(now));

        Task<TransferProbeResult> first = provider.GetAsync();
        Task<TransferProbeResult> second = provider.GetAsync();
        await WaitUntilAsync(() => Volatile.Read(ref repository.Gets) >= 4 && shell.UnameEntered.Task.IsCompleted);
        DateTime quietUntil = DateTime.UtcNow.AddMilliseconds(200);
        while (DateTime.UtcNow < quietUntil)
        {
            Assert.Equal(1, Volatile.Read(ref shell.UnameCount));
            await Task.Delay(10);
        }

        shell.Release.TrySetResult();
        TransferProbeResult[] probed = await Task.WhenAll(first, second);
        Assert.Same(probed[0], probed[1]);
        Assert.Equal(RemotePlatform.Linux, probed[0].Platform);
        Assert.Equal([TransferHashAlgorithm.Sha256], probed[0].Hashes);
        Assert.Equal([TransferArchiveFormat.Gzip], probed[0].Archives);
        Assert.Equal(1, Volatile.Read(ref shell.UnameCount));
        Assert.Equal(1, repository.Sets);

        TransferProbeResult cached = await provider.GetAsync();
        Assert.Equal(1, Volatile.Read(ref shell.UnameCount));
        Assert.Equal(probed[0].ProbedAtUtc, cached.ProbedAtUtc);

        await repository.ClearTransferProbeAsync(sessionId);
        TransferProbeResult again = await provider.GetAsync();
        Assert.Equal(2, Volatile.Read(ref shell.UnameCount));
        Assert.Equal(RemotePlatform.Linux, again.Platform);
        Assert.Equal(2, repository.Sets);

        CountingMetadata missing = new() { AcceptWrite = false };
        HoldingShell unsaved = new(command => new RemoteCommandResult("Linux\n", "", 0));
        unsaved.Release.TrySetResult();
        TransferCapabilityProvider memory = new(sessionId, missing, () => new FileTransferSettings(), _ => Task.FromResult<IRemoteShell?>(unsaved), new FixedClock(now));
        TransferProbeResult firstMemory = await memory.GetAsync();
        TransferProbeResult secondMemory = await memory.GetAsync();
        Assert.Same(firstMemory, secondMemory);
        Assert.Equal(1, Volatile.Read(ref unsaved.UnameCount));
        Assert.Equal(1, missing.Sets);
        Assert.Null(missing.Value);

        int acquires = 0;
        TransferCapabilityProvider noShell = new(null, repository, () => new FileTransferSettings(), _ =>
        {
            Interlocked.Increment(ref acquires);
            return Task.FromResult<IRemoteShell?>(null);
        }, new FixedClock(now));
        TransferProbeResult denied = await noShell.GetAsync();
        TransferProbeResult deniedAgain = await noShell.GetAsync();
        Assert.Equal(RemotePlatform.Unknown, denied.Platform);
        Assert.Empty(denied.Hashes);
        Assert.Same(denied, deniedAgain);
        Assert.Equal(1, Volatile.Read(ref acquires));
    }

    [Fact]
    public async Task Endpoint_ExposesShell_AndBoardInstallsProvider()
    {
        EchoShell echo = new();
        await using ChannelTransferEndpoint channel = new(new ShellChannel(echo));
        IRemoteShell? fromChannel = await channel.AcquireShellAsync();
        Assert.Same(echo, fromChannel);

        ShellFileSystem shellFs = new();
        await using FileSystemTransferEndpoint scp = new(shellFs, FileTransferProtocol.Scp, owns: false);
        IRemoteShell? fromScp = await scp.AcquireShellAsync();
        Assert.Same(shellFs, fromScp);
        IRemoteShell? again = await scp.AcquireShellAsync();
        Assert.Same(shellFs, again);
        Assert.Equal(1, shellFs.Connects);

        BareFileSystem browse = new();
        await using FileSystemTransferEndpoint sftp = new(browse, FileTransferProtocol.Sftp, owns: false);
        Assert.Null(await sftp.AcquireShellAsync());
        Assert.Equal(1, browse.Connects);

        await using ConnectionTransferBoard board = ConnectionTransferBoard.Create(
            new IdleEndpoint(),
            () => new FileTransferSettings(),
            NullInteractionService.Instance);
        Assert.NotNull(board.Queue.Capabilities);
        TransferProbeResult result = await board.Queue.Capabilities!.GetAsync();
        Assert.Equal(RemotePlatform.Unknown, result.Platform);
        Assert.Empty(result.Hashes);
        Assert.Empty(result.Archives);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("条件没有在 2 秒内成立");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

    private sealed class HoldingShell(Func<string, RemoteCommandResult> handle) : IRemoteShell
    {
        public int UnameCount;
        public TaskCompletionSource UnameEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RemoteCommandResult> RunAsync(string command, CancellationToken cancellationToken = default)
        {
            if (command.StartsWith("uname", StringComparison.Ordinal))
            {
                int count = Interlocked.Increment(ref UnameCount);
                if (count == 1)
                {
                    UnameEntered.TrySetResult();
                }

                await Release.Task.WaitAsync(cancellationToken);
            }

            return handle(command);
        }
    }

    private sealed class EchoShell : IRemoteShell
    {
        public Task<RemoteCommandResult> RunAsync(string command, CancellationToken cancellationToken = default)
            => Task.FromResult(new RemoteCommandResult(command, "", 0));
    }

    private sealed class CountingMetadata : ISessionMetadataRepository
    {
        public TransferProbeResult? Value;
        public int Gets;
        public int Sets;
        public bool AcceptWrite = true;

        public Task<TransferProbeResult?> GetTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Gets);
            return Task.FromResult(Value);
        }

        public Task<bool> SetTransferProbeAsync(Guid sessionId, TransferProbeResult result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Sets);
            if (!AcceptWrite)
            {
                return Task.FromResult(false);
            }

            Value = result;
            return Task.FromResult(true);
        }

        public Task ClearTransferProbeAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Value = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ShellChannel(IRemoteShell shell) : IRemoteTransferChannel
    {
        public Task<IRemoteTransferLease> AcquireAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IRemoteShell?> AcquireShellAsync(CancellationToken ct = default)
            => Task.FromResult<IRemoteShell?>(shell);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdleEndpoint : ITransferEndpoint
    {
        public FileTransferProtocol Protocol => FileTransferProtocol.Sftp;

        public Task<IRemoteTransferLease> AcquireAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class BareFileSystem : IRemoteFileSystem
    {
        public int Connects;
        public bool IsConnected { get; private set; }
        public string WorkingDirectory => "/";

        public Task ConnectAsync(CancellationToken ct = default)
        {
            Connects++;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ShellFileSystem : BareFileSystem, IRemoteShell
    {
        public Task<RemoteCommandResult> RunAsync(string command, CancellationToken cancellationToken = default)
            => Task.FromResult(new RemoteCommandResult("ok", "", 0));
    }
}
