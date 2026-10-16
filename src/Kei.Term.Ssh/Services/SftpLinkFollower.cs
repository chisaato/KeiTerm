namespace Kei.Term.Ssh.Services;

using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

// 只用公开 API 探测符号链接目标。两种探测都失败时，可能是断开，也可能是权限或笼统失败。
// 只有两边都明确不存在才记为断开；其余记为无法确认，调用方必须跳过，不能读写目标。
internal static class SftpLinkFollower
{
    internal readonly record struct Probe(SftpLinkTargetKind Kind, long? Size);

    public static async Task<Probe> ProbeAsync(SftpClient client, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ProbeOutcome directory = await TryOpenDirectoryAsync(client, path, ct);
        if (directory == ProbeOutcome.Opened)
        {
            return new Probe(SftpLinkTargetKind.Directory, null);
        }

        // 目录探测已是权限不足时不再 OpenRead，避免把不可读链接误当成可读文件。
        if (directory == ProbeOutcome.PermissionDenied)
        {
            return new Probe(SftpLinkTargetKind.Indeterminate, null);
        }

        ct.ThrowIfCancellationRequested();
        (ProbeOutcome file, long size) = TryOpenFile(client, path);
        return new Probe(Classify(directory, file), file == ProbeOutcome.Opened ? size : null);
    }

    internal static SftpLinkTargetKind Classify(ProbeOutcome directory, ProbeOutcome file)
    {
        if (directory == ProbeOutcome.Opened)
        {
            return SftpLinkTargetKind.Directory;
        }

        if (file == ProbeOutcome.Opened)
        {
            return SftpLinkTargetKind.File;
        }

        if (directory == ProbeOutcome.NotFound && file == ProbeOutcome.NotFound)
        {
            return SftpLinkTargetKind.Broken;
        }

        return SftpLinkTargetKind.Indeterminate;
    }

    // 第一次 MoveNext 才会 opendir。成功（含空目录）就是指向目录；读到一条就停。
    private static async Task<ProbeOutcome> TryOpenDirectoryAsync(SftpClient client, string path, CancellationToken ct)
    {
        IAsyncEnumerator<ISftpFile> enumerator = client.ListDirectoryAsync(path, ct).GetAsyncEnumerator(ct);
        try
        {
            await enumerator.MoveNextAsync();
            return ProbeOutcome.Opened;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ClassifyFailure(ex);
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 关闭目录句柄失败不影响探测结论
            }
        }
    }

    private static (ProbeOutcome Outcome, long Size) TryOpenFile(SftpClient client, string path)
    {
        try
        {
            using SftpFileStream stream = client.Open(path, FileMode.Open, FileAccess.Read);
            return (ProbeOutcome.Opened, stream.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (ClassifyFailure(ex), 0);
        }
    }

    private static ProbeOutcome ClassifyFailure(Exception exception)
    {
        if (exception is SftpPermissionDeniedException)
        {
            return ProbeOutcome.PermissionDenied;
        }

        if (exception is SftpPathNotFoundException)
        {
            return ProbeOutcome.NotFound;
        }

        return ProbeOutcome.Ambiguous;
    }
}

internal enum SftpLinkTargetKind
{
    Directory,
    File,
    Broken,
    Indeterminate,
}

internal enum ProbeOutcome
{
    Opened,
    NotFound,
    PermissionDenied,
    Ambiguous,
}
