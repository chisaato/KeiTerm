namespace Kei.Term.Core.Transfers;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;

public sealed partial class TransferQueue
{
    // false 只表示执行前不可用或「较旧覆盖」；执行开始后的失败绝不展开成逐文件传输。
    private async Task<bool> TryArchiveAsync(Work work, Action release, Func<Task> reacquire)
    {
        FileTransferSettings settings = Snapshot();
        TransferConflictPolicy policy = PolicyFor(work.Task.BatchId, settings);
        if (!(_archiveDirectoriesOverride?.Invoke() ?? settings.ArchiveDirectoriesEnabled)
            || settings.EffectiveArchiveFormatOrder.Count == 0
            || policy == TransferConflictPolicy.OverwriteIfOlder || Capabilities == null) return false;

        TransferProbeResult probe = await Capabilities.GetAsync(work.Token).ConfigureAwait(false);
        TransferArchiveFormat? format = probe.SelectArchive(settings.EffectiveArchiveFormatOrder);
        IRemoteToolset? tools = RemoteToolsets.ForArchives(probe.Platform);
        if (format == null || tools == null) return false;
        IRemoteShell? shell = await _endpoint.AcquireShellAsync(work.Token).ConfigureAwait(false);
        if (shell == null) return false;

        await using IRemoteTransferLease lease = await _endpoint.AcquireAsync(work.Token).ConfigureAwait(false);
        bool upload = work.Task.Direction == TransferDirection.Upload;
        string parent = upload ? work.Task.RemotePath : Path.GetFullPath(work.Task.LocalPath);
        // 与单文件一样，只接受一个目标名，不能通过归档根名跳出目标父目录。
        _ = AutoRename.NextAvailable(work.Task.Name, _ => false);
        string destination = FullName(parent, work.Task.Name, work.Task.Direction);
        TransferFileFacts? target = await ArchiveTargetAsync(lease.FileSystem, destination, upload, work.Token).ConfigureAwait(false);
        if (target != null && policy == TransferConflictPolicy.Ask)
        {
            Report(work, QueuedTransferState.AwaitingConflict, null, null, null);
            release();
            TransferConflictChoice? choice;
            try
            {
                choice = _promptConflict == null ? null
                    : await _promptConflict(work.Task.Name, new TransferFileFacts(0, work.Task.Submission.ModifiedUtc), target.Value, work.Token).ConfigureAwait(false);
            }
            finally { await reacquire().ConfigureAwait(false); }
            if (choice == null)
            {
                Report(work, QueuedTransferState.Cancelled, null, null, null);
                return true;
            }
            policy = choice.Value.Policy;
            if (choice.Value.ApplyToRemaining)
            {
                lock (_gate) _batchPolicy[work.Task.BatchId] = policy;
            }
        }
        if (policy == TransferConflictPolicy.OverwriteIfOlder) return false;
        if (target != null && policy == TransferConflictPolicy.Skip)
        {
            Report(work, QueuedTransferState.Skipped, 0, "已跳过", null);
            return true;
        }

        // 声明一直持有到整包尝试和重试结束；自动改名在持有声明后重新确认，防并发选择同名。
        IDisposable? claim = null;
        bool rename = target != null && policy == TransferConflictPolicy.AutoRename;
        while (claim == null)
        {
            if (rename)
            {
                string name = await NextNameAsync(lease.FileSystem, parent, work.Task.Name, work.Task.Direction, work.Token).ConfigureAwait(false);
                destination = FullName(parent, name, work.Task.Direction);
            }
            DestinationClaimGate.Entry entry = _claims.TryEnter(ClaimKeys(destination, destination, work.Task.Direction), work, null);
            if (entry.Wait is Task wait)
            {
                release();
                try { await wait.WaitAsync(work.Token).ConfigureAwait(false); }
                finally { await reacquire().ConfigureAwait(false); }
                continue;
            }
            claim = entry.Lease;
            try
            {
                if (rename && await ArchiveTargetAsync(lease.FileSystem, destination, upload, work.Token).ConfigureAwait(false) != null)
                {
                    claim?.Dispose();
                    claim = null;
                }
            }
            catch
            {
                claim?.Dispose();
                throw;
            }
        }

        using (claim)
        {
            // 等待其他同名任务期间目标可能已经发布；重新整体判定，不能把 Skip / Ask 静默变成覆盖。
            if (!rename && target == null && await ArchiveTargetAsync(lease.FileSystem, destination, upload, work.Token).ConfigureAwait(false) != null)
            {
                claim.Dispose();
                return await TryArchiveAsync(work, release, reacquire).ConfigureAwait(false);
            }
            if (upload) await EnsureRemoteDirectoryAsync(lease.FileSystem, parent, work.Token).ConfigureAwait(false);
            else EnsureLocalDirectory(parent);
            int failures = 0;
            while (true)
            {
                work.Token.ThrowIfCancellationRequested();
                try
                {
                    (ITransferHashVerifier? verifier, bool unverified) = await ResolveVerifierAsync(settings, work.Token).ConfigureAwait(false);
                    if (unverified) work.Task.NoteUnverified(work.Generation);
                    DirectoryArchiveTransfer archive = new(_executor);
                    await archive.ExecuteAsync(work.Task.Submission, destination, rename, format.Value,
                        lease.FileSystem, shell, tools, settings, verifier,
                        state => Report(work, state, null, null, null), work.Task.SetTotal,
                        new SyncProgress(bytes => work.Task.ReportBytes(work.Generation, -1, bytes)), _afterCopy, work.Token).ConfigureAwait(false);
                    Report(work, QueuedTransferState.Completed, work.Task.TotalBytes, null, null);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    TransferFailureKind kind = Classify(ex);
                    if (!RetryBackoff.ShouldRetry(kind, ++failures, settings))
                    {
                        Report(work, QueuedTransferState.Failed, null, ex.Message, kind);
                        return true;
                    }
                    Report(work, QueuedTransferState.RetryWait, 0, ex.Message, kind);
                    release();
                    try { await DelayAsync(_time, RetryBackoff.Delay(failures, settings), work.Token).ConfigureAwait(false); }
                    finally { await reacquire().ConfigureAwait(false); }
                }
            }
        }
    }

    private static async Task<TransferFileFacts?> ArchiveTargetAsync(IRemoteFileSystem remote, string path, bool upload, CancellationToken ct)
    {
        if (upload)
        {
            RemoteFileItem? item = await remote.GetAttributesAsync(path, ct).ConfigureAwait(false);
            return item == null ? null : new TransferFileFacts(item.Size, item.LastModified);
        }
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists || info.LinkTarget != null
            ? new TransferFileFacts(info is FileInfo file && file.Exists ? file.Length : 0, new DateTimeOffset(info.LastWriteTimeUtc))
            : null;
    }
}
