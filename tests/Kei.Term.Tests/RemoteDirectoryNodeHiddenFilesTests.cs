namespace Kei.Term.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Xunit;

public class RemoteDirectoryNodeHiddenFilesTests
{
    private sealed class MemoryFileSystem : IRemoteFileSystem
    {
        private readonly List<RemoteFileItem> _items = [];

        public bool IsConnected => true;
        public string WorkingDirectory => "/";

        public void AddDirectory(string path)
        {
            string name = path.TrimEnd('/').Split('/').Last();
            _items.Add(new RemoteFileItem(name, path, true, 0, DateTimeOffset.UtcNow, "drwxr-xr-x"));
        }

        public void AddFile(string path)
        {
            string name = path.TrimEnd('/').Split('/').Last();
            _items.Add(new RemoteFileItem(name, path, false, 1024, DateTimeOffset.UtcNow, "-rw-r--r--"));
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            string prefix = path.TrimEnd('/') + "/";
            var result = _items
                .Where(item => item.FullPath.StartsWith(prefix, StringComparison.Ordinal)
                    && !item.FullPath[prefix.Length..].Contains('/'))
                .ToList();
            return Task.FromResult<IReadOnlyList<RemoteFileItem>>(result);
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ReloadAsync_RevealsHiddenDirectoriesAndRestoresHasChildren()
    {
        var fs = new MemoryFileSystem();
        string conversationDir = "/brain/f3a55c5e-ea51-4982-bc73-ec111e3902bd";
        fs.AddDirectory(conversationDir);
        fs.AddDirectory($"{conversationDir}/scratch");
        fs.AddDirectory($"{conversationDir}/.system_generated");
        fs.AddDirectory($"{conversationDir}/.user_uploaded");
        // scratch 下只有一个以 . 开头的隐藏目录
        fs.AddDirectory($"{conversationDir}/scratch/.internal_cache");

        bool showHidden = false;
        var node = new RemoteDirectoryNodeViewModel("f3a55c5e-ea51-4982-bc73-ec111e3902bd", conversationDir, fs, () => showHidden, hasDummyChild: false);

        // 第一次加载：隐藏文件关闭
        await node.LoadChildrenAsync();

        Assert.Single(node.Children);
        Assert.Equal("scratch", node.Children[0].Name);

        var scratchNode = node.Children[0];
        await scratchNode.LoadChildrenAsync();

        // 因为 scratch 唯一的子目录是隐藏目录，showHidden 为 false 时，子目录列表为空，HasChildren 为 false
        Assert.Empty(scratchNode.Children);
        Assert.False(scratchNode.HasChildren);

        // 现在用户开启“显示隐藏文件/目录”并触发 ReloadAsync
        showHidden = true;
        scratchNode.IsExpanded = true;
        await node.ReloadAsync(recursiveExpanded: true);

        // conversationDir 下现在包含 scratch, .system_generated, .user_uploaded
        var childNames = node.Children.Select(c => c.Name).OrderBy(x => x).ToList();
        Assert.Equal([".system_generated", ".user_uploaded", "scratch"], childNames);

        // scratch 重新加载后，找到了 .internal_cache，HasChildren 变回 true！
        var reloadedScratch = node.Children.First(c => c.Name == "scratch");
        Assert.True(reloadedScratch.HasChildren);
        Assert.Single(reloadedScratch.Children);
        Assert.Equal(".internal_cache", reloadedScratch.Children[0].Name);
    }
}
