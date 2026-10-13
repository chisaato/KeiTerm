using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Kei.Term.App.ViewModels;

public partial class TerminalTabViewModel
{
    private CancellationTokenSource? _fileManagerInitializationCancellation;
    private readonly List<Task> _fileManagerInitializations = [];

    // 调用方切回 UI 线程；源会话用于拒绝文件通道创建完成前发生的关闭或重连。
    public Task InitializeFileManagerAsync(
        IRemoteFileSystem fileSystem,
        ILocalFileTracker fileTracker,
        ISettingsService? settingsService = null,
        IExternalEditorRepository? editorRepo = null,
        ILogger? logger = null,
        ITerminalSession? expectedSession = null)
    {
        ITerminalSession? session = _session;
        if (IsDisposed || session == null || expectedSession != null && !ReferenceEquals(session, expectedSession))
            return ReleaseUnattachedFileManagerAsync(fileSystem, fileTracker);

        _fileManagerInitializationCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _fileManagerInitializationCancellation = cancellation;
        _fileManagerInitializations.RemoveAll(task => task.IsCompleted);
        Task initialization = InitializeFileManagerCoreAsync(
            session, fileSystem, fileTracker, settingsService, editorRepo, logger, cancellation);
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
        CancellationTokenSource cancellation)
    {
        RemoteFileManagerViewModel? manager = null;
        bool attached = false;
        try
        {
            manager = new(session.SessionId, fileSystem, fileTracker, settingsService, editorRepo, logger ?? _logger);
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
            if (IsDisposed || cancellation.IsCancellationRequested || !ReferenceEquals(_session, session)) return;
            FileManager = manager;
            attached = true;
        }
        finally
        {
            if (ReferenceEquals(_fileManagerInitializationCancellation, cancellation))
                _fileManagerInitializationCancellation = null;
            cancellation.Dispose();
            if (manager == null) await ReleaseUnattachedFileManagerAsync(fileSystem, fileTracker);
            else if (!attached) await Task.Run(async () => await manager.DisposeAsync());
        }
    }

    private static async Task ReleaseUnattachedFileManagerAsync(IRemoteFileSystem fileSystem, ILocalFileTracker tracker)
    {
        await Task.Run(async () =>
        {
            try { await fileSystem.DisposeAsync(); }
            finally { await tracker.DisposeAsync(); }
        });
    }

    // 先取消初始化并等它释放临时实例，再释放已挂载实例；不让迟到结果复活已关闭的标签。
    private async Task CloseFileManagerAsync()
    {
        // 在任何 await 前摘下本次持有的实例；排空旧请求之后不得再清空新连接的属性。
        RemoteFileManagerViewModel? manager = FileManager;
        IsFileManagerVisible = false;
        FileManager = null;
        _fileManagerInitializationCancellation?.Cancel();
        Task[] initializations = _fileManagerInitializations.ToArray();
        _fileManagerInitializations.Clear();
        try { await Task.WhenAll(initializations); }
        catch (Exception ex) { _logger.LogWarning(ex, "等待文件侧栏初始化结束失败 标题={Title}", Title); }
        if (manager == null) return;
        try { await Task.Run(async () => await manager.DisposeAsync()); }
        catch (Exception ex) { _logger.LogWarning(ex, "释放文件侧栏失败 标题={Title}", Title); }
    }
}
