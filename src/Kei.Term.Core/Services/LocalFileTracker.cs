namespace Kei.Term.Core.Services;

using System.IO;
using System.IO.Hashing;
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
    }

    private readonly string _cacheBaseDirectory;
    private readonly FileWatcherMode _configuredMode;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _writeDebounce;
    private readonly ILogger _logger;

    private readonly Dictionary<string, TrackedEntry> _trackedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _activeWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    private readonly CancellationTokenSource _lifetimeCts = new();
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
            CancellationToken ct = _lifetimeCts.Token;
            _pollingTask = Task.Run(() => PollingLoopAsync(ct));
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
            LastLength = length
        };

        _logger.LogInformation("RegisterTrackedFileAsync: {LocalFilePath} -> {RemotePath}", entry.LocalFilePath, entry.RemotePath);

        lock (_lock)
        {
            ThrowIfDisposed();
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
        CancellationToken ct;
        lock (_lock)
        {
            if (_isDisposed || !_trackedFiles.TryGetValue(filePath, out entry)) return;
            ct = _lifetimeCts.Token;
        }

        try
        {
            // 每次事件都延迟检查最新内容，不能丢掉防抖窗口内的最后一次保存。
            await Task.Delay(_writeDebounce, ct);
            if (!File.Exists(filePath)) return;
            byte[]? newHash = await WaitForFileReadyAndComputeHashAsync(filePath, ct);
            if (newHash == null) return;

            lock (_lock)
            {
                // 用户停止监视或关闭会话之后，已排队的文件事件不能再次触发回写。
                if (_isDisposed || !_trackedFiles.TryGetValue(filePath, out TrackedEntry? current)
                    || !ReferenceEquals(entry, current)) return;
                if (entry.LastHash != null && entry.LastHash.SequenceEqual(newHash)) return;

                FileInfo info = new(filePath);
                entry.LastHash = newHash;
                entry.LastWriteTimeUtc = info.LastWriteTimeUtc;
                entry.LastLength = info.Length;
                FileChanged?.Invoke(this, new LocalFileChangedEventArgs
                {
                    LocalFilePath = entry.LocalFilePath,
                    RemotePath = entry.RemotePath,
                    NewHash = newHash,
                    DetectedAt = DateTimeOffset.UtcNow
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "文件保存期间暂不可读: {LocalFilePath}", filePath);
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
                    // 编辑器通常只在保存时持有句柄；空闲不等于编辑结束。
                    // 保留缓存和监视，直到用户注销或所属会话释放。
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

    private async Task<byte[]?> WaitForFileReadyAndComputeHashAsync(string filePath, CancellationToken ct)
    {
        // 最多重试 5 次等待写锁释放
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var xxh = new XxHash128();
                await xxh.AppendAsync(stream, ct);
                return xxh.GetCurrentHash();
            }
            catch (IOException)
            {
                await Task.Delay(200 * (attempt + 1), ct);
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
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            foreach (FileSystemWatcher watcher in _activeWatchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _activeWatchers.Clear();
            _trackedFiles.Clear();
        }

        _lifetimeCts.Cancel();
        if (_pollingTask != null)
        {
            try { await _pollingTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetimeCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
