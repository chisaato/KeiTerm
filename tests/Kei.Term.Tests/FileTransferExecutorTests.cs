using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Services;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Kei.Term.Tests;

public sealed class FileTransferExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keiterm-xfer-" + Guid.NewGuid().ToString("N"));
    private readonly FileTransferExecutor _executor = new();

    public FileTransferExecutorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Success_RenamesPartialAndSetsModifiedTime(bool upload)
    {
        DateTimeOffset modified = new(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        byte[] content = "payload"u8.ToArray();
        MemoryRemote remote = new();
        string source = await PrepareSourceAsync(upload, remote, "src.bin", content, modified);
        string destinationDirectory = upload ? "/out" : Path.Combine(_root, "down");

        await TransferAsync(upload, remote, source, destinationDirectory, "final.bin", content.Length, modified, resume: false);

        Assert.False(Exists(upload, remote, destinationDirectory, "final.bin.keipart"));
        Assert.Equal(content, await ReadAsync(upload, remote, destinationDirectory, "final.bin"));
        Assert.Equal(modified, Modified(upload, remote, destinationDirectory, "final.bin"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task CancelOrFailure_CleansPartialAccordingToResume(bool resume, bool cancel)
    {
        MemoryRemote remote = new()
        {
            WriteFault = cancel ? new OperationCanceledException() : new IOException("写入失败"),
            ReadFault = cancel ? new OperationCanceledException() : new IOException("读取失败"),
        };
        byte[] content = "abcdefghij"u8.ToArray();
        string source = await PrepareSourceAsync(upload: true, remote, "src.bin", content, DateTimeOffset.UnixEpoch);
        string downSource = "/remote-src.bin";
        remote.Put(downSource, content, DateTimeOffset.UnixEpoch);
        string destinationDirectory = Path.Combine(_root, "local-out");
        FileTransferSettings settings = Settings(resume);

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _executor.UploadAsync(
                Request(source, "/out", "a.bin", settings, content.Length, DateTimeOffset.UnixEpoch), remote));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _executor.DownloadAsync(
                Request(downSource, destinationDirectory, "b.bin", settings, content.Length, DateTimeOffset.UnixEpoch), remote));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => _executor.UploadAsync(
                Request(source, "/out", "a.bin", settings, content.Length, DateTimeOffset.UnixEpoch), remote));
            await Assert.ThrowsAsync<IOException>(() => _executor.DownloadAsync(
                Request(downSource, destinationDirectory, "b.bin", settings, content.Length, DateTimeOffset.UnixEpoch), remote));
        }

        Assert.Equal(resume, remote.Exists("/out/a.bin.keipart"));
        Assert.Equal(resume, File.Exists(Path.Combine(destinationDirectory, "b.bin.keipart")));
        Assert.False(remote.Exists("/out/a.bin"));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, "b.bin")));
    }

    [Fact]
    public async Task Resume_AppendsFromPartialLength_WhenSourceUnchanged()
    {
        MemoryRemote remote = new();
        byte[] content = "abcdefghij"u8.ToArray();
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, content);
        remote.Put("/out/final.bin.keipart", "abcde"u8.ToArray(), DateTimeOffset.UnixEpoch);
        DateTimeOffset modified = new(File.GetLastWriteTimeUtc(source));
        List<long> progress = [];
        TransferFileFacts live = new(content.Length, modified);

        await _executor.UploadAsync(new FileTransferRequest(
            source,
            "/out",
            "final.bin",
            Settings(resume: true),
            live,
            live,
            new SyncProgress(progress)), remote);

        Assert.Equal(content, remote.Read("/out/final.bin"));
        Assert.False(remote.Exists("/out/final.bin.keipart"));
        Assert.Equal(5, progress[0]);
        Assert.Equal(content.Length, progress[^1]);
    }

    [Fact]
    public async Task SourceChange_StartsOverEvenWhenResumeEnabled()
    {
        MemoryRemote remote = new();
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "NEW"u8.ToArray());
        remote.Put("/out/final.bin.keipart", "old-partial"u8.ToArray(), DateTimeOffset.UnixEpoch);
        TransferFileFacts current = new(3, DateTimeOffset.UnixEpoch.AddMinutes(5));
        TransferFileFacts previous = new(11, DateTimeOffset.UnixEpoch);

        await _executor.UploadAsync(new FileTransferRequest(
            source,
            "/out",
            "final.bin",
            Settings(resume: true),
            current,
            previous), remote);

        Assert.Equal("NEW"u8.ToArray(), remote.Read("/out/final.bin"));
    }

    [Fact]
    public async Task LengthMismatch_ThrowsTransient_AndDeletesPartialWhenResumeOff()
    {
        MemoryRemote remote = new();
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "hello"u8.ToArray());
        remote.TruncateWrites = true;

        TransferLengthMismatchException error = await Assert.ThrowsAsync<TransferLengthMismatchException>(() =>
            _executor.UploadAsync(Request(source, "/out", "final.bin", Settings(resume: false), sourceSize: 100, DateTimeOffset.UnixEpoch), remote));

        Assert.Equal(5, error.Expected);
        Assert.True(error.Actual < error.Expected);
        Assert.False(remote.Exists("/out/final.bin.keipart"));
        Assert.Equal(TransferFailureKind.Transient, DotNetTransferFailureClassifier.Instance.Classify(error));
        Assert.Equal(TransferFailureKind.Transient, SshTransferFailureClassifier.Instance.Classify(error));
    }

    [Fact]
    public async Task UntrustedPartial_DoesNotSpliceDifferentSourceEvenWhenShorter()
    {
        MemoryRemote remote = new();
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "BBBB"u8.ToArray());
        remote.Put("/out/final.bin.keipart", "AA"u8.ToArray(), DateTimeOffset.UnixEpoch);

        await _executor.UploadAsync(Request(source, "/out", "final.bin", Settings(resume: true), 4, DateTimeOffset.UnixEpoch), remote);

        Assert.Equal("BBBB"u8.ToArray(), remote.Read("/out/final.bin"));
    }

    [Fact]
    public async Task SameSizeDifferentMtime_RestartsInsteadOfTrustingLength()
    {
        MemoryRemote remote = new();
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "BBBB"u8.ToArray());
        DateTimeOffset liveModified = new(File.GetLastWriteTimeUtc(source));
        remote.Put("/out/final.bin.keipart", "AX"u8.ToArray(), DateTimeOffset.UnixEpoch);
        TransferFileFacts stale = new(4, liveModified.AddMinutes(-5));

        await _executor.UploadAsync(new FileTransferRequest(
            source, "/out", "final.bin", Settings(resume: true), stale, stale), remote);

        Assert.Equal("BBBB"u8.ToArray(), remote.Read("/out/final.bin"));
    }

    [Fact]
    public async Task RetryWithOriginalSnapshot_RestartsWhenLiveSourceChanged()
    {
        MemoryRemote remote = new();
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "AAAAAA"u8.ToArray());
        DateTimeOffset originalModified = new(2017, 1, 1, 0, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(source, originalModified.UtcDateTime);
        TransferFileFacts original = new(6, originalModified);
        remote.Put("/out/final.bin.keipart", "AAA"u8.ToArray(), DateTimeOffset.UnixEpoch);
        await File.WriteAllBytesAsync(source, "ZZ"u8.ToArray());
        File.SetLastWriteTimeUtc(source, originalModified.AddHours(1).UtcDateTime);

        await new FileTransferExecutor().UploadAsync(new FileTransferRequest(
            source, "/out", "final.bin", Settings(resume: true), original, original), remote);

        Assert.Equal("ZZ"u8.ToArray(), remote.Read("/out/final.bin"));
    }

    [Fact]
    public async Task PartialWriteStarted_ReportsLiveFactsOnlyAfterWriteOpens()
    {
        MemoryRemote remote = new() { WriteFault = new TimeoutException("reset") };
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "BBBBBB"u8.ToArray());
        File.SetLastWriteTimeUtc(source, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        DateTimeOffset liveModified = new(DateTime.SpecifyKind(File.GetLastWriteTimeUtc(source), DateTimeKind.Utc));
        TransferFileFacts stale = new(6, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        PartialWriteStarted? started = null;

        await Assert.ThrowsAsync<TimeoutException>(() => _executor.UploadAsync(new FileTransferRequest(
            source,
            "/out",
            "final.bin",
            Settings(resume: true),
            stale,
            null,
            null,
            value => started = value), remote));

        Assert.Equal(new TransferFileFacts(6, liveModified), started!.Value.Source);
        Assert.Equal(0, started.Value.StartOffset);
    }

    [Fact]
    public async Task PartialWriteStarted_IsNotRaisedWhenPathCheckFailsBeforeReset()
    {
        MemoryRemote remote = new() { AttributeFault = new TimeoutException("parent") };
        remote.Put("/out/final.bin.keipart", "AA"u8.ToArray(), DateTimeOffset.UnixEpoch);
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "AAAAAA"u8.ToArray());
        int calls = 0;
        TransferFileFacts facts = new(6, DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<TimeoutException>(() => _executor.UploadAsync(new FileTransferRequest(
            source,
            "/out",
            "final.bin",
            Settings(resume: true),
            facts,
            facts,
            null,
            _ => calls++), remote));

        Assert.Equal(0, calls);
        Assert.Equal("AA"u8.ToArray(), remote.Read("/out/final.bin.keipart"));
    }

    [Fact]
    public async Task PublishFailure_KeepsCompletePartialAndDoesNotRemoveTarget()
    {
        MemoryRemote remote = new() { ThrowOnRename = true };
        remote.Put("/out/final.bin", "old-target"u8.ToArray(), DateTimeOffset.UnixEpoch);
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "new-body"u8.ToArray());

        await Assert.ThrowsAsync<IOException>(() => _executor.UploadAsync(
            Request(source, "/out", "final.bin", Settings(resume: false), 8, DateTimeOffset.UnixEpoch), remote));

        Assert.Equal("old-target"u8.ToArray(), remote.Read("/out/final.bin"));
        Assert.Equal("new-body"u8.ToArray(), remote.Read("/out/final.bin.keipart"));
    }

    [Fact]
    public async Task MtimeFailure_PropagatesAndKeepsRenamedFile()
    {
        MemoryRemote remote = new() { ThrowOnSetMtime = true };
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "body"u8.ToArray());

        await Assert.ThrowsAsync<IOException>(() => _executor.UploadAsync(
            Request(source, "/out", "final.bin", Settings(resume: false), 4, DateTimeOffset.UnixEpoch), remote));

        Assert.Equal("body"u8.ToArray(), remote.Read("/out/final.bin"));
        Assert.False(remote.Exists("/out/final.bin.keipart"));
    }

    [Fact]
    public async Task LocalPartialSymlink_IsRejectedAndVictimStays()
    {
        string destination = Path.Combine(_root, "down");
        Directory.CreateDirectory(destination);
        string victim = Path.Combine(_root, "victim.txt");
        await File.WriteAllTextAsync(victim, "secret");
        File.CreateSymbolicLink(Path.Combine(destination, "final.bin.keipart"), victim);
        MemoryRemote remote = new();
        remote.Put("/src.bin", "new"u8.ToArray(), DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<TransferPathRejectedException>(() => _executor.DownloadAsync(
            Request("/src.bin", destination, "final.bin", Settings(resume: false), 3, DateTimeOffset.UnixEpoch), remote));

        Assert.Equal("secret", await File.ReadAllTextAsync(victim));
        Assert.NotNull(new FileInfo(Path.Combine(destination, "final.bin.keipart")).LinkTarget);
    }

    [Fact]
    public async Task LocalDestinationSymlink_IsRejected()
    {
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string victim = Path.Combine(outside, "victim.txt");
        await File.WriteAllTextAsync(victim, "keep");
        string link = Path.Combine(_root, "linkdest");
        Directory.CreateSymbolicLink(link, outside);
        MemoryRemote remote = new();
        remote.Put("/src.bin", "new"u8.ToArray(), DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<TransferPathRejectedException>(() => _executor.DownloadAsync(
            Request("/src.bin", link, "final.bin", Settings(resume: false), 3, DateTimeOffset.UnixEpoch), remote));

        Assert.Equal("keep", await File.ReadAllTextAsync(victim));
        Assert.False(File.Exists(Path.Combine(outside, "final.bin.keipart")));
    }

    [Fact]
    public async Task RemotePartialSymlink_IsRejectedWithoutWriting()
    {
        MemoryRemote remote = new();
        remote.Put("/victim.txt", "secret"u8.ToArray(), DateTimeOffset.UnixEpoch);
        remote.AddLink("/out/final.bin.keipart", directory: false, size: 6, modified: DateTimeOffset.UnixEpoch);
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "new"u8.ToArray());

        await Assert.ThrowsAsync<TransferPathRejectedException>(() => _executor.UploadAsync(
            Request(source, "/out", "final.bin", Settings(resume: true), 3, DateTimeOffset.UnixEpoch), remote));

        Assert.Equal("secret"u8.ToArray(), remote.Read("/victim.txt"));
        Assert.True(remote.IsLink("/out/final.bin.keipart"));
    }

    [Fact]
    public async Task ProtocolWithoutResume_DeletesPartialAndCopiesFromStart()
    {
        MemoryRemote remote = new() { SupportsResume = false };
        string source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "abcdef"u8.ToArray());
        remote.Put("/out/final.bin.keipart", "abc"u8.ToArray(), DateTimeOffset.UnixEpoch);

        await _executor.UploadAsync(Request(
            source, "/out", "final.bin", Settings(resume: true), 6, DateTimeOffset.UnixEpoch), remote);

        Assert.Equal("abcdef"u8.ToArray(), remote.Read("/out/final.bin"));
    }

    [Fact]
    public async Task LocalTree_IncludesEmptyDirectoryAndClassifiesSymlinks()
    {
        string root = Path.Combine(_root, "tree");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "nested.txt"), "nested");
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "file");
        File.CreateSymbolicLink(Path.Combine(root, "link-file"), "file.txt");
        File.CreateSymbolicLink(Path.Combine(root, "link-dir"), "sub");
        File.CreateSymbolicLink(Path.Combine(root, "link-broken"), "missing-target");

        IReadOnlyList<SourceTreeEntry> entries = await SourceTreeReader.ReadLocalAsync(root);
        IReadOnlyList<TransferPlanOp> plan = DirectoryTransferPlanner.Plan(entries);

        Assert.Contains(entries, entry => entry.RelativePath == "empty" && entry.Kind == SourceEntryKind.Directory);
        Assert.Contains(entries, entry => entry.RelativePath == "link-file" && entry.Kind == SourceEntryKind.SymlinkToFile && entry.Size == 4);
        Assert.Contains(entries, entry => entry.RelativePath == "link-dir" && entry.Kind == SourceEntryKind.SymlinkToDirectory);
        Assert.Contains(entries, entry => entry.RelativePath == "link-broken" && entry.Kind == SourceEntryKind.BrokenSymlink);
        Assert.DoesNotContain(entries, entry => entry.RelativePath.StartsWith("link-dir/", StringComparison.Ordinal));
        Assert.Contains(plan, op => op is TransferPlanOp.CreateDirectory { RelativePath: "empty" });
        Assert.Contains(plan, op => op is TransferPlanOp.Skip { RelativePath: "link-dir", Reason: DirectoryTransferPlanner.DirectorySymlinkSkipReason });
        Assert.Contains(plan, op => op is TransferPlanOp.Skip { RelativePath: "link-broken", Reason: DirectoryTransferPlanner.BrokenSymlinkSkipReason });
        Assert.Contains(plan, op => op is TransferPlanOp.TransferFile { RelativePath: "link-file", FromSymlink: true, Size: 4 });
    }

    [Fact]
    public async Task RemoteTree_UsesLinkTargetFactsAndDoesNotEnterDirectoryLinks()
    {
        MemoryRemote remote = new();
        remote.Put("/tree/file.txt", "file"u8.ToArray(), DateTimeOffset.UnixEpoch, directory: false);
        remote.AddDirectory("/tree/sub");
        remote.Put("/tree/sub/nested.txt", "nested"u8.ToArray(), DateTimeOffset.UnixEpoch);
        remote.AddLink("/tree/link-file", directory: false, size: 4, modified: DateTimeOffset.UnixEpoch);
        remote.AddLink("/tree/link-dir", directory: true, size: 0, modified: DateTimeOffset.UnixEpoch);
        remote.AddLink("/tree/link-broken", directory: false, size: 0, modified: DateTimeOffset.UnixEpoch, broken: true);
        remote.Put("/tree/link-dir/should-not-appear.txt", "x"u8.ToArray(), DateTimeOffset.UnixEpoch);

        IReadOnlyList<SourceTreeEntry> entries = await SourceTreeReader.ReadRemoteAsync(remote, "/tree");

        SourceTreeEntry link = Assert.Single(entries, entry => entry.RelativePath == "link-file");
        Assert.Equal(SourceEntryKind.SymlinkToFile, link.Kind);
        Assert.Equal(4, link.Size);
        Assert.Contains(entries, entry => entry.RelativePath == "link-dir" && entry.Kind == SourceEntryKind.SymlinkToDirectory);
        Assert.Contains(entries, entry => entry.RelativePath == "link-broken" && entry.Kind == SourceEntryKind.BrokenSymlink);
        Assert.DoesNotContain(entries, entry => entry.RelativePath == "link-dir/should-not-appear.txt");
        Assert.Contains(entries, entry => entry.RelativePath == "sub/nested.txt" && entry.Kind == SourceEntryKind.File);
        IReadOnlyList<TransferPlanOp> plan = DirectoryTransferPlanner.Plan(entries);
        Assert.Contains(plan, op => op is TransferPlanOp.Skip { RelativePath: "link-broken", Reason: DirectoryTransferPlanner.BrokenSymlinkSkipReason });
    }

    [Fact]
    public void ScpLargeFileWarning_IsExclusiveOf512MiB()
    {
        Assert.False(ScpTransferLimits.NeedsLargeFileWarning(FileTransferProtocol.Scp, ScpTransferLimits.LargeFileBytes));
        Assert.True(ScpTransferLimits.NeedsLargeFileWarning(FileTransferProtocol.Scp, ScpTransferLimits.LargeFileBytes + 1));
        Assert.False(ScpTransferLimits.NeedsLargeFileWarning(FileTransferProtocol.Sftp, ScpTransferLimits.LargeFileBytes + 1));
        Assert.Equal(512L * 1024 * 1024, ScpTransferLimits.LargeFileBytes);
    }

    [Fact]
    public void MissingPreviousSource_IsNotModified()
    {
        TransferFileFacts current = new(1, DateTimeOffset.UnixEpoch);
        Assert.True(FileTransferExecutor.IsSourceModified(current, null));
        Assert.True(FileTransferExecutor.IsSourceModified(current, current with { Size = current.Size + 1 }));
        Assert.True(FileTransferExecutor.IsSourceModified(current, current with { ModifiedUtc = current.ModifiedUtc.AddSeconds(1) }));
        Assert.False(FileTransferExecutor.IsSourceModified(current, current));
    }

    [Fact]
    public void SshFailureClassifier_MapsSshNetExceptions()
    {
        SshTransferFailureClassifier classifier = SshTransferFailureClassifier.Instance;

        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new SshConnectionException("Host key could not be verified.")));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new SshOperationTimeoutException("timeout")));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new ProxyException("proxy failed")));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new SftpException(StatusCode.ConnectionLost, "lost")));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new SftpException(StatusCode.NoConnection, "none")));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new SshException("Connection was closed by the server.")));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new SshException("Secure copy execution request was rejected by the server.")));
        Assert.Equal(TransferFailureKind.Transient, classifier.Classify(new IOException("传输连接已关闭")));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(new SftpPermissionDeniedException("denied")));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(new SftpPathNotFoundException("missing")));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(new SshAuthenticationException("auth")));
        Assert.Equal(TransferFailureKind.Unknown, classifier.Classify(new SftpException(StatusCode.Failure, "generic")));
        Assert.Equal(TransferFailureKind.Unknown, classifier.Classify(new SftpException(StatusCode.BadMessage, "bad")));
        Assert.Equal(TransferFailureKind.Unknown, classifier.Classify(new SshException("Invalid private key file.")));
        Assert.Equal(TransferFailureKind.Permanent, classifier.Classify(new UnauthorizedAccessException()));
        Assert.Throws<ArgumentException>(() => classifier.Classify(new OperationCanceledException()));
        Assert.Throws<ArgumentNullException>(() => classifier.Classify(null!));
    }

    private async Task TransferAsync(
        bool upload,
        MemoryRemote remote,
        string source,
        string destinationDirectory,
        string name,
        long size,
        DateTimeOffset modified,
        bool resume)
    {
        FileTransferRequest request = Request(source, destinationDirectory, name, Settings(resume), size, modified);
        if (upload)
        {
            await _executor.UploadAsync(request, remote);
        }
        else
        {
            await _executor.DownloadAsync(request, remote);
        }
    }

    private async Task<string> PrepareSourceAsync(bool upload, MemoryRemote remote, string name, byte[] content, DateTimeOffset modified)
    {
        if (upload)
        {
            string path = Path.Combine(_root, name);
            await File.WriteAllBytesAsync(path, content);
            File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
            return path;
        }

        string remotePath = "/" + name;
        remote.Put(remotePath, content, modified);
        return remotePath;
    }

    private static bool Exists(bool upload, MemoryRemote remote, string directory, string name)
    {
        if (upload)
        {
            return remote.Exists(directory.TrimEnd('/') + "/" + name);
        }

        return File.Exists(Path.Combine(directory, name));
    }

    private async Task<byte[]> ReadAsync(bool upload, MemoryRemote remote, string directory, string name)
    {
        if (upload)
        {
            return remote.Read(directory.TrimEnd('/') + "/" + name);
        }

        return await File.ReadAllBytesAsync(Path.Combine(directory, name));
    }

    private static DateTimeOffset Modified(bool upload, MemoryRemote remote, string directory, string name)
    {
        if (upload)
        {
            return remote.Modified(directory.TrimEnd('/') + "/" + name);
        }

        DateTime utc = File.GetLastWriteTimeUtc(Path.Combine(directory, name));
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static FileTransferSettings Settings(bool resume) => new() { ResumeEnabled = resume };

    private static FileTransferRequest Request(
        string source,
        string destinationDirectory,
        string name,
        FileTransferSettings settings,
        long sourceSize,
        DateTimeOffset modified,
        IProgress<long>? progress = null)
        => new(source, destinationDirectory, name, settings, new TransferFileFacts(sourceSize, modified), Progress: progress);

    private sealed class SyncProgress(List<long> values) : IProgress<long>
    {
        public void Report(long value) => values.Add(value);
    }

    private sealed class MemoryRemote : IRemoteFileSystem
    {
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

        public bool SupportsResume { get; set; } = true;
        public bool TruncateWrites { get; set; }
        public bool ThrowOnRename { get; set; }
        public bool ThrowOnSetMtime { get; set; }
        public Exception? WriteFault { get; set; }
        public Exception? ReadFault { get; set; }
        public Exception? AttributeFault { get; set; }
        public bool IsConnected => true;
        public string WorkingDirectory => "/";

        public void Put(string path, byte[] data, DateTimeOffset modified, bool directory = false)
        {
            _nodes[path] = new Node { Data = data.ToArray(), Modified = modified, IsDirectory = directory };
        }

        public void AddDirectory(string path) => Put(path, [], DateTimeOffset.UnixEpoch, directory: true);

        public void AddLink(string path, bool directory, long size, DateTimeOffset modified, bool broken = false)
        {
            _nodes[path] = new Node
            {
                Data = new byte[size],
                Modified = modified,
                IsSymbolicLink = true,
                IsBrokenSymbolicLink = broken,
                LinkTargetIsDirectory = broken ? null : directory,
                LinkTargetSize = directory || broken ? null : size,
                LinkTargetModified = directory || broken ? null : modified,
            };
        }

        public bool Exists(string path) => _nodes.ContainsKey(path);

        public bool IsLink(string path) => _nodes.TryGetValue(path, out Node? node) && node.IsSymbolicLink;

        public byte[] Read(string path) => _nodes[path].Data.ToArray();

        public DateTimeOffset Modified(string path) => _nodes[path].Modified;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            string prefix = path.TrimEnd('/') + "/";
            List<RemoteFileItem> items = [];
            foreach ((string full, Node node) in _nodes)
            {
                if (!full.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string rest = full[prefix.Length..];
                if (rest.Length == 0 || rest.Contains('/'))
                {
                    continue;
                }

                items.Add(ToItem(rest, full, node));
            }

            return Task.FromResult<IReadOnlyList<RemoteFileItem>>(items);
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
            => OpenReadAsync(path, 0, ct);

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default)
        {
            if (ReadFault is not null)
            {
                return Task.FromResult<Stream>(new FaultStream(ReadFault));
            }

            Node node = _nodes[path];
            MemoryStream stream = new(node.Data.ToArray(), writable: false);
            stream.Position = offset;
            return Task.FromResult<Stream>(stream);
        }

        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
            => OpenWriteAsync(path, 0, ct);

        public Task<Stream> OpenWriteAsync(string path, long offset, CancellationToken ct = default)
        {
            if (!_nodes.TryGetValue(path, out Node? node))
            {
                node = new Node();
                _nodes[path] = node;
            }

            if (offset == 0)
            {
                node.Data = [];
            }

            return Task.FromResult<Stream>(new WriteStream(node, offset, WriteFault, TruncateWrites));
        }

        public Task CommitWriteAsync(Stream stream, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            if (!_nodes.Remove(path))
            {
                throw new FileNotFoundException(path);
            }

            return Task.CompletedTask;
        }

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
            => RenameOverwriteAsync(oldPath, newPath, ct);

        public Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            if (ThrowOnRename)
            {
                throw new IOException("改名失败");
            }

            Node node = _nodes[oldPath];
            _nodes.Remove(oldPath);
            _nodes[newPath] = node;
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            AddDirectory(path);
            return Task.CompletedTask;
        }

        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RemoteFileItem?> GetAttributesAsync(string path, CancellationToken ct = default)
        {
            if (AttributeFault is not null && path == "/out")
            {
                throw AttributeFault;
            }

            return GetItemAsync(path, ct);
        }

        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
        {
            if (!_nodes.TryGetValue(path, out Node? node))
            {
                return Task.FromResult<RemoteFileItem?>(null);
            }

            return Task.FromResult<RemoteFileItem?>(ToItem(Path.GetFileName(path), path, node));
        }

        public Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct = default)
        {
            if (ThrowOnSetMtime)
            {
                throw new IOException("设置修改时间失败");
            }

            _nodes[path].Modified = new DateTimeOffset(DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static RemoteFileItem ToItem(string name, string full, Node node)
            => new(
                name,
                full,
                node.IsDirectory,
                node.Data.Length,
                node.Modified,
                node.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--",
                IsSymbolicLink: node.IsSymbolicLink,
                LinkTargetIsDirectory: node.LinkTargetIsDirectory,
                LinkTargetSize: node.LinkTargetSize,
                LinkTargetModified: node.LinkTargetModified,
                IsBrokenSymbolicLink: node.IsBrokenSymbolicLink);

        private sealed class Node
        {
            public byte[] Data { get; set; } = [];
            public DateTimeOffset Modified { get; set; }
            public bool IsDirectory { get; set; }
            public bool IsSymbolicLink { get; set; }
            public bool IsBrokenSymbolicLink { get; set; }
            public bool? LinkTargetIsDirectory { get; set; }
            public long? LinkTargetSize { get; set; }
            public DateTimeOffset? LinkTargetModified { get; set; }
        }

        private sealed class FaultStream(Exception fault) : MemoryStream
        {
            public override int Read(byte[] buffer, int offset, int count) => throw fault;
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => throw fault;
        }

        private sealed class WriteStream(Node node, long offset, Exception? fault, bool truncate) : Stream
        {
            private long _position = offset;

            public override bool CanRead => false;
            public override bool CanSeek => true;
            public override bool CanWrite => true;
            public override long Length => node.Data.Length;
            public override long Position { get => _position; set => _position = value; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => _position = offset;
            public override void SetLength(long value) { }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (fault is not null)
                {
                    throw fault;
                }

                if (truncate)
                {
                    count = 1;
                }

                byte[] data = node.Data;
                int end = checked((int)(_position + count));
                if (data.Length < end)
                {
                    Array.Resize(ref data, end);
                }

                Buffer.BlockCopy(buffer, offset, data, (int)_position, count);
                node.Data = data;
                _position += count;
            }
        }
    }
}
