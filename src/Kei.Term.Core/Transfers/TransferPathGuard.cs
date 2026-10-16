namespace Kei.Term.Core.Transfers;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

// 拒绝把符号链接、重解析点或目录当作部分文件。检查与打开之间仍有竞态，尤其远程不能消掉 TOCTOU。
public static class TransferPathGuard
{
    public static void RejectLocalDestination(string directory)
    {
        string full = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        string relative = full[root.Length..];
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..")
            {
                throw new TransferPathRejectedException("目标路径不能包含 . 或 .. 。");
            }

            current = Path.Combine(current, part);
            if (IsReparse(new DirectoryInfo(current)))
            {
                throw new TransferPathRejectedException($"目标目录路径包含符号链接，拒绝跟随：{current}");
            }
        }
    }

    public static void RejectLocalPartial(string path)
    {
        FileInfo info = new(path);
        if (IsReparse(info))
        {
            throw new TransferPathRejectedException($"部分文件路径是符号链接，拒绝跟随或删除：{path}");
        }

        if (info.Exists && info.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw new TransferPathRejectedException($"部分文件路径是目录：{path}");
        }
    }

    public static async Task RejectRemoteDestinationAsync(IRemoteFileSystem remote, string directory, CancellationToken ct)
    {
        if (directory.Contains('\\'))
        {
            throw new TransferPathRejectedException("远程目标路径不能包含反斜杠。");
        }

        bool absolute = directory.StartsWith('/');
        string current = absolute ? string.Empty : string.Empty;
        foreach (string part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..")
            {
                throw new TransferPathRejectedException("远程目标路径不能包含 . 或 .. 。");
            }

            current = current.Length == 0
                ? (absolute ? "/" + part : part)
                : current.TrimEnd('/') + "/" + part;
            await RejectIfLinkAsync(remote, current, "目标目录路径包含符号链接，拒绝跟随", ct);
        }
    }

    public static async Task RejectRemotePartialAsync(IRemoteFileSystem remote, string path, CancellationToken ct)
    {
        RemoteFileItem? item = await remote.GetAttributesAsync(path, ct);
        if (item is null)
        {
            return;
        }

        if (IsLink(item))
        {
            throw new TransferPathRejectedException($"部分文件路径是符号链接，拒绝跟随或删除：{path}");
        }

        if (item.IsDirectory)
        {
            throw new TransferPathRejectedException($"部分文件路径是目录：{path}");
        }
    }

    public static bool IsLink(RemoteFileItem item)
        => item.IsSymbolicLink
           || item.IsBrokenSymbolicLink
           || item.IsIndeterminateSymbolicLink
           || item.Permissions.StartsWith('l');

    private static async Task RejectIfLinkAsync(IRemoteFileSystem remote, string path, string reason, CancellationToken ct)
    {
        RemoteFileItem? item = await remote.GetAttributesAsync(path, ct);
        if (item is not null && IsLink(item))
        {
            throw new TransferPathRejectedException($"{reason}：{path}。远程检查与打开之间仍可能被替换，不能当作绝对安全。");
        }
    }

    // Attributes 为 -1 时 HasFlag 会把所有位都当成真，不能用来判断不存在的路径。
    private static bool IsReparse(FileSystemInfo info)
    {
        if (info.LinkTarget is not null)
        {
            return true;
        }

        try
        {
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
