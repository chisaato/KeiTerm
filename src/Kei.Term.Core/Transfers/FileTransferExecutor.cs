namespace Kei.Term.Core.Transfers;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// 单个文件的上传或下载。冲突已由上层决定，这里只写 <最终名>.keipart，确认长度后覆盖式改名。
// 远程文件系统由调用方选择：SFTP 用专用传输连接上的租约，SCP 用浏览用的 ScpRemoteFileSystem。
public sealed class FileTransferExecutor
{
    public const int BufferSize = 64 * 1024;

    private readonly ILogger _logger;

    public FileTransferExecutor(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    // PreviouslyRecordedSource 是「这份部分文件开始时」记录的源事实，不是重试时重新读到的当前源。
    // null 表示没有可信历史（新任务、跨重启残留、调用方没有快照），不能续传，从 0 重传。
    // 非 null 时，开始前会重新读取源的真实大小和修改时间；任一与快照不一致就从 0 重传。
    // 大小相同不是校验。队列重试必须传回上一份已确认部分文件的原始快照，不要把刚读到的新事实填进 PreviouslyRecordedSource，
    // 否则旧前缀会被当成新源的合法续传。路径检查或删除旧部分文件之前失败，调用方不得把本次源记成历史。
    // OnPartialWriteStarted 在旧部分文件已按决定删除、并且对应写流成功打开之后触发。
    // 它携带的是本次 ReadLiveSource 读到的事实，不是 request.Source。没有这次回调就没有可续传历史。
    // Settings 按快照使用，执行期间不要改。
    // Progress 报告的是已落在目标侧的字节数，含续传起点，不是本次新写的增量。
    // request.Source 只是调用方提示，续传、长度确认和修改时间都以开始前重新读取的事实为准。
    public Task UploadAsync(FileTransferRequest request, IRemoteFileSystem remote, CancellationToken ct = default)
        => TransferAsync(request, remote, upload: true, ct);

    public Task DownloadAsync(FileTransferRequest request, IRemoteFileSystem remote, CancellationToken ct = default)
        => TransferAsync(request, remote, upload: false, ct);

    // true = 不能续传。没有历史，或大小、修改时间任一不一致，都算源已变化或无法验证。
    public static bool IsSourceModified(TransferFileFacts live, TransferFileFacts? previouslyRecorded)
    {
        if (previouslyRecorded is not { } previous)
        {
            return true;
        }

        return previous.Size != live.Size || previous.ModifiedUtc != live.ModifiedUtc;
    }

    private async Task TransferAsync(FileTransferRequest request, IRemoteFileSystem remote, bool upload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(remote);
        Validate(request);
        ct.ThrowIfCancellationRequested();

        string partialPath = Join(request.DestinationDirectory, PartialFileNames.ToPartial(request.DestinationFileName), upload);
        string finalPath = Join(request.DestinationDirectory, request.DestinationFileName, upload);
        if (upload)
        {
            await TransferPathGuard.RejectRemoteDestinationAsync(remote, request.DestinationDirectory, ct);
        }
        else
        {
            TransferPathGuard.RejectLocalDestination(request.DestinationDirectory);
            Directory.CreateDirectory(request.DestinationDirectory);
        }

        await RejectUnsafePartialAsync(remote, partialPath, upload, ct);
        TransferFileFacts live = await ReadLiveSourceAsync(request, remote, upload, ct);
        long? partialLength = await ReadPartialLengthAsync(remote, partialPath, upload, ct);
        ResumeDecision decision = ResumeRules.Decide(
            partialLength,
            live.Size,
            IsSourceModified(live, request.PreviouslyRecordedSource),
            request.Settings.ResumeEnabled,
            remote.SupportsResume);

        bool copyComplete = false;
        try
        {
            if (decision.DeleteExistingPartial)
            {
                await RejectUnsafePartialAsync(remote, partialPath, upload, ct);
                await DeletePartialAsync(remote, partialPath, upload, ct);
            }

            await CopyAsync(request, remote, partialPath, decision.StartOffset, live, upload, ct);
            long actual = await ReadRequiredLengthAsync(remote, partialPath, live.Size, upload, ct);
            if (actual != live.Size)
            {
                throw new TransferLengthMismatchException(live.Size, actual);
            }

            // 从此刻起部分文件是唯一完整新副本。发布失败不能再按续传开关删掉它。
            copyComplete = true;
            ct.ThrowIfCancellationRequested();
            await RejectUnsafePartialAsync(remote, partialPath, upload, ct);
            await RejectUnsafeFinalAsync(remote, finalPath, upload, ct);
            await RenameAsync(remote, partialPath, finalPath, upload, ct);
            await SetModifiedAsync(remote, finalPath, live.ModifiedUtc, upload, ct);
        }
        catch (Exception ex)
        {
            if (!copyComplete)
            {
                await CleanupPartialAsync(remote, partialPath, upload, request.Settings.ResumeEnabled);
            }
            else
            {
                _logger.LogWarning(ex, "发布已完成的部分文件失败，已保留 {Path}", partialPath);
            }

            throw;
        }
    }

    private async Task CopyAsync(
        FileTransferRequest request,
        IRemoteFileSystem remote,
        string partialPath,
        long offset,
        TransferFileFacts live,
        bool upload,
        CancellationToken ct)
    {
        Stream source = upload
            ? OpenLocalRead(request.SourcePath, offset)
            : await remote.OpenReadAsync(request.SourcePath, offset, ct);
        await using (source)
        {
            Stream destination = upload
                ? await remote.OpenWriteAsync(partialPath, offset, ct)
                : OpenLocalWrite(partialPath, offset);
            await using (destination)
            {
                // 写流已经打开。此前的路径检查或重置失败不会走到这里，也不能提前记历史。
                request.OnPartialWriteStarted?.Invoke(new PartialWriteStarted(live, offset));
                byte[] buffer = new byte[BufferSize];
                long transferred = offset;
                request.Progress?.Report(transferred);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = await source.ReadAsync(buffer, ct);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    transferred += read;
                    request.Progress?.Report(transferred);
                }

                if (upload)
                {
                    await remote.CommitWriteAsync(destination, ct);
                }
                else
                {
                    await destination.FlushAsync(ct);
                }
            }
        }
    }

    private async Task CleanupPartialAsync(IRemoteFileSystem remote, string partialPath, bool upload, bool resumeEnabled)
    {
        if (resumeEnabled)
        {
            return;
        }

        try
        {
            await DeletePartialAsync(remote, partialPath, upload, CancellationToken.None);
        }
        catch (Exception ex) when (IsMissing(ex))
        {
            // 还没创建出来，或 SCP 在提交前失败
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除部分文件失败 {Path}", partialPath);
        }
    }

    private static async Task RejectUnsafePartialAsync(IRemoteFileSystem remote, string partialPath, bool upload, CancellationToken ct)
    {
        if (upload)
        {
            await TransferPathGuard.RejectRemotePartialAsync(remote, partialPath, ct);
            return;
        }

        TransferPathGuard.RejectLocalPartial(partialPath);
    }

    private static async Task RejectUnsafeFinalAsync(IRemoteFileSystem remote, string finalPath, bool upload, CancellationToken ct)
    {
        if (upload)
        {
            await TransferPathGuard.RejectRemotePartialAsync(remote, finalPath, ct);
            return;
        }

        TransferPathGuard.RejectLocalPartial(finalPath);
    }

    private static async Task<TransferFileFacts> ReadLiveSourceAsync(
        FileTransferRequest request,
        IRemoteFileSystem remote,
        bool upload,
        CancellationToken ct)
    {
        if (upload)
        {
            return ReadLocalSource(request.SourcePath);
        }

        RemoteFileItem? item = await remote.GetItemAsync(request.SourcePath, ct);
        if (item is null)
        {
            throw new FileNotFoundException("源文件不存在。", request.SourcePath);
        }

        if (item.IsIndeterminateSymbolicLink || item.IsBrokenSymbolicLink)
        {
            throw new TransferPathRejectedException($"源符号链接无法确认，拒绝读取：{request.SourcePath}");
        }

        if (item.IsDirectory || item.LinkTargetIsDirectory == true)
        {
            throw new TransferPathRejectedException($"源路径是目录：{request.SourcePath}");
        }

        long size = item.IsSymbolicLink ? item.LinkTargetSize ?? item.Size : item.Size;
        DateTimeOffset modified = item.IsSymbolicLink
            ? item.LinkTargetModified ?? item.LastModified
            : item.LastModified;
        return new TransferFileFacts(size, modified);
    }

    private static TransferFileFacts ReadLocalSource(string path)
    {
        FileInfo info = new(path);
        if (info.LinkTarget is not null)
        {
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !target.Exists)
            {
                throw new FileNotFoundException("源符号链接目标不存在。", path);
            }

            if (target.Attributes.HasFlag(FileAttributes.Directory))
            {
                throw new TransferPathRejectedException($"拒绝把目录符号链接当作源文件：{path}");
            }

            if (target is FileInfo file)
            {
                return new TransferFileFacts(file.Length, new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc)));
            }
        }

        if (!info.Exists)
        {
            throw new FileNotFoundException("源文件不存在。", path);
        }

        if (info.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw new TransferPathRejectedException($"源路径是目录：{path}");
        }

        return new TransferFileFacts(info.Length, new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)));
    }

    private static async Task<long?> ReadPartialLengthAsync(
        IRemoteFileSystem remote,
        string partialPath,
        bool upload,
        CancellationToken ct)
    {
        if (upload)
        {
            RemoteFileItem? item = await remote.GetItemAsync(partialPath, ct);
            if (item is null)
            {
                return null;
            }

            if (item.IsDirectory)
            {
                throw new IOException($"部分文件路径是目录：{partialPath}");
            }

            return item.Size;
        }

        if (!File.Exists(partialPath))
        {
            return null;
        }

        return new FileInfo(partialPath).Length;
    }

    private static async Task<long> ReadRequiredLengthAsync(
        IRemoteFileSystem remote,
        string partialPath,
        long expected,
        bool upload,
        CancellationToken ct)
    {
        long? length = await ReadPartialLengthAsync(remote, partialPath, upload, ct);
        return length ?? throw new TransferLengthMismatchException(expected, actual: 0);
    }

    private static Task DeletePartialAsync(IRemoteFileSystem remote, string partialPath, bool upload, CancellationToken ct)
    {
        if (upload)
        {
            return remote.DeleteAsync(partialPath, isDirectory: false, ct);
        }

        File.Delete(partialPath);
        return Task.CompletedTask;
    }

    private static async Task RenameAsync(
        IRemoteFileSystem remote,
        string partialPath,
        string finalPath,
        bool upload,
        CancellationToken ct)
    {
        if (upload)
        {
            await remote.RenameOverwriteAsync(partialPath, finalPath, ct);
            return;
        }

        File.Move(partialPath, finalPath, overwrite: true);
    }

    private static async Task SetModifiedAsync(
        IRemoteFileSystem remote,
        string finalPath,
        DateTimeOffset modified,
        bool upload,
        CancellationToken ct)
    {
        DateTime utc = modified.UtcDateTime;
        if (upload)
        {
            await remote.SetLastWriteTimeUtcAsync(finalPath, utc, ct);
            return;
        }

        File.SetLastWriteTimeUtc(finalPath, utc);
    }

    private static Stream OpenLocalRead(string path, long offset)
    {
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        return stream;
    }

    private static Stream OpenLocalWrite(string path, long offset)
    {
        FileMode mode = offset == 0 ? FileMode.Create : FileMode.Open;
        FileStream stream = new(path, mode, FileAccess.Write, FileShare.None);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        return stream;
    }

    private static string Join(string directory, string name, bool remote)
    {
        if (!remote)
        {
            return Path.Combine(directory, name);
        }

        string trimmed = directory.TrimEnd('/');
        return trimmed.Length == 0 ? "/" + name : trimmed + "/" + name;
    }

    private static void Validate(FileTransferRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationFileName);
        ArgumentNullException.ThrowIfNull(request.Settings);
        if (request.Source.Size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Source.Size, "源文件大小不能为负。");
        }

        if (request.DestinationFileName is "." or ".."
            || request.DestinationFileName.Contains('/')
            || request.DestinationFileName.Contains('\\'))
        {
            throw new ArgumentException("最终文件名不能包含路径。", nameof(request));
        }
    }

    private static bool IsMissing(Exception exception)
        => exception is FileNotFoundException or DirectoryNotFoundException
           || exception.GetType().Name.Contains("PathNotFound", StringComparison.Ordinal);
}

// 对应写流已成功打开。Source 是执行器刚刚读到的现场事实，不是调用方请求里的快照。
public readonly record struct PartialWriteStarted(TransferFileFacts Source, long StartOffset);

public sealed record FileTransferRequest(
    string SourcePath,
    string DestinationDirectory,
    string DestinationFileName,
    FileTransferSettings Settings,
    TransferFileFacts Source,
    TransferFileFacts? PreviouslyRecordedSource = null,
    IProgress<long>? Progress = null,
    Action<PartialWriteStarted>? OnPartialWriteStarted = null);
