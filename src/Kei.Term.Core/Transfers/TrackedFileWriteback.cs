namespace Kei.Term.Core.Transfers;

using System.IO.Hashing;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Microsoft.Extensions.Logging;

// 外部编辑器的保存先写入同一目录下的私有暂存目录，原子改名成功后才确认内容哈希。
public static class TrackedFileWriteback
{
    public static async Task<byte[]> PublishAsync(IRemoteFileSystem remote, string path, Stream local, ILogger logger, CancellationToken ct)
    {
        string parent = path[..(path.LastIndexOf('/') + 1)];
        string staging = parent + ".keiterm-edit-" + Guid.NewGuid().ToString("N");
        string temporary = staging + "/content";
        RemoteFileItem? original = await remote.GetAttributesAsync(path, ct).ConfigureAwait(false);
        if (original == null) throw new FileNotFoundException("远程编辑文件已不存在。", path);
        if (original.IsDirectory || TransferPathGuard.IsLink(original))
            throw new TransferPathRejectedException($"拒绝替换目录或符号链接：{path}");

        await remote.CreateDirectoryAsync(staging, ct).ConfigureAwait(false);
        try
        {
            const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            await remote.ChangePermissionsAsync(staging, (int)privateDirectory, ct).ConfigureAwait(false);
            XxHash128 hash = new();
            byte[] buffer = new byte[FileTransferExecutor.BufferSize];
            await using (Stream output = await remote.OpenWriteAsync(temporary, ct).ConfigureAwait(false))
            {
                int read;
                while ((read = await local.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash.Append(buffer.AsSpan(0, read));
                }
                await remote.CommitWriteAsync(output, ct).ConfigureAwait(false);
            }

            RemoteFileItem staged = await remote.GetAttributesAsync(temporary, ct).ConfigureAwait(false)
                ?? throw new IOException("远程暂存文件不存在。");
            if (staged.UserId != original.UserId || staged.GroupId != original.GroupId)
                await remote.ChangeOwnershipAsync(temporary, original.UserId, original.GroupId, ct).ConfigureAwait(false);
            await remote.ChangePermissionsAsync(temporary, ParseMode(original.Permissions), ct).ConfigureAwait(false);
            await TransferPathGuard.RejectRemotePartialAsync(remote, path, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await remote.RenameOverwriteAsync(temporary, path, ct).ConfigureAwait(false);
            return hash.GetCurrentHash();
        }
        finally
        {
            // 不递归删除。即使发布失败或取消，也只清理本次操作生成的两个目录项。
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));
            try
            {
                if (await remote.GetAttributesAsync(temporary, cleanup.Token).ConfigureAwait(false) != null)
                    await remote.DeleteEntryAsync(temporary, false, cleanup.Token).ConfigureAwait(false);
                await remote.DeleteEntryAsync(staging, true, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { logger.LogWarning(ex, "清理回写暂存目录失败 {Directory}", staging); }
        }
    }

    internal static int ParseMode(string permissions)
    {
        // Go SFTP 返回八进制，SCP 和 SSH.NET 返回 ls 风格的符号权限。
        if (permissions.Length is >= 1 and <= 4 && permissions.All(c => c is >= '0' and <= '7'))
            return Convert.ToInt32(permissions, 8);
        if (permissions.Length != 10 || permissions[0] != '-')
            throw new IOException($"无法保留远程文件权限：{permissions}");
        int mode = 0;
        for (int group = 0; group < 3; group++)
        {
            int start = 1 + group * 3;
            char special = group == 2 ? 't' : 's';
            char execute = permissions[start + 2];
            if (permissions[start] is not ('r' or '-') || permissions[start + 1] is not ('w' or '-')
                || (execute is not ('x' or '-') && char.ToLowerInvariant(execute) != special))
                throw new IOException($"无法保留远程文件权限：{permissions}");
            int bits = (permissions[start] == 'r' ? 4 : 0) | (permissions[start + 1] == 'w' ? 2 : 0)
                | (execute == 'x' || execute == special ? 1 : 0);
            mode |= bits << (6 - group * 3);
            if (char.ToLowerInvariant(execute) == special) mode |= 1 << (11 - group);
        }
        return mode;
    }
}
