using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.BatchEdit;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.App.Views.Settings;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

public class UiAuditFixesTests
{
    [Fact]
    public async Task RemoteDirectoryNode_LoadFailure_KeepsExpandable_SetsError_AndAllowsRetry()
    {
        var fs = new StubFileSystem { FailNextList = true };
        string? failedPath = null;
        Exception? failedEx = null;
        var node = new RemoteDirectoryNodeViewModel(
            "logs",
            "/var/logs",
            fs,
            () => false,
            hasDummyChild: true,
            onLoadFailed: (path, ex) =>
            {
                failedPath = path;
                failedEx = ex;
            });

        await node.LoadChildrenAsync();

        Assert.True(node.HasChildren);
        Assert.False(node.IsExpanded);
        Assert.NotNull(node.LoadError);
        Assert.Equal("/var/logs", failedPath);
        Assert.NotNull(failedEx);

        fs.FailNextList = false;
        fs.ItemsToReturn = [new RemoteFileItem("nginx", "/var/logs/nginx", true, 0, DateTimeOffset.UtcNow, "drwxr-xr-x")];
        await node.LoadChildrenAsync();

        Assert.Null(node.LoadError);
        Assert.Single(node.Children);
        Assert.Equal("nginx", node.Children[0].Name);
    }

    [Fact]
    public async Task RemoteFileManager_NavigateFailure_PreservesCurrentPath_ShowsErrorBanner_AndGatesCommands()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keiterm_rfm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new JsonSettingsService(Path.Combine(dir, "settings.json"));
            var fs = new StubFileSystem
            {
                ItemsToReturn = [new RemoteFileItem("app.log", "/home/app.log", false, 128, DateTimeOffset.UtcNow, "-rw-r--r--")]
            };
            var vm = new RemoteFileManagerViewModel(Guid.NewGuid(), fs, new StubLocalTracker(), settings);

            await vm.NavigateToPathAsync("/home");
            Assert.Equal("/home", vm.CurrentPath);
            RemoteFileItem appLog = Assert.Single(vm.Items, i => i.Name == "app.log");
            vm.SelectedItem = appLog;
            Assert.True(vm.DeleteItemCommand.CanExecute(null));

            fs.FailNextList = true;
            await vm.NavigateToPathAsync("/root");

            Assert.Equal("/home", vm.CurrentPath);
            Assert.True(vm.HasOperationError);
            Assert.False(string.IsNullOrWhiteSpace(vm.OperationErrorMessage));
            Assert.Equal("/root", vm.RetryTargetPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BatchSessionEdit_SaveFailure_DoesNotMutateLiveSessions_AndAllowsRetry()
    {
        var session = new SessionNode
        {
            Name = "web-01",
            Host = "192.0.2.10",
            Port = 22,
            Username = "original"
        };
        bool shouldThrow = true;
        int saveCalls = 0;
        IReadOnlyCollection<TreeNodeBase>? savedNodes = null;
        var vm = new BatchSessionEditViewModel(
            [session],
            [session.Id],
            new BatchEditContext([], new AppSettings()),
            nodes =>
            {
                saveCalls++;
                if (shouldThrow) throw new InvalidOperationException("模拟保存失败");
                savedNodes = nodes;
                return Task.CompletedTask;
            });

        BatchFieldEditorViewModel usernameField = vm.Fields.First(f => f.IsText && f.Definition.Field == SessionBatchFields.Username);
        usernameField.IsEnabled = true;
        usernameField.Text = "deploy";

        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(1, saveCalls);
        Assert.Equal("original", session.Username);
        Assert.Equal(0, vm.ChangedCount);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusText));

        shouldThrow = false;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(2, saveCalls);
        Assert.Equal(1, vm.ChangedCount);
        Assert.Equal("deploy", session.Username);
        Assert.NotNull(savedNodes);
        Assert.Equal("deploy", savedNodes!.OfType<SessionNode>().Single().Username);
    }

    [Fact]
    public void DuplicateSession_UpdatesCanConfirm_AndSupportsSerialPort()
    {
        var sshSession = new SessionNode { Name = "prod", Host = "192.0.2.20", Port = 22, Protocol = SessionProtocols.Ssh };
        var sshVm = new DuplicateSessionViewModel(sshSession, [sshSession]);
        Assert.False(sshVm.IsSerialSession);
        Assert.True(sshVm.ConfirmCommand.CanExecute(null));

        sshVm.Name = " ";
        Assert.False(sshVm.ConfirmCommand.CanExecute(null));
        sshVm.Name = "prod-copy";
        Assert.True(sshVm.ConfirmCommand.CanExecute(null));

        var serialSession = new SessionNode
        {
            Name = "switch-console",
            Protocol = SessionProtocols.Serial,
            Overrides = new SessionOverrides { Serial = new SerialOptions { PortName = "/dev/ttyUSB0" } }
        };
        var serialVm = new DuplicateSessionViewModel(serialSession, [serialSession]);
        Assert.True(serialVm.IsSerialSession);
        Assert.Equal("/dev/ttyUSB0", serialVm.SerialPortName);
        Assert.True(serialVm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public Task FileManagerSettingsPageView_ClearCacheDoesNotOverlap_AndBlankDirResolvesStandardPath() => HeadlessAvalonia.RunAsync(() =>
    {
        UiDesignSystemService.Apply();
        var page = new FileManagerSettingsPage { CacheDirectory = "   " };
        var view = new FileManagerSettingsPageView { DataContext = page };
        var window = new Window { Width = 900, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            HeadlessAvalonia.Pump();

            Button clearBtn = view.GetVisualDescendants().OfType<Button>()
                .First(b => b.Command == page.ClearCacheDirectoryCommand);
            TextBox dirBox = view.GetVisualDescendants().OfType<TextBox>()
                .First(t => t.Text == page.CacheDirectory);

            Assert.True(dirBox.Bounds.Width > 0 && clearBtn.Bounds.Width > 0);
            Assert.True(dirBox.Bounds.Right <= clearBtn.Bounds.Left + 1,
                $"缓存路径框 ({dirBox.Bounds}) 与清理按钮 ({clearBtn.Bounds}) 重叠");

            page.ClearCacheDirectoryCommand.Execute(null);
            Assert.True(page.HasCacheStatus);
            Assert.False(page.IsCacheStatusError);
            Assert.False(string.IsNullOrWhiteSpace(page.CacheStatusMessage));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public async Task SettingsViewModel_InvalidTabBarTemplate_AutoNavigatesToTabBarOnApply()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keiterm_settings_tabbar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new JsonSettingsService(Path.Combine(dir, "settings.json"));
            var vm = new SettingsViewModel(service, dir);
            TerminalSettingsPage terminal = vm.Categories
                .Select(c => c.Page)
                .OfType<TabBarSettingsProxyPage>()
                .Single()
                .Terminal;
            terminal.TabTitleTemplate = "{unclosed";
            Assert.True(terminal.HasTitleTemplateError);

            vm.SelectedCategory = vm.Categories.First(c => c.Page is not TabBarSettingsProxyPage);
            bool saved = await vm.ApplyChangesAsync();

            Assert.False(saved);
            Assert.IsType<TabBarSettingsProxyPage>(vm.SelectedCategory?.Page);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProxyEdit_ExcludesHttpForNew_DisablesSaveForLegacyHttp_AndExcludesHttpFromSessionFirewall()
    {
        var newProxy = new ProxyEditViewModel(null);
        Assert.DoesNotContain(ProxyConfigKind.Http, newProxy.Types);
        Assert.True(newProxy.CanSave);

        var httpProfile = new ProxyProfile
        {
            Id = Guid.NewGuid(),
            Name = "legacy-http",
            Config = new HttpProxyConfig("192.0.2.50", 8080)
        };
        var legacyVm = new ProxyEditViewModel(httpProfile);
        Assert.Contains(ProxyConfigKind.Http, legacyVm.Types);
        Assert.False(legacyVm.CanSave);

        var socksProfile = new ProxyProfile
        {
            Id = Guid.NewGuid(),
            Name = "corp-socks",
            Config = new Socks5ProxyConfig("192.0.2.51", 1080)
        };
        var sessionVm = new SessionEditViewModel(null, null, [], new AppSettings(), proxies: [httpProfile, socksProfile]);
        Assert.DoesNotContain(sessionVm.FirewallOptions, o => o.Id == httpProfile.Id);
        Assert.Contains(sessionVm.FirewallOptions, o => o.Id == socksProfile.Id);
    }

    [Fact]
    public async Task KnownHostsManager_ToggleRevokePreservesSelection_AndFiltersByLocalizedStatus()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keiterm_kh_audit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var repo = new SqliteKnownHostRepository($"Data Source={Path.Combine(dir, "kh.db")}");
            await repo.InitializeAsync();
            string importPath = Path.Combine(dir, "known_hosts");
            await File.WriteAllTextAsync(importPath, """
                alpha.example ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINNOCjYfYNvN56zScDjq/dDohnNs4wdauZTYtC54QykV
                beta.example ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIN/OYsUEL5deLJb9X+lVVMyeUpEed4y/0VWHOn369umv
                """);

            var vm = new KnownHostsManagerViewModel(repo) { ImportPathProvider = () => importPath };
            await vm.ImportCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.Entries.Count);

            KnownHostRowViewModel alpha = vm.Entries.Single(h => h.HostDisplay == "alpha.example");
            vm.SelectedEntry = alpha;
            await vm.ToggleRevokeCommand.ExecuteAsync(null);

            Assert.NotNull(vm.SelectedEntry);
            Assert.Equal("alpha.example", vm.SelectedEntry!.HostDisplay);
            Assert.True(vm.SelectedEntry.IsRevoked);

            vm.FilterText = vm.SelectedEntry.StatusText;
            Assert.Single(vm.Entries);
            Assert.Equal("alpha.example", vm.Entries[0].HostDisplay);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class StubFileSystem : IRemoteFileSystem
    {
        public bool FailNextList { get; set; }
        public IReadOnlyList<RemoteFileItem> ItemsToReturn { get; set; } = [];
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            if (FailNextList) throw new IOException($"无法访问 {path}");
            return Task.FromResult(ItemsToReturn);
        }
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubLocalTracker : ILocalFileTracker
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
