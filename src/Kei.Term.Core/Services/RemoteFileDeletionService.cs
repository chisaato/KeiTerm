namespace Kei.Term.Core.Services;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Transfers;

// 把选中项展开成后序计划再逐项删除。扫描与删除之间仍有竞态，不能当作消除了远端 TOCTOU。
public sealed class RemoteFileDeletionService(IRemoteFileSystem fileSystem)
{
    public async Task DeleteAsync(
        IReadOnlyList<RemoteFileItem> targets,
        IProgress<RemoteFileDeletionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(targets);
        List<DeletionNode> roots = [];
        foreach (RemoteFileItem target in targets)
        {
            ct.ThrowIfCancellationRequested();
            string path = Normalize(target.FullPath);
            if (roots.Any(root => Covers(root.Path, path)))
            {
                continue;
            }

            roots.RemoveAll(root => Covers(path, root.Path));
            roots.Add(await ScanAsync(path, progress, ct));
        }

        int total = roots.Sum(Count);
        int completed = 0;
        foreach (DeletionNode root in roots)
        {
            completed = await ExecuteAsync(root, progress, total, completed, ct);
        }
    }

    private async Task<DeletionNode> ScanAsync(
        string path,
        IProgress<RemoteFileDeletionProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 总量要等整棵树看完才知道。
        progress?.Report(new RemoteFileDeletionProgress(path, 0, 0, IsScanning: true));
        RemoteFileItem attributes = await RequireAttributesAsync(path, ct);
        if (!IsRealDirectory(attributes))
        {
            return new DeletionNode(path, IsDirectory: false, []);
        }

        IReadOnlyList<RemoteFileItem> children = await fileSystem.ListDirectoryAsync(path, ct);
        List<DeletionNode> planned = [];
        foreach (RemoteFileItem child in children)
        {
            if (child.Name is "." or "..")
            {
                continue;
            }

            // 名称必须就是这一个子项。不能先拼路径再规范化，否则空名、斜杠或 NUL 会逃出当前目录。
            planned.Add(await ScanAsync(RequireDirectChild(path, child.Name), progress, ct));
        }

        return new DeletionNode(path, IsDirectory: true, planned);
    }

    private async Task<int> ExecuteAsync(
        DeletionNode node,
        IProgress<RemoteFileDeletionProgress>? progress,
        int total,
        int completed,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool directory = node.IsDirectory;
        if (directory)
        {
            // 目录在扫描后可能被换成链接。此时只删链接，不再删除原先计划的子项。
            RemoteFileItem again = await RequireAttributesAsync(node.Path, ct);
            directory = IsRealDirectory(again);
        }

        if (directory)
        {
            foreach (DeletionNode child in node.Children)
            {
                completed = await ExecuteAsync(child, progress, total, completed, ct);
            }
        }

        progress?.Report(new RemoteFileDeletionProgress(node.Path, completed, total, IsScanning: false));
        ct.ThrowIfCancellationRequested();
        await fileSystem.DeleteEntryAsync(node.Path, directory, ct);
        completed++;
        progress?.Report(new RemoteFileDeletionProgress(node.Path, completed, total, IsScanning: false));
        return completed;
    }

    private async Task<RemoteFileItem> RequireAttributesAsync(string path, CancellationToken ct)
    {
        RemoteFileItem? item = await fileSystem.GetAttributesAsync(path, ct);
        return item ?? throw new IOException($"远程路径不存在：{path}");
    }

    private static bool IsRealDirectory(RemoteFileItem item)
        => item.IsDirectory && !TransferPathGuard.IsLink(item);

    private static int Count(DeletionNode node) => 1 + node.Children.Sum(Count);

    private static bool Covers(string ancestor, string path)
        => ancestor == path || path.StartsWith(ancestor + "/", StringComparison.Ordinal);

    private static string RequireDirectChild(string parent, string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\0'))
        {
            throw new TransferPathRejectedException($"拒绝异常的远端名称：{name}");
        }

        string combined = parent + "/" + name;
        string normalized = Normalize(combined);
        int slash = normalized.LastIndexOf('/');
        string segment = slash < 0 ? normalized : normalized[(slash + 1)..];
        string normalizedParent = slash <= 0 ? string.Empty : normalized[..slash];
        if (!string.Equals(segment, name, StringComparison.Ordinal)
            || !string.Equals(normalizedParent, parent, StringComparison.Ordinal))
        {
            throw new TransferPathRejectedException($"拒绝异常的远端名称：{name}");
        }

        return normalized;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw new TransferPathRejectedException($"拒绝删除危险路径：{path}");
        }

        bool absolute = path.StartsWith('/');
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            throw new TransferPathRejectedException($"拒绝删除危险路径：{path}");
        }

        return (absolute ? "/" : string.Empty) + string.Join('/', parts);
    }

    private sealed record DeletionNode(string Path, bool IsDirectory, List<DeletionNode> Children);
}

public sealed record RemoteFileDeletionProgress(
    string CurrentPath,
    int CompletedCount,
    int TotalCount,
    bool IsScanning);
