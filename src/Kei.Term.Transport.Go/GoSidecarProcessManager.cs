using System.Diagnostics;

namespace Kei.Term.Transport.Go;

// 构造不触碰磁盘、不创建进程；仅首次 Go 连接才初始化。
public sealed class GoSidecarProcessManager : IAsyncDisposable
{
    private readonly string? _binaryPath;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private GoIpcClient? _client;
    private string? _directory;
    private int _disposed;
    public GoSidecarProcessManager(string? binaryPath = null) => _binaryPath = binaryPath;

    public async Task<GoIpcClient> GetClientAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_client?.IsConnected == true && _process?.HasExited == false) return _client;
            await StopProcessAsync();
            string binary = FindBinary();
            string name = "keiterm-" + Guid.NewGuid().ToString("N");
            string endpoint;
            if (OperatingSystem.IsWindows()) endpoint = @"\\.\pipe\" + name;
            else
            {
                _directory = Path.Combine(Path.GetTempPath(), name);
                Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                endpoint = Path.Combine(_directory, "ipc.sock");
            }
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-socket");
            start.ArgumentList.Add(endpoint);
            start.ArgumentList.Add("-parent-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _process = Process.Start(start) ?? throw new IOException("无法启动 Go Sidecar");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (_process.HasExited) throw new IOException("Go Sidecar 在启动期间退出");
                    try
                    {
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        attempt.CancelAfter(TimeSpan.FromMilliseconds(250));
                        _client = await GoIpcClient.ConnectAsync(endpoint, attempt.Token);
                        await _client.CallAsync("daemon.ping", ct: timeout.Token);
                        _ = SuperviseAsync(_process);
                        return _client;
                    }
                    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException && !timeout.IsCancellationRequested)
                    {
                        if (_client != null) { await _client.DisposeAsync(); _client = null; }
                        await Task.Delay(50, timeout.Token);
                    }
                }
            }
            catch { await StopProcessAsync(); throw; }
        }
        finally { _gate.Release(); }
    }

    private async Task SuperviseAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(_stop.Token);
            // 旧进程清理不能唤醒新一代进程；会话本身报断开，由上层决定是否重连。
            for (int attempt = 0; attempt < 3 && !_stop.IsCancellationRequested && ReferenceEquals(_process, process); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), _stop.Token);
                try { await GetClientAsync(_stop.Token); return; }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
    }

    private string FindBinary()
    {
        string? configured = _binaryPath ?? Environment.GetEnvironmentVariable("KEITERM_GO_SIDECAR_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : throw new FileNotFoundException("配置的 Go Sidecar 不存在", configured);
        string name = OperatingSystem.IsWindows() ? "kei-trans-daemon.exe" : "kei-trans-daemon";
        foreach (string directory in new[] { AppContext.BaseDirectory }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new FileNotFoundException("找不到 kei-trans-daemon；请设置 KEITERM_GO_SIDECAR_PATH");
    }

    private async Task StopProcessAsync()
    {
        if (_client != null) { await _client.DisposeAsync(); _client = null; }
        if (_process != null)
        {
            if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            _process.Dispose();
            _process = null;
        }
        if (_directory != null)
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            _directory = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (_client?.IsConnected == true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await _client.CallAsync("daemon.shutdown", ct: timeout.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            await StopProcessAsync();
        }
        finally { _gate.Release(); _stop.Dispose(); }
    }
}
