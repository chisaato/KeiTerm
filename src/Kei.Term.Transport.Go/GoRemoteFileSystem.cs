using System.Text.Json;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

namespace Kei.Term.Transport.Go;

public sealed class GoRemoteFileSystem : IRemoteFileSystem
{
    private readonly GoSshSession _session;
    private readonly bool _ownsSession;
    private int _disposed;
    internal GoRemoteFileSystem(GoSshSession session, bool ownsSession = true) { _session = session; _ownsSession = ownsSession; }
    internal GoRemoteFileSystem Borrow() => new(_session, ownsSession: false);
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _session.IsConnected;
    public string WorkingDirectory => ".";
    public bool SupportsResume => false;
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _session.ConnectAsync(ct);
        await CallAsync("sftp.open", new { sessionId = _session.RemoteId }, ct);
    }
    private Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct)
    {
        if (!IsConnected) throw new IOException("Go 文件通道已断开或释放");
        return _session.Client.CallAsync(method, parameters, ct);
    }
    public async Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        var result = await CallAsync("sftp.list", new { sessionId = _session.RemoteId, path }, ct);
        return result.EnumerateArray().Select(item => Map(item, path.TrimEnd('/') + "/" + item.GetProperty("name").GetString())).ToArray();
    }
    private static RemoteFileItem Map(JsonElement item, string path)
    {
        uint mode = item.GetProperty("mode").GetUInt32();
        long permissions = mode & 0x1ff;
        if ((mode & (1u << 23)) != 0) permissions |= 0x800;
        if ((mode & (1u << 22)) != 0) permissions |= 0x400;
        if ((mode & (1u << 20)) != 0) permissions |= 0x200;
        return new(item.GetProperty("name").GetString()!, path, item.GetProperty("isDir").GetBoolean(),
            item.GetProperty("size").GetInt64(), DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("mtime").GetInt64()),
            Convert.ToString(permissions, 8),
            item.TryGetProperty("uid", out var uid) ? unchecked((int)uid.GetUInt32()) : 0,
            item.TryGetProperty("gid", out var gid) ? unchecked((int)gid.GetUInt32()) : 0,
            IsSymbolicLink: (mode & (1u << 27)) != 0);
    }
    public async Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default)
        => Map(await CallAsync("sftp.stat", new { sessionId = _session.RemoteId, path }, ct), path);
    public async Task<RemoteFileItem?> GetAttributesAsync(string path, CancellationToken ct = default)
    {
        var result = await CallAsync("sftp.lstat", new { sessionId = _session.RemoteId, path }, ct);
        return result.ValueKind == JsonValueKind.Null ? null : Map(result, path);
    }
    public Task<RemoteFileItem?> GetFileAttributesAsync(string path, CancellationToken ct = default) => GetItemAsync(path, ct);
    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        => CallAsync("sftp.mkdir", new { sessionId = _session.RemoteId, path }, ct);
    public Task DeleteFileAsync(string path, CancellationToken ct = default)
        => CallAsync("sftp.remove", new { sessionId = _session.RemoteId, path, isDir = false }, ct);
    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
        => CallAsync("sftp.remove", new { sessionId = _session.RemoteId, path, isDir = true }, ct);
    public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
        => throw new NotSupportedException("Go IPC 尚未提供拒绝符号链接的安全递归删除");
    public Task DeleteEntryAsync(string path, bool isDirectory, CancellationToken ct = default)
        => isDirectory ? DeleteDirectoryAsync(path, ct) : DeleteFileAsync(path, ct);
    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
        => CallAsync("sftp.rename", new { sessionId = _session.RemoteId, oldPath, newPath }, ct);
    public Task RenameOverwriteAsync(string oldPath, string newPath, CancellationToken ct = default)
        => CallAsync("sftp.posix_rename", new { sessionId = _session.RemoteId, oldPath, newPath }, ct);
    public Task ChangeOwnershipAsync(string path, int userId, int groupId, CancellationToken ct = default)
        => CallAsync("sftp.chown", new { sessionId = _session.RemoteId, path, uid = unchecked((uint)userId), gid = unchecked((uint)groupId) }, ct);
    public Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default)
        => CallAsync("sftp.chmod", new { sessionId = _session.RemoteId, path, mode = octalPermissions }, ct);
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => OpenAsync(path, false, ct);
    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default) => OpenAsync(path, true, ct);
    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset != 0) throw new NotSupportedException("Go IPC 尚未提供断点续传");
        return OpenReadAsync(path, ct);
    }
    public Task<Stream> OpenWriteAsync(string path, long offset, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset != 0) throw new NotSupportedException("Go IPC 尚未提供断点续传");
        return OpenWriteAsync(path, ct);
    }
    private async Task<Stream> OpenAsync(string path, bool write, CancellationToken ct)
    {
        var result = await CallAsync(write ? "sftp.upload_stream" : "sftp.download_stream", new { sessionId = _session.RemoteId, path }, ct);
        return _session.Client.GetStream(result.GetProperty("streamId").GetUInt32());
    }
    public Task CommitWriteAsync(Stream stream, CancellationToken ct = default)
    {
        if (!IsConnected) throw new IOException("Go 文件通道已断开或释放");
        return stream is GoIpcStream ipc ? ipc.CompleteWriteAsync(ct) : throw new ArgumentException("不是 Go IPC 文件流", nameof(stream));
    }
    public async ValueTask DisposeAsync()
    {
        // 租约视图不拥有共享连接，不能让一个使用方关闭其他租约。
        if (!_ownsSession) return;
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _session.DisposeAsync();
    }
}
