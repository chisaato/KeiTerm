using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using RoyalTerminal.Terminal;

namespace Kei.Term.App.ViewModels;

public partial class TerminalTabViewModel
{
    private string? _terminalWorkingDirectory;
    private TerminalDirectoryTracker _directoryTracker = new();
    private CancellationTokenSource? _directoryFollowCancellation;
    private Task _directoryFollowTask = Task.CompletedTask;
    private bool _hasFollowedDirectory;
    private long _directoryConnectionGeneration;
    private TerminalMultiplexerMode _directoryMultiplexerMode = TerminalMultiplexerMode.Auto;
    private CwdFollowMode _directoryFollowMode;

    private void ResetDirectoryFollow()
    {
        CancelDirectoryFollow();
        _directoryConnectionGeneration++;
        _directoryTracker = new TerminalDirectoryTracker();
        _hasFollowedDirectory = false;
        PublishTerminalDirectory();
    }

    private void OnTerminalShellIntegrationEvent(object? sender, TerminalShellIntegrationEventArgs args)
    {
        string? directory = args.Value.WorkingDirectory;
        string? host = args.Value.Host;
        long generation = _directoryConnectionGeneration;
        // 库的事件也可能由解析线程发出；所有绑定状态、导航和设置读取统一回 UI 线程。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyTerminalDirectory(directory, host, generation));
            return;
        }
        ApplyTerminalDirectory(directory, host, generation);
    }

    private void ApplyTerminalDirectory(string? directory, string? host, long generation)
    {
        if (IsDisposed || generation != _directoryConnectionGeneration || _session == null
            || State is ConnectionState.Disconnected or ConnectionState.Error) return;
        if (!_directoryTracker.AcceptOsc7(directory, string.IsNullOrEmpty(host) ? Config?.Host : host)) return;
        PublishTerminalDirectory();
        ScheduleDirectoryFollow();
    }

    private void TrackDirectoryTitle(string? title)
    {
        if (IsDisposed || _session == null || State is ConnectionState.Disconnected or ConnectionState.Error) return;
        if (!_directoryTracker.AcceptTitle(title, _directoryMultiplexerMode)) return;
        PublishTerminalDirectory();
        ScheduleDirectoryFollow();
    }

    private void PublishTerminalDirectory()
    {
        _terminalWorkingDirectory = _directoryTracker.Directory;
        if (FileManager == null) return;
        FileManager.TerminalDirectory = _terminalWorkingDirectory;
        FileManager.TerminalDirectoryStatus = _directoryTracker.RejectedHost is { } host
            ? Helpers.Strings.Format("FileManager.DirectoryFollow.HostMismatch", host) : string.Empty;
    }

    private void ScheduleDirectoryFollow()
    {
        CancelDirectoryFollow();
        CwdFollowMode mode = _directoryFollowMode;
        if (IsDisposed || State != ConnectionState.Connected || !IsFileManagerVisible || FileManager == null || _terminalWorkingDirectory == null
            || mode == CwdFollowMode.Off || mode == CwdFollowMode.OnceOnOpen && _hasFollowedDirectory) return;

        CancellationTokenSource cancellation = new();
        _directoryFollowCancellation = cancellation;
        _directoryFollowTask = FollowDirectoryAsync(FileManager, _terminalWorkingDirectory, cancellation);
    }

    private async Task FollowDirectoryAsync(RemoteFileManagerViewModel manager, string directory, CancellationTokenSource cancellation)
    {
        try
        {
            // 快速 cd 或切 pane 时只列最后一个目录，隐藏面板、断开、重连都会取消。
            await Task.Delay(300, cancellation.Token);
            if (IsDisposed || !ReferenceEquals(manager, FileManager) || !IsFileManagerVisible) return;
            bool followed = await manager.NavigateToTerminalDirectoryAsync(directory, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (followed) _hasFollowedDirectory = true;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_directoryFollowCancellation, cancellation)) _directoryFollowCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelDirectoryFollow() => _directoryFollowCancellation?.Cancel();

    private async Task DrainDirectoryFollowAsync()
    {
        CancelDirectoryFollow();
        await _directoryFollowTask;
    }
}
