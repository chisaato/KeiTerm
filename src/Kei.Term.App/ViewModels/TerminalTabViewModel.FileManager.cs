using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Transfers;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

public partial class TerminalTabViewModel
{
    private CancellationTokenSource? _fileManagerInitializationCancellation;
    private readonly List<Task> _fileManagerInitializations = [];
    private readonly object _transferBoardLock = new();
    private ConnectionTransferBoard? _transferBoard;
    private int _closingFileManager;

    partial void OnFileManagerChanged(RemoteFileManagerViewModel? oldValue, RemoteFileManagerViewModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnFileManagerHeaderChanged;
        if (newValue != null) newValue.PropertyChanged += OnFileManagerHeaderChanged;
        UpdateTitle();
        UpdateToolTip();
    }

    private void OnFileManagerHeaderChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(RemoteFileManagerViewModel.CurrentPath) || IsShellVisible || !IsFileManagerVisible) return;
        UpdateTitle();
        // 同名目录之间切换时标题不变，悬停提示仍需更新完整路径。
        UpdateToolTip();
    }

    // 调用方切回 UI 线程；源会话用于拒绝文件通道创建完成前发生的关闭或重连。
    public Task InitializeFileManagerAsync(
        IRemoteFileSystem fileSystem,
        ILocalFileTracker fileTracker,
        ISettingsService? settingsService = null,
        IExternalEditorRepository? editorRepo = null,
        ILogger? logger = null,
        ITerminalSession? expectedSession = null,
        ITransferEndpoint? transfers = null,
        string? transferUnavailableReason = null,
        ISessionMetadataRepository? metadata = null)
    {
        ITerminalSession? session = _session;
        if (IsDisposed || session == null || expectedSession != null && !ReferenceEquals(session, expectedSession))
            return ReleaseUnattachedFileManagerAsync(fileSystem, fileTracker, transfers);

        _fileManagerInitializationCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _fileManagerInitializationCancellation = cancellation;
        _fileManagerInitializations.RemoveAll(task => task.IsCompleted);
        Task initialization = InitializeFileManagerCoreAsync(
            session, fileSystem, fileTracker, settingsService, editorRepo, logger, cancellation, transfers, transferUnavailableReason, metadata);
        _fileManagerInitializations.Add(initialization);
        return initialization;
    }

    private async Task InitializeFileManagerCoreAsync(
        ITerminalSession session,
        IRemoteFileSystem fileSystem,
        ILocalFileTracker fileTracker,
        ISettingsService? settingsService,
        IExternalEditorRepository? editorRepo,
        ILogger? logger,
        CancellationTokenSource cancellation,
        ITransferEndpoint? transfers,
        string? transferUnavailableReason,
        ISessionMetadataRepository? metadata)
    {
        RemoteFileManagerViewModel? manager = null;
        ConnectionTransferBoard? ownedBoard = null;
        bool attached = false;
        try
        {
            if (transfers != null)
            {
                ownedBoard = ConnectionTransferBoard.Create(
                    transfers,
                    () => settingsService?.Current.FileTransfer ?? new FileTransferSettings(),
                    _dropInteraction,
                    logger: logger ?? _logger,
                    sessionId: session.SessionId,
                    metadata: metadata,
                    // 连接建立时的会话覆盖快照；编辑会话后需重连才生效，与其他连接级覆盖一致
                    overrides: new SessionOverrides
                    {
                        VerifyHashEnabled = Config?.VerifyHashOverride,
                        ArchiveDirectoriesEnabled = Config?.ArchiveDirectoriesOverride,
                    });
            }

            manager = new(session.SessionId, fileSystem, fileTracker, settingsService, editorRepo, logger ?? _logger, ownedBoard, _dropInteraction);
            if (ownedBoard == null) manager.NoteTransferUnavailable(transferUnavailableReason);
            manager.CloseRequested += () =>
            {
                CloseFileManagerDocument();
                Dispatcher.UIThread.Post(() => TrySyncTerminalSize(), DispatcherPriority.Render);
            };
            manager.ToggleDockPositionRequested += ToggleDockPosition;
            if (settingsService != null)
            {
                IsFileManagerOnLeft = settingsService.Current.FileTransfer.IsFileManagerOnLeft;
                manager.IsOnLeft = IsFileManagerOnLeft;
            }

            await manager.InitializeAsync(cancellation.Token);
            // 初始化可能吞掉取消异常，也可能经过不支持取消的远端操作；完成后仍需核对归属。
            if (IsDisposed || cancellation.IsCancellationRequested || !ReferenceEquals(_session, session)
                || Volatile.Read(ref _closingFileManager) != 0)
            {
                return;
            }

            lock (_transferBoardLock)
            {
                if (Volatile.Read(ref _closingFileManager) != 0 || cancellation.IsCancellationRequested)
                {
                    return;
                }

                _transferBoard = ownedBoard;
                ownedBoard = null;
            }

            manager.IsShellVisible = IsShellVisible;
            manager.IsMaximized = IsFileManagerMaximized;
            FileManager = manager;
            PublishTerminalDirectory();
            ScheduleDirectoryFollow();
            attached = true;
        }
        finally
        {
            if (ReferenceEquals(_fileManagerInitializationCancellation, cancellation))
                _fileManagerInitializationCancellation = null;
            cancellation.Dispose();
            if (ownedBoard != null)
            {
                try { await ownedBoard.CloseAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "关闭未挂上的传输队列失败"); }
            }

            if (manager == null) await ReleaseUnattachedFileManagerAsync(fileSystem, fileTracker);
            else if (!attached) await Task.Run(async () => await manager.DisposeAsync());
        }
    }

    private static async Task ReleaseUnattachedFileManagerAsync(
        IRemoteFileSystem fileSystem,
        ILocalFileTracker tracker,
        ITransferEndpoint? transfers = null)
    {
        await Task.Run(async () =>
        {
            try { await fileSystem.DisposeAsync(); }
            finally
            {
                try { await tracker.DisposeAsync(); }
                finally
                {
                    if (transfers != null)
                    {
                        try { await transfers.DisposeAsync(); }
                        catch (Exception) { }
                    }
                }
            }
        });
    }

    // 先取消初始化并等它释放临时实例，再释放已挂载实例；不让迟到结果复活已关闭的标签。
    private async Task CloseFileManagerAsync()
    {
        // 在任何 await 前摘下本次持有的实例；排空旧请求之后不得再清空新连接的属性。
        Interlocked.Exchange(ref _closingFileManager, 1);
        ConnectionTransferBoard? board;
        lock (_transferBoardLock)
        {
            board = _transferBoard;
            _transferBoard = null;
        }

        RemoteFileManagerViewModel? manager = FileManager;
        if (IsFileManagerMaximized)
        {
            IsShellVisible = true;
            IsFileManagerMaximized = false;
        }
        IsFileManagerVisible = false;
        FileManager = null;
        Task directoryFollow = DrainDirectoryFollowAsync();
        _fileManagerInitializationCancellation?.Cancel();
        Task[] initializations = _fileManagerInitializations.ToArray();
        _fileManagerInitializations.Clear();
        await directoryFollow;
        try { await Task.WhenAll(initializations); }
        catch (Exception ex) { _logger.LogWarning(ex, "等待文件视图初始化结束失败 标题={Title}", Title); }
        try
        {
            if (board != null)
            {
                try { await board.CloseAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "关闭传输队列失败 标题={Title}", Title); }
            }

            if (manager == null) return;
            try { await Task.Run(async () => await manager.DisposeAsync()); }
            catch (Exception ex) { _logger.LogWarning(ex, "释放文件视图失败 标题={Title}", Title); }
        }
        finally
        {
            Interlocked.Exchange(ref _closingFileManager, 0);
        }
    }
}
