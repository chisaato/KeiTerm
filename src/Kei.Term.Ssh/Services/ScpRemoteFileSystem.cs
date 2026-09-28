namespace Kei.Term.Ssh.Services;

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

public partial class ScpRemoteFileSystem : IRemoteFileSystem
{
    private readonly ScpClient _scpClient;
    private readonly SshClient _sshCommandClient;
    private readonly ILogger _logger;
    private bool _isDisposed;

    public bool IsConnected => !_isDisposed && _scpClient.IsConnected && _sshCommandClient.IsConnected;
    public string WorkingDirectory { get; private set; } = ".";

    public ScpRemoteFileSystem(ConnectionInfo connectionInfo, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _scpClient = new ScpClient(connectionInfo, RemotePathTransformation.ShellQuote);
        _sshCommandClient = new SshClient(connectionInfo);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (IsConnected) return;

        await _scpClient.ConnectAsync(ct);
        await _sshCommandClient.ConnectAsync(ct);
        _logger.LogInformation("SCP & Exec 辅助通道已建立");

        // 获取远程工作目录 pwd
        try
        {
            var cmd = _sshCommandClient.CreateCommand("pwd");
            var result = await Task.Run(() => cmd.Execute(), ct);
            if (!string.IsNullOrWhiteSpace(result))
            {
                WorkingDirectory = result.Trim();
            }
        }
        catch
        {
            WorkingDirectory = "/";
        }
    }

    public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        string target = string.IsNullOrWhiteSpace(path) ? WorkingDirectory : path;
        // 使用标准 ls -la 命令辅助获取目录列表
        string commandText = $"ls -la --time-style=+%s \"{EscapePath(target)}\" 2>/dev/null || ls -la \"{EscapePath(target)}\"";
        var cmd = _sshCommandClient.CreateCommand(commandText);

        string output = await Task.Run(() => cmd.Execute(), ct);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<RemoteFileItem>();

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("total", StringComparison.OrdinalIgnoreCase)) continue;

            var item = ParseLsLine(line, target);
            if (item != null && item.Name is not "." and not "..")
            {
                list.Add(item);
            }
        }

        return list.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        // SCP 下载为整流传输，先缓存至内存流中以支持任意读取
        var ms = new MemoryStream();
        await Task.Run(() => _scpClient.Download(path, ms), ct);
        ms.Position = 0;
        return ms;
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        // 返回代理流：关闭时触发 SCP 上传
        var proxyStream = new ScpUploadProxyStream(async (stream) =>
        {
            stream.Position = 0;
            await Task.Run(() => _scpClient.Upload(stream, path));
        });

        return Task.FromResult<Stream>(proxyStream);
    }

    public async Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        string cmdText = isDirectory ? $"rm -rf \"{EscapePath(path)}\"" : $"rm -f \"{EscapePath(path)}\"";
        var cmd = _sshCommandClient.CreateCommand(cmdText);
        await Task.Run(() => cmd.Execute(), ct);
    }

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        string cmdText = $"mv \"{EscapePath(oldPath)}\" \"{EscapePath(newPath)}\"";
        var cmd = _sshCommandClient.CreateCommand(cmdText);
        await Task.Run(() => cmd.Execute(), ct);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        string cmdText = $"mkdir -p \"{EscapePath(path)}\"";
        var cmd = _sshCommandClient.CreateCommand(cmdText);
        await Task.Run(() => cmd.Execute(), ct);
    }

    public async Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        string octal = Convert.ToString(octalPermissions, 8);
        string cmdText = $"chmod {octal} \"{EscapePath(path)}\"";
        var cmd = _sshCommandClient.CreateCommand(cmdText);
        await Task.Run(() => cmd.Execute(), ct);
    }

    public async Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        string parentDir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "/";
        var items = await ListDirectoryAsync(parentDir, ct);
        string targetName = Path.GetFileName(path);
        return items.FirstOrDefault(i => string.Equals(i.Name, targetName, StringComparison.Ordinal));
    }

    private static string EscapePath(string path)
    {
        return path.Replace("\"", "\\\"");
    }

    private static RemoteFileItem? ParseLsLine(string line, string directory)
    {
        // 典型格式: drwxr-xr-x 2 root root 4096 1695880000 filename
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 7) return null;

        string permissions = parts[0];
        bool isDirectory = permissions.StartsWith('d');
        long size = 0;
        long.TryParse(parts[4], out size);

        // 解析时间戳或普通日期
        DateTimeOffset mtime = DateTimeOffset.UtcNow;
        int nameStartIndex;
        if (long.TryParse(parts[5], out long unixSeconds))
        {
            mtime = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            nameStartIndex = 6;
        }
        else if (parts.Length >= 8)
        {
            nameStartIndex = 8;
        }
        else
        {
            nameStartIndex = parts.Length - 1;
        }

        string name = string.Join(" ", parts.Skip(nameStartIndex));
        string fullPath = directory.TrimEnd('/') + "/" + name;

        return new RemoteFileItem(name, fullPath, isDirectory, size, mtime, permissions);
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("SCP 客户端未连接");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed) return ValueTask.CompletedTask;
        _isDisposed = true;

        try
        {
            if (_scpClient.IsConnected) _scpClient.Disconnect();
            _scpClient.Dispose();
        }
        catch { }

        try
        {
            if (_sshCommandClient.IsConnected) _sshCommandClient.Disconnect();
            _sshCommandClient.Dispose();
        }
        catch { }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // 用于代理上传的临时内存写入流
    private sealed class ScpUploadProxyStream : MemoryStream
    {
        private readonly Func<Stream, Task> _onDisposeAsync;
        private bool _disposed;

        public ScpUploadProxyStream(Func<Stream, Task> onDisposeAsync)
        {
            _onDisposeAsync = onDisposeAsync;
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await _onDisposeAsync(this);
            }
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _onDisposeAsync(this).GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }
    }
}
