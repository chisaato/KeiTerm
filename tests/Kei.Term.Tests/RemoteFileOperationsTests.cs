using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Delete_ConfirmationNamesFirstThreeAndTotalCount(int count)
    {
        MemoryFileSystem fs = new();
        string[] names = ["alpha", "beta", "gamma", "delta"];
        RemoteFileItem[] items = new RemoteFileItem[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = Item("/" + names[i]);
            fs.Entries.Add(items[i].FullPath, items[i]);
        }

        ScriptedInteraction interaction = new();
        interaction.DeleteAnswers.Enqueue(false);
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        foreach (RemoteFileItem item in items) vm.SelectedItems.Add(item);
        await vm.DeleteItemCommand.ExecuteAsync(items[0]);

        string prompt = Assert.Single(interaction.DeletePrompts);
        string expected = count switch
        {
            1 => "alpha",
            2 => Strings.Format("FileManager.DeleteConfirm.Two", "alpha", "beta", 2),
            3 => Strings.Format("FileManager.DeleteConfirm.Three", "alpha", "beta", "gamma", 3),
            _ => Strings.Format("FileManager.DeleteConfirm.Many", "alpha", "beta", "gamma", count),
        };
        Assert.Equal(expected, prompt);
        Assert.Equal(count, fs.Entries.Count);
    }

    [Fact]
    public async Task Delete_SecondRequestDuringConfirmationIsIgnored()
    {
        MemoryFileSystem fs = new();
        RemoteFileItem item = Item("/alpha");
        fs.Entries.Add(item.FullPath, item);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int prompts = 0;
        ScriptedInteraction interaction = new();
        interaction.ConfirmDeleteHandler = _ =>
        {
            if (Interlocked.Increment(ref prompts) == 1)
            {
                started.TrySetResult();
                return release.Task;
            }

            return Task.FromResult(false);
        };
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        Task deleting = vm.DeleteItemCommand.ExecuteAsync(item);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await vm.DeleteItemCommand.ExecuteAsync(item).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, prompts);
        Assert.Single(interaction.DeletePrompts);
        release.TrySetResult(false);
        await deleting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fs.Entries.ContainsKey(item.FullPath));
    }

    [Fact]
    public async Task Delete_DoesNotOverwriteTransferStatus()
    {
        MemoryFileSystem fs = new();
        RemoteFileItem item = Item("/alpha");
        fs.Entries.Add(item.FullPath, item);
        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        vm.TransferStatusMessage = "transfer-owned";
        vm.TransferProgress = 42;
        vm.IsTransferring = true;
        await vm.DeleteItemCommand.ExecuteAsync(item);
        Assert.Equal("transfer-owned", vm.TransferStatusMessage);
        Assert.Equal(42, vm.TransferProgress);
        Assert.True(vm.IsTransferring);
        Assert.False(fs.Entries.ContainsKey(item.FullPath));
    }

    [Fact]
    public Task Delete_ReportsScanThenPathProgress_AndTerminalStatusSurvivesQueuedCallbacks() => HeadlessAvalonia.RunAsync(async () =>
    {
        MemoryFileSystem fs = new();
        RemoteFileItem directory = Item("/dir", directory: true);
        RemoteFileItem child = Item("/dir/child");
        fs.Entries.Add(directory.FullPath, directory);
        fs.Entries.Add(child.FullPath, child);
        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        vm.SelectedItems.Add(directory);
        vm.TransferStatusMessage = "transfer-owned";
        vm.TransferProgress = 42;
        vm.IsTransferring = true;

        bool sawScan = false;
        bool sawExecute = false;
        fs.OnGetItem = () =>
        {
            if (sawScan) return;
            HeadlessAvalonia.Pump();
            sawScan = vm.IsDeleting
                && vm.IsDeleteScanning
                && vm.DeleteProgress == 0d
                && vm.HasDeleteStatus
                && vm.DeleteStatusMessage == Strings.Format("FileManager.Delete.Scanning", "/dir");
        };
        fs.BeforeDelete = (path, _) =>
        {
            if (path != "/dir") return Task.CompletedTask;
            HeadlessAvalonia.Pump();
            int prompts = interaction.DeletePrompts.Count;
            Task second = vm.DeleteItemCommand.ExecuteAsync(directory);
            Assert.True(second.IsCompletedSuccessfully);
            Assert.Equal(prompts, interaction.DeletePrompts.Count);
            sawExecute = vm.IsDeleting
                && !vm.IsDeleteScanning
                && vm.DeleteProgress == 50d
                && vm.CancelDeleteCommand.CanExecute(null)
                && vm.DeleteStatusMessage == Strings.Format("FileManager.Delete.Progress", "/dir", 1, 2);
            return Task.CompletedTask;
        };

        await vm.DeleteItemCommand.ExecuteAsync(directory);
        HeadlessAvalonia.Pump();

        Assert.True(sawScan);
        Assert.True(sawExecute);
        Assert.False(vm.IsDeleting);
        Assert.False(vm.IsDeleteScanning);
        Assert.False(vm.CancelDeleteCommand.CanExecute(null));
        Assert.Equal(100d, vm.DeleteProgress);
        Assert.True(vm.HasDeleteStatus);
        Assert.Equal(Strings.Format("FileManager.Delete.Completed", 2), vm.DeleteStatusMessage);
        Assert.Equal("transfer-owned", vm.TransferStatusMessage);
        Assert.Equal(42, vm.TransferProgress);
        Assert.True(vm.IsTransferring);
        Assert.Empty(fs.Entries);
        Assert.DoesNotContain(vm.Items, item => item.FullPath is "/dir" or "/dir/child");
    });

    [Fact]
    public Task Delete_CancelStopsLaterFilesWithoutRestoringEarlierOnes() => HeadlessAvalonia.RunAsync(async () =>
    {
        MemoryFileSystem fs = new();
        RemoteFileItem alpha = Item("/alpha");
        RemoteFileItem beta = Item("/beta");
        fs.Entries.Add(alpha.FullPath, alpha);
        fs.Entries.Add(beta.FullPath, beta);
        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        vm.SelectedItems.Add(alpha);
        vm.SelectedItems.Add(beta);
        vm.TransferStatusMessage = "transfer-owned";
        fs.BeforeDelete = (path, _) =>
        {
            if (path == "/beta") vm.CancelDeleteCommand.Execute(null);
            return Task.CompletedTask;
        };

        await vm.DeleteItemCommand.ExecuteAsync(alpha);
        HeadlessAvalonia.Pump();
        string? status = vm.DeleteStatusMessage;
        HeadlessAvalonia.Pump();

        Assert.False(fs.Entries.ContainsKey("/alpha"));
        Assert.True(fs.Entries.ContainsKey("/beta"));
        Assert.False(vm.IsDeleting);
        Assert.True(vm.HasDeleteStatus);
        Assert.Equal(Strings.Format("FileManager.Delete.Cancelled", 1, 2), status);
        Assert.Equal(status, vm.DeleteStatusMessage);
        Assert.NotEmpty(fs.ListCancelled);
        Assert.All(fs.ListCancelled, cancelled => Assert.False(cancelled));
        Assert.Equal("transfer-owned", vm.TransferStatusMessage);
        Assert.Contains(vm.Items, item => item.FullPath == "/beta");
        Assert.DoesNotContain(vm.Items, item => item.FullPath == "/alpha");
    });

    [Fact]
    public Task Delete_FailureKeepsPathCompletedCountAndRefreshes() => HeadlessAvalonia.RunAsync(async () =>
    {
        MemoryFileSystem fs = new();
        RemoteFileItem alpha = Item("/alpha");
        RemoteFileItem beta = Item("/beta");
        fs.Entries.Add(alpha.FullPath, alpha);
        fs.Entries.Add(beta.FullPath, beta);
        fs.FailPath = "/beta";
        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        vm.SelectedItems.Add(alpha);
        vm.SelectedItems.Add(beta);
        vm.TransferStatusMessage = "transfer-owned";

        await vm.DeleteItemCommand.ExecuteAsync(alpha);
        HeadlessAvalonia.Pump();

        Assert.False(fs.Entries.ContainsKey("/alpha"));
        Assert.True(fs.Entries.ContainsKey("/beta"));
        Assert.False(vm.IsDeleting);
        Assert.Equal(Strings.Format("FileManager.Delete.Failed", "/beta", 1, 2, "disk"), vm.DeleteStatusMessage);
        Assert.Equal("transfer-owned", vm.TransferStatusMessage);
        Assert.NotEmpty(fs.ListCancelled);
        Assert.All(fs.ListCancelled, cancelled => Assert.False(cancelled));
        Assert.Contains(vm.Items, item => item.FullPath == "/beta");
    });

    [Fact]
    public Task Delete_DisposeCancelsAndCompletes() => HeadlessAvalonia.RunAsync(async () =>
    {
        MemoryFileSystem fs = new();
        RemoteFileItem item = Item("/alpha");
        fs.Entries.Add(item.FullPath, item);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedInteraction interaction = new();
        RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        fs.BeforeDelete = (_, ct) =>
        {
            entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        };

        Task deleting = vm.DeleteItemCommand.ExecuteAsync(item);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await deleting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fs.Entries.ContainsKey(item.FullPath));
    });

    [Fact]
    public async Task Delete_DisposeDuringConfirmation_IsDrainedAndDoesNotDelete()
    {
        MemoryFileSystem fs = new();
        RemoteFileItem item = Item("/alpha");
        fs.Entries.Add(item.FullPath, item);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedInteraction interaction = new();
        interaction.ConfirmDeleteHandler = _ =>
        {
            started.TrySetResult();
            return release.Task;
        };
        RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        Task deleting = vm.DeleteItemCommand.ExecuteAsync(item);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task dispose = vm.DisposeAsync().AsTask();
        Task pending = await Task.WhenAny(dispose, Task.Delay(200));
        Assert.NotSame(dispose, pending);
        await vm.DeleteItemCommand.ExecuteAsync(item);
        Assert.Single(interaction.DeletePrompts);
        release.TrySetResult(true);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        await deleting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fs.Entries.ContainsKey(item.FullPath));
    }

    [Fact]
    public async Task Delete_ParentEntryIsNotATarget()
    {
        MemoryFileSystem fs = new();
        RemoteFileItem parent = Item("/home", directory: true);
        parent = parent with { Name = ".." };
        fs.Entries.Add("/home", parent);
        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        vm.SelectedItems.Add(parent);
        await vm.DeleteItemCommand.ExecuteAsync(parent);
        Assert.Empty(interaction.DeletePrompts);
        Assert.True(fs.Entries.ContainsKey("/home"));
    }

    [Fact]
    public Task DeleteKey_OnFileList_DeletesSelection_ButNotFromPathInput() => HeadlessAvalonia.RunAsync(async () =>
    {
        MemoryFileSystem fs = new();
        RemoteFileItem target = Item("/target");
        fs.Entries.Add(target.FullPath, target);
        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        await vm.RefreshDirectoryAsync();
        RemoteFileManagerView view = new() { DataContext = vm };
        Window window = new() { Width = 820, Height = 400, Content = view };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();
            ListBox list = view.FindControl<ListBox>("FileListBox")!;
            TextBox path = view.FindControl<TextBox>("FileManagerPathInput")!;
            list.SelectedItem = vm.Items.Single(item => item.FullPath == target.FullPath);

            // 路径框里的 Delete 属于文本编辑，不能删除列表选中项。改路径会清掉选集，所以后面要重新选中。
            Assert.True(path.Focus());
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            HeadlessAvalonia.Pump();
            Assert.Empty(interaction.DeletePrompts);
            Assert.True(fs.Entries.ContainsKey(target.FullPath));

            list.SelectedItem = vm.Items.Single(item => item.FullPath == target.FullPath);
            HeadlessAvalonia.Pump();
            // 点击条目后焦点落在 ListBoxItem 上，按键沿冒泡到达列表的 KeyBindings。
            Assert.True(list.ContainerFromItem(list.SelectedItem!)!.Focus());
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            HeadlessAvalonia.Pump();
            Assert.Equal(["target"], interaction.DeletePrompts);
            Assert.False(fs.Entries.ContainsKey(target.FullPath));
        }
        finally
        {
            window.Close();
        }
    });

    private static RemoteFileItem Item(string path, bool directory = false)
        => new(path[(path.LastIndexOf('/') + 1)..], path, directory, 0, DateTimeOffset.UtcNow, "");

    private sealed class MemoryFileSystem : IRemoteFileSystem
    {
        public Dictionary<string, RemoteFileItem> Entries { get; } = new();
        public int Writes { get; private set; }
        public int Commits { get; private set; }
        public int Renames { get; private set; }
        public Func<string, CancellationToken, Task>? BeforeDelete { get; set; }
        public string? FailPath { get; set; }
        public Action? OnGetItem { get; set; }
        public List<bool> ListCancelled { get; } = [];
        private string? _writing;
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            ListCancelled.Add(ct.IsCancellationRequested);
            return Task.FromResult<IReadOnlyList<RemoteFileItem>>(Entries.Values.Where(item => item.FullPath[..item.FullPath.LastIndexOf('/')] == path.TrimEnd('/')).ToArray());
        }
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
        public async Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            if (BeforeDelete != null) await BeforeDelete(path, ct);
            ct.ThrowIfCancellationRequested();
            if (FailPath == path) throw new IOException("disk");
            Entries.Remove(path);
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
        {
            OnGetItem?.Invoke();
            return Task.FromResult(Entries.GetValueOrDefault(path));
        }
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
