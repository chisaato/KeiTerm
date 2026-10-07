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

    // 是否展开底部传输任务抽屉
    [ObservableProperty]
    private bool _isTransferDrawerOpen;

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

    private readonly Dictionary<string, string> _trackedFileLocalMap = new(StringComparer.OrdinalIgnoreCase);

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
    public void ToggleTransferDrawer()
    {
        IsTransferDrawerOpen = !IsTransferDrawerOpen;
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
        if (TransferTasks.Count == 0)
        {
            IsTransferDrawerOpen = false;
        }
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
    public async Task EnterOrOpenFileAsync(RemoteFileItem item)
    {
        SelectedItem = item;
        if (item.IsDirectory)
        {
            CurrentPath = item.FullPath;
            await RefreshDirectoryAsync();
        }
        else
        {
            // 双击文件：下载到本地缓存并在外部编辑器中打开追踪
            TransferStatusMessage = $"正在打开: {item.Name}...";
            try
            {
                await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem);
                string localPath = _fileTracker.GetLocalCachePath(_sessionId, item.FullPath);
                _trackedFileLocalMap[item.Name] = localPath;
                if (!ActiveTrackedFiles.Contains(item.Name))
                {
                    ActiveTrackedFiles.Add(item.Name);
                }
                UpdateTrackedStatusMessage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打开远程文件失败: {Path}", item.FullPath);
                TransferStatusMessage = $"打开失败: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    public async Task OpenWithEditorAsync(ExternalEditor editor)
    {
        if (SelectedItem == null || SelectedItem.IsDirectory) return;
        var item = SelectedItem;

        TransferStatusMessage = $"正在用 {editor.Name} 打开: {item.Name}...";
        try
        {
            await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem, overrideEditor: editor);
            string localPath = _fileTracker.GetLocalCachePath(_sessionId, item.FullPath);
            _trackedFileLocalMap[item.Name] = localPath;
            if (!ActiveTrackedFiles.Contains(item.Name))
            {
                ActiveTrackedFiles.Add(item.Name);
            }
            UpdateTrackedStatusMessage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "使用编辑器 {Editor} 打开远程文件失败: {Path}", editor.Name, item.FullPath);
            TransferStatusMessage = $"打开失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task OpenWithSystemDefaultAsync(RemoteFileItem? item)
    {
        item ??= SelectedItem;
        if (item == null || item.IsDirectory) return;
        TransferStatusMessage = $"正在用系统默认程序打开: {item.Name}...";
        try
        {
            // 通过直接传空编辑器触发 LaunchDefaultEditor
            string localPath = _fileTracker.GetLocalCachePath(_sessionId, item.FullPath);
            if (!File.Exists(localPath))
            {
                await using var remoteStream = await _fileSystem.OpenReadAsync(item.FullPath);
                await using var localStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await remoteStream.CopyToAsync(localStream);
            }
            if (FileEditorLauncher.IsBinaryFile(localPath))
            {
                throw new InvalidOperationException($"文件 \"{item.Name}\" 检测为二进制文件，已阻止外部文本编辑器打开。");
            }
            await _fileTracker.RegisterTrackedFileAsync(_sessionId, item.FullPath, localPath);
            _trackedFileLocalMap[item.Name] = localPath;
            FileEditorLauncher.LaunchDefaultEditor(localPath);

            if (!ActiveTrackedFiles.Contains(item.Name))
            {
                ActiveTrackedFiles.Add(item.Name);
            }
            UpdateTrackedStatusMessage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "系统默认程序打开远程文件失败: {Path}", item.FullPath);
            TransferStatusMessage = $"打开失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task OpenWithCustomExecutableAsync(RemoteFileItem? item)
    {
        item ??= SelectedItem;
        if (item == null || item.IsDirectory) return;

        if (PickExecutableFileDialogAsync == null)
        {
            _logger.LogWarning("PickExecutableFileDialogAsync 未挂载，无法弹出文件选择器");
            return;
        }

        var execPath = await PickExecutableFileDialogAsync();
        if (string.IsNullOrWhiteSpace(execPath)) return;

        TransferStatusMessage = $"正在用 {Path.GetFileName(execPath)} 打开: {item.Name}...";
        try
        {
            await _editorLauncher.OpenAndTrackAsync(_sessionId, item, _fileSystem, directExecutablePath: execPath);
            string localPath = _fileTracker.GetLocalCachePath(_sessionId, item.FullPath);
            _trackedFileLocalMap[item.Name] = localPath;
            if (!ActiveTrackedFiles.Contains(item.Name))
            {
                ActiveTrackedFiles.Add(item.Name);
            }
            UpdateTrackedStatusMessage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "指定程序打开远程文件失败: {Path}", item.FullPath);
            TransferStatusMessage = $"打开失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task DownloadToDesktopAsync(RemoteFileItem? singleItem)
    {
        var targets = (SelectedItems.Count > 0)
            ? SelectedItems.Where(x => !x.IsDirectory && x.Name != "..").ToList()
            : (singleItem != null && !singleItem.IsDirectory && singleItem.Name != ".." ? [singleItem] : []);

        if (targets.Count == 0) return;

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        IsTransferDrawerOpen = true;

        foreach (var item in targets)
        {
            string localTarget = Path.Combine(desktop, item.Name);
            var taskVm = new FileTransferTaskItemViewModel(item.Name, localTarget, item.FullPath, FileTransferDirection.Download);
            taskVm.RemoveRequested = t => TransferTasks.Remove(t);
            TransferTasks.Add(taskVm);

            _ = ExecuteDownloadTaskAsync(taskVm, item, localTarget);
        }
    }

    private async Task ExecuteDownloadTaskAsync(FileTransferTaskItemViewModel taskVm, RemoteFileItem item, string localTarget)
    {
        taskVm.State = FileTransferState.Transferring;
        taskVm.StatusText = "下载中...";
        IsTransferring = true;

        try
        {
            await using var remoteStream = await _fileSystem.OpenReadAsync(item.FullPath, taskVm.Cts.Token);
            await using var localStream = new FileStream(localTarget, FileMode.Create, FileAccess.Write);

            long totalBytes = item.Size;
            long transferred = 0;
            byte[] buffer = new byte[64 * 1024];
            int read;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while ((read = await remoteStream.ReadAsync(buffer, 0, buffer.Length, taskVm.Cts.Token)) > 0)
            {
                await localStream.WriteAsync(buffer.AsMemory(0, read), taskVm.Cts.Token);
                transferred += read;
                if (totalBytes > 0)
                {
                    taskVm.Progress = Math.Min(100.0, (double)transferred / totalBytes * 100.0);
                    double elapsedSec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    taskVm.SpeedMBs = (transferred / (1024.0 * 1024.0)) / elapsedSec;
                    taskVm.StatusText = $"{taskVm.Progress:F1}% ({taskVm.SpeedMBs:F1} MB/s)";
                }
            }

            taskVm.Progress = 100;
            taskVm.State = FileTransferState.Completed;
            taskVm.StatusText = "完成";
            TransferStatusMessage = $"已下载: {item.Name}";
        }
        catch (OperationCanceledException)
        {
            taskVm.State = FileTransferState.Cancelled;
            taskVm.StatusText = "已取消";
            if (File.Exists(localTarget))
            {
                try { File.Delete(localTarget); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载任务失败: {Name}", item.Name);
            taskVm.State = FileTransferState.Failed;
            taskVm.StatusText = $"失败: {ex.Message}";
        }
        finally
        {
            UpdateOverallTransferringState();
        }
    }

    [RelayCommand]
    public async Task UploadLocalFilesAsync(string[] filePaths)
    {
        if (filePaths == null || filePaths.Length == 0) return;

        IsTransferDrawerOpen = true;

        foreach (var localPath in filePaths)
        {
            if (!File.Exists(localPath)) continue;

            string fileName = Path.GetFileName(localPath);
            string remotePath = CurrentPath.TrimEnd('/') + "/" + fileName;

            var taskVm = new FileTransferTaskItemViewModel(fileName, localPath, remotePath, FileTransferDirection.Upload);
            taskVm.RemoveRequested = t => TransferTasks.Remove(t);
            TransferTasks.Add(taskVm);

            _ = ExecuteUploadTaskAsync(taskVm, localPath, remotePath, fileName);
        }
    }

    private async Task ExecuteUploadTaskAsync(FileTransferTaskItemViewModel taskVm, string localPath, string remotePath, string fileName)
    {
        taskVm.State = FileTransferState.Transferring;
        taskVm.StatusText = "上传中...";
        IsTransferring = true;

        try
        {
            var fileInfo = new FileInfo(localPath);
            long totalBytes = fileInfo.Length;
            long transferred = 0;

            await using var localStream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            await using var remoteStream = await _fileSystem.OpenWriteAsync(remotePath, taskVm.Cts.Token);

            byte[] buffer = new byte[64 * 1024];
            int read;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while ((read = await localStream.ReadAsync(buffer, 0, buffer.Length, taskVm.Cts.Token)) > 0)
            {
                await remoteStream.WriteAsync(buffer.AsMemory(0, read), taskVm.Cts.Token);
                transferred += read;
                if (totalBytes > 0)
                {
                    taskVm.Progress = Math.Min(100.0, (double)transferred / totalBytes * 100.0);
                    double elapsedSec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    taskVm.SpeedMBs = (transferred / (1024.0 * 1024.0)) / elapsedSec;
                    taskVm.StatusText = $"{taskVm.Progress:F1}% ({taskVm.SpeedMBs:F1} MB/s)";
                }
            }

            taskVm.Progress = 100;
            taskVm.State = FileTransferState.Completed;
            taskVm.StatusText = "完成";
            TransferStatusMessage = $"已上传: {fileName}";
            await RefreshDirectoryAsync();
        }
        catch (OperationCanceledException)
        {
            taskVm.State = FileTransferState.Cancelled;
            taskVm.StatusText = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上传任务失败: {LocalPath} -> {RemotePath}", localPath, remotePath);
            taskVm.State = FileTransferState.Failed;
            taskVm.StatusText = $"失败: {ex.Message}";
        }
        finally
        {
            UpdateOverallTransferringState();
        }
    }

    private void UpdateOverallTransferringState()
    {
        bool anyActive = TransferTasks.Any(t => t.State is FileTransferState.Pending or FileTransferState.Transferring);
        IsTransferring = anyActive;
        if (!anyActive)
        {
            TransferProgress = 100;
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!TransferTasks.Any(t => t.State is FileTransferState.Pending or FileTransferState.Transferring))
                    {
                        TransferProgress = 0;
                    }
                });
            });
        }
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

    private void UpdateTrackedStatusMessage()
    {
        if (ActiveTrackedFiles.Count == 0)
        {
            TransferStatusMessage = "就绪";
        }
        else if (ActiveTrackedFiles.Count == 1)
        {
            TransferStatusMessage = $"后台监视: {ActiveTrackedFiles[0]}";
        }
        else
        {
            TransferStatusMessage = $"后台监视中 ({ActiveTrackedFiles.Count} 个文件: {string.Join(", ", ActiveTrackedFiles.Take(2))}...)";
        }
    }

    [RelayCommand]
    public async Task StopTrackingFileAsync(string fileName)
    {
        _logger.LogInformation("用户主动请求停止监视文件: {FileName}", fileName);
        ActiveTrackedFiles.Remove(fileName);
        UpdateTrackedStatusMessage();

        // 尝试从映射表或本地缓存中注销 tracker 并删除本地缓存文件
        try
        {
            if (!_trackedFileLocalMap.TryGetValue(fileName, out var localPath))
            {
                string remotePath = CurrentPath.TrimEnd('/') + "/" + fileName;
                localPath = _fileTracker.GetLocalCachePath(_sessionId, remotePath);
            }

            _trackedFileLocalMap.Remove(fileName);
            await _fileTracker.UnregisterTrackedFileAsync(localPath);
            _logger.LogInformation("已从 LocalFileTracker 注销文件: {LocalPath}", localPath);

            if (File.Exists(localPath))
            {
                File.Delete(localPath);
                _logger.LogInformation("已清理本地缓存文件: {LocalPath}", localPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "注销监视文件失败: {FileName}", fileName);
        }
    }

    private void OnTrackedFileChanged(object? sender, LocalFileChangedEventArgs e)
    {
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            string fileName = Path.GetFileName(e.RemotePath);
            TransferStatusMessage = $"检测到修改，正在回写: {fileName}...";
            try
            {
                // 回写上传至远程
                await using var localStream = new FileStream(e.LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var remoteStream = await _fileSystem.OpenWriteAsync(e.RemotePath);
                await localStream.CopyToAsync(remoteStream);

                TransferStatusMessage = $"回写成功: {fileName} ({DateTime.Now:HH:mm:ss})";
                await RefreshDirectoryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动回写远程文件失败: {Path}", e.RemotePath);
                TransferStatusMessage = $"同步回写失败: {ex.Message}";
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        DisposeSizeDisplay();
        _fileTracker.FileChanged -= OnTrackedFileChanged;
        await _fileSystem.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
