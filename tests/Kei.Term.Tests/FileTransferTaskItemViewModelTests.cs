using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Xunit;

namespace Kei.Term.Tests;

public class FileTransferTaskItemViewModelTests
{
    [Fact]
    public void FormattedProperties_FormatCorrectly()
    {
        // 验证基本属性与格式化输出
        FileTransferTaskItemViewModel vm = new("test.txt", "/local/test.txt", "/remote/test.txt", FileTransferDirection.Upload)
        {
            SizeDisplayMode = FileSizeDisplayMode.Iec
        };

        // 初始未传输
        Assert.Equal("—", vm.FormattedSizeText);
        Assert.Equal("", vm.FormattedProgressText);
        Assert.Equal("", vm.FormattedSpeedText);
        Assert.Equal("—", vm.FormattedRemainingTimeText);
        Assert.Equal("/local/test.txt → /remote/test.txt", vm.TransferPathTooltip);

        // 传输中并上报进度
        vm.State = FileTransferState.Transferring;
        vm.NoteProgress(1024 * 1024, 2 * 1024 * 1024); // 1 MiB / 2 MiB
        Assert.Equal("1 MiB / 2 MiB", vm.FormattedSizeText);
        Assert.Equal("50%", vm.FormattedProgressText);

        // 切换为 SI 模式
        vm.SizeDisplayMode = FileSizeDisplayMode.Si;
        Assert.Equal("1.05 MB / 2.1 MB", vm.FormattedSizeText);

        // 下载方向流向提示
        FileTransferTaskItemViewModel downloadVm = new("download.bin", "/local/download.bin", "/remote/download.bin", FileTransferDirection.Download);
        Assert.Equal("/remote/download.bin → /local/download.bin", downloadVm.TransferPathTooltip);
    }

    [Fact]
    public void StateDrivenColumns_CompletedQueuedFailedTransferring()
    {
        FileTransferTaskItemViewModel vm = new("a.bin", "/l/a.bin", "/r/a.bin", FileTransferDirection.Download);

        // 传输中：已传/总、整数百分比（99.9 不得显示 100%）、即时速度
        vm.State = FileTransferState.Transferring;
        vm.NoteProgress(999 * 1024, 1000 * 1024);
        vm.SpeedMBs = 2.5;
        Assert.Equal("99%", vm.FormattedProgressText);
        Assert.Equal("2.5 MB/s", vm.FormattedSpeedText);
        Assert.Contains(" / ", vm.FormattedSizeText);
        Assert.True(vm.ShowsProgress);

        // 完成：大小只显示总大小，进度与速度留空
        vm.NoteProgress(1000 * 1024, 1000 * 1024);
        vm.State = FileTransferState.Completed;
        Assert.DoesNotContain("/", vm.FormattedSizeText);
        Assert.Equal(FileSizeFormatter.Format(1000 * 1024, FileSizeDisplayMode.Iec), vm.FormattedSizeText);
        Assert.Equal("", vm.FormattedProgressText);
        Assert.Equal("", vm.FormattedSpeedText);
        Assert.False(vm.ShowsProgress);

        // 排队：进度与速度均为空
        FileTransferTaskItemViewModel queued = new("b.bin", "/l/b.bin", "/r/b.bin", FileTransferDirection.Upload);
        Assert.Equal("", queued.FormattedProgressText);
        Assert.Equal("", queued.FormattedSpeedText);

        // 失败且有部分进度：保留已传/总与百分比，但没有速度
        FileTransferTaskItemViewModel failed = new("c.bin", "/l/c.bin", "/r/c.bin", FileTransferDirection.Upload);
        failed.State = FileTransferState.Transferring;
        failed.NoteProgress(512 * 1024, 1024 * 1024);
        failed.SpeedMBs = 1.0;
        failed.State = FileTransferState.Failed;
        Assert.Equal("50%", failed.FormattedProgressText);
        Assert.Equal("", failed.FormattedSpeedText);
        Assert.Contains(" / ", failed.FormattedSizeText);
    }

    [Fact]
    public Task UploadCompleted_TriggersAutoRefresh_WithDebounce() => HeadlessAvalonia.RunAsync(async () =>
    {
        // 验证上传完成后自动触发目标目录刷新，且多次快速完成会防抖合并
        RecordingFileSystem fs = new();
        fs.Files["/remote/dir/file1.txt"] = new RemoteFileItem("file1.txt", "/remote/dir/file1.txt", false, 10, DateTimeOffset.UtcNow, "-rw-r--r--");

        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), fs, new DummyFileTracker())
        {
            CurrentPath = "/remote/dir"
        };

        // 初始执行一次 Refresh
        await model.RefreshDirectoryAsync();
        int initialListCalls = fs.ListCalls;

        // 添加上传任务并设为 Completed
        FileTransferTaskItemViewModel task1 = new("file1.txt", "/local/file1.txt", "/remote/dir", FileTransferDirection.Upload);
        FileTransferTaskItemViewModel task2 = new("file2.txt", "/local/file2.txt", "/remote/dir", FileTransferDirection.Upload);

        model.TransferTasks.Add(task1);
        model.TransferTasks.Add(task2);

        // 快速连续将两者标记为完成
        task1.State = FileTransferState.Completed;
        task2.State = FileTransferState.Completed;

        // 等待防抖延迟（150ms）生效
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(25);
            HeadlessAvalonia.Pump();
            if (fs.ListCalls > initialListCalls) break;
        }

        // 应该仅刷新一次
        Assert.True(fs.ListCalls > initialListCalls);
        Assert.Equal(initialListCalls + 1, fs.ListCalls);
    });

    [Fact]
    public Task UploadCompleted_DifferentDirectory_DoesNotRefresh() => HeadlessAvalonia.RunAsync(async () =>
    {
        // 目标目录不是当前目录时不触发刷新
        RecordingFileSystem fs = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), fs, new DummyFileTracker())
        {
            CurrentPath = "/remote/other"
        };

        await model.RefreshDirectoryAsync();
        int initialListCalls = fs.ListCalls;

        FileTransferTaskItemViewModel task = new("file1.txt", "/local/file1.txt", "/remote/dir", FileTransferDirection.Upload);
        model.TransferTasks.Add(task);
        task.State = FileTransferState.Completed;

        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(25);
            HeadlessAvalonia.Pump();
        }

        Assert.Equal(initialListCalls, fs.ListCalls);
    });

    [Fact]
    public Task RemoteFileManagerView_TransferQueue_RendersTableColumnsAndHeaders() => HeadlessAvalonia.RunAsync(async () =>
    {
        // 渲染 RemoteFileManagerView 并验证表格各列及表头结构
        RecordingFileSystem fs = new();
        await using RemoteFileManagerViewModel model = new(Guid.NewGuid(), fs, new DummyFileTracker())
        {
            IsActivityPanelOpen = true,
            SelectedActivityTab = 0
        };

        FileTransferTaskItemViewModel task = new("build-artifact.tar.gz", "/tmp/build.tar.gz", "/var/www/build.tar.gz", FileTransferDirection.Upload)
        {
            State = FileTransferState.Transferring,
            StatusText = "传输中"
        };
        task.NoteProgress(500 * 1024, 1000 * 1024);
        model.TransferTasks.Add(task);

        RemoteFileManagerView view = new() { DataContext = model };
        Avalonia.Controls.Window window = new() { Width = 700, Height = 600, Content = view };
        window.Show();

        try
        {
            window.UpdateLayout();
            HeadlessAvalonia.Pump();

            // 验证传输队列容器与固定表头
            Avalonia.Controls.Grid container = view.FindControl<Avalonia.Controls.Grid>("TransferQueueContainer")!;
            Assert.NotNull(container);

            // 查找表头中的文字（名称、大小、进度、速度、状态、操作等）
            var textBlocks = container.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().ToArray();
            Assert.Contains(textBlocks, tb => tb.Text == Strings.Get("FileActivity.Header.Name"));
            Assert.Contains(textBlocks, tb => tb.Text == Strings.Get("FileActivity.Header.Size"));
            Assert.Contains(textBlocks, tb => tb.Text == Strings.Get("FileActivity.Header.Progress"));
            Assert.Contains(textBlocks, tb => tb.Text == Strings.Get("FileActivity.Header.Speed"));
            Assert.Contains(textBlocks, tb => tb.Text == Strings.Get("FileActivity.Header.Status"));

            // 验证任务列表中的表格行
            Avalonia.Controls.ItemsControl taskList = view.FindControl<Avalonia.Controls.ItemsControl>("TransferTaskList")!;
            Assert.NotNull(taskList);

            var rows = taskList.GetVisualDescendants().OfType<Avalonia.Controls.Border>()
                .Where(b => b.Classes.Contains("transferTableRow")).ToArray();
            Assert.Single(rows);

            Avalonia.Controls.Border row = rows[0];
            Assert.Contains("transferTask", row.Classes);
            Assert.Contains("transferTableRow", row.Classes);

            // 验证行模板内的控件（文件名、格式化大小、进度百分比、右键菜单等）
            var rowTexts = row.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().ToArray();
            Assert.Contains(rowTexts, tb => tb.Text == "build-artifact.tar.gz");
            Assert.Contains(rowTexts, tb => tb.Text == task.FormattedSizeText);
            Assert.Contains(rowTexts, tb => tb.Text == task.FormattedProgressText);

            Assert.NotNull(row.ContextMenu);
            Assert.True(row.ContextMenu!.Items.Count > 0);

            // 行模板与表头的列宽必须一致，且宽面板下大小列真实可见
            Avalonia.Controls.Grid headerGrid = view.FindControl<Avalonia.Controls.Grid>("TransferHeaderGrid")!;
            Avalonia.Controls.Grid rowGrid = row.GetVisualDescendants().OfType<Avalonia.Controls.Grid>()
                .First(g => g.ColumnDefinitions.Count == 8);
            double[] Widths(Avalonia.Controls.Grid g) => g.ColumnDefinitions.Select(c => c.ActualWidth).ToArray();
            Assert.Equal(Widths(headerGrid), Widths(rowGrid));
            Assert.True(Widths(rowGrid)[3] > 0, $"大小列宽度为 0：{string.Join(",", Widths(rowGrid))}");
            Assert.Equal(model.TransferLayout.Size.Width, Widths(rowGrid)[3], 1);
            // 远端路径默认隐藏
            Assert.Equal(0, Widths(headerGrid)[2]);
            Assert.Equal(0, Widths(rowGrid)[2]);

            // 修改列宽状态：表头与行的该列同步变化
            model.TransferLayout.Size.SetWidth(200);
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            Assert.Equal(200, Widths(headerGrid)[3], 1);
            Assert.Equal(200, Widths(rowGrid)[3], 1);

            // 显示远端路径列，再隐藏大小列：隐藏列在表头与行均为 0
            model.TransferLayout.RemotePath.IsVisible = true;
            model.TransferLayout.Size.IsVisible = false;
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            Assert.Equal(model.TransferLayout.RemotePath.Width, Widths(headerGrid)[2], 1);
            Assert.Equal(model.TransferLayout.RemotePath.Width, Widths(rowGrid)[2], 1);
            Assert.Equal(0, Widths(headerGrid)[3]);
            Assert.Equal(0, Widths(rowGrid)[3]);

            // 完成状态：进度与速度单元格不再显示文字
            task.State = FileTransferState.Completed;
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            // 进度条模板自带的隐藏百分比文本不算显示内容
            var completedTexts = row.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Where(tb => tb.IsEffectivelyVisible).ToArray();
            Assert.DoesNotContain(completedTexts, tb => tb.Text == "50%" || (tb.Text?.EndsWith("MB/s") ?? false));
        }
        finally
        {
            window.Close();
        }
    });

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        public Dictionary<string, RemoteFileItem> Files { get; } = new(StringComparer.Ordinal);
        public int ListCalls { get; private set; }

        public string WorkingDirectory => "/";
        public bool IsConnected => true;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            ListCalls++;
            List<RemoteFileItem> results = [];
            foreach (KeyValuePair<string, RemoteFileItem> pair in Files)
            {
                if (pair.Key.StartsWith(path.TrimEnd('/') + "/"))
                {
                    results.Add(pair.Value);
                }
            }
            return Task.FromResult<IReadOnlyList<RemoteFileItem>>(results);
        }

        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetLastWriteTimeUtcAsync(string path, DateTimeOffset lastWriteTimeUtc, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyFileTracker : ILocalFileTracker
    {
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged { add { } remove { } }
        public event EventHandler<string>? FileUntracked { add { } remove { } }
        public string GetLocalCachePath(Guid sessionId, string remotePath) => remotePath;
        public bool IsTracking(string localFilePath) => false;
        public Task CheckForChangesAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
