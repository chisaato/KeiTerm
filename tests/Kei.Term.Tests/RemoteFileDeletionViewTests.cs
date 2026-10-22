using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class RemoteFileDeletionViewTests
{
    private static RemoteFileItem Item(string path, bool directory = false)
        => new(path[(path.LastIndexOf('/') + 1)..], path, directory, 1024, DateTimeOffset.UtcNow, directory ? "drwxr-xr-x" : "-rw-r--r--");

    private sealed class ControlledFileSystem : IRemoteFileSystem
    {
        public Dictionary<string, RemoteFileItem> Entries { get; } = new();
        public List<string> DeletedPaths { get; } = [];

        public TaskCompletionSource<bool>? ScanGate { get; set; }
        public TaskCompletionSource<bool>? DeleteGate { get; set; }
        public Action? OnScanStarted { get; set; }
        public Action<string>? OnDeleteStarted { get; set; }

        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            OnScanStarted?.Invoke();
            if (ScanGate is { } scanGate)
            {
                using var reg = ct.Register(() => scanGate.TrySetCanceled());
                await scanGate.Task;
            }

            string p = path.TrimEnd('/');
            if (string.IsNullOrEmpty(p)) p = "";
            return Entries.Values.Where(item =>
            {
                int lastSlash = item.FullPath.LastIndexOf('/');
                string parent = lastSlash <= 0 ? "" : item.FullPath[..lastSlash];
                return parent == p;
            }).ToArray();
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task CommitWriteAsync(Stream stream, CancellationToken ct = default) => Task.CompletedTask;

        public async Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        {
            OnDeleteStarted?.Invoke(path);
            if (DeleteGate is { } deleteGate)
            {
                using var reg = ct.Register(() => deleteGate.TrySetCanceled());
                await deleteGate.Task;
            }

            ct.ThrowIfCancellationRequested();
            DeletedPaths.Add(path);
            Entries.Remove(path);
        }

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
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

    [Fact]
    public Task DeleteKey_MultipleSelection_WithoutContextMenu_ConfirmsNamesAndDeletesAll() => HeadlessAvalonia.RunAsync(async () =>
    {
        ControlledFileSystem fs = new();
        RemoteFileItem file1 = Item("/alpha.txt");
        RemoteFileItem file2 = Item("/beta.txt");
        RemoteFileItem file3 = Item("/gamma.txt");
        RemoteFileItem file4 = Item("/delta.txt");
        fs.Entries.Add(file1.FullPath, file1);
        fs.Entries.Add(file2.FullPath, file2);
        fs.Entries.Add(file3.FullPath, file3);
        fs.Entries.Add(file4.FullPath, file4);

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
            Assert.NotNull(list);

            // 多选选中 4 个项（不打开右键菜单）
            list.SelectedItems!.Clear();
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == file1.FullPath));
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == file2.FullPath));
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == file3.FullPath));
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == file4.FullPath));
            HeadlessAvalonia.Pump();

            // 焦点在 list item 上并按下 Delete
            Control container = (Control)list.ContainerFromItem(list.SelectedItems[0]!)!;
            Assert.True(container.Focus());
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            HeadlessAvalonia.Pump();

            // 确认提示必须包含前三个文件名称及总数量（"4"），且成功删除所有选中的 4 个文件
            Assert.Single(interaction.DeletePrompts);
            string prompt = interaction.DeletePrompts[0];
            Assert.Contains("alpha.txt", prompt);
            Assert.Contains("beta.txt", prompt);
            Assert.Contains("gamma.txt", prompt);
            Assert.Contains("4", prompt);

            Assert.Contains(file1.FullPath, fs.DeletedPaths);
            Assert.Contains(file2.FullPath, fs.DeletedPaths);
            Assert.Contains(file3.FullPath, fs.DeletedPaths);
            Assert.Contains(file4.FullPath, fs.DeletedPaths);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ClearAndChangeSelection_DoesNotDeleteStaleTargets() => HeadlessAvalonia.RunAsync(async () =>
    {
        ControlledFileSystem fs = new();
        RemoteFileItem file1 = Item("/stale1.txt");
        RemoteFileItem file2 = Item("/stale2.txt");
        RemoteFileItem file3 = Item("/fresh.txt");
        fs.Entries.Add(file1.FullPath, file1);
        fs.Entries.Add(file2.FullPath, file2);
        fs.Entries.Add(file3.FullPath, file3);

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

            // 1. 先选中两个 stale 项
            list.SelectedItems!.Clear();
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == file1.FullPath));
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == file2.FullPath));
            HeadlessAvalonia.Pump();
            Assert.Equal(2, vm.SelectedItems.Count);

            // 2. 清除并改选为 fresh 项
            list.SelectedItems.Clear();
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == file3.FullPath));
            HeadlessAvalonia.Pump();
            Assert.Single(vm.SelectedItems);
            Assert.Equal(file3.FullPath, vm.SelectedItems[0].FullPath);

            // 3. 执行删除
            Control container = (Control)list.ContainerFromItem(list.SelectedItems[0]!)!;
            Assert.True(container.Focus());
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            HeadlessAvalonia.Pump();

            // 4. 验证不删除陈旧目标
            Assert.Contains(file3.FullPath, fs.DeletedPaths);
            Assert.DoesNotContain(file1.FullPath, fs.DeletedPaths);
            Assert.DoesNotContain(file2.FullPath, fs.DeletedPaths);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task DirectorySwitch_And_DataContextChanged_ClearsSelectionWithoutResidue_AndSharesWithNewView() => HeadlessAvalonia.RunAsync(async () =>
    {
        ControlledFileSystem fs = new();
        RemoteFileItem file1 = Item("/dirA/fileA.txt");
        RemoteFileItem file2 = Item("/dirB/fileB.txt");
        fs.Entries.Add(file1.FullPath, file1);
        fs.Entries.Add(file2.FullPath, file2);

        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm1 = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        vm1.CurrentPath = "/dirA";
        await vm1.RefreshDirectoryAsync();

        RemoteFileManagerView view1 = new() { DataContext = vm1 };
        Window window = new() { Width = 820, Height = 400, Content = view1 };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            ListBox list1 = view1.FindControl<ListBox>("FileListBox")!;
            list1.SelectedItems!.Clear();
            list1.SelectedItems.Add(vm1.Items.First(x => x.FullPath == file1.FullPath));
            HeadlessAvalonia.Pump();
            Assert.Single(vm1.SelectedItems);

            // 1. 切换目录 -> 选集清空不残留
            vm1.CurrentPath = "/dirB";
            HeadlessAvalonia.Pump();
            Assert.Empty(list1.SelectedItems);
            Assert.Empty(vm1.SelectedItems);

            // 2. 在 dirB 选中文件
            await vm1.RefreshDirectoryAsync();
            HeadlessAvalonia.Pump();
            list1.SelectedItems.Add(vm1.Items.First(x => x.FullPath == file2.FullPath));
            HeadlessAvalonia.Pump();
            Assert.Single(vm1.SelectedItems);

            // 3. 同一个 VM 挂载到新视图（例如侧栏切到文档标签）：新视图继承已选项目，旧视图脱离不误清 VM 选集
            RemoteFileManagerView view2 = new() { DataContext = vm1 };
            window.Content = view2;
            HeadlessAvalonia.Pump();

            ListBox list2 = view2.FindControl<ListBox>("FileListBox")!;
            Assert.Single(vm1.SelectedItems);
            Assert.Equal(file2.FullPath, vm1.SelectedItems[0].FullPath);
            Assert.Single(list2.SelectedItems!);
            Assert.Equal(file2.FullPath, ((RemoteFileItem)list2.SelectedItems[0]!).FullPath);

            // 4. DataContext 变更为另一个完全不同的 VM -> 选集安全清空不串扰
            await using RemoteFileManagerViewModel vm2 = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
            vm2.CurrentPath = "/dirA";
            await vm2.RefreshDirectoryAsync();

            view2.DataContext = vm2;
            HeadlessAvalonia.Pump();
            Assert.Empty(list2.SelectedItems!);
            Assert.Empty(vm2.SelectedItems);
            Assert.Null(vm2.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task PreconfiguredSelectedItem_PreservedOnInitialMount_And_DataContextChange() => HeadlessAvalonia.RunAsync(async () =>
    {
        ControlledFileSystem fs = new();
        RemoteFileItem file1 = Item("/preset1.txt");
        RemoteFileItem file2 = Item("/preset2.txt");
        fs.Entries.Add(file1.FullPath, file1);
        fs.Entries.Add(file2.FullPath, file2);

        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm1 = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        await vm1.RefreshDirectoryAsync();

        // 仅预设单选 SelectedItem，不预设 SelectedItems
        RemoteFileItem preset1 = vm1.Items.First(x => x.FullPath == file1.FullPath);
        vm1.SelectedItem = preset1;
        Assert.Same(preset1, vm1.SelectedItem);

        // 1. 首次将该 VM 挂载到新 View 上：预设的 SelectedItem 绝不被冲刷为 null
        RemoteFileManagerView view = new() { DataContext = vm1 };
        Window window = new() { Width = 820, Height = 400, Content = view };
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            ListBox list = view.FindControl<ListBox>("FileListBox")!;
            Assert.Same(preset1, vm1.SelectedItem);
            Assert.Same(preset1, list.SelectedItem);
            Assert.Single(vm1.SelectedItems);
            Assert.Same(preset1, vm1.SelectedItems[0]);

            // 2. 准备第二个 VM，同样仅预设 SelectedItem
            await using RemoteFileManagerViewModel vm2 = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
            await vm2.RefreshDirectoryAsync();
            RemoteFileItem preset2 = vm2.Items.First(x => x.FullPath == file2.FullPath);
            vm2.SelectedItem = preset2;

            // 3. 动态切换 View 的 DataContext 到 vm2：vm2 的预设 SelectedItem 得到保留，且与旧 VM 隔离
            view.DataContext = vm2;
            HeadlessAvalonia.Pump();

            Assert.Same(preset2, vm2.SelectedItem);
            Assert.Same(preset2, list.SelectedItem);
            Assert.Single(vm2.SelectedItems);
            Assert.Same(preset2, vm2.SelectedItems[0]);

            // 旧 VM 上的选集不受影响（脱离后保持已选状态）
            Assert.Single(vm1.SelectedItems);
            Assert.Same(preset1, vm1.SelectedItems[0]);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task PathInput_DeleteKey_DoesNotTriggerDeletion() => HeadlessAvalonia.RunAsync(async () =>
    {
        ControlledFileSystem fs = new();
        RemoteFileItem target = Item("/target.txt");
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
            TextBox pathInput = view.FindControl<TextBox>("FileManagerPathInput")!;

            list.SelectedItems!.Clear();
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == target.FullPath));
            HeadlessAvalonia.Pump();

            // 路径框获取焦点后按 Delete
            Assert.True(pathInput.Focus());
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            HeadlessAvalonia.Pump();

            // 不触发删除
            Assert.Empty(interaction.DeletePrompts);
            Assert.DoesNotContain(target.FullPath, fs.DeletedPaths);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task RealVm_ScanningStage_ShowsIndeterminateBar_AndCancelStopsScan() => HeadlessAvalonia.RunAsync(async () =>
    {
        ControlledFileSystem fs = new();
        RemoteFileItem dir = Item("/folder", directory: true);
        RemoteFileItem child = Item("/folder/child.txt");
        fs.Entries.Add(dir.FullPath, dir);
        fs.Entries.Add(child.FullPath, child);

        TaskCompletionSource<bool> scanGate = new();
        bool scanStarted = false;

        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        await vm.RefreshDirectoryAsync();
        fs.OnScanStarted = () =>
        {
            if (scanStarted) return;
            scanStarted = true;
            fs.ScanGate = scanGate;
        };

        RemoteFileManagerView view = new() { DataContext = vm };
        Window window = new() { Width = 820, Height = 400, Content = view };
        Task? deleteTask = null;
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            ListBox list = view.FindControl<ListBox>("FileListBox")!;
            Border statusBar = view.FindControl<Border>("DeletionStatusBar")!;
            ProgressBar progressBar = view.FindControl<ProgressBar>("DeleteProgressBar")!;
            TextBlock statusText = view.FindControl<TextBlock>("DeleteStatusTextBlock")!;
            Button cancelButton = view.FindControl<Button>("CancelDeleteButton")!;

            list.SelectedItems!.Clear();
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == dir.FullPath));
            HeadlessAvalonia.Pump();

            // 触发删除，进入扫描阶段并阻塞在 scanGate
            deleteTask = vm.DeleteItemCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();

            // 断言扫描阶段控件：不确定动画激活，进度条与取消按钮可见，状态文本非空
            Assert.True(statusBar.IsVisible);
            Assert.True(progressBar.IsVisible);
            Assert.True(progressBar.IsIndeterminate);
            Assert.True(cancelButton.IsVisible);
            Assert.NotNull(statusText.Text);

            // 点击真实取消按钮
            cancelButton.Command?.Execute(null);
            HeadlessAvalonia.Pump();

            // 释放扫描 Gate，等待删除流程退出
            fs.ScanGate = null;
            scanGate.TrySetResult(true);
            await deleteTask;
            HeadlessAvalonia.Pump();

            // 验证取消后：未删除任何文件，进度条与取消按钮隐藏，底栏保留取消状态
            Assert.Empty(fs.DeletedPaths);
            Assert.True(statusBar.IsVisible);
            Assert.False(progressBar.IsVisible);
            Assert.False(cancelButton.IsVisible);
            Assert.NotNull(statusText.Text);
            Assert.Contains("取消", statusText.Text);
        }
        finally
        {
            fs.ScanGate = null;
            scanGate.TrySetResult(true);
            if (deleteTask != null)
            {
                try { await deleteTask; } catch { }
            }
            window.Close();
        }
    });

    [Fact]
    public Task RealVm_ExecutionStage_ShowsDeterminateBar_AndCancelStopsSubsequentDeletions() => HeadlessAvalonia.RunAsync(async () =>
    {
        ControlledFileSystem fs = new();
        RemoteFileItem fileA = Item("/item_a.txt");
        RemoteFileItem fileB = Item("/item_b.txt");
        RemoteFileItem fileC = Item("/item_c.txt");
        fs.Entries.Add(fileA.FullPath, fileA);
        fs.Entries.Add(fileB.FullPath, fileB);
        fs.Entries.Add(fileC.FullPath, fileC);

        TaskCompletionSource<bool> deleteGate = new();
        fs.OnDeleteStarted = path =>
        {
            if (path == fileB.FullPath) fs.DeleteGate = deleteGate;
        };

        ScriptedInteraction interaction = new();
        await using RemoteFileManagerViewModel vm = new(Guid.NewGuid(), fs, new IdleTracker(), interaction: interaction);
        await vm.RefreshDirectoryAsync();

        RemoteFileManagerView view = new() { DataContext = vm };
        Window window = new() { Width = 820, Height = 400, Content = view };
        Task? deleteTask = null;
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            ListBox list = view.FindControl<ListBox>("FileListBox")!;
            Border statusBar = view.FindControl<Border>("DeletionStatusBar")!;
            ProgressBar progressBar = view.FindControl<ProgressBar>("DeleteProgressBar")!;
            TextBlock statusText = view.FindControl<TextBlock>("DeleteStatusTextBlock")!;
            Button cancelButton = view.FindControl<Button>("CancelDeleteButton")!;

            list.SelectedItems!.Clear();
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == fileA.FullPath));
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == fileB.FullPath));
            list.SelectedItems.Add(vm.Items.First(x => x.FullPath == fileC.FullPath));
            HeadlessAvalonia.Pump();

            // 首项已完成，在第二项阻塞以核对真实进度。
            deleteTask = vm.DeleteItemCommand.ExecuteAsync(null);
            HeadlessAvalonia.Pump();

            // 断言执行中控件状态：确定进度条（非 Indeterminate），包含当前路径与进度/数量文本
            Assert.True(statusBar.IsVisible);
            Assert.True(progressBar.IsVisible);
            Assert.False(progressBar.IsIndeterminate);
            Assert.Equal(100d / 3, progressBar.Value, precision: 5);
            Assert.Equal([fileA.FullPath], fs.DeletedPaths);
            Assert.True(cancelButton.IsVisible);
            Assert.Contains(fileB.FullPath, statusText.Text);

            // 点击真实取消按钮
            cancelButton.Command?.Execute(null);
            HeadlessAvalonia.Pump();

            // 释放 Delete 阻塞栅门让被取消的流程完成
            fs.DeleteGate = null;
            deleteGate.TrySetResult(true);
            await deleteTask;
            HeadlessAvalonia.Pump();

            // 验证真实取消结果：首项放行后中断，后续文件 (item_b, item_c) 绝不删除，且底栏保留取消后的状态文本
            Assert.DoesNotContain(fileB.FullPath, fs.DeletedPaths);
            Assert.DoesNotContain(fileC.FullPath, fs.DeletedPaths);
            Assert.True(statusBar.IsVisible);
            Assert.False(progressBar.IsVisible);
            Assert.False(cancelButton.IsVisible);
            Assert.NotNull(statusText.Text);
            Assert.Contains("取消", statusText.Text);
        }
        finally
        {
            fs.DeleteGate = null;
            deleteGate.TrySetResult(true);
            if (deleteTask != null)
            {
                try { await deleteTask; } catch { }
            }
            window.Close();
        }
    });
}
