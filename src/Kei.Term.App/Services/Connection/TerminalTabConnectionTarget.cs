using System.Threading.Tasks;
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

    public void AttachSession(ITerminalSession session) => _tab.AttachSession(session);

    public Task DetachSessionAsync() => _tab.DetachSessionAsync();

    public void MarkConnected() => _tab.MarkConnected();

    public void ReportError(string message) => _tab.ReportError(message);

    public void ReportWarning(string message) => _tab.ReportWarning(message);

    public void WriteLocalStatus(string message) => _tab.WriteLocalStatus(message);

    public async Task ResetForReconnectAsync(ResolvedSessionConfig config)
    {
        await _tab.PrepareReconnectAsync();
        _tab.BindConfig(config);
    }

    public Task AttachFileSystemAsync(IRemoteFileSystem fileSystem)
    {
        FileTransferSettings transfer = _settings.Current.FileTransfer;
        var tracker = new LocalFileTracker(
            cacheBaseDirectory: transfer.CacheDirectory,
            mode: transfer.WatcherMode,
            pollingIntervalSeconds: transfer.PollingIntervalSeconds,
            writeDebounceMilliseconds: transfer.WriteDebounceMilliseconds,
            logger: _loggerFactory?.CreateLogger<LocalFileTracker>() ?? NullLogger<LocalFileTracker>.Instance);

        ILogger fileManagerLogger = (ILogger?)_loggerFactory?.CreateLogger<FileEditorLauncher>() ?? NullLogger.Instance;
        return _tab.InitializeFileManagerAsync(fileSystem, tracker, _settings, _editorRepo, fileManagerLogger);
    }
}
