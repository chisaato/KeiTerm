namespace Kei.Term.Core.Transfers;

using System.Text;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;

// 一次完整归档尝试。临时源/部分文件的生命周期不越过此次尝试；失败重试必须重新整包。
public sealed class DirectoryArchiveTransfer
{
    private readonly FileTransferExecutor _executor;
    private readonly Func<string> _createLocalTemporaryDirectory;

    public DirectoryArchiveTransfer(FileTransferExecutor executor)
        : this(executor, () => Directory.CreateTempSubdirectory("keiterm-archive-").FullName) { }

    internal DirectoryArchiveTransfer(FileTransferExecutor executor, Func<string> createLocalTemporaryDirectory)
    {
        _executor = executor;
        _createLocalTemporaryDirectory = createLocalTemporaryDirectory;
    }

    public async Task ExecuteAsync(
        TransferSubmission submission,
        string destination,
        bool rename,
        TransferArchiveFormat format,
        IRemoteFileSystem remote,
        IRemoteShell shell,
        IRemoteToolset tools,
        FileTransferSettings settings,
        ITransferHashVerifier? verifier,
        Action<QueuedTransferState> state,
        Action<long> total,
        IProgress<long>? progress = null,
        Func<CancellationToken, Task>? afterCopy = null,
        CancellationToken ct = default)
    {
        bool upload = submission.Direction == TransferDirection.Upload;
        string local = _createLocalTemporaryDirectory();
        string? remoteTemp = null;
        string? remoteStage = null;
        try
        {
            state(QueuedTransferState.Packing);
            remoteTemp = await TemporaryAsync(shell, tools, null, ct).ConfigureAwait(false);
            string localArchive = Path.Combine(local, "archive");
            string remoteArchive = remoteTemp + "/archive";
            TransferFileFacts source;
            if (upload)
            {
                await LocalDirectoryArchive.PackAsync(submission.LocalPath, localArchive, format, ct).ConfigureAwait(false);
                FileInfo info = new(localArchive);
                source = new(info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
            }
            else
            {
                await TransferPathGuard.RejectRemoteDestinationAsync(remote, submission.RemotePath, ct).ConfigureAwait(false);
                IReadOnlyList<SourceTreeEntry> entries = await SourceTreeReader.ReadRemoteAsync(remote, submission.RemotePath, ct).ConfigureAwait(false);
                string manifest = remoteTemp + "/files";
                byte[] names = Encoding.UTF8.GetBytes(".\0" + string.Concat(entries
                    .Where(entry => entry.Kind is SourceEntryKind.File or SourceEntryKind.SymlinkToFile or SourceEntryKind.Directory)
                    .Select(entry => entry.RelativePath + "\0")));
                await using (Stream output = await remote.OpenWriteAsync(manifest, ct).ConfigureAwait(false))
                {
                    await output.WriteAsync(names, ct).ConfigureAwait(false);
                    await remote.CommitWriteAsync(output, ct).ConfigureAwait(false);
                }
                await RunAsync(shell, tools.PackTreeCommand(format, submission.RemotePath, remoteArchive, manifest), ct).ConfigureAwait(false);
                RemoteFileItem item = await remote.GetItemAsync(remoteArchive, ct).ConfigureAwait(false)
                    ?? throw new IOException("远端没有生成归档。");
                source = new(item.Size, item.LastModified);
            }

            total(source.Size);
            state(QueuedTransferState.Transferring);
            FileTransferRequest request = new(
                upload ? localArchive : remoteArchive,
                upload ? remoteTemp : local,
                "archive", settings, source, null, progress,
                Verifier: verifier,
                OnVerifying: () => state(QueuedTransferState.Verifying),
                AfterCopy: afterCopy);
            if (upload) await _executor.UploadAsync(request, remote, ct).ConfigureAwait(false);
            else await _executor.DownloadAsync(request, remote, ct).ConfigureAwait(false);

            state(QueuedTransferState.Unpacking);
            if (upload)
            {
                string parent = destination[..destination.LastIndexOf('/')];
                if (parent.Length == 0) parent = "/";
                await TransferPathGuard.RejectRemoteDestinationAsync(remote, parent, ct).ConfigureAwait(false);
                remoteStage = await TemporaryAsync(shell, tools, parent, ct).ConfigureAwait(false);
                await RunAsync(shell, tools.UnpackCommand(format, remoteArchive, remoteStage), ct).ConfigureAwait(false);
                // 不把 tar 直接解进已有目标：先解到同一父目录的私有暂存目录，再检查合并路径。
                await TransferPathGuard.RejectRemoteDestinationAsync(remote, destination, ct).ConfigureAwait(false);
                IReadOnlyList<SourceTreeEntry> staged = await SourceTreeReader.ReadRemoteAsync(remote, remoteStage, ct).ConfigureAwait(false);
                foreach (SourceTreeEntry entry in staged)
                    await TransferPathGuard.RejectRemoteDestinationAsync(remote, destination + "/" + entry.RelativePath, ct).ConfigureAwait(false);
                RemoteFileItem? existing = await remote.GetAttributesAsync(destination, ct).ConfigureAwait(false);
                if (!rename && existing is { IsDirectory: false }) await remote.DeleteAsync(destination, false, ct).ConfigureAwait(false);
                await RunAsync(shell, tools.PublishDirectoryCommand(remoteStage, destination, rename), ct).ConfigureAwait(false);
            }
            else
            {
                string parent = Path.GetDirectoryName(destination)!;
                TransferPathGuard.RejectLocalDestination(parent);
                Directory.CreateDirectory(parent);
                // 暂存必须与最终目录在同一文件系统，自动改名才是真正的目录重命名。
                string staging = Path.Combine(parent, ".keiterm-archive-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await LocalDirectoryArchive.UnpackAsync(localArchive, staging, format, ct).ConfigureAwait(false);
                    if (rename)
                    {
                        TransferPathGuard.RejectLocalDestination(destination);
                        Directory.Move(staging, destination);
                    }
                    else MergeLocal(staging, destination, ct);
                }
                finally
                {
                    DeleteTemporaryLocal(staging);
                }
            }
        }
        finally
        {
            // 清理不沿用已取消的 token；端点被关闭时尽力清理，不能掩盖原始失败。
            try { if (remoteStage != null) await CleanupAsync(shell, tools, remoteStage).ConfigureAwait(false); }
            finally
            {
                try { if (remoteTemp != null) await CleanupAsync(shell, tools, remoteTemp).ConfigureAwait(false); }
                finally { DeleteTemporaryLocal(local); }
            }
        }
    }

    private static void MergeLocal(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TransferPathGuard.RejectLocalDestination(destination);
        if (File.Exists(destination)) File.Delete(destination);
        if (!Directory.Exists(destination))
        {
            Directory.Move(source, destination);
            return;
        }

        DateTimeOffset modified = new(Directory.GetLastWriteTimeUtc(source));
        UnixFileMode mode = OperatingSystem.IsWindows() ? 0 : File.GetUnixFileMode(source);
        // 合并需要移走暂存条目；目标仍恢复原 mode，暂存目录临时加写权限。
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(source, mode | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (FileSystemInfo item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, item.Name);
            TransferPathGuard.RejectLocalDestination(target);
            if (item is DirectoryInfo && item.LinkTarget == null) MergeLocal(item.FullName, target, ct);
            else File.Move(item.FullName, target, overwrite: true);
        }
        LocalDirectoryArchive.SetMetadata(destination, modified, mode);
    }

    private static void DeleteTemporaryLocal(string directory)
    {
        if (!Directory.Exists(directory)) return;
        MakeWritable(directory);
        Directory.Delete(directory, true);

        static void MakeWritable(string path)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (DirectoryInfo child in new DirectoryInfo(path).EnumerateDirectories())
                if (child.LinkTarget == null) MakeWritable(child.FullName);
        }
    }

    private static async Task<string> TemporaryAsync(IRemoteShell shell, IRemoteToolset tools, string? parent, CancellationToken ct)
    {
        RemoteCommandResult result = await RunAsync(shell, tools.CreateTemporaryDirectoryCommand(parent), ct).ConfigureAwait(false);
        string path = result.Output.TrimEnd('\r', '\n');
        if (!path.StartsWith('/') || path == "/" || path.Contains('\n') || path.Contains('\r')
            || path.Split('/').Any(part => part is "." or "..")
            || parent != null && !path.StartsWith(parent.TrimEnd('/') + "/.keiterm-archive.", StringComparison.Ordinal))
            throw new InvalidDataException("mktemp 返回了不安全的临时路径。");
        return path;
    }

    private static async Task<RemoteCommandResult> RunAsync(IRemoteShell shell, string command, CancellationToken ct)
    {
        RemoteCommandResult result = await shell.RunAsync(command, ct).ConfigureAwait(false);
        if (result.ExitStatus != 0) throw new IOException($"归档命令失败（{result.ExitStatus}）：{result.Error}");
        return result;
    }

    private static async Task CleanupAsync(IRemoteShell shell, IRemoteToolset tools, string directory)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try { await RunAsync(shell, tools.DeleteTemporaryDirectoryCommand(directory), timeout.Token).ConfigureAwait(false); }
        catch (Exception) { /* 连接已关闭时不能阻止本地清理，也不能掩盖原始异常。 */ }
    }
}
