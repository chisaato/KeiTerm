namespace Kei.Term.Ssh.Services;

using Kei.Term.Core.Transfers;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

// 覆盖式改名：SSH.NET 2026 的 RenameFile(old, new, isPosix: true) 发送 posix-rename@openssh.com。
// 服务器没有该扩展时，不再先删除目标。目标已存在就拒绝非原子覆盖，旧文件和部分文件都保留。
// 目标不存在时才普通改名。权限不足等其它失败不退回。
internal static class SftpOverwriteRename
{
    public static void Execute(Action posixRename, Func<bool> targetExists, Action plainRename)
    {
        try
        {
            posixRename();
        }
        catch (Exception ex) when (IsExtensionMissing(ex))
        {
            if (targetExists())
            {
                throw new TransferAtomicOverwriteUnsupportedException(
                    "服务器不支持原子覆盖改名（posix-rename）。已保留原文件和已完成的部分文件，未删除任何一方。",
                    ex);
            }

            plainRename();
        }
    }

    internal static bool IsExtensionMissing(Exception ex)
    {
        if (ex is NotSupportedException)
        {
            return true;
        }

        return ex is SftpException sftp && sftp.StatusCode == StatusCode.OperationUnsupported;
    }
}
