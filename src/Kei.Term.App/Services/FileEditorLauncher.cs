namespace Kei.Term.App.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
public class FileEditorLauncher
{
    private readonly ILocalFileTracker _tracker;
    private readonly ISettingsService? _settingsService;
    private readonly IExternalEditorRepository? _editorRepo;
    private readonly ILogger _logger;

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
    }

    public async Task OpenAndTrackAsync(
        Guid sessionId,
        RemoteFileItem remoteFile,
        IRemoteFileSystem fileSystem,
        ExternalEditor? overrideEditor = null,
        string? directExecutablePath = null,
        CancellationToken ct = default)
    {
        string localPath = _tracker.GetLocalCachePath(sessionId, remoteFile.FullPath);

        // 如果本地文件不存在或需要刷新，先下载到本地
        _logger.LogInformation("Downloading remote file for local editing: {RemotePath} -> {LocalPath}", remoteFile.FullPath, localPath);
        await using (var remoteStream = await fileSystem.OpenReadAsync(remoteFile.FullPath, ct))
        await using (var localStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await remoteStream.CopyToAsync(localStream, ct);
        }

        // 校验是否为二进制文件
        if (IsBinaryFile(localPath))
        {
            try { File.Delete(localPath); } catch { }
            throw new InvalidOperationException($"文件 \"{remoteFile.Name}\" 检测为二进制文件，已阻止外部文本编辑器打开。");
        }

        // 注册到文件监视引擎
        await _tracker.RegisterTrackedFileAsync(sessionId, remoteFile.FullPath, localPath, ct);

        // 决策使用哪种方式打开：
        Process? process = null;
        // 1. 指定了临时运行的可执行文件路径
        if (!string.IsNullOrWhiteSpace(directExecutablePath))
        {
            _logger.LogInformation("Launching with directExecutablePath: {ExecutablePath}", directExecutablePath);
            process = LaunchEditor(localPath, directExecutablePath, "\"{path}\"");
            LogLaunchedProcess(process, localPath);
            return;
        }

        // 2. 指定了特定的逻辑 ExternalEditor
        if (overrideEditor != null)
        {
            _logger.LogInformation("Launching with overrideEditor: {EditorName}", overrideEditor.Name);
            process = LaunchWithEditor(localPath, overrideEditor);
            LogLaunchedProcess(process, localPath);
            return;
        }

        // 3. 检查文件后缀规则匹配
        if (_editorRepo != null)
        {
            var rules = await _editorRepo.GetAllAssociationsAsync(ct);
            var matchedEditorId = FileAssociationResolver.ResolveEditorId(remoteFile.Name, rules);
            if (matchedEditorId.HasValue)
            {
                var matchedEditor = await _editorRepo.GetEditorByIdAsync(matchedEditorId.Value, ct);
                if (matchedEditor != null)
                {
                    _logger.LogInformation("Launching with matched rule editor: {EditorName}", matchedEditor.Name);
                    process = LaunchWithEditor(localPath, matchedEditor);
                    LogLaunchedProcess(process, localPath);
                    return;
                }
            }

            // 4. 检查是否有标记为默认的逻辑 ExternalEditor
            var allEditors = await _editorRepo.GetAllEditorsAsync(ct);
            var defaultEditor = allEditors.FirstOrDefault(e => e.IsDefault);
            if (defaultEditor != null)
            {
                _logger.LogInformation("Launching with default editor: {EditorName}", defaultEditor.Name);
                process = LaunchWithEditor(localPath, defaultEditor);
                LogLaunchedProcess(process, localPath);
                return;
            }
        }

        // 5. 兜底回退：用户配置的 CustomEditorPath 或系统默认程序
        string? customEditor = _settingsService?.Current.FileTransfer.CustomEditorPath;
        _logger.LogInformation("Launching with fallback customEditor: {CustomEditor}", customEditor);
        process = LaunchEditor(localPath, customEditor);
        LogLaunchedProcess(process, localPath);
    }

    private void LogLaunchedProcess(Process? process, string localPath)
    {
        if (process == null)
        {
            _logger.LogInformation("Editor process returned null (e.g. launched via Shell/xdg-open)");
            return;
        }

        try
        {
            _logger.LogInformation("Editor process started: Id={ProcessId}, Name={ProcessName}, HasExited={HasExited}, File={LocalPath}",
                process.Id, process.ProcessName, process.HasExited, localPath);

            // open / xdg-open 和单实例编辑器的启动进程可提前退出。
            // 文件监视由用户显式停止或会话释放结束，不能用进程退出推断编辑完成。
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Editor process started, but cannot query info: {Message}", ex.Message);
        }
        finally
        {
            // 仅释放本地进程句柄，不终止外部编辑器。
            process.Dispose();
        }
    }

    private Process? LaunchWithEditor(string localPath, ExternalEditor editor)
    {
        string currentOs = PlatformHelper.CurrentOs;
        string? execPath = editor.GetEffectivePath(currentOs);
        if (string.IsNullOrWhiteSpace(execPath))
        {
            throw new InvalidOperationException($"编辑器 \"{editor.Name}\" 未在操作系统 [{currentOs}] 上配置有效路径或通用命令。");
        }

        return LaunchEditor(localPath, execPath, editor.ArgumentsTemplate);
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
            string bundledCode = Path.Combine(customEditor, "Contents", "Resources", "app", "bin", "code");
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
        if (string.Equals(customEditor, "code", StringComparison.OrdinalIgnoreCase)
            || customEditor.EndsWith("/code", StringComparison.OrdinalIgnoreCase)
            || customEditor.EndsWith("\\code.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(customEditor, "subl", StringComparison.OrdinalIgnoreCase)
            || customEditor.EndsWith("/subl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(customEditor, "kate", StringComparison.OrdinalIgnoreCase))
        {
            if (!args.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(a => a is "-w" or "--wait")) args = "--wait " + args;
        }
        return new ProcessStartInfo(customEditor)
        {
            Arguments = args.Replace("{path}", filePath),
            UseShellExecute = false
        };
    }

    public static Process? LaunchDefaultEditor(string filePath)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return Process.Start("xdg-open", $"\"{filePath}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Process.Start("open", $"\"{filePath}\"");
            }
        }
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

        return null;
    }
}
