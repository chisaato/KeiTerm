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
    private readonly SftpClient _sftpClient;
    private readonly ISshSession? _hostSession;
    private readonly SftpChannelMode _mode;
    private readonly ILogger _logger;
    private readonly bool _ownsClient;
    private bool _isDisposed;

    public bool IsConnected => !_isDisposed && _sftpClient.IsConnected;
    public string WorkingDirectory => IsConnected ? _sftpClient.WorkingDirectory : "/";

    public SftpRemoteFileSystem(
        ConnectionInfo connectionInfo,
        SftpChannelMode mode = SftpChannelMode.Dedicated,
        ISshSession? hostSession = null,
        ILogger? logger = null)
    {
        _mode = mode;
        _hostSession = hostSession;
        _logger = logger ?? NullLogger.Instance;

        // 根据模式初始化 SftpClient
        _sftpClient = new SftpClient(connectionInfo);
        _ownsClient = true;
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
            await _sftpClient.ConnectAsync(ct);
            _logger.LogInformation("SFTP 已连接 Mode={Mode} WorkingDirectory={Dir}", _mode, _sftpClient.WorkingDirectory);
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
            return _sftpClient.OpenWrite(path);
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

    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;
        if (_ownsClient)
        {
            try
            {
                if (_sftpClient.IsConnected)
                {
                    _sftpClient.Disconnect();
                }
                _sftpClient.Dispose();
            }
            catch
            {
                // 忽略释放阶段异常
            }
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
