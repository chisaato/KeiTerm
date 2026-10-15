namespace Kei.Term.Ssh.Services;

using System.Runtime.CompilerServices;
using System.Text;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Abstractions;
using Renci.SshNet;

// SCP 使用已校验连接的 exec 通道；Modem 使用当前 PTY 的独占字节通道。
public sealed class TerminalFileUploader : ITerminalFileUploader
{
    private static readonly ConditionalWeakTable<ITerminalSession, SemaphoreSlim> SessionGates = new();

    public async Task UploadAsync(
        ITerminalSession session,
        IReadOnlyList<string> paths,
        TerminalUploadProtocol protocol,
        string remoteDirectory,
        IProgress<TerminalUploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrEmpty(remoteDirectory)) remoteDirectory = ".";
        if (paths.Count == 0) return;
        if (!Enum.IsDefined(protocol)) throw new ArgumentOutOfRangeException(nameof(protocol));
        // 在启动远端接收器之前验证整批文件，避免传到一半才发现目录或非法文件名。
        foreach (string path in paths)
        {
            if (Directory.Exists(path)) throw new IOException("终端上传仅支持文件，目录可以选择粘贴路径或文件名");
            if (!File.Exists(path)) throw new FileNotFoundException("上传文件不存在", path);
            string name = Path.GetFileName(path);
            if (name.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new IOException("上传文件名不能包含换行或 NUL 字符");
            if (protocol == TerminalUploadProtocol.ZModem && new FileInfo(path).Length > uint.MaxValue)
                throw new IOException("Z Modem 文件大小不能超过 4 GiB");
        }

        SemaphoreSlim gate = SessionGates.GetValue(session, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!session.IsConnected) throw new IOException("终端连接已断开");
            if (protocol == TerminalUploadProtocol.Sftp)
            {
                if (session is not SshNetSession sshSession)
                    throw new NotSupportedException("SFTP 上传需要 SSH 连接");
                using SftpClient sftp = await sshSession.ConnectUploadSftpAsync(ct);
                byte[] buffer = new byte[64 * 1024];
                foreach (string path in paths)
                {
                    await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    string remotePath = remoteDirectory.TrimEnd('/') + "/" + Path.GetFileName(path);
                    await using Stream destination = await sftp.OpenAsync(remotePath, FileMode.Create, FileAccess.Write, ct);
                    long length = file.Length;
                    long sent = 0;
                    progress?.Report(new(path, sent, length));
                    while (sent < length)
                    {
                        int count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), ct);
                        if (count == 0) throw new IOException("上传期间本地文件被截断");
                        await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                        sent += count;
                        progress?.Report(new(path, sent, length));
                    }
                    await destination.FlushAsync(ct);
                }
                return;
            }
            if (protocol == TerminalUploadProtocol.Scp)
            {
                if (session is not ISshSession { UnderlyingClient: SshClient client })
                    throw new NotSupportedException("SCP 上传需要 SSH 连接");
                await UploadScpAsync(client, paths, remoteDirectory, progress, ct);
                return;
            }

            if (session is not ITerminalBinaryTransferSession binarySession)
                throw new NotSupportedException("当前连接不支持终端二进制文件传输");
            await using ITerminalBinaryChannel channel = await binarySession.AcquireBinaryChannelAsync(ct);
            string receiver = protocol == TerminalUploadProtocol.ZModem ? "rz -b -y" : "rb -b -y";
            string marker = "KEITERM_" + Guid.NewGuid().ToString("N");
            string receiverCommand = $"cd -- {ScpRemoteFileSystem.ShellQuote(remoteDirectory)} && printf '\\036{marker}\\037' && {receiver}; status=$?; printf '\\036{marker}_END:%s\\037' \"$status\"";
            string command = $"\x15/bin/sh -c {ScpRemoteFileSystem.ShellQuote(receiverCommand)}\r";
            try
            {
                await channel.SendAsync(Encoding.UTF8.GetBytes(command), ct);
                // YMODEM 的 'C' 是单字节握手，必须先过滤命令回显中可能出现的同名字符。
                await ReadStartMarkerAsync(channel, marker, ct);
                if (protocol == TerminalUploadProtocol.ZModem)
                    await new ZModemSender(channel).SendAsync(paths, progress, ct);
                else
                    await new YModemSender(channel).SendAsync(paths, progress, ct);
                int status = await ReadReceiverExitAsync(channel, marker, ct);
                if (status != 0) throw new IOException($"远端接收器上传失败（退出码 {status}）");
                await channel.CompleteAsync();
            }
            catch (TimeoutException ex)
            {
                await CancelReceiverAsync(channel, marker);
                throw new IOException($"{protocol} 接收超时，请确认远端已安装 {(protocol == TerminalUploadProtocol.ZModem ? "rz" : "rb")}（lrzsz），且终端当前处于 shell 提示符", ex);
            }
            catch
            {
                await CancelReceiverAsync(channel, marker);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<int> ReadReceiverExitAsync(ITerminalBinaryChannel channel, string marker, CancellationToken ct)
    {
        byte[] expected = Encoding.ASCII.GetBytes("\u001e" + marker + "_END:");
        ModemTransport transport = new(channel);
        return await transport.ReadResponseAsync(async token =>
        {
            int matched = 0;
            while (matched < expected.Length)
            {
                byte value = await transport.ReadByteAsync(token);
                matched = value == expected[matched] ? matched + 1 : value == expected[0] ? 1 : 0;
            }
            string status = "";
            while (status.Length < 8)
            {
                byte value = await transport.ReadByteAsync(token);
                if (value == 31 && int.TryParse(status, out int result)) return result;
                if (value is < 48 or > 57) throw new IOException("远端接收器结束状态无效");
                status += (char)value;
            }
            throw new IOException("远端接收器结束状态无效");
        }, ct);
    }

    private static async Task ReadStartMarkerAsync(ITerminalBinaryChannel channel, string marker, CancellationToken ct)
    {
        byte[] expected = Encoding.ASCII.GetBytes("\u001e" + marker + "\u001f");
        ModemTransport transport = new(channel);
        await transport.ReadResponseAsync(async token =>
        {
            int matched = 0;
            while (matched < expected.Length)
            {
                byte value = await transport.ReadByteAsync(token);
                matched = value == expected[matched] ? matched + 1 : value == expected[0] ? 1 : 0;
            }
            return true;
        }, ct);
    }

    private static async Task CancelReceiverAsync(ITerminalBinaryChannel channel, string marker)
    {
        try
        {
            // ZMODEM/YMODEM 的取消序列；使用独立短超时，让用户取消也能结束远端接收器。
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(2));
            while (!cleanup.IsCancellationRequested)
            {
                await channel.SendAsync(new byte[] { 24, 24, 24, 24, 24, 24, 24, 24, 8, 8, 8, 8, 8, 8, 8, 8 }, cleanup.Token);
                using CancellationTokenSource interval = CancellationTokenSource.CreateLinkedTokenSource(cleanup.Token);
                interval.CancelAfter(TimeSpan.FromMilliseconds(250));
                try
                {
                    await ReadReceiverExitAsync(channel, marker, interval.Token);
                    await channel.CompleteAsync();
                    return;
                }
                catch (OperationCanceledException) when (!cleanup.IsCancellationRequested)
                {
                    // 接收器启动切换 raw mode 时可能清空输入；重新取消，直到收到真实退出标记。
                }
            }
        }
        catch { /* 断连时不覆盖最初的传输异常。 */ }
    }

    private static async Task UploadScpAsync(
        SshClient client,
        IReadOnlyList<string> paths,
        string remoteDirectory,
        IProgress<TerminalUploadProgress>? progress,
        CancellationToken ct)
    {
        using SshCommand command = client.CreateCommand($"scp -t -- {ScpRemoteFileSystem.ShellQuote(remoteDirectory)}");
        Task execution = command.ExecuteAsync(ct);
        try
        {
            using (Stream input = command.CreateInputStream())
            {
                await ScpTerminalSender.SendAsync(input, command.OutputStream, paths, progress, ct);
            }
            await execution;
            if (command.ExitStatus != 0)
                throw new IOException($"SCP 上传失败：{command.Error.Trim()}");
        }
        finally
        {
            // 失败/取消时先关闭 exec 通道，再观察执行任务，避免遗留接收器或未观察异常。
            if (!execution.IsCompleted) command.Dispose();
            try { await execution; } catch { }
        }
    }
}
