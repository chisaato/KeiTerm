namespace Kei.Term.App.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;

// 外部默认编辑器唤醒器与回写同步调度器
public class FileEditorLauncher : IAsyncDisposable
{
    private readonly ILocalFileTracker _tracker;
    private readonly ISettingsService? _settingsService;
    private readonly IExternalEditorRepository? _editorRepo;
    private readonly ILogger _logger;
    private readonly Lock _waitLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _waits = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _waitTasks = [];
    private bool _disposed;
    private Task? _disposeTask;

    public FileEditorLauncher(
        ILocalFileTracker tracker,
        ISettingsService? settingsService = null,
        IExternalEditorRepository? editorRepo = null,
        ILogger? logger = null)
    {
        _tracker = tracker;
        _settingsService = settingsService;
        _editorRepo = editorRepo;
        _logger = logger ?? NullLogger.Instance;
        _tracker.FileUntracked += OnFileUntracked;
    }

    public async Task OpenAndTrackAsync(
        Guid sessionId,
        RemoteFileItem remoteFile,
        IRemoteFileSystem fileSystem,
        ExternalEditor? overrideEditor = null,
        string? directExecutablePath = null,
        CancellationToken ct = default,
        bool useSystemDefault = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string localPath = _tracker.GetLocalCachePath(sessionId, remoteFile.FullPath);
        bool alreadyTracked = _tracker.IsTracking(localPath);
        ProcessStartInfo startInfo = useSystemDefault ? CreateDefaultEditorStartInfo(localPath)
            : await ResolveStartInfoAsync(localPath, remoteFile.Name, overrideEditor, directExecutablePath, ct);
        CancelEditorWait(localPath);
        try
        {
            // 再次打开正在编辑的文件时保留本地修改，不能从远端覆盖缓存。
            if (!alreadyTracked)
            {
                _logger.LogInformation("Downloading remote file for local editing: Session={SessionId}, {RemotePath} -> {LocalPath}", sessionId, remoteFile.FullPath, localPath);
                await using (Stream remoteStream = await fileSystem.OpenReadAsync(remoteFile.FullPath, ct))
                await using (FileStream localStream = new(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await remoteStream.CopyToAsync(localStream, ct);
                }
                if (IsBinaryFile(localPath))
                {
                    File.Delete(localPath);
                    throw new InvalidOperationException($"文件 \"{remoteFile.Name}\" 检测为二进制文件，已阻止外部文本编辑器打开。");
                }
                await _tracker.RegisterTrackedFileAsync(sessionId, remoteFile.FullPath, localPath, ct);
            }
            else _logger.LogInformation("Reopening tracked local cache: {LocalPath}", localPath);

            StartAndObserveEditor(startInfo, localPath);
        }
        catch
        {
            if (!alreadyTracked && _tracker.IsTracking(localPath))
                await _tracker.UnregisterTrackedFileAsync(localPath, CancellationToken.None);
            throw;
        }
    }

    private async Task<ProcessStartInfo> ResolveStartInfoAsync(string localPath, string fileName,
        ExternalEditor? overrideEditor, string? directExecutablePath, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(directExecutablePath))
            return CreateEditorStartInfo(localPath, directExecutablePath);
        ExternalEditor? editor = overrideEditor;
        if (editor == null && _editorRepo != null)
        {
            var rules = await _editorRepo.GetAllAssociationsAsync(ct);
            Guid? editorId = FileAssociationResolver.ResolveEditorId(fileName, rules);
            if (editorId.HasValue) editor = await _editorRepo.GetEditorByIdAsync(editorId.Value, ct);
            editor ??= (await _editorRepo.GetAllEditorsAsync(ct)).FirstOrDefault(e => e.IsDefault);
        }
        if (editor != null)
        {
            string? executable = editor.GetEffectivePath(PlatformHelper.CurrentOs);
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException($"编辑器 \"{editor.Name}\" 未在操作系统 [{PlatformHelper.CurrentOs}] 上配置有效路径或通用命令。");
            return CreateEditorStartInfo(localPath, executable, editor.ArgumentsTemplate);
        }
        string? customEditor = _settingsService?.Current.FileTransfer.CustomEditorPath;
        return string.IsNullOrWhiteSpace(customEditor)
            ? CreateDefaultEditorStartInfo(localPath) : CreateEditorStartInfo(localPath, customEditor);
    }

    private void StartAndObserveEditor(ProcessStartInfo startInfo, string localPath)
    {
        bool waitsForClose = WaitsForEditorClose(startInfo);
        _logger.LogInformation("Launching editor: Executable={Executable}, WaitForClose={WaitForClose}, File={LocalPath}",
            startInfo.FileName, waitsForClose, localPath);
        Process? process = Process.Start(startInfo);
        if (process == null)
        {
            _logger.LogInformation("Editor launch returned no process; monitoring requires manual stop: {LocalPath}", localPath);
            return;
        }
        _logger.LogInformation("Editor process started: ProcessId={ProcessId}, WaitForClose={WaitForClose}, File={LocalPath}",
            process.Id, waitsForClose, localPath);
        if (!waitsForClose)
        {
            _logger.LogInformation("Editor launcher has no reliable close signal; monitoring requires manual stop: {LocalPath}", localPath);
            process.Dispose();
            return;
        }
        lock (_waitLock)
        {
            if (_disposed) { process.Dispose(); return; }
            if (_waits.Remove(localPath, out CancellationTokenSource? previous)) previous.Cancel();
            CancellationTokenSource cancellation = new();
            _waits[localPath] = cancellation;
            Task task = Task.Run(() => ObserveEditorExitAsync(process, localPath, cancellation));
            _waitTasks.Add(task);
            _ = RemoveWaitTaskAsync(task);
        }
    }

    private async Task ObserveEditorExitAsync(Process process, string localPath, CancellationTokenSource cancellation)
    {
        try
        {
            _logger.LogInformation("Waiting for editor close: ProcessId={ProcessId}, File={LocalPath}", process.Id, localPath);
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            _logger.LogInformation("Editor wait returned: ExitCode={ExitCode}, File={LocalPath}", process.ExitCode, localPath);
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Editor wait failed; keeping monitoring and cache: {LocalPath}", localPath);
                return;
            }
            await _tracker.CheckForChangesAsync(localPath, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            // 检查事件已排队回写，保留缓存供最后一次提交及失败恢复使用。
            await _tracker.UnregisterTrackedFileAsync(localPath, cancellation.Token).ConfigureAwait(false);
            _logger.LogInformation("Editor closed; monitoring stopped, cache preserved: {LocalPath}", localPath);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Editor close check failed; preserving cache: {LocalPath}", localPath);
        }
        finally
        {
            lock (_waitLock)
            {
                if (_waits.TryGetValue(localPath, out CancellationTokenSource? current) && ReferenceEquals(current, cancellation))
                    _waits.Remove(localPath);
            }
            process.Dispose();
            cancellation.Dispose();
        }
    }

    private async Task RemoveWaitTaskAsync(Task task)
    {
        await task.ConfigureAwait(false);
        lock (_waitLock) _waitTasks.Remove(task);
    }

    private void OnFileUntracked(object? sender, string localPath)
        => CancelEditorWait(localPath);

    private void CancelEditorWait(string localPath)
    {
        lock (_waitLock)
        {
            if (_waits.TryGetValue(localPath, out CancellationTokenSource? cancellation)) cancellation.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_waitLock)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = true;
            _tracker.FileUntracked -= OnFileUntracked;
            foreach (CancellationTokenSource cancellation in _waits.Values) cancellation.Cancel();
            pending = _waitTasks.ToArray();
            _disposeTask = Task.WhenAll(pending);
            return new ValueTask(_disposeTask);
        }
    }

    // 探测文件前 8KB 是否包含零字节或高比例控制字符，以此判定是否为二进制文件
    public static bool IsBinaryFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            byte[] buffer = new byte[8192];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) return false;

            int controlChars = 0;
            for (int i = 0; i < read; i++)
            {
                byte b = buffer[i];
                if (b == 0) return true; // NUL 字节几乎必然为二进制文件
                if (b < 7 || (b > 13 && b < 27))
                {
                    controlChars++;
                }
            }

            // 如果控制字符占比超过 15%，判定为二进制
            return (controlChars / (double)read) > 0.15;
        }
        catch
        {
            return false;
        }
    }

    public static Process? LaunchEditor(string filePath, string? customEditor = null, string? argumentsTemplate = null)
    {
        if (!string.IsNullOrWhiteSpace(customEditor))
        {
            try
            {
                ProcessStartInfo psi = CreateEditorStartInfo(filePath, customEditor, argumentsTemplate);
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法调用编辑器 \"{customEditor}\": {ex.Message}", ex);
            }
        }

        return LaunchDefaultEditor(filePath);
    }

    public static ProcessStartInfo CreateEditorStartInfo(string filePath, string customEditor, string? argumentsTemplate = null)
    {
        string template = string.IsNullOrWhiteSpace(argumentsTemplate) ? "\"{path}\"" : argumentsTemplate;
        if (OperatingSystem.IsMacOS() && customEditor.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            // VS Code 的 CLI 能等待当前文件关闭，避免启动应用后立即停止回写监视。
            string bundledBin = Path.Combine(customEditor, "Contents", "Resources", "app", "bin");
            string bundledCode = Path.Combine(bundledBin, "code");
            if (!File.Exists(bundledCode)) bundledCode = Path.Combine(bundledBin, "code-insiders");
            if (File.Exists(bundledCode)) customEditor = bundledCode;
            else
            {
                ProcessStartInfo app = new("/usr/bin/open") { UseShellExecute = false };
                app.ArgumentList.Add("-W");
                app.ArgumentList.Add("-a");
                app.ArgumentList.Add(customEditor);
                app.ArgumentList.Add(filePath);
                if (template != "\"{path}\"")
                {
                    // 自定义参数保持原模板语义；open 的文件参数负责文档关联。
                    app.ArgumentList.Clear();
                    app.Arguments = $"-W -a \"{customEditor.Replace("\"", "\\\"")}\" \"{filePath.Replace("\"", "\\\"")}\" --args " + template.Replace("{path}", filePath);
                }
                return app;
            }
        }

        string args = template;
        if (IsWaitCapableEditor(customEditor))
        {
            if (!args.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(a => a is "-w" or "--wait")) args = "--wait " + args;
        }
        if (OperatingSystem.IsWindows() && TryCreateWindowsCodeCli(customEditor, out ProcessStartInfo? codeCli))
        {
            codeCli.Arguments += " " + args.Replace("{path}", filePath);
            return codeCli;
        }
        return new ProcessStartInfo(customEditor)
        {
            Arguments = args.Replace("{path}", filePath),
            UseShellExecute = false
        };
    }

    private static bool TryCreateWindowsCodeCli(string command, out ProcessStartInfo info)
    {
        info = null!;
        string name = Path.GetFileName(command).ToLowerInvariant();
        if (name is not ("code" or "code.cmd" or "code.exe" or "code-insiders" or "code-insiders.cmd" or "code - insiders.exe")) return false;
        string? resolved = File.Exists(command) ? Path.GetFullPath(command) : null;
        if (resolved == null && Path.GetDirectoryName(command) is "" or null)
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string candidate = Path.Combine(directory.Trim('"'), name.EndsWith(".cmd") || name.EndsWith(".exe") ? name : name + ".cmd");
                if (File.Exists(candidate)) { resolved = candidate; break; }
            }
        }
        if (resolved == null) return false;
        string? root = Path.GetDirectoryName(resolved);
        if (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) root = Path.GetDirectoryName(root);
        if (root == null) return false;
        string executable = resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? resolved
            : Path.Combine(root, name.Contains("insiders", StringComparison.Ordinal) ? "Code - Insiders.exe" : "Code.exe");
        string script = Path.Combine(root, "resources", "app", "out", "cli.js");
        if (!File.Exists(executable) || !File.Exists(script)) return false;

        // 与官方 code.cmd 等价，直接运行 CLI 保留 --wait 的进程生命周期。
        info = new(executable) { Arguments = $"\"{script}\"", UseShellExecute = false };
        info.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        info.Environment.Remove("VSCODE_DEV");
        return true;
    }

    private static bool IsWaitCapableEditor(string executable)
    {
        string name = executable.Replace('\\', '/').Split('/').Last().ToLowerInvariant();
        return name is "code" or "code.exe" or "code.cmd" or "code-insiders" or "code-insiders.exe" or "code-insiders.cmd" or "code - insiders.exe"
            or "subl" or "subl.exe" or "kate" or "kate.exe";
    }

    private static bool WaitsForEditorClose(ProcessStartInfo startInfo)
        => (IsWaitCapableEditor(startInfo.FileName)
            && startInfo.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(a => a is "-w" or "--wait"))
            || (startInfo.FileName == "/usr/bin/open" && (startInfo.ArgumentList.Contains("-W") || startInfo.Arguments.StartsWith("-W ", StringComparison.Ordinal)));

    public static ProcessStartInfo CreateDefaultEditorStartInfo(string filePath)
    {
        if (OperatingSystem.IsWindows()) return new(filePath) { UseShellExecute = true };
        ProcessStartInfo info = new(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
        if (OperatingSystem.IsMacOS()) info.ArgumentList.Add("-W");
        info.ArgumentList.Add(filePath);
        return info;
    }

    public static Process? LaunchDefaultEditor(string filePath)
    {
        try { return Process.Start(CreateDefaultEditorStartInfo(filePath)); }
        catch (Exception ex)
        {
            // 降级使用普通 ProcessStartInfo
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                };
                return Process.Start(psi);
            }
            catch
            {
                throw new InvalidOperationException($"无法调用系统默认程序打开文件: {filePath}，原因: {ex.Message}", ex);
            }
        }
    }
}
