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
    bool IsTracking(string localFilePath);
    Task RegisterTrackedFileAsync(Guid sessionId, string remotePath, string localFilePath, CancellationToken ct = default);
    Task CheckForChangesAsync(string localFilePath, CancellationToken ct = default);
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
        public SemaphoreSlim CheckGate { get; } = new(1, 1);
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
        _logger.LogInformation("File tracker started: Mode={Mode}, PollInterval={PollIntervalMs}ms, Debounce={DebounceMs}ms, Cache={CacheDirectory}",
            _configuredMode, _pollingInterval.TotalMilliseconds, _writeDebounce.TotalMilliseconds, _cacheBaseDirectory);

        // 如果启用轮询模式或智能模式，启动后台兜底轮询
        if (_configuredMode is FileWatcherMode.Auto or FileWatcherMode.Polling)
        {
            CancellationToken ct = _lifetimeCts.Token;
            _pollingTask = Task.Run(() => PollingLoopAsync(ct));
        }
    }

    public bool IsTracking(string localFilePath)
    {
        lock (_lock) return !_isDisposed && _trackedFiles.ContainsKey(Path.GetFullPath(localFilePath));
    }

    // 编辑器的等待进程结束时主动检查，避免最后一次保存落在轮询周期之间。
    public Task CheckForChangesAsync(string localFilePath, CancellationToken ct = default)
        => ProcessFilePotentialChangeAsync(Path.GetFullPath(localFilePath), "final-check", ct);

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

        if (File.Exists(localFilePath))
        {
            initialHash = await TryComputeXxHash128Async(localFilePath, ct);
        }

        var entry = new TrackedEntry
        {
            SessionId = sessionId,
            RemotePath = remotePath,
            LocalFilePath = Path.GetFullPath(localFilePath),
            LastHash = initialHash
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
        ct.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(localFilePath);

        _logger.LogInformation("UnregisterTrackedFileAsync: {LocalFilePath}", fullPath);

        lock (_lock)
        {
            ct.ThrowIfCancellationRequested();
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
            watcher.Deleted += OnFileSystemEvent;
            watcher.Error += (_, e) => _logger.LogWarning(e.GetException(),
                "FileSystemWatcher failed for {Directory}; Mode={Mode}, polling fallback={PollingFallback}",
                directory, _configuredMode, _configuredMode == FileWatcherMode.Auto);

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
        _ = ObserveNativeChangeAsync(e.FullPath);
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogDebug("FileSystemWatcher renamed: {OldFullPath} -> {FullPath}", e.OldFullPath, e.FullPath);
        _ = ObserveNativeChangeAsync(e.FullPath);
        _ = ObserveNativeChangeAsync(e.OldFullPath);
    }

    private async Task ObserveNativeChangeAsync(string filePath)
    {
        try { await ProcessFilePotentialChangeAsync(filePath, "native"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Native file check failed: {LocalFilePath}", filePath); }
    }

    private async Task ProcessFilePotentialChangeAsync(string filePath, string source, CancellationToken cancellationToken = default)
    {
        TrackedEntry? entry;
        CancellationToken ct;
        lock (_lock)
        {
            if (_isDisposed || !_trackedFiles.TryGetValue(filePath, out entry)) return;
            ct = _lifetimeCts.Token;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
        ct = linked.Token;
        bool entered = false;
        try
        {
            // 每次事件都延迟检查最新内容，不能丢掉防抖窗口内的最后一次保存。
            await Task.Delay(_writeDebounce, ct);
            await entry.CheckGate.WaitAsync(ct);
            entered = true;
            if (!File.Exists(filePath))
            {
                _logger.LogDebug("Tracked file temporarily missing: Source={Source}, File={LocalFilePath}", source, filePath);
                if (source == "final-check") throw new IOException($"编辑文件不存在: {filePath}");
                return;
            }
            byte[]? newHash = await WaitForFileReadyAndComputeHashAsync(filePath, ct);
            if (newHash == null) throw new IOException($"编辑文件暂不可读: {filePath}");

            lock (_lock)
            {
                // 用户停止监视或关闭会话之后，已排队的文件事件不能再次触发回写。
                if (_isDisposed || !_trackedFiles.TryGetValue(filePath, out TrackedEntry? current)
                    || !ReferenceEquals(entry, current)) return;
                FileInfo info = new(filePath);
                if (entry.LastHash != null && entry.LastHash.SequenceEqual(newHash))
                {
                    _logger.LogDebug("File check unchanged: Source={Source}, File={LocalFilePath}, Bytes={Length}", source, filePath, info.Length);
                    return;
                }
                entry.LastHash = newHash;
                _logger.LogInformation("File change detected: Source={Source}, Session={SessionId}, File={LocalFilePath}, Remote={RemotePath}, Bytes={Length}",
                    source, entry.SessionId, filePath, entry.RemotePath, info.Length);
                FileChanged?.Invoke(this, new LocalFileChangedEventArgs
                {
                    LocalFilePath = entry.LocalFilePath,
                    RemotePath = entry.RemotePath,
                    NewHash = newHash,
                    DetectedAt = DateTimeOffset.UtcNow
                });
            }
        }
        finally { if (entered) entry.CheckGate.Release(); }
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

                if (snapshot.Count > 0) _logger.LogDebug("File polling tick: TrackedCount={Count}", snapshot.Count);
                // 独立检查各文件；一个正在写入的文件不能延迟其余文件的保存检测。
                await Task.WhenAll(snapshot.Select(entry => PollEntryAsync(entry, ct)));
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

    private async Task PollEntryAsync(TrackedEntry entry, CancellationToken ct)
    {
        try
        {
            // 每轮比较内容摘要，支持保留时间戳且字节数不变的保存/原子替换。
            await ProcessFilePotentialChangeAsync(entry.LocalFilePath, "polling", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Polling file check failed: {LocalFilePath}", entry.LocalFilePath);
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
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "File hash retry: File={LocalFilePath}, Attempt={Attempt}", filePath, attempt + 1);
                await Task.Delay(200 * (attempt + 1), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot hash tracked file: {LocalFilePath}", filePath);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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
