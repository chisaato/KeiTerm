using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;

namespace Kei.Term.Tests;

public class TerminalFileManagerLifecycleTests
{
    [Fact]
    public Task BackgroundAttachment_WithLeftDocking_InitializesTheBoundViewOnUiThread() => HeadlessAvalonia.RunAsync(async () =>
    {
        UiDesignSystemService.Apply();
        TerminalTabViewModel tab = new("files", "monospace", 14);
        IdleSession session = new();
        tab.AttachSession(session);
        TerminalConnectionView view = new() { DataContext = tab };
        Window window = new() { Width = 900, Height = 700, Content = view };
        window.Show();
        HeadlessAvalonia.Pump();
        FixedSettingsService settings = new(new AppSettings());
        settings.Current.FileTransfer.IsFileManagerOnLeft = true;
        settings.Current.FileTransfer.CacheDirectory = Path.Combine(Path.GetTempPath(), "keiterm-tests", Guid.NewGuid().ToString("N"));
        TerminalTabConnectionTarget target = new(tab, settings, null, null);
        RecordingFileSystem fileSystem = new();
        try
        {
            await Task.Run(() => target.AttachFileSystemAsync(fileSystem, session));
            Assert.True(tab.IsFileManagerOnLeft);
            Assert.NotNull(tab.FileManager);
            Assert.Equal("/", tab.FileManager.CurrentPath);
            Assert.Single(tab.FileManager.Items);
        }
        finally
        {
            window.Close();
            await tab.DisposeAsync();
        }
        Assert.Equal(1, fileSystem.DisposeCount);
    });

    [Fact]
    public Task ClosingDuringInitialization_DrainsAndDisposesTheLateManager() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = new("closing", "monospace", 14);
        tab.AttachSession(new IdleSession());
        RecordingFileSystem fileSystem = new() { BlockListings = true };
        RecordingTracker tracker = new();
        Task initialize = tab.InitializeFileManagerAsync(fileSystem, tracker);
        await fileSystem.ListingStarted.Task;
        Task closing = tab.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        fileSystem.ResumeListings.TrySetResult();
        await Task.WhenAll(initialize, closing);

        Assert.True(tab.IsDisposed);
        Assert.Null(tab.FileManager);
        Assert.Equal(1, fileSystem.DisposeCount);
        Assert.Equal(1, tracker.DisposeCount);
    });

    [Fact]
    public Task LateFileSystemFromPreviousSession_IsDisposedWithoutReplacingTheNewManager() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = new("reconnected", "monospace", 14);
        IdleSession oldSession = new();
        tab.AttachSession(oldSession);
        await tab.PrepareReconnectAsync();
        tab.AttachSession(new IdleSession());
        RecordingFileSystem currentFileSystem = new();
        RecordingTracker currentTracker = new();
        await tab.InitializeFileManagerAsync(currentFileSystem, currentTracker);
        RemoteFileManagerViewModel currentManager = tab.FileManager!;
        RecordingFileSystem lateFileSystem = new();
        RecordingTracker lateTracker = new();

        await tab.InitializeFileManagerAsync(lateFileSystem, lateTracker, expectedSession: oldSession);

        Assert.Same(currentManager, tab.FileManager);
        Assert.Equal(1, lateFileSystem.DisposeCount);
        Assert.Equal(1, lateTracker.DisposeCount);
        Assert.Equal(0, currentFileSystem.DisposeCount);
        await tab.DisposeAsync();
    });

    [Fact]
    public Task CancelledOldConnection_DoesNotDetachTheReplacementSession() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = new("replacement", "monospace", 14);
        IdleSession oldSession = new();
        tab.AttachSession(oldSession);
        await tab.DetachSessionAsync();
        IdleSession replacement = new();
        tab.AttachSession(replacement);

        await tab.DetachSessionAsync(oldSession);
        await tab.SendCommandAsync("echo connected");

        Assert.Equal(1, replacement.SentCommands);
        await tab.DisposeAsync();
    });

    [Fact]
    public Task CancelledResetWaitingForOldDisposal_CannotOverwriteAManualReconnect() => HeadlessAvalonia.RunAsync(async () =>
    {
        TerminalTabViewModel tab = new("reset", "monospace", 14);
        GatedDisposalSession oldSession = new();
        tab.AttachSession(oldSession);
        TerminalTabConnectionTarget target = new(tab, new FixedSettingsService(), null, null);
        ResolvedSessionConfig oldConfig = new(Guid.NewGuid(), "old", "old.example", 22, "ops", null,
            "xterm-256color", null, null, new Dictionary<string, string>());
        ResolvedSessionConfig newConfig = oldConfig with { Host = "new.example", SessionName = "new" };
        using CancellationTokenSource cancellation = new();
        Task oldReset = target.ResetForReconnectAsync(oldConfig, cancellation.Token);
        await oldSession.DisposalStarted.Task;
        cancellation.Cancel();

        await target.ResetForReconnectAsync(newConfig);
        IdleSession replacement = new();
        tab.AttachSession(replacement);
        tab.MarkConnected();
        await tab.InitializeFileManagerAsync(new RecordingFileSystem(), new RecordingTracker());
        RemoteFileManagerViewModel manager = tab.FileManager!;
        oldSession.ResumeDisposal.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldReset);

        Assert.Equal(newConfig, tab.Config);
        Assert.Equal(ConnectionState.Connected, tab.State);
        Assert.Same(manager, tab.FileManager);
        await tab.SendCommandAsync("echo new");
        Assert.Equal(1, replacement.SentCommands);
        await tab.DisposeAsync();
    });

    private sealed class GatedDisposalSession : IdleSession
    {
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask DisposeAsync()
        {
            DisposalStarted.TrySetResult();
            await ResumeDisposal.Task;
        }
    }

    private class IdleSession : ITerminalSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public int SentCommands { get; private set; }
        public event Action<byte[]>? OutputReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) { SentCommands++; return Task.CompletedTask; }
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        public bool BlockListings { get; init; }
        public TaskCompletionSource ListingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeListings { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public bool IsConnected => true;
        public string WorkingDirectory => "/";
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
        {
            ListingStarted.TrySetResult();
            // 模拟不支持中途取消的远端请求，确保完成后的归属检查仍能阻止回挂。
            if (BlockListings) await ResumeListings.Task;
            return [new RemoteFileItem("file.txt", "/file.txt", false, 1, DateTimeOffset.UnixEpoch, "-rw-r--r--")];
        }
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => Task.FromResult(Stream.Null);
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default) => Task.FromResult<RemoteFileItem?>(null);
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class RecordingTracker : ILocalFileTracker
    {
        public int DisposeCount { get; private set; }
        public event EventHandler<LocalFileChangedEventArgs>? FileChanged { add { } remove { } }
        public event EventHandler<string>? FileUntracked { add { } remove { } }
        public string GetLocalCachePath(Guid sessionId, string remotePath) => remotePath;
        public bool IsTracking(string localFilePath) => false;
        public Task CheckForChangesAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
