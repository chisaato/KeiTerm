using System.Linq;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Transfers;
using Kei.Term.Infrastructure.Settings;

namespace Kei.Term.Tests;

public class TransferSettingsTests
{
    [Fact]
    public void NewSettings_UseSpecDefaults()
    {
        FileTransferSettings settings = new();
        Assert.Equal(3, settings.Concurrency);
        Assert.Equal(string.Empty, settings.DefaultDownloadDirectory);
        Assert.Equal(TransferConflictPolicy.Ask, settings.DefaultConflictPolicy);
        Assert.False(settings.ResumeEnabled);
        Assert.True(settings.AutoRetryEnabled);
        Assert.Equal(3, settings.RetryCount);
        Assert.Equal(RetryBackoffMode.Exponential, settings.BackoffMode);
        Assert.Equal(2, settings.InitialRetryIntervalSeconds);
        Assert.Equal(60, settings.MaxRetryIntervalSeconds);
        Assert.Equal(3, settings.EffectiveConcurrency);
    }

    [Fact]
    public void ClampConcurrency_DoesNotRewriteStoredValue()
    {
        FileTransferSettings settings = new() { Concurrency = 0 };
        Assert.Equal(0, settings.Concurrency);
        Assert.Equal(1, settings.EffectiveConcurrency);
        Assert.Equal(1, FileTransferSettings.ClampConcurrency(int.MinValue));
        Assert.Equal(1, FileTransferSettings.ClampConcurrency(1));
        Assert.Equal(int.MaxValue, FileTransferSettings.ClampConcurrency(int.MaxValue));
    }

    [Fact]
    public async Task MissingFields_LoadAsDefaults_AndKeepExistingFields()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, """
                {
                  "FontSize": 15,
                  "FileTransfer": {
                    "PollingIntervalSeconds": 17,
                    "CacheDirectory": "/tmp/cache"
                  }
                }
                """);

            JsonSettingsService reader = new(path);
            AppSettings loaded = await reader.LoadSettingsAsync();

            Assert.Equal(15, loaded.FontSize);
            Assert.Equal(17, loaded.FileTransfer.PollingIntervalSeconds);
            Assert.Equal("/tmp/cache", loaded.FileTransfer.CacheDirectory);
            Assert.Equal(3, loaded.FileTransfer.Concurrency);
            Assert.Equal(string.Empty, loaded.FileTransfer.DefaultDownloadDirectory);
            Assert.Equal(TransferConflictPolicy.Ask, loaded.FileTransfer.DefaultConflictPolicy);
            Assert.False(loaded.FileTransfer.ResumeEnabled);
            Assert.True(loaded.FileTransfer.AutoRetryEnabled);
            Assert.Equal(3, loaded.FileTransfer.RetryCount);
            Assert.Equal(RetryBackoffMode.Exponential, loaded.FileTransfer.BackoffMode);
            Assert.Equal(2, loaded.FileTransfer.InitialRetryIntervalSeconds);
            Assert.Equal(60, loaded.FileTransfer.MaxRetryIntervalSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NonDefaultValues_RoundTripWithoutClampingConcurrency()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService writer = new(path);
            await writer.SaveSettingsAsync(new AppSettings
            {
                FileTransfer = new FileTransferSettings
                {
                    Concurrency = 0,
                    DefaultDownloadDirectory = "/data/dl",
                    DefaultConflictPolicy = TransferConflictPolicy.OverwriteIfOlder,
                    ResumeEnabled = true,
                    AutoRetryEnabled = false,
                    RetryCount = 1,
                    BackoffMode = RetryBackoffMode.Fixed,
                    InitialRetryIntervalSeconds = 5,
                    MaxRetryIntervalSeconds = 9,
                    PollingIntervalSeconds = 17,
                },
            });

            JsonSettingsService reader = new(path);
            FileTransferSettings loaded = (await reader.LoadSettingsAsync()).FileTransfer;
            Assert.Equal(0, loaded.Concurrency);
            Assert.Equal(1, loaded.EffectiveConcurrency);
            Assert.Equal("/data/dl", loaded.DefaultDownloadDirectory);
            Assert.Equal(TransferConflictPolicy.OverwriteIfOlder, loaded.DefaultConflictPolicy);
            Assert.True(loaded.ResumeEnabled);
            Assert.False(loaded.AutoRetryEnabled);
            Assert.Equal(1, loaded.RetryCount);
            Assert.Equal(RetryBackoffMode.Fixed, loaded.BackoffMode);
            Assert.Equal(5, loaded.InitialRetryIntervalSeconds);
            Assert.Equal(9, loaded.MaxRetryIntervalSeconds);
            Assert.Equal(17, loaded.PollingIntervalSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FileTransferSettingsPage_ReorderingAndEnablingAlgorithmsAndFormats()
    {
        FileTransferSettingsPage page = new();
        page.InitializeTransferPreferences(
            verifyHashEnabled: true,
            hashOrder: [TransferHashAlgorithm.Sha1, TransferHashAlgorithm.Sha256],
            archiveDirectoriesEnabled: true,
            archiveOrder: [TransferArchiveFormat.Tar, TransferArchiveFormat.Zstd]);

        // 初始顺序与启用状态
        Assert.Equal(3, page.HashAlgorithms.Count);
        Assert.Equal(TransferHashAlgorithm.Sha1, page.HashAlgorithms[0].Value);
        Assert.True(page.HashAlgorithms[0].IsEnabled);
        Assert.Equal(TransferHashAlgorithm.Sha256, page.HashAlgorithms[1].Value);
        Assert.True(page.HashAlgorithms[1].IsEnabled);
        Assert.Equal(TransferHashAlgorithm.Md5, page.HashAlgorithms[2].Value);
        Assert.False(page.HashAlgorithms[2].IsEnabled);

        // 下移 Sha1
        page.SelectedHashAlgorithm = page.HashAlgorithms[0];
        page.MoveDownHashAlgorithmCommand.Execute(null);
        Assert.Equal(TransferHashAlgorithm.Sha256, page.HashAlgorithms[0].Value);
        Assert.Equal(TransferHashAlgorithm.Sha1, page.HashAlgorithms[1].Value);

        // 上移 Sha1 回去
        page.MoveUpHashAlgorithmCommand.Execute(null);
        Assert.Equal(TransferHashAlgorithm.Sha1, page.HashAlgorithms[0].Value);

        // 收集顺序（只收录 IsEnabled 为 true 的）
        List<TransferHashAlgorithm> collectedHashes = page.CollectHashAlgorithmOrder();
        Assert.Equal([TransferHashAlgorithm.Sha1, TransferHashAlgorithm.Sha256], collectedHashes);

        // 禁用 Sha1，启用 Md5
        page.HashAlgorithms[0].IsEnabled = false;
        page.HashAlgorithms[2].IsEnabled = true;
        Assert.Equal([TransferHashAlgorithm.Sha256, TransferHashAlgorithm.Md5], page.CollectHashAlgorithmOrder());

        // 归档格式上下移与收集
        Assert.Equal(3, page.ArchiveFormats.Count);
        Assert.Equal(TransferArchiveFormat.Tar, page.ArchiveFormats[0].Value);
        page.SelectedArchiveFormat = page.ArchiveFormats[0];
        page.MoveDownArchiveFormatCommand.Execute(null);
        Assert.Equal(TransferArchiveFormat.Zstd, page.ArchiveFormats[0].Value);
        Assert.Equal(TransferArchiveFormat.Tar, page.ArchiveFormats[1].Value);

        page.ArchiveFormats[2].IsEnabled = true; // Gzip
        List<TransferArchiveFormat> collectedArchives = page.CollectArchiveFormatOrder();
        Assert.Equal([TransferArchiveFormat.Zstd, TransferArchiveFormat.Tar, TransferArchiveFormat.Gzip], collectedArchives);
    }

    [Fact]
    public void ResolveDownloadDirectory_BlankUsesSystem_ConfiguredWins()
    {
        FileTransferSettings blank = new() { DefaultDownloadDirectory = "  " };
        Assert.Equal(DownloadDirectoryResolver.Resolve(null), blank.ResolveDownloadDirectory());
        Assert.False(string.IsNullOrWhiteSpace(blank.ResolveDownloadDirectory()));

        FileTransferSettings configured = new() { DefaultDownloadDirectory = "  /data/dl  " };
        Assert.Equal("/data/dl", configured.ResolveDownloadDirectory());
    }

    [Fact]
    public void DownloadDirectory_XdgOverridesFallback_AndConfiguredIgnoresXdg()
    {
        string home = "/home/kei";
        string text = """
            # comment
            XDG_DESKTOP_DIR="$HOME/Desktop"
            XDG_DOWNLOAD_DIR="$HOME/A"
            XDG_DOWNLOAD_DIR="${HOME}/My Downloads"
            """;

        Assert.Equal("/home/kei/My Downloads", DownloadDirectoryResolver.Resolve("  ", home, text));
        Assert.Equal("/mnt/dl", DownloadDirectoryResolver.Resolve(null, home, "XDG_DOWNLOAD_DIR=\"/mnt/dl\"\n"));
        Assert.Equal(
            Path.Combine(home, "Downloads"),
            DownloadDirectoryResolver.Resolve(null, home, "XDG_DOCUMENTS_DIR=\"$HOME/Documents\"\n"));
        Assert.Equal("/var/out", DownloadDirectoryResolver.Resolve("/var/out", home, "XDG_DOWNLOAD_DIR=\"/mnt/dl\"\n"));
        Assert.Equal(Path.Combine(home, "dl"), DownloadDirectoryResolver.Resolve(null, home, "XDG_DOWNLOAD_DIR=\"dl\"\n"));
        Assert.Equal("Downloads", DownloadDirectoryResolver.Resolve(null, "", null));
    }

    [Fact]
    public void DownloadDirectory_DoesNotExpandHomePrefixOfLongerName()
    {
        string resolved = DownloadDirectoryResolver.Resolve(null, "/home/kei", "XDG_DOWNLOAD_DIR=\"$HOMEDIR/nope\"\n");
        Assert.Equal(Path.Combine("/home/kei", "$HOMEDIR/nope"), resolved);
    }

    [Fact]
    public Task FileTransferSettingsPage_RoundTripsThroughViewModel_AndBindsAllControls() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        string directory = CreateTempDirectory();
        SettingsWindow? window = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            JsonSettingsService service = new(path);
            await service.SaveSettingsAsync(new AppSettings
            {
                FileTransfer = new FileTransferSettings
                {
                    Concurrency = 5,
                    DefaultDownloadDirectory = "/custom/downloads",
                    DefaultConflictPolicy = TransferConflictPolicy.AutoRename,
                    ResumeEnabled = true,
                    AutoRetryEnabled = true,
                    RetryCount = 4,
                    BackoffMode = RetryBackoffMode.Fixed,
                    InitialRetryIntervalSeconds = 7,
                    MaxRetryIntervalSeconds = 88,
                },
            });

            SettingsViewModel model = new(service, directory);
            window = new SettingsWindow(model);
            window.Show();

            model.SelectedCategory = model.Categories.Single(c => c.Page is FileTransferSettingsPage);
            window.UpdateLayout();
            FileTransferSettingsPage page = (FileTransferSettingsPage)model.SelectedCategory.Page;

            Assert.Equal(5, page.Concurrency);
            Assert.Equal("/custom/downloads", page.DefaultDownloadDirectory);
            Assert.Equal(TransferConflictPolicy.AutoRename, page.SelectedConflictPolicy.Policy);
            Assert.True(page.ResumeEnabled);
            Assert.True(page.AutoRetryEnabled);
            Assert.Equal(4, page.RetryCount);
            Assert.Equal(RetryBackoffMode.Fixed, page.SelectedBackoffMode.Mode);
            Assert.Equal(7, page.InitialRetryIntervalSeconds);
            Assert.Equal(88, page.MaxRetryIntervalSeconds);
            Assert.True(page.VerifyHashEnabled);
            Assert.False(page.ArchiveDirectoriesEnabled);

            // 修改值并保存
            page.Concurrency = 8;
            page.DefaultDownloadDirectory = "/new/dir";
            page.SetConflictPolicy(TransferConflictPolicy.OverwriteIfOlder);
            page.ResumeEnabled = false;
            page.AutoRetryEnabled = false;
            page.RetryCount = 2;
            page.SetBackoffMode(RetryBackoffMode.Exponential);
            page.InitialRetryIntervalSeconds = 3;
            page.MaxRetryIntervalSeconds = 45;
            page.VerifyHashEnabled = false;
            page.ArchiveDirectoriesEnabled = true;

            Assert.True(await model.ApplyChangesAsync());

            JsonSettingsService reader = new(path);
            FileTransferSettings saved = (await reader.LoadSettingsAsync()).FileTransfer;
            Assert.Equal(8, saved.Concurrency);
            Assert.Equal("/new/dir", saved.DefaultDownloadDirectory);
            Assert.Equal(TransferConflictPolicy.OverwriteIfOlder, saved.DefaultConflictPolicy);
            Assert.False(saved.ResumeEnabled);
            Assert.False(saved.AutoRetryEnabled);
            Assert.Equal(2, saved.RetryCount);
            Assert.Equal(RetryBackoffMode.Exponential, saved.BackoffMode);
            Assert.Equal(3, saved.InitialRetryIntervalSeconds);
            Assert.Equal(45, saved.MaxRetryIntervalSeconds);
            Assert.False(saved.VerifyHashEnabled);
            Assert.True(saved.ArchiveDirectoriesEnabled);
        }
        finally
        {
            window?.Close();
            Directory.Delete(directory, recursive: true);
        }
    });

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "keiterm_transfer_settings_" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        return directory;
    }
}
