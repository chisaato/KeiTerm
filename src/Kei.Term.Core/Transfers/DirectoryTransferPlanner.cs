namespace Kei.Term.Core.Transfers;

public enum SourceEntryKind
{
    Directory,
    File,
    SymlinkToFile,
    SymlinkToDirectory,
    BrokenSymlink,
    IndeterminateSymlink,
}

// 相对路径用 /。指向文件的链接，Size 与 ModifiedUtc 是链接目标内容的事实，计划只负责把它排成传文件。
public sealed record SourceTreeEntry(
    string RelativePath,
    SourceEntryKind Kind,
    long Size = 0,
    DateTimeOffset ModifiedUtc = default);

public enum TransferPlanOpKind
{
    CreateDirectory,
    TransferFile,
    Skip,
}

public abstract record TransferPlanOp(string RelativePath)
{
    // ModifiedUtc 为 null 表示这是补出来的父目录，调用方没有源时间。
    public sealed record CreateDirectory(string RelativePath, DateTimeOffset? ModifiedUtc)
        : TransferPlanOp(RelativePath);

    public sealed record TransferFile(string RelativePath, long Size, DateTimeOffset ModifiedUtc, bool FromSymlink)
        : TransferPlanOp(RelativePath);

    public sealed record Skip(string RelativePath, string Reason)
        : TransferPlanOp(RelativePath);
}

public static class DirectoryTransferPlanner
{
    public const string DirectorySymlinkSkipReason = "指向目录的符号链接，不进入";
    public const string UnderDirectorySymlinkSkipReason = "位于被跳过的目录符号链接之下";
    public const string BrokenSymlinkSkipReason = "符号链接已断开";
    public const string IndeterminateSymlinkSkipReason = "无法确认符号链接目标，已跳过";

    // 调用方要包含目录本身。空列表不会凭空建目录。顺序按路径分段稳定排序，父目录在子项之前。
    public static IReadOnlyList<TransferPlanOp> Plan(IEnumerable<SourceTreeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Dictionary<string, SourceTreeEntry> byPath = new(StringComparer.Ordinal);
        foreach (SourceTreeEntry entry in entries)
        {
            if (entry is null)
            {
                throw new ArgumentException("源树条目不能为空。", nameof(entries));
            }

            if (!Enum.IsDefined(entry.Kind))
            {
                throw new ArgumentOutOfRangeException(nameof(entries), entry.Kind, "未知的源条目类型。");
            }

            if (entry.Size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entries), entry.Size, "大小不能为负。");
            }

            string path = NormalizeRelativePath(entry.RelativePath);
            if (!byPath.TryAdd(path, entry with { RelativePath = path }))
            {
                throw new ArgumentException($"重复的相对路径：{path}", nameof(entries));
            }
        }

        HashSet<string> symlinkDirectories = new(StringComparer.Ordinal);
        foreach ((string path, SourceTreeEntry entry) in byPath)
        {
            if (entry.Kind == SourceEntryKind.SymlinkToDirectory)
            {
                symlinkDirectories.Add(path);
            }
        }

        List<TransferPlanOp> ops = [];
        HashSet<string> createDirectories = new(StringComparer.Ordinal);
        foreach ((string path, SourceTreeEntry entry) in byPath)
        {
            if (IsUnderSymlinkDirectory(path, symlinkDirectories))
            {
                ops.Add(new TransferPlanOp.Skip(path, UnderDirectorySymlinkSkipReason));
                continue;
            }

            if (entry.Kind == SourceEntryKind.SymlinkToDirectory)
            {
                RequireParentsAreDirectories(path, byPath);
                AddAncestorDirectories(path, createDirectories);
                ops.Add(new TransferPlanOp.Skip(path, DirectorySymlinkSkipReason));
                continue;
            }

            if (entry.Kind is SourceEntryKind.BrokenSymlink or SourceEntryKind.IndeterminateSymlink)
            {
                RequireParentsAreDirectories(path, byPath);
                AddAncestorDirectories(path, createDirectories);
                string reason = entry.Kind == SourceEntryKind.BrokenSymlink
                    ? BrokenSymlinkSkipReason
                    : IndeterminateSymlinkSkipReason;
                ops.Add(new TransferPlanOp.Skip(path, reason));
                continue;
            }

            if (entry.Kind == SourceEntryKind.Directory)
            {
                RequireParentsAreDirectories(path, byPath);
                createDirectories.Add(path);
                AddAncestorDirectories(path, createDirectories);
                continue;
            }

            RequireParentsAreDirectories(path, byPath);
            AddAncestorDirectories(path, createDirectories);
            ops.Add(new TransferPlanOp.TransferFile(
                path,
                entry.Size,
                entry.ModifiedUtc,
                entry.Kind == SourceEntryKind.SymlinkToFile));
        }

        foreach (string directory in createDirectories)
        {
            DateTimeOffset? modified = null;
            if (byPath.TryGetValue(directory, out SourceTreeEntry? existing) && existing is not null)
            {
                modified = existing.ModifiedUtc;
            }

            ops.Add(new TransferPlanOp.CreateDirectory(directory, modified));
        }

        ops.Sort(static (left, right) => ComparePaths(left.RelativePath, right.RelativePath));
        return ops;
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("相对路径不能为空。", nameof(relativePath));
        }

        string unified = relativePath.Replace('\\', '/').Trim();
        if (unified.StartsWith('/') || (unified.Length >= 2 && unified[1] == ':'))
        {
            throw new ArgumentException("不接受绝对路径。", nameof(relativePath));
        }

        string[] raw = unified.Split('/');
        List<string> parts = [];
        foreach (string part in raw)
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                throw new ArgumentException("相对路径不能包含 .. 。", nameof(relativePath));
            }

            parts.Add(part);
        }

        if (parts.Count == 0)
        {
            throw new ArgumentException("相对路径不能为空。", nameof(relativePath));
        }

        return string.Join('/', parts);
    }

    private static void RequireParentsAreDirectories(string path, Dictionary<string, SourceTreeEntry> byPath)
    {
        string? parent = Parent(path);
        while (parent is not null)
        {
            if (byPath.TryGetValue(parent, out SourceTreeEntry? existing)
                && existing is not null
                && existing.Kind != SourceEntryKind.Directory)
            {
                throw new ArgumentException($"路径 {parent} 不能既是目录又是其他条目。", nameof(path));
            }

            parent = Parent(parent);
        }
    }

    private static void AddAncestorDirectories(string path, HashSet<string> createDirectories)
    {
        string? parent = Parent(path);
        while (parent is not null)
        {
            createDirectories.Add(parent);
            parent = Parent(parent);
        }
    }

    private static bool IsUnderSymlinkDirectory(string path, HashSet<string> symlinkDirectories)
    {
        string? parent = Parent(path);
        while (parent is not null)
        {
            if (symlinkDirectories.Contains(parent))
            {
                return true;
            }

            parent = Parent(parent);
        }

        return false;
    }

    private static string? Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? null : path[..slash];
    }

    private static int ComparePaths(string left, string right)
    {
        string[] leftParts = left.Split('/');
        string[] rightParts = right.Split('/');
        int count = Math.Min(leftParts.Length, rightParts.Length);
        for (int i = 0; i < count; i++)
        {
            int cmp = string.CompareOrdinal(leftParts[i], rightParts[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }
}
