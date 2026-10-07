using System.Threading.Tasks;
using System.Threading;
using Avalonia.Threading;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.Services.Connection;

// 终端标签 → 连接目标适配：文件侧栏所需的本地缓存跟踪器在此装配
public sealed class TerminalTabConnectionTarget : IConnectionTarget
{
    private readonly TerminalTabViewModel _tab;
    private readonly ISettingsService _settings;
    private readonly IExternalEditorRepository? _editorRepo;
    private readonly ILoggerFactory? _loggerFactory;

    public TerminalTabConnectionTarget(
        TerminalTabViewModel tab,
        ISettingsService settings,
        IExternalEditorRepository? editorRepo,
        ILoggerFactory? loggerFactory)
    {
        _tab = tab;
        _settings = settings;
        _editorRepo = editorRepo;
        _loggerFactory = loggerFactory;
    }

    public bool IsDisposed => _tab.IsDisposed;
    public bool RequiresInteractiveAuthentication => _tab.RequiresInteractiveAuthentication;
    public void SetRequiresInteractiveAuthentication(bool value) => _tab.RequiresInteractiveAuthentication = value;

    public void AttachSession(ITerminalSession session) => _tab.AttachSession(session);

    public Task DetachSessionAsync() => _tab.DetachSessionAsync();
    public Task DetachSessionAsync(ITerminalSession session) => _tab.DetachSessionAsync(session);

    public void MarkConnected() => _tab.MarkConnected();

    public void ReportError(string message) => _tab.ReportError(message);

    public void ReportWarning(string message) => _tab.ReportWarning(message);

    public void WriteLocalStatus(string message) => _tab.WriteLocalStatus(message);

    public Task ResetForReconnectAsync(ResolvedSessionConfig config)
        => ResetForReconnectAsync(config, CancellationToken.None);

    public async Task ResetForReconnectAsync(ResolvedSessionConfig config, CancellationToken ct)
    {
        await _tab.PrepareReconnectAsync(ct);
        ct.ThrowIfCancellationRequested();
        _tab.BindConfig(config);
    }

    public Task AttachFileSystemAsync(IRemoteFileSystem fileSystem)
        => AttachFileSystemCoreAsync(fileSystem, null);

    public Task AttachFileSystemAsync(IRemoteFileSystem fileSystem, ITerminalSession session)
        => AttachFileSystemCoreAsync(fileSystem, session);

    private async Task AttachFileSystemCoreAsync(IRemoteFileSystem fileSystem, ITerminalSession? session)
    {
        // 属性变更会同步触发视图的布局处理器，整个 VM 初始化必须从 UI 上下文开始。
        await Dispatcher.UIThread.InvokeAsync(() => InitializeFileManagerAsync(fileSystem, session));
    }

    private async Task InitializeFileManagerAsync(IRemoteFileSystem fileSystem, ITerminalSession? session)
    {
        FileTransferSettings transfer = _settings.Current.FileTransfer;
        LocalFileTracker tracker;
        try
        {
            tracker = new LocalFileTracker(
                cacheBaseDirectory: transfer.CacheDirectory,
                mode: transfer.WatcherMode,
                pollingIntervalSeconds: transfer.PollingIntervalSeconds,
                writeDebounceMilliseconds: transfer.WriteDebounceMilliseconds,
                logger: _loggerFactory?.CreateLogger<LocalFileTracker>() ?? NullLogger<LocalFileTracker>.Instance);
        }
        catch
        {
            await Task.Run(async () => await fileSystem.DisposeAsync());
            throw;
        }

        ILogger fileManagerLogger = (ILogger?)_loggerFactory?.CreateLogger<FileEditorLauncher>() ?? NullLogger.Instance;
        await _tab.InitializeFileManagerAsync(fileSystem, tracker, _settings, _editorRepo, fileManagerLogger, session);
    }
}
