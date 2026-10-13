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

public partial class RemoteFileManagerViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly Guid _sessionId;
    private readonly IRemoteFileSystem _fileSystem;
    private readonly ILocalFileTracker _fileTracker;
    private readonly ISettingsService? _settingsService;
    private readonly IExternalEditorRepository? _editorRepo;
    private readonly FileEditorLauncher _editorLauncher;
    private readonly ILogger _logger;

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

    // 活跃的传输任务列表
    public ObservableCollection<FileTransferTaskItemViewModel> TransferTasks { get; } = [];

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

    private readonly Dictionary<string, string> _trackedFileLocalMap = new(StringComparer.Ordinal);

    public ObservableCollection<RemoteFileItem> Items { get; } = [];
    public ObservableCollection<string> ActiveTrackedFiles { get; } = [];
    public ObservableCollection<ExternalEditor> AvailableEditors { get; } = [];

    // 文件选择委托（单次打开选择其他本地程序）
    public Func<Task<string?>>? PickExecutableFileDialogAsync { get; set; }

    public RemoteFileManagerViewModel(
        Guid sessionId,
        IRemoteFileSystem fileSystem,
        ILocalFileTracker fileTracker,
        ISettingsService? settingsService = null,
        IExternalEditorRepository? editorRepo = null,
        ILogger? logger = null)
    {
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
        _editorLauncher = new FileEditorLauncher(_fileTracker, settingsService, editorRepo, launcherLogger);

        // 订阅本地文件变动自动回写事件
        _fileTracker.FileChanged += OnTrackedFileChanged;
        _fileTracker.FileUntracked += OnFileUntracked;
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

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            IsLoading = true;
            if (!_fileSystem.IsConnected)
            {
                await _fileSystem.ConnectAsync(ct);
            }
            CurrentPath = _fileSystem.WorkingDirectory;

            // 初始化左侧目录树根节点 "/"
            DirectoryTreeRoots.Clear();
            var rootNode = new RemoteDirectoryNodeViewModel("/", "/", _fileSystem, () => ShowHiddenFiles, hasDummyChild: false)
            {
                IsExpanded = true
            };
            DirectoryTreeRoots.Add(rootNode);

            // 根节点先加载一级子目录
            await rootNode.LoadChildrenAsync(ct);

            // 像 SecureCRT 一样懒加载展开：沿当前工作目录逐步展开并选中，其它同级不预先全量加载
            await RefreshDirectoryAsync(ct);
            await LoadAvailableEditorsAsync(ct);
        }
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
    public async Task RefreshDirectoryAsync(CancellationToken ct = default)
    {
        try
        {
            IsLoading = true;
            var list = await _fileSystem.ListDirectoryAsync(CurrentPath, ct);

            Items.Clear();

            // 若不是根目录，加入 SecureCRT 风格的 ".." 上级目录快捷项
            if (!string.IsNullOrWhiteSpace(CurrentPath) && CurrentPath != "/")
            {
                string parent = Path.GetDirectoryName(CurrentPath.TrimEnd('/'))?.Replace('\\', '/') ?? "/";
                if (string.IsNullOrWhiteSpace(parent)) parent = "/";
                Items.Add(new RemoteFileItem("..", parent, true, 0, DateTimeOffset.MinValue, "drwxr-xr-x"));
            }

            var sorted = list
                .Where(item => ShowHiddenFiles || !item.Name.StartsWith('.'))
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var item in sorted)
            {
                Items.Add(item);
            }

            // 同步目录树高亮节点（像 SecureCRT 一样只沿着当前路径按需懒加载展开并单向跟踪）
            if (DirectoryTreeRoots.Count > 0)
            {
                _isInternalDirectoryNavigating = true;
                try
                {
                    _logger.LogDebug("TreeSync start: CurrentPath={Path}, RootExpanded before={Expanded}", CurrentPath, DirectoryTreeRoots[0].IsExpanded);
                    // 确保根节点自身展开
                    DirectoryTreeRoots[0].IsExpanded = true;
                    var targetNode = await DirectoryTreeRoots[0].EnsurePathLoadedAsync(CurrentPath, ct);
                    if (targetNode != null)
                    {
                        // 展开并预载当前目录的子级，方便用户继续向下点击展开
                        targetNode.IsExpanded = true;
                        _logger.LogDebug("TreeSync resolved: {Node} target={Path} expanded={Expanded} selected(prev)={PrevSelected}", targetNode.FullPath, CurrentPath, targetNode.IsExpanded, SelectedDirectoryNode?.FullPath);
                        SelectedDirectoryNode = targetNode;
                    }
                    else
                    {
                        _logger.LogDebug("TreeSync failed: EnsurePathLoadedAsync returned null for {Path}", CurrentPath);
                    }
                }
                finally
                {
                    _isInternalDirectoryNavigating = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新目录失败: {Path}", CurrentPath);
            TransferStatusMessage = $"刷新失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void ClearCompletedTransferTasks()
    {
        var toRemove = TransferTasks
            .Where(t => t.State is FileTransferState.Completed or FileTransferState.Failed or FileTransferState.Cancelled)
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
        if (_settingsService != null)
        {
            _settingsService.Current.FileTransfer.IsFileManagerOnLeft = IsOnLeft;
            _ = _settingsService.SaveSettingsAsync(_settingsService.Current);
        }
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
