using System.Diagnostics;
using System.Text.Json;

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
    private readonly Action<SidecarDiagnostic>? _diagnostics;
    private Generation? _generation;
    private sealed class Generation(Process process, string binary, string endpoint)
    {
        internal readonly Process Process = process;
        internal readonly string Binary = binary;
        internal readonly string Endpoint = endpoint;
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal GoIpcClient? Client;
        internal volatile bool Expected;
        internal Task Output = Task.CompletedTask;
        internal Task Error = Task.CompletedTask;
        internal Task Monitor = Task.CompletedTask;
    }
    public static bool IsBinaryAvailable()
    {
        try
        {
            _ = new GoSidecarProcessManager().FindBinary();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public GoSidecarProcessManager(string? binaryPath = null, Action<SidecarDiagnostic>? diagnostics = null)
    { _binaryPath = binaryPath; _diagnostics = diagnostics; }

    private void Report(Generation generation, string kind, string? reason = null, int? exitCode = null)
        => SidecarDiagnostics.Emit(_diagnostics, new(kind, generation.Id, generation.Process.Id,
            generation.Binary, exitCode, Reason: reason, Expected: generation.Expected, Endpoint: generation.Endpoint));

    public async Task<GoIpcClient> GetClientAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_client?.IsConnected == true && _process?.HasExited == false) return _client;
            await StopProcessAsync();
            string binary;
            try { binary = FindBinary(); }
            catch
            {
                SidecarDiagnostics.Emit(_diagnostics, new("process-start-failed", Reason: "binary-not-found"));
                throw;
            }
            string name = "keiterm-" + Guid.NewGuid().ToString("N");
            string endpoint;
            if (OperatingSystem.IsWindows()) endpoint = @"\\.\pipe\" + name;
            else
            {
                _directory = Path.Combine(Path.GetTempPath(), name);
                Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                endpoint = Path.Combine(_directory, "ipc.sock");
            }
            var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-socket");
            start.ArgumentList.Add(endpoint);
            start.ArgumentList.Add("-parent-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try { _process = Process.Start(start) ?? throw new IOException("无法启动 Go Sidecar"); }
            catch
            {
                SidecarDiagnostics.Emit(_diagnostics, new("process-start-failed", BinaryPath: binary, Reason: "launch-failed"));
                await StopProcessAsync();
                throw;
            }
            var generation = _generation = new Generation(_process, binary, endpoint);
            Report(generation, "process-started");
            generation.Output = CaptureAsync(generation, _process.StandardOutput, "stdout");
            generation.Error = CaptureAsync(generation, _process.StandardError, "stderr");
            generation.Monitor = SuperviseAsync(generation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (_process.HasExited) throw new IOException($"Go Sidecar 在启动期间退出，退出码 {_process.ExitCode}（{binary}）");
                    try
                    {
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                        attempt.CancelAfter(TimeSpan.FromMilliseconds(250));
                        _client = await GoIpcClient.ConnectAsync(endpoint, attempt.Token, _diagnostics, generation.Id);
                        generation.Client = _client;
                        if (_process.HasExited) _client.Fail(new IOException($"Go Sidecar 进程退出，退出码 {_process.ExitCode}"));
                        await _client.CallAsync("daemon.ping", ct: timeout.Token).WaitAsync(timeout.Token);
                        return _client;
                    }
                    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException && !timeout.IsCancellationRequested)
                    {
                        if (_client != null) { await _client.DisposeAsync(); _client = null; }
                        await Task.Delay(50, timeout.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                Report(generation, "startup-failed", ex.GetType().Name);
                await StopProcessAsync();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task SuperviseAsync(Generation generation)
    {
        await generation.Process.WaitForExitAsync();
        int code = generation.Process.ExitCode;
        if (!generation.Expected) generation.Client?.Fail(new IOException($"Go Sidecar 进程退出，退出码 {code}"));
        Report(generation, "process-exited", exitCode: code);
        // 只在下一次显式拨号重建 daemon；不恢复已失去的远端业务连接。
    }

    private async Task CaptureAsync(Generation generation, StreamReader reader, string source)
    {
        char[] buffer = new char[1024];
        string line = "";
        bool overflow = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer)) != 0)
            {
                // 原始输出可能包含私钥或终端内容，只记录来源，不记录原文。
                Report(generation, "process-output", source);
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (!overflow) CaptureDiagnostic(generation, line);
                        line = "";
                        overflow = false;
                    }
                    else if (line.Length < 4096) line += buffer[i];
                    else overflow = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void CaptureDiagnostic(Generation generation, string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("event", out var kind) && kind.GetString() == "router-ended" &&
                root.TryGetProperty("reason", out var reason) && reason.GetString() is
                    "eof" or "canceled" or "timeout" or "protocol" or "io")
                Report(generation, "router-ended", reason.GetString());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
    }

    private string FindBinary() => FindBinary(_binaryPath,
        Environment.GetEnvironmentVariable("KEITERM_GO_SIDECAR_PATH"), AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("PATH"));

    internal static string FindBinary(string? binaryPath, string? environmentPath, string baseDirectory, string? searchPath)
    {
        string? configured = !string.IsNullOrWhiteSpace(binaryPath) ? binaryPath : environmentPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : throw new FileNotFoundException("配置的 Go Sidecar 不存在", configured);
        string name = OperatingSystem.IsWindows() ? "kei-trans-daemon.exe" : "kei-trans-daemon";
        foreach (string directory in GetSearchDirectories(baseDirectory).Concat((searchPath ?? "").Split(Path.PathSeparator)))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new FileNotFoundException("找不到 Go Sidecar（kei-trans-daemon）；请安装 Go 后重新运行 dotnet build，或将 KEITERM_GO_SIDECAR_PATH 设置为已有二进制的路径。也可切换为 BuiltIn 引擎。");
    }

    private static IEnumerable<string> GetSearchDirectories(string baseDirectory)
    {
        yield return baseDirectory;
        // 从实际输出目录逐层定位源码树，兼容 App、测试和自定义构建目录的不同深度。
        for (DirectoryInfo? directory = new(baseDirectory); directory != null; directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, "src", "Kei.Term.Transport.Go", "engine", "cmd", "daemon");
            if (directory.Name == "Kei.Term.Transport.Go")
                yield return Path.Combine(directory.FullName, "engine", "cmd", "daemon");
        }
    }

    private async Task StopProcessAsync()
    {
        if (_generation != null && !_generation.Process.HasExited) _generation.Expected = true;
        if (_client != null) { await _client.DisposeAsync(); _client = null; }
        if (_process != null)
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (_process.HasExited) { }
                await _process.WaitForExitAsync();
            }
            if (_generation != null)
            {
                await _generation.Monitor;
                await Task.WhenAll(_generation.Output, _generation.Error).WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                _generation = null;
            }
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
            if (_generation != null) _generation.Expected = true;
            _client?.ExpectClose();
            if (_client?.IsConnected == true)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await _client.CallAsync("daemon.shutdown", ct: timeout.Token).WaitAsync(timeout.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            await StopProcessAsync();
        }
        finally { _gate.Release(); _stop.Dispose(); }
    }
}
