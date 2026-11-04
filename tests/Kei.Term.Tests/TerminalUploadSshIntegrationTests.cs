namespace Kei.Term.Tests;

using System.Text;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Ssh.Services;

public sealed class TerminalUploadSshIntegrationTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    [SshdFact]
    public async Task CtrlCDuringBoundedOutputBackpressureInterruptsRemoteAndRestoresShell()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await using ISshSession session = await new SshSessionFactory().CreateSessionAsync(sshd.Config(), sshd.KeyAuth());
        await session.ConnectAsync(timeout.Token);
        using var unblock = new ManualResetEventSlim();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string token = Guid.NewGuid().ToString("N");
        string tail = string.Empty;
        int blockOnce = 0;
        Action<byte[]> output = chunk =>
        {
            string text = tail + Encoding.UTF8.GetString(chunk);
            if (text.Contains($"\u001eINTERRUPTED:{token}\u001f", StringComparison.Ordinal)) interrupted.TrySetResult();
            if (text.Contains($"\u001eREADY:{token}\u001f", StringComparison.Ordinal)) ready.TrySetResult();
            // 仅保留有限后缀以识别跨块 marker，不收集洪流全文。
            tail = text.Length > 1024 ? text[^1024..] : text;
            if (text.Contains(new string('X', 64), StringComparison.Ordinal)
                && Interlocked.Exchange(ref blockOnce, 1) == 0)
            {
                blocked.TrySetResult();
                try
                {
                    if (!unblock.Wait(TimeSpan.FromSeconds(25))) throw new TimeoutException("测试输出背压未释放");
                }
                finally { callbackExited.TrySetResult(); }
            }
        };
        session.OutputReceived += output;
        Task<Task>? inputInvocation = null;
        try
        {
            // 无文件、最多 2 MiB/16 次循环；即使 Ctrl+C 失效，远端命令也不会无限运行。
            // 子 shell 的 INT trap 证明前台任务确实被中断，而非自然结束后碰巧接受输入。
            string script = $"trap 'printf \"\\036INTERRUPTED:{token}\\037\"; exit 130' INT; "
                + "i=0; while [ \"$i\" -lt 16 ]; do "
                + "dd if=/dev/zero bs=131072 count=1 2>/dev/null | tr '\\000' X; "
                + "sleep 1; i=$((i+1)); done";
            string command = "stty -echo; sh -c '" + script.Replace("'", "'\\''", StringComparison.Ordinal) + "'\r";
            await session.SendInputAsync(Encoding.UTF8.GetBytes(command), timeout.Token);
            await blocked.Task.WaitAsync(timeout.Token);

            // 使用独立线程区分方法调用返回与返回的 Task 完成，旧死锁会卡在前者。
            inputInvocation = Task.Factory.StartNew(
                () => session.SendInputAsync(new byte[] { 0x03 }, timeout.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task sent = await inputInvocation.WaitAsync(timeout.Token);
            await sent.WaitAsync(timeout.Token);

            unblock.Set();
            await interrupted.Task.WaitAsync(timeout.Token);
            // 命令文本仅含八进制转义及 %s，完整控制字符 marker 不可能来自终端 echo。
            await session.SendInputAsync(Encoding.UTF8.GetBytes(
                $"printf '\\036READY:%s\\037' '{token}'\r"), timeout.Token);
            await ready.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            unblock.Set();
            session.OutputReceived -= output;
            try
            {
                if (inputInvocation != null)
                    await inputInvocation.Unwrap().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await session.DisposeAsync();
                if (blocked.Task.IsCompleted)
                    await callbackExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

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
