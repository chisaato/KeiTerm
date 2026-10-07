using System.Globalization;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;

namespace Kei.Term.Tests;

public class FileSizeDisplayTests
{
    [Theory]
    [InlineData(0, FileSizeDisplayMode.Iec, "0 B")]
    [InlineData(1023, FileSizeDisplayMode.Iec, "1023 B")]
    [InlineData(1024, FileSizeDisplayMode.Iec, "1 KiB")]
    [InlineData(1536, FileSizeDisplayMode.Iec, "1.5 KiB")]
    [InlineData(1048576, FileSizeDisplayMode.Iec, "1 MiB")]
    [InlineData(1048575, FileSizeDisplayMode.Iec, "1 MiB")]
    [InlineData(999, FileSizeDisplayMode.Si, "999 B")]
    [InlineData(1000, FileSizeDisplayMode.Si, "1 kB")]
    [InlineData(1024, FileSizeDisplayMode.Si, "1.02 kB")]
    [InlineData(1500000, FileSizeDisplayMode.Si, "1.5 MB")]
    [InlineData(999999, FileSizeDisplayMode.Si, "1 MB")]
    [InlineData(1073741824, FileSizeDisplayMode.Iec, "1 GiB")]
    [InlineData(1000000000, FileSizeDisplayMode.Si, "1 GB")]
    [InlineData(long.MaxValue, FileSizeDisplayMode.Iec, "8 EiB")]
    [InlineData(long.MaxValue, FileSizeDisplayMode.Si, "9.22 EB")]
    [InlineData(long.MaxValue, FileSizeDisplayMode.Bytes, "9223372036854775807 B")]
    [InlineData(-1, FileSizeDisplayMode.Iec, "—")]
    public void Format_UsesCorrectRadixAndUnits_WithoutOverflow(long bytes, FileSizeDisplayMode mode, string expected)
        => Assert.Equal(expected, FileSizeFormatter.Format(bytes, mode, CultureInfo.InvariantCulture));

    [Fact]
    public void Format_UsesDisplayCulture_AndKeepsRawBytesExact()
    {
        CultureInfo culture = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("1,5 KiB", FileSizeFormatter.Format(1536, FileSizeDisplayMode.Iec, culture));
        Assert.Equal("1536 B", FileSizeFormatter.Format(1536, FileSizeDisplayMode.Bytes, culture));
    }

    [Theory]
    [InlineData(FileSizeDisplayMode.Iec)]
    [InlineData(FileSizeDisplayMode.Si)]
    [InlineData(FileSizeDisplayMode.Bytes)]
    public async Task SettingsPage_AppliesAndReloadsUnit_KeepingOtherTransferSettings(FileSizeDisplayMode mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_file_units_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            await service.SaveSettingsAsync(new AppSettings
            {
                FileTransfer = new FileTransferSettings
                {
                    SizeDisplayMode = FileSizeDisplayMode.Bytes,
                    IsFileManagerOnLeft = true,
                    PollingIntervalSeconds = 17
                }
            });
            SettingsViewModel settings = new(service, directory);
            FileTransferSettingsPage page = settings.Categories.Select(c => c.Page).OfType<FileTransferSettingsPage>().Single();
            page.SelectedSizeDisplay = page.SizeDisplayOptions.Single(option => option.Mode == mode);
            Assert.True(await settings.ApplyChangesAsync());

            JsonSettingsService reader = new(path);
            await reader.LoadSettingsAsync();
            SettingsViewModel reopened = new(reader, directory);
            FileTransferSettingsPage reloaded = reopened.Categories.Select(c => c.Page).OfType<FileTransferSettingsPage>().Single();
            Assert.Equal(mode, reloaded.SelectedSizeDisplay.Mode);
            Assert.True(reader.Current.FileTransfer.IsFileManagerOnLeft);
            Assert.Equal(17, reader.Current.FileTransfer.PollingIntervalSeconds);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public Task SettingsApply_UpdatesVisibleFileSize_WithoutReloadingFilesOrChangingSelection() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_file_view_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        Window? window = null;
        RemoteFileManagerViewModel? files = null;
        try
        {
            JsonSettingsService service = new(Path.Combine(directory, "settings.json"));
            files = new RemoteFileManagerViewModel(Guid.NewGuid(), new IdleFileSystem(), new IdleFileTracker(), service);
            RemoteFileItem item = new("example.tar", "/example.tar", false, 1024, DateTimeOffset.UtcNow, "-rw-r--r--");
            files.Items.Add(item);
            files.SelectedItem = item;
            window = new Window { Width = 820, Height = 400, Content = new RemoteFileManagerView { DataContext = files } };
            window.Show();
            window.UpdateLayout();
            HeadlessAvalonia.Pump();
            Assert.Contains("1 KiB", Texts());

            SettingsViewModel settings = new(service, directory);
            FileTransferSettingsPage page = settings.Categories.Select(c => c.Page).OfType<FileTransferSettingsPage>().Single();
            page.SetSizeDisplayMode(FileSizeDisplayMode.Si);
            HeadlessAvalonia.Pump();
            Assert.Contains("1 KiB", Texts());
            Assert.DoesNotContain("1.02 kB", Texts());
            Assert.True(await settings.ApplyChangesAsync());
            HeadlessAvalonia.Pump();
            Assert.Contains("1.02 kB", Texts());

            page.SetSizeDisplayMode(FileSizeDisplayMode.Bytes);
            Assert.True(await settings.ApplyChangesAsync());
            HeadlessAvalonia.Pump();
            Assert.Contains("1024 B", Texts());
            Assert.Same(item, files.SelectedItem);
            Assert.Same(item, Assert.Single(files.Items));
            Assert.Equal(1024, item.Size);

            string?[] Texts() => window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
        }
        finally
        {
            window?.Close();
            if (files != null) await files.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    });

    [Fact]
    public Task DisposedFileManager_IgnoresLaterUnitChanges() => HeadlessAvalonia.RunAsync(async () =>
    {
        RemoteFileManagerViewModel files = new(Guid.NewGuid(), new IdleFileSystem(), new IdleFileTracker());
        await files.DisposeAsync();
        WeakReferenceMessenger.Default.Send(new FileSizeDisplayChangedMessage(FileSizeDisplayMode.Bytes));
        HeadlessAvalonia.Pump();
        Assert.Equal(FileSizeDisplayMode.Iec, files.SizeDisplayMode);
    });

    [Fact]
    public Task SettingsWindow_ExposesAllThreeUnitChoices() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        JsonSettingsService service = new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        SettingsViewModel settings = new(service);
        settings.SelectedCategory = settings.Categories.Single(c => c.Page is FileTransferSettingsPage);
        SettingsWindow window = new(settings);
        try
        {
            window.Show();
            window.UpdateLayout();
            ComboBox selector = window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "FileSizeDisplayComboBox");
            Assert.Equal(new[] { FileSizeDisplayMode.Iec, FileSizeDisplayMode.Si, FileSizeDisplayMode.Bytes },
                selector.Items.OfType<FileSizeDisplayOption>().Select(option => option.Mode));
            selector.SelectedIndex = 2;
            Assert.Equal(FileSizeDisplayMode.Bytes, ((FileTransferSettingsPage)settings.SelectedCategory.Page).SelectedSizeDisplay.Mode);
        }
        finally { window.Close(); }
    });

    private sealed class IdleFileSystem : IRemoteFileSystem
    {
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RemoteFileItem>>([]);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdleFileTracker : ILocalFileTracker
    {
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged { add { } remove { } }
        public event EventHandler<string>? FileUntracked { add { } remove { } }
        public string GetLocalCachePath(Guid sessionId, string remotePath) => remotePath;
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
