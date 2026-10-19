using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public class RemoteFileOperationsTests
{
    [Theory]
    [InlineData("", false, "")]
    [InlineData("  ", false, "")]
    [InlineData(".", false, ".")]
    [InlineData("..", false, "..")]
    [InlineData("a/b", false, "a/b")]
    [InlineData("a\0b", false, "a\0b")]
    [InlineData("  文件.txt  ", true, "文件.txt")]
    [InlineData("a\\b", true, "a\\b")]
    public void Names_AreNormalizedAndValidated(string input, bool valid, string expected)
    {
        Assert.Equal(valid, RemoteFileManagerViewModel.TryNormalizeName(input, out string name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("/", "/new")]
    [InlineData("/home/", "/home/new")]
    [InlineData("/home", "/home/new")]
    public void Paths_UseRemoteSeparators(string parent, string expected)
        => Assert.Equal(expected, RemoteFileManagerViewModel.JoinRemotePath(parent, "new"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_UsesNormalizedPathAndRefreshes(bool directory)
    {
        MemoryFileSystem fs = new();
        ScriptedInteraction interaction = new();
        interaction.TextAnswers.Enqueue("  new  ");
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        vm.CurrentPath = "/home/";
        if (directory) await vm.CreateDirectoryCommand.ExecuteAsync(null);
        else await vm.CreateFileCommand.ExecuteAsync(null);
        RemoteFileItem created = fs.Entries["/home/new"];
        Assert.Equal(directory, created.IsDirectory);
        Assert.Equal(directory ? 0 : 1, fs.Commits);
        Assert.Contains(vm.Items, item => item.FullPath == created.FullPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_ExistingTargetIsNotOverwritten(bool directory)
    {
        MemoryFileSystem fs = new();
        RemoteFileItem original = Item("/new");
        fs.Entries.Add("/new", original);
        ScriptedInteraction interaction = new();
        interaction.TextAnswers.Enqueue("new");
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        if (directory) await vm.CreateDirectoryAsync(); else await vm.CreateFileAsync();
        Assert.Same(original, fs.Entries["/new"]);
        Assert.Equal(0, fs.Writes);
        Assert.Equal(0, fs.Commits);
        Assert.NotNull(vm.TransferStatusMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("old")]
    [InlineData("..")]
    public async Task Rename_CancelSameOrInvalidDoesNotMutate(string? answer)
    {
        MemoryFileSystem fs = new();
        RemoteFileItem original = Item("/old");
        fs.Entries.Add("/old", original);
        ScriptedInteraction interaction = new();
        interaction.TextAnswers.Enqueue(answer);
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        await vm.RenameItemAsync(original);
        Assert.Equal(0, fs.Renames);
        Assert.Same(original, fs.Entries["/old"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_RespectsExistingTarget(bool exists)
    {
        MemoryFileSystem fs = new();
        RemoteFileItem original = Item("/home/old");
        fs.Entries.Add(original.FullPath, original);
        if (exists) fs.Entries.Add("/home/new", Item("/home/new"));
        ScriptedInteraction interaction = new();
        interaction.TextAnswers.Enqueue("new");
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        await vm.RenameItemCommand.ExecuteAsync(original);
        Assert.Equal(exists ? 0 : 1, fs.Renames);
        Assert.Equal(exists, fs.Entries.ContainsKey("/home/old"));
        Assert.True(fs.Entries.ContainsKey("/home/new"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Delete_RequiresConfirmation(bool confirmed, bool multiple)
    {
        MemoryFileSystem fs = new();
        RemoteFileItem first = Item("/first");
        fs.Entries.Add(first.FullPath, first);
        ScriptedInteraction interaction = new();
        interaction.DeleteAnswers.Enqueue(confirmed);
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        if (multiple)
        {
            RemoteFileItem second = Item("/second");
            fs.Entries.Add(second.FullPath, second);
            vm.SelectedItems.Add(first);
            vm.SelectedItems.Add(second);
        }
        await vm.DeleteItemCommand.ExecuteAsync(first);
        Assert.Single(interaction.DeletePrompts);
        Assert.Equal(confirmed ? 0 : multiple ? 2 : 1, fs.Entries.Count);
    }

    private static RemoteFileItem Item(string path, bool directory = false)
        => new(path[(path.LastIndexOf('/') + 1)..], path, directory, 0, DateTimeOffset.UtcNow, "");

    private sealed class MemoryFileSystem : IRemoteFileSystem
    {
        public Dictionary<string, RemoteFileItem> Entries { get; } = new();
        public int Writes { get; private set; }
        public int Commits { get; private set; }
        public int Renames { get; private set; }
        private string? _writing;
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>(Entries.Values.Where(item => item.FullPath[..item.FullPath.LastIndexOf('/')] == path.TrimEnd('/')).ToArray());
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        {
            Writes++;
            _writing = path;
            return Task.FromResult<Stream>(new MemoryStream());
        }
        public Task CommitWriteAsync(Stream stream, CancellationToken ct = default)
        {
            Assert.Equal(0, stream.Length);
            Commits++;
            Entries[_writing!] = Item(_writing!);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            Entries.Remove(path);
            return Task.CompletedTask;
        }
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
        {
            Renames++;
            RemoteFileItem source = Entries[oldPath];
            Entries.Remove(oldPath);
            Entries.Add(newPath, Item(newPath, source.IsDirectory));
            return Task.CompletedTask;
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            Entries.Add(path, Item(path, true));
            return Task.CompletedTask;
        }
        public Task ChangePermissionsAsync(string path, int permissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
            => Task.FromResult(Entries.GetValueOrDefault(path));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdleTracker : ILocalFileTracker
    {
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged { add { } remove { } }
        public event EventHandler<string>? FileUntracked { add { } remove { } }
        public string GetLocalCachePath(Guid sessionId, string remotePath) => remotePath;
        public bool IsTracking(string path) => false;
        public Task CheckForChangesAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterTrackedFileAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
