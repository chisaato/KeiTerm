using System.Diagnostics;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Services;

namespace Kei.Term.Tests;

public sealed class RemoteFileDeletionTests
{
    [Fact]
    public async Task NestedEmptyAndHiddenEntries_DeleteInPostOrder()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/work");
        fs.AddDirectory("/work/nested");
        fs.AddDirectory("/work/empty");
        fs.AddFile("/work/nested/leaf.txt");
        fs.AddFile("/work/.hidden");
        fs.AddDirectory("/work/.hidden-dir");
        fs.AddFile("/work/.hidden-dir/inside.txt");
        List<RemoteFileDeletionProgress> progress = [];
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync([Item("/work", directory: true)], new SyncProgress(progress.Add));

        Assert.Equal(
            [
                "/work/nested/leaf.txt",
                "/work/nested",
                "/work/empty",
                "/work/.hidden",
                "/work/.hidden-dir/inside.txt",
                "/work/.hidden-dir",
                "/work",
            ],
            fs.Deleted);
        Assert.Empty(fs.Entries);
        Assert.Contains(progress, item => item.IsScanning && item.CurrentPath == "/work/nested" && item.TotalCount == 0);
        Assert.Equal(
            [
                ("/work/nested/leaf.txt", 0, 7, false),
                ("/work/nested/leaf.txt", 1, 7, false),
                ("/work/nested", 1, 7, false),
                ("/work/nested", 2, 7, false),
                ("/work/empty", 2, 7, false),
                ("/work/empty", 3, 7, false),
                ("/work/.hidden", 3, 7, false),
                ("/work/.hidden", 4, 7, false),
                ("/work/.hidden-dir/inside.txt", 4, 7, false),
                ("/work/.hidden-dir/inside.txt", 5, 7, false),
                ("/work/.hidden-dir", 5, 7, false),
                ("/work/.hidden-dir", 6, 7, false),
                ("/work", 6, 7, false),
                ("/work", 7, 7, false),
            ],
            progress.Where(item => !item.IsScanning).Select(item => (item.CurrentPath, item.CompletedCount, item.TotalCount, item.IsScanning)));
        Assert.All(progress.Where(item => item.IsScanning), item => Assert.Equal(0, item.CompletedCount));
    }

    [Fact]
    public async Task SymbolicLinks_AreRemovedWithoutTheirTargets()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/keep");
        fs.AddFile("/keep/target.txt");
        fs.AddDirectory("/keep/dir");
        fs.AddFile("/keep/dir/inside.txt");
        fs.AddLink("/work/file-link", "/keep/target.txt");
        fs.AddLink("/work/dir-link", "/keep/dir", directoryListing: ["/keep/dir/inside.txt"]);
        fs.AddLink("/work/broken", "/missing");
        fs.AddDirectory("/work");
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync(
            [
                Item("/work/file-link", link: true),
                Item("/work/dir-link", directory: true, link: true, targetIsDirectory: true),
                Item("/work/broken", link: true, broken: true),
            ]);

        Assert.Equal(["/work/file-link", "/work/dir-link", "/work/broken"], fs.Deleted);
        Assert.DoesNotContain(true, fs.DeletedAsDirectory);
        Assert.Contains("/keep/target.txt", fs.Entries);
        Assert.Contains("/keep/dir/inside.txt", fs.Entries);
        Assert.Contains("/keep/dir", fs.Entries);
    }

    [Fact]
    public async Task ListedDirectoryLink_UsesDirectChildAttributesAndDoesNotDescend()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/parent");
        fs.AddLink("/parent/alias", "/outside", directoryListing: ["/outside/secret.txt"]);
        fs.AddFile("/outside/secret.txt");
        fs.Listings["/parent"] =
        [
            Item("/parent/alias", directory: true, link: false),
        ];
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync([Item("/parent", directory: true)]);

        Assert.Equal(["/parent/alias", "/parent"], fs.Deleted);
        Assert.Contains("/outside/secret.txt", fs.Entries);
        Assert.Contains("/parent/alias", fs.AttributeLookups);
    }

    [Fact]
    public async Task OverlappingTargets_AreDeletedOnce()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/tree");
        fs.AddDirectory("/tree/child");
        fs.AddFile("/tree/child/file.txt");
        fs.AddFile("/other.txt");
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync(
            [
                Item("/tree/child/file.txt"),
                Item("/tree/", directory: true),
                Item("/tree/child", directory: true),
                Item("/other.txt"),
                Item("/other.txt"),
            ]);

        Assert.Equal(["/tree/child/file.txt", "/tree/child", "/tree", "/other.txt"], fs.Deleted);
    }

    [Fact]
    public async Task CancellationDuringScan_DeletesNothing()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/tree");
        fs.AddFile("/tree/file.txt");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel());
        var service = new RemoteFileDeletionService(fs);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DeleteAsync([Item("/tree", directory: true)], progress, cts.Token));

        Assert.Empty(fs.Deleted);
        Assert.Contains("/tree/file.txt", fs.Entries);
    }

    [Fact]
    public async Task CancellationBeforeNextDeletion_KeepsCompletedEntries()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/tree");
        fs.AddFile("/tree/first.txt");
        fs.AddFile("/tree/second.txt");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(item =>
        {
            if (!item.IsScanning && item.CurrentPath == "/tree/second.txt" && item.CompletedCount == 1)
            {
                cts.Cancel();
            }
        });
        var service = new RemoteFileDeletionService(fs);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DeleteAsync([Item("/tree", directory: true)], progress, cts.Token));

        Assert.Equal(["/tree/first.txt"], fs.Deleted);
        Assert.Contains("/tree/second.txt", fs.Entries);
        Assert.Contains("/tree", fs.Entries);
    }

    [Fact]
    public async Task ScanFailure_ReportsScanAndDeletesNothing()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/tree");
        fs.Failures["/tree"] = new IOException("无法列出目录");
        List<RemoteFileDeletionProgress> progress = [];
        var service = new RemoteFileDeletionService(fs);

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            service.DeleteAsync([Item("/tree", directory: true)], new SyncProgress(progress.Add)));

        Assert.Equal("无法列出目录", error.Message);
        Assert.Empty(fs.Deleted);
        Assert.Contains(progress, item => item.IsScanning && item.CurrentPath == "/tree" && item.TotalCount == 0);
        Assert.DoesNotContain(progress, item => !item.IsScanning);
    }

    [Fact]
    public async Task DeletionFailure_StopsAndReportsPartialProgress()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/tree");
        fs.AddFile("/tree/first.txt");
        fs.AddFile("/tree/second.txt");
        fs.DeleteFailures["/tree/second.txt"] = new IOException("删除失败");
        List<RemoteFileDeletionProgress> progress = [];
        var service = new RemoteFileDeletionService(fs);

        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            service.DeleteAsync([Item("/tree", directory: true)], new SyncProgress(progress.Add)));

        Assert.Equal("删除失败", error.Message);
        Assert.Equal(["/tree/first.txt"], fs.Deleted);
        Assert.Contains("/tree/second.txt", fs.Entries);
        Assert.Contains(progress, item => !item.IsScanning && item.CurrentPath == "/tree/second.txt" && item.CompletedCount == 1 && item.TotalCount == 3);
        Assert.DoesNotContain(progress, item => item.CurrentPath == "/tree" && !item.IsScanning);
    }

    [Fact]
    public async Task DangerousPaths_AreRejectedBeforeAnyDeletion()
    {
        var fs = new RecordingFileSystem();
        fs.AddFile("/safe.txt");
        var service = new RemoteFileDeletionService(fs);
        string[] dangerous = ["/", "//", "/.", "/..", ".", "..", "/safe/../other", "foo/./bar", " ", ""];

        foreach (string path in dangerous)
        {
            await Assert.ThrowsAsync<TransferPathRejectedException>(() => service.DeleteAsync([Item(path)]));
        }

        await Assert.ThrowsAsync<TransferPathRejectedException>(() =>
            service.DeleteAsync([Item("/safe.txt"), Item("/")]));
        Assert.Empty(fs.Deleted);
        Assert.Contains("/safe.txt", fs.Entries);
    }

    [Fact]
    public async Task DirectoryReplacedByLinkBeforeExecution_DoesNotDeletePlannedChildren()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/tree");
        fs.AddFile("/tree/child.txt");
        fs.AddFile("/elsewhere/secret.txt");
        int attributeReads = 0;
        fs.AttributeOverride = path =>
        {
            if (path != "/tree")
            {
                return null;
            }

            attributeReads++;
            return attributeReads == 1
                ? Item("/tree", directory: true)
                : Item("/tree", link: true, targetIsDirectory: true);
        };
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync([Item("/tree", directory: true)]);

        Assert.Equal(["/tree"], fs.Deleted);
        Assert.Equal([false], fs.DeletedAsDirectory);
        Assert.Contains("/tree/child.txt", fs.Entries);
        Assert.Contains("/elsewhere/secret.txt", fs.Entries);
    }

    [Fact]
    public async Task BackslashName_IsOneFile_AndDoesNotMatchSimilarChildPath()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/dir");
        fs.AddDirectory("/dir/a");
        fs.AddFile("/dir/a/b");
        fs.AddFile("/dir/a\\b");
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync([Item("/dir/a\\b")]);

        Assert.Equal(["/dir/a\\b"], fs.Deleted);
        Assert.Contains("/dir/a/b", fs.Entries);
        Assert.Contains("/dir/a", fs.Entries);
    }

    [Fact]
    public async Task AbnormalChildNames_FailDuringScanAndDeleteNothing()
    {
        foreach (string name in new[] { "", "a/b", "a\0b" })
        {
            var fs = new RecordingFileSystem();
            fs.AddDirectory("/tree");
            fs.AddDirectory("/tree/a");
            fs.AddFile("/tree/a/b");
            fs.AddFile("/tree/a\0b");
            fs.AddFile("/tree/keep.txt");
            fs.Listings["/tree"] =
            [
                Item("/tree/keep.txt"),
                new RemoteFileItem(name, "/tree/" + name, false, 0, DateTimeOffset.UnixEpoch, "-rw-r--r--"),
            ];
            var service = new RemoteFileDeletionService(fs);

            await Assert.ThrowsAsync<TransferPathRejectedException>(() =>
                service.DeleteAsync([Item("/tree", directory: true)]));

            Assert.Empty(fs.Deleted);
            Assert.Contains("/tree/keep.txt", fs.Entries);
            Assert.Contains("/tree/a/b", fs.Entries);
            Assert.Contains("/tree/a\0b", fs.Entries);
        }
    }

    [Fact]
    public async Task DotEntries_AreSkippedWhileRealChildIsDeleted()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/tree");
        fs.AddFile("/tree/keep.txt");
        fs.Listings["/tree"] =
        [
            new RemoteFileItem(".", "/tree/.", true, 0, DateTimeOffset.UnixEpoch, "drwxr-xr-x"),
            new RemoteFileItem("..", "/tree/..", true, 0, DateTimeOffset.UnixEpoch, "drwxr-xr-x"),
            Item("/tree/keep.txt"),
        ];
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync([Item("/tree", directory: true)]);

        Assert.Equal(["/tree/keep.txt", "/tree"], fs.Deleted);
    }

    [Fact]
    public async Task ReplacedDirectoryAmongRootsAndSubtree_CountsOnlySuccessfulDeletes()
    {
        var fs = new RecordingFileSystem();
        fs.AddDirectory("/parent");
        fs.AddDirectory("/parent/replaced");
        fs.AddFile("/parent/replaced/nested.txt");
        fs.AddFile("/parent/keep.txt");
        fs.AddFile("/after.txt");
        int reads = 0;
        fs.AttributeOverride = path =>
        {
            if (path != "/parent/replaced")
            {
                return null;
            }

            reads++;
            return reads == 1
                ? Item("/parent/replaced", directory: true)
                : Item("/parent/replaced", link: true, targetIsDirectory: true);
        };
        List<RemoteFileDeletionProgress> progress = [];
        var service = new RemoteFileDeletionService(fs);

        await service.DeleteAsync(
            [Item("/parent", directory: true), Item("/after.txt")],
            new SyncProgress(progress.Add));

        Assert.Equal(["/parent/replaced", "/parent/keep.txt", "/parent", "/after.txt"], fs.Deleted);
        Assert.Contains("/parent/replaced/nested.txt", fs.Entries);
        Assert.Equal(
            [
                ("/parent/replaced", 0, 5, false),
                ("/parent/replaced", 1, 5, false),
                ("/parent/keep.txt", 1, 5, false),
                ("/parent/keep.txt", 2, 5, false),
                ("/parent", 2, 5, false),
                ("/parent", 3, 5, false),
                ("/after.txt", 3, 5, false),
                ("/after.txt", 4, 5, false),
            ],
            progress.Where(item => !item.IsScanning).Select(item => (item.CurrentPath, item.CompletedCount, item.TotalCount, item.IsScanning)));
    }

    [UnixShellFact]
    public async Task ScpDeleteEntry_RemovesOnlyEmptyDirectoryFileOrLink()
    {
        string root = Path.Combine(Path.GetTempPath(), $"keiterm delete {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string filled = Path.Combine(root, "filled");
            string nested = Path.Combine(filled, "nested");
            Directory.CreateDirectory(nested);
            string kept = Path.Combine(nested, "kept.txt");
            await File.WriteAllTextAsync(kept, "kept");
            string empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            string file = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(file, "file");
            string target = Path.Combine(root, "target.txt");
            await File.WriteAllTextAsync(target, "target");
            string link = Path.Combine(root, "link");
            File.CreateSymbolicLink(link, "target.txt");
            string directoryLink = Path.Combine(root, "dir-link");
            Directory.CreateSymbolicLink(directoryLink, "filled");
            string awkward = Path.Combine(root, "it's $(touch injected)");
            await File.WriteAllTextAsync(awkward, "awkward");
            var shell = new ScpShellFileSystem(ExecuteLocallyAsync);

            await Assert.ThrowsAsync<IOException>(() => shell.DeleteEntryAsync(filled, true, CancellationToken.None));
            Assert.Equal("kept", await File.ReadAllTextAsync(kept));
            await shell.DeleteEntryAsync(empty, true, CancellationToken.None);
            await shell.DeleteEntryAsync(file, false, CancellationToken.None);
            await shell.DeleteEntryAsync(link, false, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => shell.DeleteEntryAsync(directoryLink, true, CancellationToken.None));
            await shell.DeleteEntryAsync(directoryLink, false, CancellationToken.None);
            await shell.DeleteEntryAsync(awkward, false, CancellationToken.None);

            Assert.False(Directory.Exists(empty));
            Assert.False(File.Exists(file));
            Assert.False(File.Exists(link));
            Assert.False(Directory.Exists(directoryLink));
            Assert.False(File.Exists(awkward));
            Assert.True(Directory.Exists(filled));
            Assert.Equal("target", await File.ReadAllTextAsync(target));
            Assert.False(File.Exists(Path.Combine(root, "injected")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<RemoteCommandResult> ExecuteLocallyAsync(string command, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 shell");
        await process.WaitForExitAsync(cancellationToken);
        return new RemoteCommandResult(
            await process.StandardOutput.ReadToEndAsync(cancellationToken),
            await process.StandardError.ReadToEndAsync(cancellationToken),
            process.ExitCode,
            null);
    }

    private sealed class SyncProgress(Action<RemoteFileDeletionProgress> handler) : IProgress<RemoteFileDeletionProgress>
    {
        public void Report(RemoteFileDeletionProgress value) => handler(value);
    }

    private static RemoteFileItem Item(
        string path,
        bool directory = false,
        bool link = false,
        bool? targetIsDirectory = null,
        bool broken = false)
        => new(
            path[(path.TrimEnd('/').LastIndexOf('/') + 1)..],
            path,
            directory,
            0,
            DateTimeOffset.UnixEpoch,
            link ? "lrwxrwxrwx" : directory ? "drwxr-xr-x" : "-rw-r--r--",
            IsSymbolicLink: link,
            LinkTargetIsDirectory: targetIsDirectory,
            IsBrokenSymbolicLink: broken);

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        private readonly Dictionary<string, RemoteFileItem> _items = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private readonly Dictionary<string, IReadOnlyList<string>> _linkListings = new(StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyList<RemoteFileItem>> Listings { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Exception> Failures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Exception> DeleteFailures { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];
        public List<bool> DeletedAsDirectory { get; } = [];
        public List<string> AttributeLookups { get; } = [];
        public Func<string, RemoteFileItem?>? AttributeOverride { get; set; }
        public IReadOnlyCollection<string> Entries => _items.Keys;
        public bool IsConnected => true;
        public string WorkingDirectory => "/";

        public void AddDirectory(string path) => Add(Item(path, directory: true));

        public void AddFile(string path) => Add(Item(path));

        public void AddLink(string path, string target, IReadOnlyList<string>? directoryListing = null)
        {
            Add(Item(path, link: true, targetIsDirectory: directoryListing != null, broken: target == "/missing"));
            if (directoryListing != null)
            {
                _linkListings[path] = directoryListing;
            }
        }

        private void Add(RemoteFileItem item)
        {
            _items[item.FullPath] = item;
            if (!_order.Contains(item.FullPath))
            {
                _order.Add(item.FullPath);
            }
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Failures.TryGetValue(path, out Exception? failure))
            {
                throw failure;
            }

            if (Listings.TryGetValue(path, out IReadOnlyList<RemoteFileItem>? forced))
            {
                return Task.FromResult(forced);
            }

            if (_linkListings.TryGetValue(path, out IReadOnlyList<string>? followed))
            {
                return Task.FromResult<IReadOnlyList<RemoteFileItem>>(followed.Select(item => _items[item]).ToArray());
            }

            string prefix = path.TrimEnd('/') + "/";
            RemoteFileItem[] children = _order
                .Where(item => item.StartsWith(prefix, StringComparison.Ordinal) && !item[prefix.Length..].Contains('/'))
                .Select(item => _items[item])
                .ToArray();
            return Task.FromResult<IReadOnlyList<RemoteFileItem>>(children);
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => GetAttributesAsync(path, ct);

        public Task<RemoteFileItem?> GetAttributesAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttributeLookups.Add(path);
            RemoteFileItem? overridden = AttributeOverride?.Invoke(path);
            if (overridden != null || AttributeOverride != null && path == "/tree")
            {
                return Task.FromResult(overridden);
            }

            return Task.FromResult(_items.GetValueOrDefault(path));
        }

        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
            => throw new InvalidOperationException("递归删除不能使用传输清理入口");

        public Task DeleteEntryAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (DeleteFailures.TryGetValue(path, out Exception? failure))
            {
                throw failure;
            }

            if (!_items.Remove(path))
            {
                throw new IOException($"路径不存在：{path}");
            }

            string prefix = path.TrimEnd('/') + "/";
            if (isDirectory && _items.Keys.Any(item => item.StartsWith(prefix, StringComparison.Ordinal) && !item[prefix.Length..].Contains('/')))
            {
                _items[path] = Item(path, directory: true);
                throw new IOException($"目录非空：{path}");
            }

            Deleted.Add(path);
            DeletedAsDirectory.Add(isDirectory);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
