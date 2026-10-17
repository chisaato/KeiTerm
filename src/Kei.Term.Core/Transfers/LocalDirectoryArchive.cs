namespace Kei.Term.Core.Transfers;

using System.Formats.Tar;
using System.IO.Compression;
using ZstdSharp;

// 只负责本地归档编解码；传输、冲突与远端命令由 DirectoryArchiveTransfer 负责。
public static class LocalDirectoryArchive
{
    public static async Task PackAsync(string source, string archive, TransferArchiveFormat format, CancellationToken ct = default)
    {
        TransferPathGuard.RejectLocalDestination(source);
        IReadOnlyList<SourceTreeEntry> entries = await SourceTreeReader.ReadLocalAsync(source, ct).ConfigureAwait(false);
        await using FileStream file = File.Create(archive);
        await using Stream compressed = Compress(file, format);
        await using TarWriter writer = new(compressed, leaveOpen: true);
        await WriteAsync(source, ".", true, new DateTimeOffset(Directory.GetLastWriteTimeUtc(source))).ConfigureAwait(false);
        foreach (SourceTreeEntry entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Kind is not (SourceEntryKind.File or SourceEntryKind.SymlinkToFile or SourceEntryKind.Directory)) continue;
            string path = Path.Combine(source, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            await WriteAsync(path, entry.RelativePath, entry.Kind == SourceEntryKind.Directory, entry.ModifiedUtc).ConfigureAwait(false);
        }

        async Task WriteAsync(string path, string name, bool directory, DateTimeOffset modified)
        {
            PaxTarEntry item = new(directory ? TarEntryType.Directory : TarEntryType.RegularFile, name)
            {
                ModificationTime = modified,
                Mode = OperatingSystem.IsWindows()
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | (directory ? UnixFileMode.UserExecute : 0)
                    : File.GetUnixFileMode(path) & (UnixFileMode)0x1FF,
            };
            // 文件软链读目标内容（不是保存软链）；与 SourceTreeReader / 逐文件路径的语义相同。
            await using Stream data = directory ? Stream.Null : File.OpenRead(path);
            if (!directory) item.DataStream = data;
            await writer.WriteEntryAsync(item, ct).ConfigureAwait(false);
        }
    }

    public static async Task UnpackAsync(string archive, string destination, TransferArchiveFormat format, CancellationToken ct = default)
    {
        TransferPathGuard.RejectLocalDestination(destination);
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination);
        Dictionary<string, TarEntryType> seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        List<(string Path, DateTimeOffset Time, UnixFileMode Mode)> directories = [];
        List<(string Path, string Target)> links = [];
        await using FileStream file = File.OpenRead(archive);
        await using Stream compressed = Decompress(file, format);
        await using TarReader reader = new(compressed, leaveOpen: true);
        while (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            string path = Resolve(root, entry.Name);
            if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.SymbolicLink))
                throw new InvalidDataException($"不允许的归档条目类型：{entry.EntryType}");
            if (!seen.TryAdd(path, entry.EntryType)) throw new InvalidDataException($"归档条目重复：{entry.Name}");
            // 无论归档顺序如何，都禁止任何条目与软链互为祖先（也禁止软链后接同名文件）。
            if (seen.Any(pair => pair.Value == TarEntryType.SymbolicLink && IsChild(pair.Key, path))
                || entry.EntryType == TarEntryType.SymbolicLink && seen.Keys.Any(other => IsChild(path, other)))
                throw new InvalidDataException("归档条目穿透符号链接。");
            TransferPathGuard.RejectLocalDestination(path);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(path);
                directories.Add((path, entry.ModificationTime, entry.Mode));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                if (Path.IsPathRooted(entry.LinkName) || entry.LinkName.Contains('\\'))
                    throw new InvalidDataException("归档软链目标不安全。");
                string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, entry.LinkName));
                if (target != root && !IsChild(root, target)) throw new InvalidDataException("归档软链指向目标树外。");
                links.Add((path, entry.LinkName));
                continue;
            }

            await using (FileStream output = File.Create(path))
            {
                if (entry.DataStream != null) await entry.DataStream.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            SetMetadata(path, entry.ModificationTime, entry.Mode);
        }

        // 软链最后才创建，解析阶段不会发生穿透；现有目标中的软链则由路径守卫拒绝。
        foreach ((string path, string target) in links)
        {
            ct.ThrowIfCancellationRequested();
            File.CreateSymbolicLink(path, target);
        }
        foreach ((string path, DateTimeOffset time, UnixFileMode mode) in directories.OrderByDescending(item => item.Path.Length))
            SetMetadata(path, time, mode);
    }

    internal static string Resolve(string root, string name)
    {
        // 同时防 Windows 盘符、UNC 与 POSIX 路径，不能在 Linux 上把 C:/x 当相对路径放行。
        if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains('\\') || name.Contains(':')
            || name.Split('/').Contains("..")) throw new InvalidDataException($"归档路径不安全：{name}");
        string path = Path.GetFullPath(Path.Combine(root, name));
        if (path != root && !IsChild(root, path)) throw new InvalidDataException($"归档路径越界：{name}");
        return path;
    }

    private static bool IsChild(string parent, string path)
        => path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static void SetMetadata(string path, DateTimeOffset time, UnixFileMode mode)
    {
        File.SetLastWriteTimeUtc(path, time.UtcDateTime);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode & (UnixFileMode)0x1FF);
    }

    private static Stream Compress(Stream stream, TransferArchiveFormat format) => format switch
    {
        TransferArchiveFormat.Tar => stream,
        TransferArchiveFormat.Gzip => new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true),
        TransferArchiveFormat.Zstd => new CompressionStream(stream, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static Stream Decompress(Stream stream, TransferArchiveFormat format) => format switch
    {
        TransferArchiveFormat.Tar => stream,
        TransferArchiveFormat.Gzip => new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true),
        TransferArchiveFormat.Zstd => new DecompressionStream(stream, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
