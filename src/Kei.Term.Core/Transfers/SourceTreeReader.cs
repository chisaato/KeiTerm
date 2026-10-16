namespace Kei.Term.Core.Transfers;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

// 把本地或远程目录列成 SourceTreeEntry，相对路径相对于传入的根目录，不包含根本身。
// 根目录由上层作为传输目标名创建。指向目录的符号链接不进入，断开的链接交给计划跳过。
public static class SourceTreeReader
{
    public static async Task<IReadOnlyList<SourceTreeEntry>> ReadRemoteAsync(
        IRemoteFileSystem fileSystem,
        string directory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        List<SourceTreeEntry> entries = [];
        await WalkRemoteAsync(fileSystem, directory, parentRelative: null, entries, ct);
        return entries;
    }

    public static Task<IReadOnlyList<SourceTreeEntry>> ReadLocalAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Task.Run(() =>
        {
            List<SourceTreeEntry> entries = [];
            WalkLocal(directory, parentRelative: null, entries, ct);
            return (IReadOnlyList<SourceTreeEntry>)entries;
        }, ct);
    }

    private static async Task WalkRemoteAsync(
        IRemoteFileSystem fileSystem,
        string fullPath,
        string? parentRelative,
        List<SourceTreeEntry> entries,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<RemoteFileItem> items = await fileSystem.ListDirectoryAsync(fullPath, ct);
        foreach (RemoteFileItem item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Name is "." or "..")
            {
                continue;
            }

            string relative = Combine(parentRelative, item.Name);
            if (item.IsSymbolicLink)
            {
                if (item.IsIndeterminateSymbolicLink)
                {
                    entries.Add(new SourceTreeEntry(relative, SourceEntryKind.IndeterminateSymlink, 0, item.LastModified));
                    continue;
                }

                if (item.IsBrokenSymbolicLink)
                {
                    entries.Add(new SourceTreeEntry(relative, SourceEntryKind.BrokenSymlink, 0, item.LastModified));
                    continue;
                }

                if (item.LinkTargetIsDirectory == true)
                {
                    entries.Add(new SourceTreeEntry(relative, SourceEntryKind.SymlinkToDirectory, 0, item.LastModified));
                    continue;
                }

                long size = item.LinkTargetSize ?? item.Size;
                DateTimeOffset modified = item.LinkTargetModified ?? item.LastModified;
                entries.Add(new SourceTreeEntry(relative, SourceEntryKind.SymlinkToFile, size, modified));
                continue;
            }

            if (item.IsDirectory)
            {
                entries.Add(new SourceTreeEntry(relative, SourceEntryKind.Directory, item.Size, item.LastModified));
                await WalkRemoteAsync(fileSystem, item.FullPath, relative, entries, ct);
                continue;
            }

            entries.Add(new SourceTreeEntry(relative, SourceEntryKind.File, item.Size, item.LastModified));
        }
    }

    private static void WalkLocal(string fullPath, string? parentRelative, List<SourceTreeEntry> entries, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (FileSystemInfo info in new DirectoryInfo(fullPath).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            string relative = Combine(parentRelative, info.Name);
            if (IsSymlink(info))
            {
                AddLocalSymlink(info, relative, entries);
                continue;
            }

            if (info is DirectoryInfo)
            {
                entries.Add(new SourceTreeEntry(relative, SourceEntryKind.Directory, 0, new DateTimeOffset(info.LastWriteTimeUtc)));
                WalkLocal(info.FullName, relative, entries, ct);
                continue;
            }

            FileInfo file = (FileInfo)info;
            entries.Add(new SourceTreeEntry(
                relative,
                SourceEntryKind.File,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc)));
        }
    }

    private static void AddLocalSymlink(FileSystemInfo info, string relative, List<SourceTreeEntry> entries)
    {
        FileSystemInfo? target;
        try
        {
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (UnauthorizedAccessException)
        {
            entries.Add(new SourceTreeEntry(relative, SourceEntryKind.IndeterminateSymlink));
            return;
        }

        // 断开时可能返回 Exists=false 且 Attributes 为 -1，不能用 HasFlag(Directory) 判断。
        if (target is null || !target.Exists)
        {
            entries.Add(new SourceTreeEntry(relative, SourceEntryKind.BrokenSymlink));
            return;
        }

        if (target.Attributes.HasFlag(FileAttributes.Directory))
        {
            entries.Add(new SourceTreeEntry(relative, SourceEntryKind.SymlinkToDirectory));
            return;
        }

        long size = 0;
        DateTimeOffset modified = default;
        if (target is FileInfo file)
        {
            size = file.Length;
            modified = new DateTimeOffset(file.LastWriteTimeUtc);
        }

        entries.Add(new SourceTreeEntry(relative, SourceEntryKind.SymlinkToFile, size, modified));
    }

    private static bool IsSymlink(FileSystemInfo info)
        => info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static string Combine(string? parent, string name)
        => string.IsNullOrEmpty(parent) ? name : parent + "/" + name;
}
