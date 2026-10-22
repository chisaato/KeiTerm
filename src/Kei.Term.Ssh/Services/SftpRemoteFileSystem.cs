namespace Kei.Term.Ssh.Services;

using Kei.Term.Core.Services;
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
using Kei.Term.Core.Transfers;
using Kei.Term.Ssh.Abstractions;

public class SftpRemoteFileSystem : IRemoteFileSystem
{
    private readonly SshDialer? _dialer;
    private readonly SftpChannelMode _mode;
    private readonly ILogger _logger;
    private readonly bool _ownsConnection;
    private readonly Func<Exception?>? _leaseFailure;
    private SftpClient? _client;
    private bool _isDisposed;

    public bool SupportsResume => true;

    public bool IsConnected
    {
        get
        {
            if (_isDisposed || _client == null)
            {
                return false;
            }

            try
            {
                return _client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

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
        _ownsConnection = true;
    }

    // 借用传输通道上已经连上的客户端：不拨号、不释放。租约失败由 leaseFailure 报告。
    internal SftpRemoteFileSystem(SftpClient client, Func<Exception?>? leaseFailure, ILogger? logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _leaseFailure = leaseFailure;
        _mode = SftpChannelMode.Dedicated;
        _logger = logger ?? NullLogger.Instance;
        _ownsConnection = false;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        EnsureLease();
        ThrowIfDisposed();

        if (IsConnected)
        {
            return;
        }

        if (!_ownsConnection || _dialer == null)
        {
            throw new IOException("传输连接已断开");
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

    public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        EnsureReady();

        string targetPath = string.IsNullOrWhiteSpace(path) ? _sftpClient.WorkingDirectory : path;
        List<ISftpFile> files = await Task.Run(() => _sftpClient.ListDirectory(targetPath).ToList(), ct);
        List<RemoteFileItem> results = [];
        foreach (ISftpFile file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Name is "." or "..")
            {
                continue;
            }

            results.Add(await ToItemAsync(file, ct));
        }

        return results.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, NaturalStringComparer.Instance).ToList();
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        => OpenReadAsync(path, 0, ct);

    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        EnsureReady();

        return Task.Run<Stream>(() =>
        {
            SftpFileStream stream = _sftpClient.Open(path, FileMode.Open, FileAccess.Read);
            return SeekOrDispose(stream, offset);
        }, ct);
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        => OpenWriteAsync(path, 0, ct);

    public Task<Stream> OpenWriteAsync(string path, long offset, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        EnsureReady();

        return Task.Run<Stream>(() =>
        {
            // lstat。符号链接即使目标不可读也必须拒绝，不能 Open 跟随覆盖。
            RejectLinkOrDirectory(path, allowMissing: offset == 0);
            if (offset == 0)
            {
                // OpenWrite 使用 OpenOrCreate，短文件覆盖会留下旧尾部；覆盖操作必须截断。
                return _sftpClient.Open(path, FileMode.Create, FileAccess.Write);
            }

            // 续传：打开已有普通文件并定位，不截断。文件不存在时由服务器拒绝。
            SftpFileStream stream = _sftpClient.Open(path, FileMode.Open, FileAccess.Write);
            return SeekOrDispose(stream, offset);
        }, ct);
    }

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        EnsureReady();

        return Task.Run(() =>
        {
            if (isDirectory)
            {
                _sftpClient.DeleteDirectory(path);
            }
            else
            {
                RejectLinkOrDirectory(path, allowMissing: false);
                _sftpClient.DeleteFile(path);
            }
        }, ct);
    }

    public Task DeleteEntryAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        EnsureReady();

        return Task.Run(() =>
        {
            // 传输清理仍走上面的拒绝逻辑。DeleteFile/DeleteDirectory 会先 REALPATH，
            // 断链会被解析成目标路径。目录项自己的 Delete 直接对列出的名称发 REMOVE 或 RMDIR。
            _ = isDirectory;
            ISftpFile? file = FindChild(path);
            if (file == null)
            {
                throw new SftpPathNotFoundException(path);
            }

            file.Delete();
        }, ct);
    }

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        EnsureReady();

        return Task.Run(() =>
        {
            _sftpClient.RenameFile(oldPath, newPath);
        }, ct);
    }

    public Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        EnsureReady();

        return Task.Run(() =>
        {
            SftpOverwriteRename.Execute(
                () => _sftpClient.RenameFile(oldPath, newPath, isPosix: true),
                () => TargetExists(newPath),
                () => _sftpClient.RenameFile(oldPath, newPath));
        }, ct);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        EnsureReady();

        return Task.Run(() =>
        {
            _sftpClient.CreateDirectory(path);
        }, ct);
    }

    public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default)
    {
        EnsureReady();

        return Task.Run(() =>
        {
            short mode = (short)octalPermissions;
            _sftpClient.ChangePermissions(path, mode);
        }, ct);
    }

    public async Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
    {
        EnsureReady();

        ISftpFile? file;
        try
        {
            file = await Task.Run(() => _sftpClient.Get(path), ct);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }

        if (file == null)
        {
            return null;
        }

        return await ToItemAsync(file, ct);
    }

    public async Task<RemoteFileItem?> GetAttributesAsync(string path, CancellationToken ct = default)
    {
        EnsureReady();
        // Get() 会先 realpath，符号链接已经被跟随。必须列父目录，用该目录项自己的 lstat。
        try
        {
            ISftpFile? file = await Task.Run(() => FindChild(path), ct);
            return file == null ? null : ToLstatItem(file);
        }
        catch (SftpPathNotFoundException)
        {
            // 归档合并检查会查询尚未创建的多层路径；父目录不存在也表示该条目不存在。
            return null;
        }
    }

    public Task SetLastWriteTimeUtcAsync(string path, DateTime lastWriteTimeUtc, CancellationToken ct = default)
    {
        EnsureReady();
        DateTime utc = ToUtc(lastWriteTimeUtc);

        return Task.Run(() =>
        {
            _sftpClient.SetLastWriteTimeUtc(path, utc);
        }, ct);
    }

    private async Task<RemoteFileItem> ToItemAsync(ISftpFile file, CancellationToken ct)
    {
        bool isLink = file.IsSymbolicLink;
        bool? targetIsDirectory = null;
        long? targetSize = null;
        bool broken = false;
        bool indeterminate = false;
        if (isLink)
        {
            // 探测失败只用于区分目标类型，不向外抛。取消除外。
            SftpLinkFollower.Probe probe = await SftpLinkFollower.ProbeAsync(_sftpClient, file.FullName, ct);
            if (probe.Kind == SftpLinkTargetKind.Directory)
            {
                targetIsDirectory = true;
            }
            else if (probe.Kind == SftpLinkTargetKind.File)
            {
                targetIsDirectory = false;
                targetSize = probe.Size;
            }
            else if (probe.Kind == SftpLinkTargetKind.Broken)
            {
                broken = true;
            }
            else
            {
                indeterminate = true;
            }
        }

        return new RemoteFileItem(
            file.Name,
            file.FullName,
            file.IsDirectory,
            file.Length,
            ToUtcOffset(file.LastWriteTimeUtc),
            FormatPermissions(file),
            file.UserId,
            file.GroupId,
            isLink,
            targetIsDirectory,
            targetSize,
            LinkTargetModified: null,
            IsBrokenSymbolicLink: broken,
            IsIndeterminateSymbolicLink: indeterminate);
    }

    // 不探测、不跟随。安全检查用这个，避免把不可读链接 Open 成普通文件。
    private static RemoteFileItem ToLstatItem(ISftpFile file)
        => new(
            file.Name,
            file.FullName,
            file.IsDirectory,
            file.Length,
            ToUtcOffset(file.LastWriteTimeUtc),
            FormatPermissions(file),
            file.UserId,
            file.GroupId,
            file.IsSymbolicLink);

    private void RejectLinkOrDirectory(string path, bool allowMissing)
    {
        ISftpFile? file = FindChild(path);
        if (file == null)
        {
            if (allowMissing)
            {
                return;
            }

            throw new SftpPathNotFoundException(path);
        }

        if (file.IsSymbolicLink)
        {
            throw new TransferPathRejectedException($"拒绝跟随符号链接写入或删除：{path}");
        }

        if (file.IsDirectory)
        {
            throw new TransferPathRejectedException($"拒绝把目录当作部分文件：{path}");
        }
    }

    private bool TargetExists(string path) => FindChild(path) != null;

    // 不解析路径本身，避免 realpath 跟随最后一段符号链接。
    // 反斜杠属于远端文件名，不能换成斜杠。属性、覆盖检查和删除都走这里。
    private ISftpFile? FindChild(string path)
    {
        int slash = path.LastIndexOf('/');
        string parent = slash <= 0 ? "/" : path[..slash];
        string name = slash < 0 ? path : path[(slash + 1)..];
        if (name.Length == 0)
        {
            return null;
        }

        foreach (ISftpFile file in _sftpClient.ListDirectory(parent))
        {
            if (file.Name == name)
            {
                return file;
            }
        }

        return null;
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

    private void EnsureReady()
    {
        EnsureLease();
        ThrowIfDisposed();
        EnsureConnected();
    }

    private void EnsureLease()
    {
        Exception? failure = _leaseFailure?.Invoke();
        if (failure != null)
        {
            throw failure;
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("SFTP 客户端尚未连接或连接已断开");
        }
    }

    private static Stream SeekOrDispose(SftpFileStream stream, long offset)
    {
        if (offset == 0)
        {
            return stream;
        }

        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // SFTP 时间戳是 UTC。Kind 未标明时按 UTC 解释，避免 DateTimeOffset 把它当成本地时间。
    private static DateTimeOffset ToUtcOffset(DateTime value) => new(ToUtc(value));

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
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

        // 共享连接由传输通道释放。这里不标记失效，避免调用方误释放视图后租约无法继续。
        if (!_ownsConnection)
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

        if (_dialer != null)
        {
            await _dialer.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
