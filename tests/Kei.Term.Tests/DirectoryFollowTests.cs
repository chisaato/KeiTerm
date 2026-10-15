using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;

namespace Kei.Term.Tests;

public class DirectoryFollowTests
{
    [Fact]
    public Task DirectoryReportedBeforePanelExists_IsUsedWhenPanelOpens() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = new("early", "monospace", 14);
        tab.BindConfig(new ResolvedSessionConfig(Guid.NewGuid(), "early", "host", 22, "ops", null,
            "xterm-256color", null, null, new Dictionary<string, string>(), CwdFollow: CwdFollowMode.OnceOnOpen));
        tab.AttachSession(new Session());
        try
        {
            Report(tab, "/early");
            HeadlessAvalonia.Pump();
            tab.MarkConnected();
            await tab.InitializeFileManagerAsync(new FileSystem(), new Tracker());
            Assert.Equal("/early", tab.FileManager!.TerminalDirectory);
            Assert.Equal("/", tab.FileManager.CurrentPath);
            tab.IsFileManagerVisible = true;
            HeadlessAvalonia.WaitUntil(() => tab.FileManager.CurrentPath == "/early");
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task ForeignHostSignal_CancelsPendingAutomaticNavigation() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = await CreateTabAsync(CwdFollowMode.Always);
        try
        {
            Report(tab, "/pending");
            HeadlessAvalonia.WaitUntil(() => tab.FileManager!.HasTerminalDirectory);
            Report(tab, "/foreign", "other");
            HeadlessAvalonia.WaitUntil(() => !tab.FileManager!.HasTerminalDirectory);
            await Task.Delay(400);
            Assert.Equal("/", tab.FileManager!.CurrentPath);
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task ManualSyncButton_NavigatesWithFollowDisabled_AndRejectsForeignHost() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel tab = await CreateTabAsync(CwdFollowMode.Off);
        RemoteFileManagerViewModel manager = tab.FileManager!;
        Window window = new() { Width = 420, Height = 600, Content = new RemoteFileManagerView { DataContext = manager } };
        window.Show();
        try
        {
            HeadlessAvalonia.Pump();
            Button sync = window.GetVisualDescendants().OfType<Button>().Single(button => button.Command == manager.JumpToTerminalDirectoryCommand);
            Assert.False(sync.IsEnabled);
            Report(tab, "/work%20space");
            HeadlessAvalonia.WaitUntil(() => manager.HasTerminalDirectory);
            Assert.Equal("/", manager.CurrentPath);
            Assert.True(sync.IsEnabled);
            await manager.JumpToTerminalDirectoryCommand.ExecuteAsync(null);
            Assert.Equal("/work space", manager.CurrentPath);
            Assert.Contains(manager.Items, item => item.Name == "work space.txt");

            Report(tab, "/foreign", "other");
            HeadlessAvalonia.WaitUntil(() => !manager.HasTerminalDirectory);
            Assert.False(sync.IsEnabled);
            Assert.Equal("/work space", manager.CurrentPath);
        }
        finally { window.Close(); await tab.DisposeAsync(); }
    });

    [Fact]
    public Task Always_FollowsOnlyWhileVisible() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = await CreateTabAsync(CwdFollowMode.Always);
        try
        {
            RemoteFileManagerViewModel manager = tab.FileManager!;
            Report(tab, "/first");
            HeadlessAvalonia.WaitUntil(() => manager.HasTerminalDirectory);
            HeadlessAvalonia.WaitUntil(() => manager.CurrentPath == "/first");
            tab.IsFileManagerVisible = false;
            Report(tab, "/hidden");
            HeadlessAvalonia.WaitUntil(() => manager.TerminalDirectory == "/hidden");
            Assert.Equal("/first", manager.CurrentPath);
            tab.IsFileManagerVisible = true;
            HeadlessAvalonia.WaitUntil(() => manager.CurrentPath == "/hidden");

        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task OnceOnOpen_WaitsForFirstSignal_DoesNotRepeatOnReopen_AndResetsOnReconnect() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = await CreateTabAsync(CwdFollowMode.OnceOnOpen);
        try
        {
            RemoteFileManagerViewModel manager = tab.FileManager!;
            Report(tab, "/first");
            HeadlessAvalonia.WaitUntil(() => manager.CurrentPath == "/first" && !manager.IsLoading);
            // 导航完成后的续体推进 OnceOnOpen 状态。
            HeadlessAvalonia.Pump();
            Report(tab, "/second");
            HeadlessAvalonia.WaitUntil(() => manager.TerminalDirectory == "/second");
            tab.IsFileManagerVisible = false;
            tab.IsFileManagerVisible = true;
            await Task.Delay(400);
            Assert.Equal("/first", manager.CurrentPath);

            await tab.PrepareReconnectAsync();
            tab.AttachSession(new Session());
            tab.MarkConnected();
            await tab.InitializeFileManagerAsync(new FileSystem(), new Tracker());
            tab.IsFileManagerVisible = true;
            Report(tab, "/reconnected");
            HeadlessAvalonia.WaitUntil(() => tab.FileManager!.CurrentPath == "/reconnected");
        }
        finally { await tab.DisposeAsync(); }
    });

    [Theory]
    [InlineData(TerminalMultiplexerMode.Auto, "kei;tmux;host;bash;/tmux", "/tmux")]
    [InlineData(TerminalMultiplexerMode.Screen, "kei;screen;host;/screen", "/screen")]
    public Task MultiplexerTitleOutput_FollowsSelectedBackend(TerminalMultiplexerMode mode, string title, string expected)
        => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = await CreateTabAsync(CwdFollowMode.Always, mode);
        try
        {
            tab.Terminal.WriteOutput(Encoding.UTF8.GetBytes($"\u001b]2;{title}\u0007"));
            HeadlessAvalonia.WaitUntil(() => tab.FileManager!.CurrentPath == expected);
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task DisabledCompatibility_StillAcceptsNativeOsc7_AndClosingCancelsPendingFollow() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = await CreateTabAsync(CwdFollowMode.Always, TerminalMultiplexerMode.Off);
        try
        {
            tab.Terminal.WriteOutput(Encoding.UTF8.GetBytes("\u001b]2;host:/ignored\u0007"));
            HeadlessAvalonia.Pump();
            Assert.False(tab.FileManager!.HasTerminalDirectory);
            Report(tab, "/native");
            HeadlessAvalonia.WaitUntil(() => tab.FileManager.CurrentPath == "/native");
            Report(tab, "/cancelled");
            HeadlessAvalonia.WaitUntil(() => tab.FileManager.TerminalDirectory == "/cancelled");
            tab.IsFileManagerVisible = false;
            await Task.Delay(400);
            Assert.Equal("/native", tab.FileManager.CurrentPath);
        }
        finally { await tab.DisposeAsync(); }
    });

    [Fact]
    public Task LateListing_CannotOverwriteNewerNavigation() => HeadlessAvalonia.RunAsync(async () =>
    {
        FileSystem fileSystem = new();
        TerminalTabViewModel tab = await CreateTabAsync(CwdFollowMode.Off, fileSystem: fileSystem);
        try
        {
            RemoteFileManagerViewModel manager = tab.FileManager!;
            fileSystem.BlockSlowListing = true;
            Task slow = manager.NavigateToTerminalDirectoryAsync("/slow");
            await fileSystem.SlowListingStarted.Task;
            await manager.NavigateToTerminalDirectoryAsync("/fast");
            fileSystem.ResumeSlowListing.TrySetResult();
            await slow;
            Assert.Equal("/fast", manager.CurrentPath);
            Assert.Contains(manager.Items, item => item.Name == "fast.txt");
            Assert.DoesNotContain(manager.Items, item => item.Name == "slow.txt");
            Assert.False(manager.IsLoading);
        }
        finally { fileSystem.ResumeSlowListing.TrySetResult(); await tab.DisposeAsync(); }
    });

    private static async Task<TerminalTabViewModel> CreateTabAsync(CwdFollowMode follow,
        TerminalMultiplexerMode multiplexer = TerminalMultiplexerMode.Auto, FileSystem? fileSystem = null)
    {
        TerminalTabViewModel tab = new("follow", "monospace", 14);
        tab.BindConfig(new ResolvedSessionConfig(Guid.NewGuid(), "follow", "host", 22, "ops", null,
            "xterm-256color", null, null, new Dictionary<string, string>(), CwdFollow: follow, MultiplexerMode: multiplexer));
        tab.AttachSession(new Session());
        tab.MarkConnected();
        await tab.InitializeFileManagerAsync(fileSystem ?? new FileSystem(), new Tracker());
        tab.IsFileManagerVisible = true;
        return tab;
    }

    private static void Report(TerminalTabViewModel tab, string path, string host = "host")
        => tab.Terminal.WriteOutput(Encoding.UTF8.GetBytes($"\u001b]7;file://{host}{path}\u0007"));

    private sealed class Session : ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public event Action<byte[]>? OutputReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FileSystem : IRemoteFileSystem
    {
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public bool BlockSlowListing { get; set; }
        public TaskCompletionSource SlowListingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeSlowListing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            if (BlockSlowListing && path == "/slow")
            {
                SlowListingStarted.TrySetResult();
                // 模拟忽略取消的远端请求。
                await ResumeSlowListing.Task;
            }
            return [new RemoteFileItem(path.Trim('/').Replace('/', '_') + ".txt", path + "/file.txt", false, 1, DateTimeOffset.UnixEpoch, "-rw-r--r--")];
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

    private sealed class Tracker : ILocalFileTracker
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
