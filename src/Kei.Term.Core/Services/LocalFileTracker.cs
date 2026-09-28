namespace Kei.Term.Core.Services;

using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kei.Term.Core.Models;

// 本地文件变更事件参数
public class LocalFileChangedEventArgs : EventArgs
{
    public required string LocalFilePath { get; init; }
    public required string RemotePath { get; init; }
    public required byte[] NewHash { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
}

public interface ILocalFileTracker : IAsyncDisposable
{
    event EventHandler<LocalFileChangedEventArgs>? FileChanged;

    public event EventHandler<string>? FileUntracked;

    string GetLocalCachePath(Guid sessionId, string remotePath);
    Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default);
    Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default);
}

// 健壮的多模式文件监视追踪服务
public class LocalFileTracker : ILocalFileTracker
{
    private class TrackedEntry
    {
        public required Guid SessionId { get; init; }
        public required string RemotePath { get; init; }
        public required string LocalFilePath { get; init; }
        public byte[]? LastHash { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public long LastLength { get; set; }
        public DateTime LastNotifiedUtc { get; set; }
        public DateTime RegisteredAtUtc { get; init; } = DateTime.UtcNow;
    }

    private readonly string _cacheBaseDirectory;
    private readonly FileWatcherMode _configuredMode;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _writeDebounce;
    private readonly ILogger _logger;

    private readonly Dictionary<string, TrackedEntry> _trackedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _activeWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private bool _isDisposed;

    public event EventHandler<LocalFileChangedEventArgs>? FileChanged;
    public event EventHandler<string>? FileUntracked;

    public LocalFileTracker(
        string? cacheBaseDirectory = null,
        FileWatcherMode mode = FileWatcherMode.Auto,
        int pollingIntervalSeconds = 3,
        int writeDebounceMilliseconds = 800,
        ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _configuredMode = mode;
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, pollingIntervalSeconds));
        _writeDebounce = TimeSpan.FromMilliseconds(Math.Max(200, writeDebounceMilliseconds));

        if (!string.IsNullOrWhiteSpace(cacheBaseDirectory))
        {
            _cacheBaseDirectory = cacheBaseDirectory;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheBaseDirectory = Path.Combine(appData, "KeiTerm", "file-cache");
        }

        Directory.CreateDirectory(_cacheBaseDirectory);

        // 如果启用轮询模式或智能模式，启动后台兜底轮询
        if (_configuredMode is FileWatcherMode.Auto or FileWatcherMode.Polling)
        {
            _pollingCts = new CancellationTokenSource();
            _pollingTask = Task.Run(() => PollingLoopAsync(_pollingCts.Token));
        }
    }

    public string GetLocalCachePath(Guid sessionId, string remotePath)
    {
        // 计算远程路径 Hash 避免不同目录下的同名文件互相碰撞 (使用 XxHash3-64，快且低碰撞)
        string sanitizedRemote = remotePath.Trim().Replace('\\', '/');
        byte[] hashBytes = XxHash3.Hash(Encoding.UTF8.GetBytes(sanitizedRemote));
        string hashPrefix = Convert.ToHexString(hashBytes)[..8].ToLowerInvariant();
        string fileName = Path.GetFileName(sanitizedRemote);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "unnamed_file";
        }

        string sessionFolder = Path.Combine(_cacheBaseDirectory, sessionId.ToString("N"));
        string targetFolder = Path.Combine(sessionFolder, hashPrefix);
        Directory.CreateDirectory(targetFolder);

        return Path.Combine(targetFolder, fileName);
    }

    public async Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        byte[]? initialHash = null;
        DateTime writeTime = DateTime.MinValue;
        long length = 0;

        if (File.Exists(localFilePath))
        {
            var info = new FileInfo(localFilePath);
            writeTime = info.LastWriteTimeUtc;
            length = info.Length;
            initialHash = await TryComputeXxHash128Async(localFilePath, ct);
        }

        var entry = new TrackedEntry
        {
            SessionId = sessionId,
            RemotePath = remotePath,
            LocalFilePath = Path.GetFullPath(localFilePath),
            LastHash = initialHash,
            LastWriteTimeUtc = writeTime,
            LastLength = length,
            LastNotifiedUtc = DateTime.MinValue
        };

        _logger.LogInformation("RegisterTrackedFileAsync: {LocalFilePath} -> {RemotePath}", entry.LocalFilePath, entry.RemotePath);

        lock (_lock)
        {
            _trackedFiles[entry.LocalFilePath] = entry;

            // 若使用原生或智能模式，挂载目录级 FileSystemWatcher
            if (_configuredMode is FileWatcherMode.Auto or FileWatcherMode.OSNative)
            {
                EnsureWatcherForFile(entry.LocalFilePath);
            }
        }
    }

    public Task UnregisterTrackedFileAsync(string localFilePath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        string fullPath = Path.GetFullPath(localFilePath);

        _logger.LogInformation("UnregisterTrackedFileAsync: {LocalFilePath}", fullPath);

        lock (_lock)
        {
            if (_trackedFiles.Remove(fullPath))
            {
                _logger.LogInformation("Successfully removed {LocalFilePath} from _trackedFiles. Remaining tracked count: {Count}", fullPath, _trackedFiles.Count);
                FileUntracked?.Invoke(this, fullPath);
            }
            else
            {
                _logger.LogWarning("Failed to remove {LocalFilePath} from _trackedFiles: path not found. Current tracked paths: [{Paths}]",
                    fullPath, string.Join(", ", _trackedFiles.Keys));
            }

            // 若该目录下无其他受控文件，可清理对应 Watcher
            string? dir = Path.GetDirectoryName(fullPath);
            if (dir != null && !_trackedFiles.Keys.Any(p => string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase)))
            {
                if (_activeWatchers.Remove(dir, out var watcher))
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                    _logger.LogInformation("Disposed FileSystemWatcher for directory: {Directory}", dir);
                }
            }
        }

        return Task.CompletedTask;
    }

    private void EnsureWatcherForFile(string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        if (_activeWatchers.ContainsKey(directory))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                Filter = "*",
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemRenamed;

            _activeWatchers[directory] = watcher;
            _logger.LogInformation("Mounted FileSystemWatcher for directory: {Directory}", directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize OS native FileSystemWatcher for {Directory}. Falling back to polling.", directory);
            // 在智能模式下静默忽略，依赖轮询兜底
            if (_configuredMode == FileWatcherMode.OSNative)
            {
                throw;
            }
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        _logger.LogDebug("FileSystemWatcher event: {ChangeType} on {FullPath}", e.ChangeType, e.FullPath);
        _ = ProcessFilePotentialChangeAsync(e.FullPath);
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogDebug("FileSystemWatcher renamed: {OldFullPath} -> {FullPath}", e.OldFullPath, e.FullPath);
        _ = ProcessFilePotentialChangeAsync(e.FullPath);
    }

    private async Task ProcessFilePotentialChangeAsync(string filePath)
    {
        TrackedEntry? entry;
        lock (_lock)
        {
            if (!_trackedFiles.TryGetValue(filePath, out entry))
            {
                return;
            }
        }

        _logger.LogDebug("Processing file change for tracked file: {LocalFilePath}", filePath);

        // 防抖：如果刚刚通知过，且在防抖窗口内，暂缓
        if (DateTime.UtcNow - entry.LastNotifiedUtc < _writeDebounce)
        {
            _logger.LogDebug("Debounce active for {LocalFilePath}, skipping", filePath);
            return;
        }

        // 等待外部编辑器完全释放写句柄（防抖指数退避）
        await Task.Delay(_writeDebounce);

        if (!File.Exists(filePath))
        {
            return;
        }

        byte[]? newHash = await WaitForFileReadyAndComputeHashAsync(filePath);
        if (newHash == null)
        {
            return;
        }

        bool hasChanged = entry.LastHash == null || !entry.LastHash.SequenceEqual(newHash);
        _logger.LogDebug("File {LocalFilePath} hash check: hasChanged={HasChanged}", filePath, hasChanged);
        if (hasChanged)
        {
            entry.LastHash = newHash;
            var info = new FileInfo(filePath);
            entry.LastWriteTimeUtc = info.LastWriteTimeUtc;
            entry.LastLength = info.Length;
            entry.LastNotifiedUtc = DateTime.UtcNow;

            _logger.LogInformation("Detected modification for tracked file: {LocalFilePath} -> {RemotePath}", entry.LocalFilePath, entry.RemotePath);

            FileChanged?.Invoke(this, new LocalFileChangedEventArgs
            {
                LocalFilePath = entry.LocalFilePath,
                RemotePath = entry.RemotePath,
                NewHash = newHash,
                DetectedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private async Task PollingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollingInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct);

                List<TrackedEntry> snapshot;
                lock (_lock)
                {
                    snapshot = _trackedFiles.Values.ToList();
                }

                foreach (var entry in snapshot)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!File.Exists(entry.LocalFilePath)) continue;

                    var info = new FileInfo(entry.LocalFilePath);
                    if (info.LastWriteTimeUtc != entry.LastWriteTimeUtc || info.Length != entry.LastLength)
                    {
                        _logger.LogDebug("PollingLoop detected difference: {LocalFilePath} info.LastWriteTimeUtc={InfoTime}, entry.LastWriteTimeUtc={EntryTime}, info.Length={InfoLen}, entry.LastLength={EntryLen}",
                            entry.LocalFilePath, info.LastWriteTimeUtc, entry.LastWriteTimeUtc, info.Length, entry.LastLength);
                        await ProcessFilePotentialChangeAsync(entry.LocalFilePath);
                    }
                    else if ((DateTime.UtcNow - entry.RegisteredAtUtc) > TimeSpan.FromSeconds(5) && !IsFileInUse(entry.LocalFilePath))
                    {
                        // 既没有被占用把持，且已注册超过5秒，自动清理监视与本地临时文件
                        _logger.LogInformation("Tracked file {LocalFilePath} is no longer in use. Auto unregistering and cleaning up cache.", entry.LocalFilePath);
                        _ = Task.Run(async () =>
                        {
                            await UnregisterTrackedFileAsync(entry.LocalFilePath);
                            try
                            {
                                if (File.Exists(entry.LocalFilePath))
                                {
                                    File.Delete(entry.LocalFilePath);
                                    _logger.LogInformation("Deleted unused cache file: {LocalFilePath}", entry.LocalFilePath);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete unused cache file: {LocalFilePath}", entry.LocalFilePath);
                            }
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error occurred in file polling loop.");
            }
        }
    }

    // 检查文件当前是否正被外部进程打开使用
    private bool IsFileInUse(string filePath)
    {
        if (!File.Exists(filePath)) return false;

        // 在 Linux/macOS 上，FileShare 在许多普通文件系统上不具有强制独占语义，因此优先结合 fuser 检查句柄占用
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "fuser",
                    Arguments = $"\"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (proc != null)
                {
                    proc.WaitForExit(1000);
                    // fuser 若返回退出码 0，表示有进程正在占用访问该文件
                    if (proc.ExitCode == 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 系统未安装 fuser 则降级到普通文件流尝试
            }
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<byte[]?> WaitForFileReadyAndComputeHashAsync(string filePath)
    {
        // 最多重试 5 次等待写锁释放
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var xxh = new XxHash128();
                await xxh.AppendAsync(stream);
                return xxh.GetCurrentHash();
            }
            catch (IOException)
            {
                await Task.Delay(200 * (attempt + 1));
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static async Task<byte[]?> TryComputeXxHash128Async(string filePath, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var xxh = new XxHash128();
            await xxh.AppendAsync(stream, ct);
            return xxh.GetCurrentHash();
        }
        catch
        {
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_pollingCts != null)
        {
            _pollingCts.Cancel();
            _pollingCts.Dispose();
        }

        if (_pollingTask != null)
        {
            try
            {
                await _pollingTask;
            }
            catch
            {
                // ignore
            }
        }

        lock (_lock)
        {
            foreach (var watcher in _activeWatchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _activeWatchers.Clear();
            _trackedFiles.Clear();
        }

        GC.SuppressFinalize(this);
    }
}
