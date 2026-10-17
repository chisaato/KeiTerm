namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kei.Term.App.Services;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Transfers;

public partial class RemoteFileManagerViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly Guid _sessionId;
    private readonly IRemoteFileSystem _fileSystem;
    private readonly ILocalFileTracker _fileTracker;
    private readonly ISettingsService? _settingsService;
    private readonly IExternalEditorRepository? _editorRepo;
    private readonly FileEditorLauncher _editorLauncher;
    private readonly ILogger _logger;
    private readonly IInteractionService? _interaction;

    public event Action? CloseRequested;
    public event Action? ToggleDockPositionRequested;

    // 当前是否停靠在左侧
    [ObservableProperty]
    private bool _isOnLeft;

    [ObservableProperty]
    private string _currentPath = "/";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _showHiddenFiles;

    [ObservableProperty]
    private string? _transferStatusMessage;

    // 当前传输进度百分比 (0-100)
    [ObservableProperty]
    private double _transferProgress;

    // 是否正在传输（控制进度条显示）
    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    private RemoteFileItem? _selectedItem;

    [ObservableProperty]
    private RemoteDirectoryNodeViewModel? _selectedDirectoryNode;

    // 活跃的传输任务列表。目录标签与侧栏指向连接上的同一集合。
    public ObservableCollection<FileTransferTaskItemViewModel> TransferTasks { get; }

    // 多选选中的项目列表
    public ObservableCollection<RemoteFileItem> SelectedItems { get; } = [];

    // 当用户手动在目录树上点击切换节点时防循环触发
    private bool _isInternalDirectoryNavigating;

    partial void OnSelectedDirectoryNodeChanged(RemoteDirectoryNodeViewModel? value)
    {
        _logger.LogDebug("TreeVM OnSelectedDirectoryNodeChanged: {Path} (internalNav={Internal})", value?.FullPath ?? "<null>", _isInternalDirectoryNavigating);
        if (_isInternalDirectoryNavigating) return;
        if (value != null && !string.IsNullOrWhiteSpace(value.FullPath))
        {
            CurrentPath = value.FullPath;
            _ = RefreshDirectoryAsync();
        }
    }

    // 目录树根节点列表（通常为根目录 "/" 节点）
    public ObservableCollection<RemoteDirectoryNodeViewModel> DirectoryTreeRoots { get; } = [];

    private readonly Dictionary<string, string> _trackedFileLocalMap;

    public ObservableCollection<RemoteFileItem> Items { get; } = [];
    public ObservableCollection<string> ActiveTrackedFiles { get; }
    public ObservableCollection<ExternalEditor> AvailableEditors { get; } = [];

    // 文件选择委托（单次打开选择其他本地程序）
    public Func<Task<string?>>? PickExecutableFileDialogAsync { get; set; }

    public RemoteFileManagerViewModel(
        Guid sessionId,
        IRemoteFileSystem fileSystem,
        ILocalFileTracker fileTracker,
        ISettingsService? settingsService = null,
        IExternalEditorRepository? editorRepo = null,
        ILogger? logger = null,
        ConnectionTransferBoard? transfers = null,
        IInteractionService? interaction = null)
        : this(sessionId, fileSystem, fileTracker, settingsService, editorRepo, logger, null, transfers, interaction)
    {
    }

    private RemoteFileManagerViewModel(
        Guid sessionId,
        IRemoteFileSystem fileSystem,
        ILocalFileTracker fileTracker,
        ISettingsService? settingsService,
        IExternalEditorRepository? editorRepo,
        ILogger? logger,
        RemoteFileManagerViewModel? owningFileView,
        ConnectionTransferBoard? transfers = null,
        IInteractionService? interaction = null)
    {
        _owningFileView = owningFileView;
        _interaction = owningFileView?._interaction ?? interaction;
        _board = owningFileView?._board ?? transfers;
        _trackedFileLocalMap = owningFileView?._trackedFileLocalMap ?? new(StringComparer.Ordinal);
        ActiveTrackedFiles = owningFileView?.ActiveTrackedFiles ?? [];
        TransferTasks = owningFileView?.TransferTasks ?? transfers?.Tasks ?? [];
        _sessionId = sessionId;
        _fileSystem = fileSystem;
        _fileTracker = fileTracker;
        _settingsService = settingsService;
        InitializeSizeDisplay();
        InitializeFileActivities();
        _editorRepo = editorRepo;
        _logger = logger ?? NullLogger.Instance;
        RemoteDirectoryNodeViewModel.SetLogger(_logger);
        var launcherLogger = _logger;
        _editorLauncher = owningFileView?._editorLauncher ?? new FileEditorLauncher(_fileTracker, settingsService, editorRepo, launcherLogger);

        // 订阅本地文件变动自动回写事件
        if (owningFileView == null)
        {
            _fileTracker.FileChanged += OnTrackedFileChanged;
            _fileTracker.FileUntracked += OnFileUntracked;
        }
    }

    private void OnFileUntracked(object? sender, string localFilePath)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || _fileTracker.IsTracking(localFilePath)) return;
            var match = _trackedFileLocalMap.FirstOrDefault(kvp => string.Equals(kvp.Value, localFilePath, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key))
            {
                _trackedFileLocalMap.Remove(match.Key);
                ActiveTrackedFiles.Remove(match.Key);
                UpdateTrackedStatusMessage();
            }
        });
    }

    public Task InitializeAsync(CancellationToken ct = default) => InitializeCoreAsync(null, ct);

    private async Task InitializeCoreAsync(string? initialDirectory, CancellationToken ct)
    {
        try
        {
            IsLoading = true;
            if (!_fileSystem.IsConnected)
            {
                await _fileSystem.ConnectAsync(ct);
            }
            CurrentPath = initialDirectory ?? _fileSystem.WorkingDirectory;

            // 初始化左侧目录树根节点 "/"
            DirectoryTreeRoots.Clear();
            RemoteDirectoryNodeViewModel rootNode = new("/", "/", _fileSystem, () => ShowHiddenFiles, hasDummyChild: false);
            DirectoryTreeRoots.Add(rootNode);

            // 根节点先加载一级子目录
            await rootNode.LoadChildrenAsync(ct);
            ct.ThrowIfCancellationRequested();
            // 加载后展开，避免 IsExpanded 提前发起不带生命周期令牌的同一次请求。
            rootNode.IsExpanded = true;

            // 像 SecureCRT 一样懒加载展开：沿当前工作目录逐步展开并选中，其它同级不预先全量加载
            await RefreshDirectoryAsync(ct);
            await LoadAvailableEditorsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化远程文件系统失败");
            TransferStatusMessage = $"连接失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadAvailableEditorsAsync(CancellationToken ct = default)
    {
        if (_editorRepo == null) return;
        try
        {
            var editors = await _editorRepo.GetAllEditorsAsync(ct);
            AvailableEditors.Clear();
            foreach (var e in editors)
            {
                AvailableEditors.Add(e);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载已注册外部编辑器失败");
        }
    }

    [RelayCommand]
    public void ClearCompletedTransferTasks()
    {
        TransferBoard?.ClearFinished();
        var toRemove = TransferTasks
            .Where(t => t.State is FileTransferState.Completed or FileTransferState.Failed or FileTransferState.Cancelled or FileTransferState.Skipped)
            .ToList();
        foreach (var task in toRemove)
        {
            TransferTasks.Remove(task);
        }
        if (TransferTasks.Count == 0 && ActiveTrackedFiles.Count > 0) SelectedActivityTab = 1;
    }

    [RelayCommand]
    public void ToggleDockPosition()
    {
        ToggleDockPositionRequested?.Invoke();
    }

    [RelayCommand]
    public async Task NavigateUpAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath) || CurrentPath == "/")
        {
            return;
        }

        string parent = Path.GetDirectoryName(CurrentPath.TrimEnd('/'))?.Replace('\\', '/') ?? "/";
        if (string.IsNullOrWhiteSpace(parent))
        {
            parent = "/";
        }

        CurrentPath = parent;
        await RefreshDirectoryAsync();
    }

    [RelayCommand]
    public async Task DeleteItemAsync(RemoteFileItem? singleItem)
    {
        var targets = (SelectedItems.Count > 0)
            ? SelectedItems.Where(x => x.Name != "..").ToList()
            : (singleItem != null && singleItem.Name != ".." ? [singleItem] : []);

        if (targets.Count == 0) return;

        try
        {
            if (targets.Count == 1)
            {
                TransferStatusMessage = $"正在删除: {targets[0].Name}...";
                await _fileSystem.DeleteAsync(targets[0].FullPath, targets[0].IsDirectory);
                TransferStatusMessage = $"已删除: {targets[0].Name}";
            }
            else
            {
                TransferStatusMessage = $"正在批量删除 {targets.Count} 个项目...";
                foreach (var item in targets)
                {
                    await _fileSystem.DeleteAsync(item.FullPath, item.IsDirectory);
                }
                TransferStatusMessage = $"已成功删除 {targets.Count} 个项目";
            }
            await RefreshDirectoryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除失败");
            TransferStatusMessage = $"删除失败: {ex.Message}";
        }
    }

}
