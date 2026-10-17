using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class DirectoryArchiveSshIntegrationTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    [SshdFact]
    public async Task DirectoryRoundTrip_AllFormats_PreservesContentTimesAndSourceTreeSelection()
    {
        foreach (TransferArchiveFormat format in Enum.GetValues<TransferArchiveFormat>())
        {
            string root = NewRoot();
            string source = Path.Combine(root, "source");
            string uploadTo = Path.Combine(root, "remote");
            string downloadTo = Path.Combine(root, "download");
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            Directory.CreateDirectory(uploadTo);
            Directory.CreateDirectory(downloadTo);
            await File.WriteAllTextAsync(Path.Combine(source, "-file ' 中文"), "payload");
            File.CreateSymbolicLink(Path.Combine(source, "file-link"), "-file ' 中文");
            Directory.CreateSymbolicLink(Path.Combine(source, "directory-link"), "empty");
            DateTime modified = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddMilliseconds(123);
            File.SetLastWriteTimeUtc(Path.Combine(source, "-file ' 中文"), modified);
            Directory.SetLastWriteTimeUtc(Path.Combine(source, "empty"), modified);
            Directory.SetLastWriteTimeUtc(source, modified);
            FileTransferSettings settings = Settings();
            settings.ArchiveFormatOrder = [format];
            await using IRemoteTransferChannel channel = await new SshSessionFactory().CreateTransferChannelAsync(sshd.Config(), sshd.KeyAuth());
            await using ChannelTransferEndpoint endpoint = new(channel);
            await using TransferQueue queue = Queue(endpoint, settings);
            QueuedTransfer upload = queue.Enqueue(new(TransferDirection.Upload, "renamed-root", source, uploadTo, true));
            await FinishAsync(upload);
            Assert.True(upload.TotalBytes > 0);
            Assert.False(upload.IsUnverified);
            string remote = Path.Combine(uploadTo, "renamed-root");
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(remote, "-file ' 中文")));
            Assert.False(Directory.Exists(Path.Combine(remote, "directory-link")));
            // 再加远端软链，确保下载打包也按 SourceTreeReader 规则解引用文件、跳过目录。
            File.CreateSymbolicLink(Path.Combine(remote, "remote-file-link"), "-file ' 中文");
            Directory.CreateSymbolicLink(Path.Combine(remote, "remote-directory-link"), "empty");
            Directory.SetLastWriteTimeUtc(remote, modified);
            QueuedTransfer download = queue.Enqueue(new(TransferDirection.Download, "downloaded", downloadTo, remote, true));
            await FinishAsync(download);
            string result = Path.Combine(downloadTo, "downloaded");
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(result, "-file ' 中文")));
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(result, "remote-file-link")));
            Assert.Null(new FileInfo(Path.Combine(result, "remote-file-link")).LinkTarget);
            Assert.False(Directory.Exists(Path.Combine(result, "remote-directory-link")));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(result, "-file ' 中文")));
            Assert.Equal(modified, Directory.GetLastWriteTimeUtc(Path.Combine(result, "empty")));
            Assert.Equal(modified, Directory.GetLastWriteTimeUtc(result));
            Assert.Empty(Directory.GetDirectories(uploadTo, ".keiterm-archive*"));
            Assert.Empty(Directory.GetDirectories(downloadTo, ".keiterm-archive*"));
        }
    }

    [SshdFact]
    public async Task GzipFallback_WholeDirectoryConflictPolicies_AndAskOnlyOnce()
    {
        foreach (TransferDirection direction in Enum.GetValues<TransferDirection>())
        foreach (TransferConflictPolicy policy in new[] { TransferConflictPolicy.Overwrite, TransferConflictPolicy.Skip, TransferConflictPolicy.AutoRename, TransferConflictPolicy.Ask })
        {
            string root = NewRoot();
            string source = Path.Combine(root, "source");
            string parent = Path.Combine(root, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(parent, "tree"));
            await File.WriteAllTextAsync(Path.Combine(source, "file"), "new");
            await File.WriteAllTextAsync(Path.Combine(source, "second"), "new2");
            await File.WriteAllTextAsync(Path.Combine(parent, "tree", "file"), "old");
            await File.WriteAllTextAsync(Path.Combine(parent, "tree", "extra"), "retained");
            FileTransferSettings settings = Settings();
            settings.DefaultConflictPolicy = policy;
            int prompts = 0;
            await using IRemoteTransferChannel channel = await new SshSessionFactory().CreateTransferChannelAsync(sshd.Config(), sshd.KeyAuth());
            await using ChannelTransferEndpoint endpoint = new(channel);
            // 注入结果：zstd 不可用，配置仍按 zstd/gzip/tar，必须真正用 gzip 成功往返。
            await using TransferQueue queue = new(endpoint, () => settings, capabilities: new GzipCapabilities(),
                promptConflict: (_, _, _, _) =>
                {
                    prompts++;
                    return Task.FromResult<TransferConflictChoice?>(new(TransferConflictPolicy.Overwrite, false));
                });
            QueuedTransfer task = queue.Enqueue(direction == TransferDirection.Upload
                ? new(direction, "tree", source, parent, true)
                : new(direction, "tree", parent, source, true));
            await FinishAsync(task, policy == TransferConflictPolicy.Skip ? QueuedTransferState.Skipped : QueuedTransferState.Completed);
            Assert.Equal(policy == TransferConflictPolicy.Ask ? 1 : 0, prompts);
            string expected = policy == TransferConflictPolicy.AutoRename ? "tree (1)" : "tree";
            Assert.Equal(policy == TransferConflictPolicy.Skip ? "old" : "new", await File.ReadAllTextAsync(Path.Combine(parent, expected, "file")));
            if (policy != TransferConflictPolicy.AutoRename)
                Assert.Equal("retained", await File.ReadAllTextAsync(Path.Combine(parent, "tree", "extra")));
            else Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(parent, "tree", "file")));
            Assert.Empty(Directory.GetDirectories(parent, ".keiterm-archive*"));
        }
    }

    [SshdFact]
    public async Task OverwriteIfOlder_FallsBackToFiles_AndSessionOverridesWin()
    {
        string root = NewRoot();
        string source = Path.Combine(root, "source");
        string parent = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(parent, "tree"));
        await File.WriteAllTextAsync(Path.Combine(source, "old"), "source");
        await File.WriteAllTextAsync(Path.Combine(parent, "tree", "old"), "newer");
        File.SetLastWriteTimeUtc(Path.Combine(source, "old"), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(parent, "tree", "old"), DateTime.UtcNow);
        FileTransferSettings settings = Settings();
        settings.DefaultConflictPolicy = TransferConflictPolicy.OverwriteIfOlder;
        await using IRemoteTransferChannel channel = await new SshSessionFactory().CreateTransferChannelAsync(sshd.Config(), sshd.KeyAuth());
        await using ChannelTransferEndpoint endpoint = new(channel);
        await using TransferQueue queue = new(endpoint, () => settings, capabilities: new GzipCapabilities(), verifyHashOverride: () => false);
        QueuedTransfer task = queue.Enqueue(new(TransferDirection.Upload, "tree", source, parent, true));
        await FinishAsync(task, QueuedTransferState.Skipped);
        Assert.Equal("newer", await File.ReadAllTextAsync(Path.Combine(parent, "tree", "old")));
        Assert.Equal(6, task.TotalBytes);

        settings.ArchiveDirectoriesEnabled = false;
        settings.DefaultConflictPolicy = TransferConflictPolicy.Overwrite;
        await using TransferQueue overridden = new(endpoint, () => settings, capabilities: new GzipCapabilities(),
            archiveDirectoriesOverride: () => true, verifyHashOverride: () => false);
        QueuedTransfer archived = overridden.Enqueue(new(TransferDirection.Upload, "overridden", source, parent, true));
        await FinishAsync(archived);
        Assert.True(archived.TotalBytes > 6);
        Assert.False(archived.IsUnverified);
    }

    private string NewRoot()
    {
        string root = Path.Combine(sshd.Directory, "archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static FileTransferSettings Settings() => new()
    {
        ArchiveDirectoriesEnabled = true,
        VerifyHashEnabled = true,
        DefaultConflictPolicy = TransferConflictPolicy.Overwrite,
        AutoRetryEnabled = false,
    };

    private static TransferQueue Queue(ITransferEndpoint endpoint, FileTransferSettings settings)
        => new(endpoint, () => settings, capabilities: new TransferCapabilityProvider(null, null, () => settings, endpoint.AcquireShellAsync));

    private static async Task FinishAsync(QueuedTransfer task, QueuedTransferState expected = QueuedTransferState.Completed)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        while (task.IsActive) await Task.Delay(20, timeout.Token);
        Assert.True(task.State == expected, $"{task.State}: {task.ErrorMessage}");
    }

    private sealed class GzipCapabilities : ITransferCapabilityProvider
    {
        public Task<TransferProbeResult> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TransferProbeResult(RemotePlatform.Linux, [TransferHashAlgorithm.Sha256],
                [TransferArchiveFormat.Gzip, TransferArchiveFormat.Tar], DateTime.UtcNow));
    }
}
