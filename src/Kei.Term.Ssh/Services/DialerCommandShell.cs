namespace Kei.Term.Ssh.Services;

using Renci.SshNet;
using Kei.Term.Core.Transfers;

// SFTP 传输通道上的 exec。单独懒开一条 SshClient，不跟 SFTP 租约的 60 秒空闲计时器走：
// 探测、哈希和归档都不持有文件租约，绑在一起会在空闲关闭时把正在跑的命令掐掉。
// 也不在这条连接上挂端口转发，避免和终端会话抢同一组本地端口。
// 通道 Dispose 时一起关掉；断线后下次 Run 再拨。
internal sealed class DialerCommandShell : IRemoteShell, IDisposable
{
    private readonly SshDialer _dialer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SshClient? _client;
    private int _disposed;

    public DialerCommandShell(SshDialer dialer)
    {
        ArgumentNullException.ThrowIfNull(dialer);
        _dialer = dialer;
    }

    public async Task<RemoteCommandResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            SshClient client = await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using SshCommand ssh = client.CreateCommand(command);
            await ssh.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new RemoteCommandResult(ssh.Result, ssh.Error, ssh.ExitStatus, ssh.ExitSignal);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 不拿闸门：Run 可能正堵在 ExecuteAsync 上，先拆客户端才能让它退出来。
        DisposeClient();
    }

    private async Task<SshClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (!IsLive(_client))
        {
            DisposeClient();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _client = await _dialer.ConnectClientAsync(info => new SshClient(info), cancellationToken, attachPortForwards: false)
                .ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) != 0)
            {
                DisposeClient();
                throw new ObjectDisposedException(nameof(DialerCommandShell));
            }
        }

        return _client!;
    }

    private static bool IsLive(SshClient? client)
    {
        if (client == null)
        {
            return false;
        }

        try
        {
            return client.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void DisposeClient()
    {
        SshClient? client = Interlocked.Exchange(ref _client, null);
        if (client == null)
        {
            return;
        }

        try
        {
            if (IsLive(client))
            {
                client.Disconnect();
            }
        }
        catch
        {
            // 已经断开
        }

        try
        {
            client.Dispose();
        }
        catch
        {
            // 释放路径防御
        }
    }
}
