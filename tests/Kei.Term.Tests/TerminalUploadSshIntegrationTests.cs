namespace Kei.Term.Tests;

using System.Text;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Ssh.Services;

public sealed class TerminalUploadSshIntegrationTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    [SshdFact]
    public async Task BinaryChannelSuppressesTerminalOutputAndOrdinaryInputThenRestoresTerminal()
    {
        await using ISshSession session = await new SshSessionFactory().CreateSessionAsync(sshd.Config(), sshd.KeyAuth());
        await session.ConnectAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        ITerminalBinaryTransferSession capability = Assert.IsAssignableFrom<ITerminalBinaryTransferSession>(session);
        await using ITerminalBinaryChannel channel = await capability.AcquireBinaryChannelAsync(timeout.Token);
        int normalOutput = 0;
        session.OutputReceived += _ => Interlocked.Increment(ref normalOutput);
        string token = Guid.NewGuid().ToString("N");
        // 用真正的 shell read 接收一行，验证普通键盘输入无法混进独占通道。
        await channel.SendAsync(Encoding.UTF8.GetBytes(
            $"printf '\\036{token}\\037'; IFS= read -r value; printf '\\036RESULT:%s\\037' \"$value\"\r"), timeout.Token);
        await ReadThroughAsync(channel, "\u001e" + token + "\u001f", timeout.Token);
        await session.SendInputAsync("SHOULD_BE_BLOCKED\r"u8.ToArray(), timeout.Token);
        await channel.SendAsync("UPLOAD_BYTES\r"u8.ToArray(), timeout.Token);
        string result = await ReadThroughAsync(channel, "\u001f", timeout.Token);
        Assert.Contains("\u001eRESULT:UPLOAD_BYTES\u001f", result);
        Assert.Equal(0, Volatile.Read(ref normalOutput));
        await channel.CompleteAsync();

        TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutputReceived += _ => resumed.TrySetResult();
        await session.SendInputAsync("printf 'TERMINAL_RESUMED\\n'\r"u8.ToArray(), timeout.Token);
        await resumed.Task.WaitAsync(timeout.Token);
    }

    [SshdFact]
    public async Task ScpAndSftpTerminalUploadsUseTheirSelectedProtocol()
    {
        string directory = TerminalModemTransferTests.CreateTemporaryDirectory();
        try
        {
            string source = Path.Combine(directory, "upload.bin");
            byte[] contents = Enumerable.Range(0, 9017).Select(index => (byte)(index * 13)).ToArray();
            await File.WriteAllBytesAsync(source, contents);
            await using ISshSession session = await new SshSessionFactory().CreateSessionAsync(sshd.Config(), sshd.KeyAuth());
            await session.ConnectAsync();
            foreach (TerminalUploadProtocol protocol in new[] { TerminalUploadProtocol.Scp, TerminalUploadProtocol.Sftp })
            {
                string destination = Path.Combine(directory, protocol.ToString());
                Directory.CreateDirectory(destination);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
                await new TerminalFileUploader().UploadAsync(session, [source], protocol, destination, null, timeout.Token);
                Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Combine(destination, "upload.bin")));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<string> ReadThroughAsync(ITerminalBinaryChannel channel, string ending, CancellationToken ct)
    {
        StringBuilder text = new();
        byte[] one = new byte[1];
        while (true)
        {
            if (await channel.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
            text.Append((char)one[0]);
            if (text.Length > 64 * 1024) throw new IOException("远端测试输出过长");
            if (text.ToString().EndsWith(ending, StringComparison.Ordinal)) return text.ToString();
        }
    }
}
