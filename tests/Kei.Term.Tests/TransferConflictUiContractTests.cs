using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;
using Xunit;

namespace Kei.Term.Tests;

public class TransferConflictUiContractTests
{
    [Fact]
    public Task ConflictWindow_RendersFileFacts_AndOptions_FollowsTokens() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TransferFileFacts source = new(1024 * 1024 * 5, DateTimeOffset.UtcNow);
        TransferFileFacts target = new(1024 * 1024 * 2, DateTimeOffset.UtcNow.AddMinutes(-10));

        TransferConflictWindow window = new("test-file.tar.gz", source, target);
        window.Show();
        window.UpdateLayout();

        Assert.Equal("文件已存在", window.Title);

        var textBlocks = window.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.Contains(textBlocks, tb => tb.Text?.Contains("test-file.tar.gz") == true);
        Assert.Contains(textBlocks, tb => tb.Text?.Contains("5 MiB") == true);
        Assert.Contains(textBlocks, tb => tb.Text?.Contains("2 MiB") == true);

        var checkBox = window.GetVisualDescendants().OfType<CheckBox>().Single();
        Assert.Equal("对本批次剩余的冲突都这样处理", checkBox.Content?.ToString());

        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string).ToList();
        var buttonLabels = buttons.Select(b => b.Content?.ToString()).Where(l => !string.IsNullOrEmpty(l)).ToList();
        Assert.Contains("覆盖", buttonLabels);
        Assert.Contains("跳过", buttonLabels);
        Assert.Contains("仅较旧时覆盖", buttonLabels);
        Assert.Contains("自动改名", buttonLabels);

        window.Close();
    });

    [Fact]
    public Task SettingsWindow_And_SessionEditWindow_TransferCapabilityControls_ExistAndFollowTokens() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();

        // 1. 设置窗口中的文件传输页
        AppSettings settings = new();
        TransferSettingsPage transferPage = new();
        transferPage.InitializeTransferPreferences(
            verifyHashEnabled: true,
            hashOrder: [TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Md5],
            archiveDirectoriesEnabled: true,
            archiveOrder: [TransferArchiveFormat.Tar, TransferArchiveFormat.Zstd]);

        // 2. 会话编辑窗口中的文件传输页
        SessionNode session = new() { Name = "test-node", Host = "127.0.0.1" };
        SessionEditViewModel editVm = new(session, null, [], settings);
        SessionEditWindow editWindow = new(editVm);
        editWindow.Show();
        editWindow.UpdateLayout();

        var editCombos = editWindow.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Contains(editCombos, c => c.Name == "SessionVerifyHashComboBox");
        Assert.Contains(editCombos, c => c.Name == "SessionArchiveDirectoriesComboBox");

        var clearBtn = editWindow.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ClearProbeButton");
        Assert.NotNull(clearBtn);

        editWindow.Close();

        SettingsViewModel settingsVm = new(new DummySettingsService(settings));
        SettingsWindow settingsWindow = new(settingsVm);
        settingsWindow.Show();
        settingsVm.SelectedCategory = settingsVm.Categories.Single(c => c.Page is TransferSettingsPage);
        settingsWindow.UpdateLayout();

        TransferSettingsPage pageInVm = (TransferSettingsPage)settingsVm.SelectedCategory.Page;
        pageInVm.DrillDownHashAlgorithmsCommand.Execute(null);
        settingsWindow.UpdateLayout();
        HeadlessAvalonia.Pump();
        var hashList = settingsWindow.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "HashAlgorithmsListBox");
        Assert.NotNull(hashList);
        Assert.Contains("compactReorderList", hashList.Classes);

        pageInVm.DrillDownArchiveFormatsCommand.Execute(null);
        settingsWindow.UpdateLayout();
        HeadlessAvalonia.Pump();

        var archiveList = settingsWindow.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "ArchiveFormatsListBox");
        Assert.NotNull(archiveList);
        Assert.Contains("compactReorderList", archiveList.Classes);

        settingsWindow.Close();
    });

    private sealed class DummySettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Current => settings;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(settings);
        public Task SaveSettingsAsync(AppSettings newSettings, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void TaskItem_And_Board_SupportPackingVerifyingUnpackingStates_AndUnverifiedTag()
    {
        FileTransferTaskItemViewModel item = new("archive.tar.zst", "/tmp/local", "/tmp/remote", FileTransferDirection.Upload);
        Assert.False(item.IsUnverified);
        Assert.Equal(FileTransferState.Pending, item.State);

        item.State = FileTransferState.Packing;
        item.StatusText = "正在打包...";
        Assert.Equal(FileTransferState.Packing, item.State);

        item.State = FileTransferState.Verifying;
        item.StatusText = "正在校验完整性...";
        Assert.Equal(FileTransferState.Verifying, item.State);

        item.State = FileTransferState.Unpacking;
        item.StatusText = "正在解包...";
        Assert.Equal(FileTransferState.Unpacking, item.State);

        item.State = FileTransferState.Completed;
        item.IsUnverified = true;
        Assert.True(item.IsUnverified);
    }

    [Fact]
    public Task RetryCommand_And_RetryAllFailedTransfers_AreWiredInItemAndBoard() => Task.Run(() =>
    {
        FileTransferTaskItemViewModel itemVm = new("remote.bin", "/tmp/local.bin", "/remote/remote.bin", FileTransferDirection.Upload);
        Assert.NotNull(itemVm.RetryCommand);
        Assert.NotNull(itemVm.CancelCommand);

        RemoteFileManagerViewModel fileManager = new(Guid.NewGuid(), new DummyFileSystem(), new DummyFileTracker());
        Assert.NotNull(fileManager.RetryAllFailedTransfersCommand);
        Assert.NotNull(fileManager.DownloadToCommand);
        Assert.NotNull(fileManager.DownloadCommand);
    });

    private sealed class DummyFileSystem : Kei.Term.Core.Abstractions.IRemoteFileSystem
    {
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task<System.Collections.Generic.IReadOnlyList<Kei.Term.Core.Models.RemoteFileItem>> ListDirectoryAsync(string path, System.Threading.CancellationToken ct = default)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<Kei.Term.Core.Models.RemoteFileItem>>([]);
        public Task<Stream> OpenReadAsync(string path, System.Threading.CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task<Stream> OpenWriteAsync(string path, System.Threading.CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteAsync(string path, bool isDirectory, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task<Kei.Term.Core.Models.RemoteFileItem?> GetItemAsync(string path, System.Threading.CancellationToken ct = default)
            => Task.FromResult<Kei.Term.Core.Models.RemoteFileItem?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyFileTracker : Kei.Term.Core.Services.ILocalFileTracker
    {
        public event EventHandler<Kei.Term.Core.Services.LocalFileChangedEventArgs>? FileChanged { add { } remove { } }
        public event EventHandler<string>? FileUntracked { add { } remove { } }
        public string GetLocalCachePath(Guid sessionId, string remotePath) => string.Empty;
        public bool IsTracking(string localFilePath) => false;
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task CheckForChangesAsync(string localFilePath, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterTrackedFileAsync(string localFilePath, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
