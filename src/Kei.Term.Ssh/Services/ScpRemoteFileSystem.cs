namespace Kei.Term.Ssh.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

public partial class ScpRemoteFileSystem : IRemoteFileSystem
{
    private readonly SshDialer _dialer;
    private readonly ILogger _logger;
    private readonly ScpShellFileSystem _shell;
    private ScpClient? _scp;
    private SshClient? _exec;
    private bool _isDisposed;

    public bool IsConnected => !_isDisposed && _scp is { IsConnected: true } && _exec is { IsConnected: true };
    public string WorkingDirectory { get; private set; } = ".";

    private ScpClient _scpClient => _scp ?? throw new InvalidOperationException("SCP 客户端尚未连接");
    private SshClient _sshCommandClient => _exec ?? throw new InvalidOperationException("SCP 辅助通道尚未连接");

    // 兼容构造：直连、不做主机密钥校验
    public ScpRemoteFileSystem(ConnectionInfo connectionInfo, ILogger? logger = null)
        : this(
            new SshDialer(
                new SshTarget(connectionInfo.Host, connectionInfo.Port, connectionInfo.Username, connectionInfo.AuthenticationMethods.ToArray()),
                [],
                null,
                new SshClientOptions(connectionInfo.Timeout, TimeSpan.Zero, null),
                logger ?? NullLogger.Instance),
            logger)
    {
    }

    internal ScpRemoteFileSystem(SshDialer dialer, ILogger? logger)
    {
        _dialer = dialer;
        _logger = logger ?? NullLogger.Instance;
        _shell = new ScpShellFileSystem(ExecuteCommandAsync);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (IsConnected) return;

        _scp?.Dispose();
        _exec?.Dispose();
        _scp = await _dialer.ConnectClientAsync(info => new ScpClient(info, RemotePathTransformation.ShellQuote), ct);
        _exec = await _dialer.ConnectClientAsync(info => new SshClient(info), ct);
        _logger.LogInformation("SCP & Exec 辅助通道已建立");

        // 获取远程工作目录 pwd
        try
        {
            string result = await _shell.ExecuteCheckedAsync("pwd", ct);
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
        return await _shell.ListDirectoryAsync(target, ct);
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

        ct.ThrowIfCancellationRequested();
        var buffer = new ScpUploadBuffer((stream, commitToken) =>
            Task.Run(() => _scpClient.Upload(stream, path), commitToken));
        return Task.FromResult<Stream>(buffer);
    }

    public Task CommitWriteAsync(Stream stream, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        return stream is ScpUploadBuffer buffer
            ? buffer.CommitAsync(ct)
            : throw new ArgumentException("写入流不属于 SCP 上传", nameof(stream));
    }

    public async Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        await _shell.DeleteAsync(path, isDirectory, ct);
    }

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        await _shell.RenameAsync(oldPath, newPath, ct);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        await _shell.CreateDirectoryAsync(path, ct);
    }

    public async Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        await _shell.ChangePermissionsAsync(path, octalPermissions, ct);
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

    // POSIX 单引号转义：单引号内 $ ` \ 均不展开，内部单引号以 '\'' 拼接。
    // 双引号包裹无法阻止 $(...) 与反引号展开，远端恶意文件名可借此在服务器上执行命令
    internal static string ShellQuote(string path)
        => "'" + path.Replace("'", "'\\''") + "'";

    private async Task<RemoteCommandResult> ExecuteCommandAsync(string text, CancellationToken ct)
    {
        using SshCommand command = _sshCommandClient.CreateCommand(text);
        await command.ExecuteAsync(ct);
        return new RemoteCommandResult(command.Result, command.Error, command.ExitStatus, command.ExitSignal);
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

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (_scp is { IsConnected: true }) _scp.Disconnect();
            _scp?.Dispose();
        }
        catch { }

        try
        {
            if (_exec is { IsConnected: true }) _exec.Disconnect();
            _exec?.Dispose();
        }
        catch { }

        await _dialer.DisposeAsync();
        GC.SuppressFinalize(this);
    }

}
