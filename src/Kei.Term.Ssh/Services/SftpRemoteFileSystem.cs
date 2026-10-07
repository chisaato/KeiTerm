namespace Kei.Term.Ssh.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Abstractions;

public class SftpRemoteFileSystem : IRemoteFileSystem
{
    private readonly SshDialer _dialer;
    private readonly SftpChannelMode _mode;
    private readonly ILogger _logger;
    private SftpClient? _client;
    private bool _isDisposed;

    public bool IsConnected => !_isDisposed && _client is { IsConnected: true };
    public string WorkingDirectory => IsConnected ? _client!.WorkingDirectory : "/";

    // 连接前访问即为编程错误；方法入口已由 EnsureConnected 保证非空
    private SftpClient _sftpClient => _client ?? throw new InvalidOperationException("SFTP 客户端尚未连接");

    // 兼容构造：直连、不做主机密钥校验
    public SftpRemoteFileSystem(ConnectionInfo connectionInfo, SftpChannelMode mode = SftpChannelMode.Dedicated, ILogger? logger = null)
        : this(
            new SshDialer(
                new SshTarget(connectionInfo.Host, connectionInfo.Port, connectionInfo.Username, connectionInfo.AuthenticationMethods.ToArray()),
                [],
                null,
                new SshClientOptions(connectionInfo.Timeout, TimeSpan.Zero, null),
                logger ?? NullLogger.Instance),
            mode,
            logger)
    {
    }

    // 注：SSH.NET 公共 API 不支持在已有 SshClient 会话上开 sftp 子系统通道，
    // Auto/Subsystem 目前均退化为独立连接（经跳板时借用终端会话的跳板链，不重复登录跳板）
    internal SftpRemoteFileSystem(SshDialer dialer, SftpChannelMode mode, ILogger? logger)
    {
        _dialer = dialer;
        _mode = mode;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (IsConnected)
        {
            return;
        }

        try
        {
            _client?.Dispose();
            _client = await _dialer.ConnectClientAsync(info => new SftpClient(info), ct);
            _logger.LogInformation("SFTP 已连接 Mode={Mode} WorkingDirectory={Dir}", _mode, _client.WorkingDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SFTP 连接失败 Mode={Mode}", _mode);
            throw;
        }
    }

    public Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        string targetPath = string.IsNullOrWhiteSpace(path) ? _sftpClient.WorkingDirectory : path;

        return Task.Run<IReadOnlyList<RemoteFileItem>>(() =>
        {
            var files = _sftpClient.ListDirectory(targetPath);
            var results = new List<RemoteFileItem>();

            foreach (var file in files)
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                results.Add(new RemoteFileItem(
                    file.Name,
                    file.FullName,
                    file.IsDirectory,
                    file.Length,
                    file.LastWriteTimeUtc,
                    FormatPermissions(file),
                    file.UserId,
                    file.GroupId
                ));
            }

            return results.OrderByDescending(r => r.IsDirectory).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }, ct);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        return Task.Run<Stream>(() =>
        {
            return _sftpClient.OpenRead(path);
        }, ct);
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        return Task.Run<Stream>(() =>
        {
            // OpenWrite 使用 OpenOrCreate，短文件覆盖会留下旧尾部；覆盖操作必须截断。
            return _sftpClient.Open(path, FileMode.Create, FileAccess.Write);
        }, ct);
    }

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        return Task.Run(() =>
        {
            if (isDirectory)
            {
                _sftpClient.DeleteDirectory(path);
            }
            else
            {
                _sftpClient.DeleteFile(path);
            }
        }, ct);
    }

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        return Task.Run(() =>
        {
            _sftpClient.RenameFile(oldPath, newPath);
        }, ct);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        return Task.Run(() =>
        {
            _sftpClient.CreateDirectory(path);
        }, ct);
    }

    public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        return Task.Run(() =>
        {
            short mode = (short)octalPermissions;
            _sftpClient.ChangePermissions(path, mode);
        }, ct);
    }

    public Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        return Task.Run<RemoteFileItem?>(() =>
        {
            try
            {
                var file = _sftpClient.Get(path);
                if (file == null) return null;

                return new RemoteFileItem(
                    file.Name,
                    file.FullName,
                    file.IsDirectory,
                    file.Length,
                    file.LastWriteTimeUtc,
                    FormatPermissions(file),
                    file.UserId,
                    file.GroupId
                );
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
        }, ct);
    }

    private static string FormatPermissions(ISftpFile file)
    {
        char type = file.IsDirectory ? 'd' : (file.IsSymbolicLink ? 'l' : '-');
        char r1 = file.OwnerCanRead ? 'r' : '-';
        char w1 = file.OwnerCanWrite ? 'w' : '-';
        char x1 = file.OwnerCanExecute ? 'x' : '-';
        char r2 = file.GroupCanRead ? 'r' : '-';
        char w2 = file.GroupCanWrite ? 'w' : '-';
        char x2 = file.GroupCanExecute ? 'x' : '-';
        char r3 = file.OthersCanRead ? 'r' : '-';
        char w3 = file.OthersCanWrite ? 'w' : '-';
        char x3 = file.OthersCanExecute ? 'x' : '-';
        return $"{type}{r1}{w1}{x1}{r2}{w2}{x2}{r3}{w3}{x3}";
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("SFTP 客户端尚未连接或连接已断开");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        try
        {
            if (_client is { IsConnected: true })
            {
                _client.Disconnect();
            }
            _client?.Dispose();
        }
        catch
        {
            // 忽略释放阶段异常
        }

        await _dialer.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
