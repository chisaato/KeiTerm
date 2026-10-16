namespace Kei.Term.Core.Transfers;

using Kei.Term.Core.Models;

// SCP 会把整个文件读进内存。超过阈值时由上层提示，执行器本身不弹窗、也不改协议。
public static class ScpTransferLimits
{
    public const long LargeFileBytes = 512L * 1024 * 1024;

    public static bool NeedsLargeFileWarning(FileTransferProtocol protocol, long size)
    {
        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "文件大小不能为负。");
        }

        return protocol == FileTransferProtocol.Scp && size > LargeFileBytes;
    }
}
