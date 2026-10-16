using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class FileTransferExecutorSshIntegrationTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    [SshdFact]
    public async Task DirectoryRoundTrip_PreservesContentAndModifiedTime_AndClassifiesSymlinks()
    {
        string local = Path.Combine(sshd.Directory, "round-src-" + Guid.NewGuid().ToString("N"));
        string remote = Path.Combine(sshd.Directory, "round-remote-" + Guid.NewGuid().ToString("N"));
        string downloaded = Path.Combine(sshd.Directory, "round-dst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(remote);
        DateTime modified = new(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await File.WriteAllTextAsync(Path.Combine(local, "file.txt"), "file-body");
        File.SetLastWriteTimeUtc(Path.Combine(local, "file.txt"), modified);
        Directory.CreateDirectory(Path.Combine(local, "empty"));
        Directory.SetLastWriteTimeUtc(Path.Combine(local, "empty"), modified);
        Directory.CreateDirectory(Path.Combine(local, "sub"));
        await File.WriteAllTextAsync(Path.Combine(local, "sub", "nested.txt"), "nested-body");
        File.SetLastWriteTimeUtc(Path.Combine(local, "sub", "nested.txt"), modified);
        File.CreateSymbolicLink(Path.Combine(local, "link-file"), "file.txt");
        File.CreateSymbolicLink(Path.Combine(local, "link-dir"), "sub");

        await using IRemoteFileSystem remoteFs = await new SshSessionFactory()
            .CreateFileSystemAsync(sshd.Config(), sshd.KeyAuth());
        await remoteFs.ConnectAsync();

        IReadOnlyList<SourceTreeEntry> uploaded = await SourceTreeReader.ReadLocalAsync(local);
        await ApplyUploadAsync(remoteFs, remote, local, DirectoryTransferPlanner.Plan(uploaded));

        IReadOnlyList<SourceTreeEntry> remoteEntries = await SourceTreeReader.ReadRemoteAsync(remoteFs, remote);
        Assert.Contains(remoteEntries, entry => entry.RelativePath == "empty" && entry.Kind == SourceEntryKind.Directory);
        Assert.Contains(remoteEntries, entry => entry.RelativePath == "link-file" && entry.Kind == SourceEntryKind.File);
        Assert.DoesNotContain(remoteEntries, entry => entry.RelativePath == "link-dir");
        Assert.Contains(remoteEntries, entry => entry.RelativePath == "sub/nested.txt");

        await ApplyDownloadAsync(remoteFs, remote, downloaded, DirectoryTransferPlanner.Plan(remoteEntries));

        Assert.Equal("file-body", await File.ReadAllTextAsync(Path.Combine(downloaded, "file.txt")));
        Assert.Equal("nested-body", await File.ReadAllTextAsync(Path.Combine(downloaded, "sub", "nested.txt")));
        Assert.Equal("file-body", await File.ReadAllTextAsync(Path.Combine(downloaded, "link-file")));
        Assert.True(Directory.Exists(Path.Combine(downloaded, "empty")));
        Assert.False(Directory.Exists(Path.Combine(downloaded, "link-dir")));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(downloaded, "file.txt")));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(downloaded, "sub", "nested.txt")));
        Assert.Equal(modified, Directory.GetLastWriteTimeUtc(Path.Combine(downloaded, "empty")));
    }

    [SshdFact]
    public async Task RemoteSymlinkToDirectory_IsNotEntered()
    {
        string root = Path.Combine(sshd.Directory, "links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "nested.txt"), "inside");
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "body");
        File.CreateSymbolicLink(Path.Combine(root, "link-file"), "file.txt");
        File.CreateSymbolicLink(Path.Combine(root, "link-dir"), "sub");
        File.CreateSymbolicLink(Path.Combine(root, "link-broken"), "no-such-file");

        await using IRemoteFileSystem remoteFs = await new SshSessionFactory()
            .CreateFileSystemAsync(sshd.Config(), sshd.KeyAuth());
        await remoteFs.ConnectAsync();
        IReadOnlyList<SourceTreeEntry> entries = await SourceTreeReader.ReadRemoteAsync(remoteFs, root);

        SourceTreeEntry fileLink = Assert.Single(entries, entry => entry.RelativePath == "link-file");
        Assert.Equal(SourceEntryKind.SymlinkToFile, fileLink.Kind);
        Assert.Equal(4, fileLink.Size);
        Assert.Contains(entries, entry => entry.RelativePath == "link-dir" && entry.Kind == SourceEntryKind.SymlinkToDirectory);
        Assert.Contains(entries, entry => entry.RelativePath == "link-broken" && entry.Kind == SourceEntryKind.BrokenSymlink);
        Assert.DoesNotContain(entries, entry => entry.RelativePath.StartsWith("link-dir/", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.RelativePath == "sub/nested.txt");
        IReadOnlyList<TransferPlanOp> plan = DirectoryTransferPlanner.Plan(entries);
        Assert.Contains(plan, op => op is TransferPlanOp.Skip { RelativePath: "link-broken", Reason: DirectoryTransferPlanner.BrokenSymlinkSkipReason });
        Assert.Contains(plan, op => op is TransferPlanOp.Skip { RelativePath: "link-dir", Reason: DirectoryTransferPlanner.DirectorySymlinkSkipReason });
        Assert.Contains(plan, op => op is TransferPlanOp.TransferFile { RelativePath: "link-file", FromSymlink: true, Size: 4 });
    }

    [SshdFact]
    public async Task InterruptedUpload_ResumesFromPartialAndMatchesSource()
    {
        string source = Path.Combine(sshd.Directory, "resume-src-" + Guid.NewGuid().ToString("N") + ".bin");
        string remoteDir = Path.Combine(sshd.Directory, "resume-dst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remoteDir);
        byte[] content = new byte[200 * 1024];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(source, content);
        DateTime modified = new(2018, 4, 3, 2, 1, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);
        TransferFileFacts facts = new(content.Length, new DateTimeOffset(modified));
        FileTransferSettings settings = new() { ResumeEnabled = true };
        await using IRemoteFileSystem remoteFs = await new SshSessionFactory()
            .CreateFileSystemAsync(sshd.Config(), sshd.KeyAuth());
        await remoteFs.ConnectAsync();
        FileTransferExecutor executor = new();

        using CancellationTokenSource cancel = new();
        SyncProgress progress = new(value =>
        {
            if (value >= FileTransferExecutor.BufferSize)
            {
                cancel.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.UploadAsync(
            new FileTransferRequest(source, remoteDir, "payload.bin", settings, facts, facts, progress),
            remoteFs,
            cancel.Token));

        string partial = Path.Combine(remoteDir, "payload.bin.keipart");
        Assert.True(File.Exists(partial));
        long partialLength = new FileInfo(partial).Length;
        Assert.InRange(partialLength, 1, content.Length - 1);

        await executor.UploadAsync(
            new FileTransferRequest(source, remoteDir, "payload.bin", settings, facts, facts),
            remoteFs);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(remoteDir, "payload.bin")));
        Assert.False(File.Exists(partial));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(remoteDir, "payload.bin")));
    }

    [SshdFact]
    public async Task RemotePartialSymlink_DoesNotOverwriteVictim()
    {
        string directory = Path.Combine(sshd.Directory, "partial-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string victim = Path.Combine(directory, "victim.txt");
        await File.WriteAllTextAsync(victim, "secret");
        File.CreateSymbolicLink(Path.Combine(directory, "final.bin.keipart"), "victim.txt");
        string source = Path.Combine(directory, "src.bin");
        await File.WriteAllBytesAsync(source, "new"u8.ToArray());
        await using IRemoteFileSystem remote = await new SshSessionFactory().CreateFileSystemAsync(sshd.Config(), sshd.KeyAuth());
        await remote.ConnectAsync();

        await Assert.ThrowsAsync<TransferPathRejectedException>(() => new FileTransferExecutor().UploadAsync(
            new FileTransferRequest(source, directory, "final.bin", new FileTransferSettings { ResumeEnabled = true }, new TransferFileFacts(3, DateTimeOffset.UtcNow)),
            remote));

        Assert.Equal("secret", await File.ReadAllTextAsync(victim));
        Assert.NotNull(new FileInfo(Path.Combine(directory, "final.bin.keipart")).LinkTarget);
    }

    [SshdFact]
    public async Task RemoteDestinationSymlink_IsRejected()
    {
        string outside = Path.Combine(sshd.Directory, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string victim = Path.Combine(outside, "victim.txt");
        await File.WriteAllTextAsync(victim, "keep");
        string link = Path.Combine(sshd.Directory, "linkdest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(link, outside);
        string source = Path.Combine(outside, "src.bin");
        await File.WriteAllBytesAsync(source, "new"u8.ToArray());
        await using IRemoteFileSystem remote = await new SshSessionFactory().CreateFileSystemAsync(sshd.Config(), sshd.KeyAuth());
        await remote.ConnectAsync();

        await Assert.ThrowsAsync<TransferPathRejectedException>(() => new FileTransferExecutor().UploadAsync(
            new FileTransferRequest(source, link, "final.bin", new FileTransferSettings(), new TransferFileFacts(3, DateTimeOffset.UtcNow)),
            remote));

        Assert.Equal("keep", await File.ReadAllTextAsync(victim));
        Assert.False(File.Exists(Path.Combine(outside, "final.bin.keipart")));
    }

    private static async Task ApplyUploadAsync(
        IRemoteFileSystem remote,
        string remoteRoot,
        string localRoot,
        IReadOnlyList<TransferPlanOp> ops)
    {
        FileTransferExecutor executor = new();
        FileTransferSettings settings = new();
        foreach (TransferPlanOp op in ops)
        {
            string remotePath = remoteRoot.TrimEnd('/') + "/" + op.RelativePath;
            if (op is TransferPlanOp.CreateDirectory directory)
            {
                await remote.CreateDirectoryAsync(remotePath);
                if (directory.ModifiedUtc is { } modified)
                {
                    await remote.SetLastWriteTimeUtcAsync(remotePath, modified.UtcDateTime);
                }

                continue;
            }

            if (op is not TransferPlanOp.TransferFile file)
            {
                continue;
            }

            string localPath = Path.Combine(localRoot, op.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            int slash = remotePath.LastIndexOf('/');
            await executor.UploadAsync(new FileTransferRequest(
                localPath,
                remotePath[..slash],
                remotePath[(slash + 1)..],
                settings,
                new TransferFileFacts(file.Size, file.ModifiedUtc)), remote);
        }
    }

    private static async Task ApplyDownloadAsync(
        IRemoteFileSystem remote,
        string remoteRoot,
        string localRoot,
        IReadOnlyList<TransferPlanOp> ops)
    {
        FileTransferExecutor executor = new();
        FileTransferSettings settings = new();
        foreach (TransferPlanOp op in ops)
        {
            string localPath = Path.Combine(localRoot, op.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (op is TransferPlanOp.CreateDirectory directory)
            {
                Directory.CreateDirectory(localPath);
                if (directory.ModifiedUtc is { } modified)
                {
                    Directory.SetLastWriteTimeUtc(localPath, modified.UtcDateTime);
                }

                continue;
            }

            if (op is not TransferPlanOp.TransferFile file)
            {
                continue;
            }

            string remotePath = remoteRoot.TrimEnd('/') + "/" + op.RelativePath;
            int slash = remotePath.LastIndexOf('/');
            await executor.DownloadAsync(new FileTransferRequest(
                remotePath,
                Path.GetDirectoryName(localPath)!,
                Path.GetFileName(localPath),
                settings,
                new TransferFileFacts(file.Size, file.ModifiedUtc)), remote);
        }
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
