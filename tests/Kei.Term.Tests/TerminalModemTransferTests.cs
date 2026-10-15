namespace Kei.Term.Tests;

using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Ssh.Services;

public sealed class TerminalModemTransferTests
{
    [Fact]
    public void ChecksumsMatchPublishedCheckValues()
    {
        Assert.Equal(0x31c3, ModemTransport.Crc16("123456789"u8));
        Assert.Equal(0xcbf43926u, ModemTransport.Crc32("123456789"u8));
    }

    [Fact]
    public async Task ZModemRejectsCorruptedResponseAndAcceptsRetransmission()
    {
        await using TestChannel channel = new();
        // ZRINIT HEX response from a CRC-16 capable receiver, then the same frame with a corrupted CRC.
        byte[] valid = Encoding.ASCII.GetBytes("**\u0018B0100000000aa51\r\n\u0011");
        byte[] corrupt = valid.ToArray();
        corrupt[15] = (byte)'0';
        channel.Enqueue(corrupt);
        channel.Enqueue(valid);
        ZModemHeader header = await new ZModemCodec(new ModemTransport(channel)).ReadHeaderAsync(default);
        Assert.Equal((byte)1, header.Type);
        Assert.Equal(0u, header.Position);
        Assert.Single(channel.Sent);
        Assert.StartsWith("**\u0018B06", Encoding.ASCII.GetString(channel.Sent[0]));
    }

    [Fact]
    public async Task YModemRetransmitsNakAndPreservesExactLengthAndContents()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "空 格.bin");
            byte[] source = Enumerable.Range(0, 2309).Select(index => (byte)(index * 37)).ToArray();
            await File.WriteAllBytesAsync(path, source);
            List<byte> received = [];
            int dataPackets = 0, eots = 0;
            byte[]? retry = null;
            await using TestChannel channel = new();
            channel.Enqueue([67]);
            channel.OnSend = packet =>
            {
                if (packet.Length == 1 && packet[0] == 4)
                {
                    channel.Enqueue(++eots == 1 ? new byte[] { 21 } : new byte[] { 6, 67 });
                    return Task.CompletedTask;
                }
                int size = packet[0] == 1 ? 128 : 1024;
                Assert.Equal(size + 5, packet.Length);
                Assert.Equal((byte)~packet[1], packet[2]);
                ushort checksum = ReferenceCrc16(packet.AsSpan(3, size));
                Assert.Equal((byte)(checksum >> 8), packet[^2]);
                Assert.Equal((byte)checksum, packet[^1]);
                if (packet[1] == 0)
                {
                    string info = Encoding.UTF8.GetString(packet, 3, size);
                    if (packet[3] == 0) channel.Enqueue([6]);
                    else
                    {
                        Assert.StartsWith("空 格.bin\0" + source.Length + "\0", info);
                        channel.Enqueue([6, 67]);
                    }
                }
                else if (++dataPackets == 1)
                {
                    retry = packet.ToArray();
                    channel.Enqueue([21]);
                }
                else
                {
                    if (dataPackets == 2) Assert.Equal(retry, packet);
                    received.AddRange(packet.AsSpan(3, Math.Min(size, source.Length - received.Count)).ToArray());
                    channel.Enqueue([6]);
                }
                return Task.CompletedTask;
            };
            await new YModemSender(channel).SendAsync([path], null, default);
            Assert.Equal(source, received.ToArray());
            Assert.Equal(4, dataPackets);
            Assert.Equal(2, eots);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ReceiverCancellationStopsYModemWithoutSendingData()
    {
        await using TestChannel channel = new();
        channel.Enqueue([24, 24]);
        await Assert.ThrowsAsync<IOException>(() => new YModemSender(channel).SendAsync([], null, default));
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task ScpUploadsExactBinaryPayloadAndSurfacesRemoteError()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "test file.bin");
            byte[] data = [0, 24, 13, 10, 255, 1];
            await File.WriteAllBytesAsync(path, data);
            using MemoryStream input = new();
            using MemoryStream responses = new(new byte[] { 0, 0, 0 });
            await ScpTerminalSender.SendAsync(input, responses, [path], null, default);
            Assert.Equal(Encoding.UTF8.GetBytes("C0644 6 test file.bin\n").Concat(data).Append((byte)0), input.ToArray());
            using MemoryStream failure = new(new byte[] { 1 }.Concat(Encoding.UTF8.GetBytes("Permission denied\n")).ToArray());
            IOException error = await Assert.ThrowsAsync<IOException>(() =>
                ScpTerminalSender.SendAsync(input, failure, [path], null, default));
            Assert.Contains("Permission denied", error.Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ushort ReferenceCrc16(ReadOnlySpan<byte> bytes)
    {
        // 独立逐位多项式除法，检查发送包的 CRC 与 CRC 字节顺序。
        int remainder = 0;
        foreach (byte value in bytes)
            for (int bit = 7; bit >= 0; bit--)
            {
                bool carry = (remainder & 0x8000) != 0;
                remainder = (remainder << 1) | ((value >> bit) & 1);
                if (carry) remainder ^= 0x1021;
            }
        for (int bit = 0; bit < 16; bit++)
        {
            bool carry = (remainder & 0x8000) != 0;
            remainder <<= 1;
            if (carry) remainder ^= 0x1021;
        }
        return (ushort)remainder;
    }

    internal static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "keiterm_modem_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestChannel : ITerminalBinaryChannel
    {
        private readonly Channel<byte> _bytes = Channel.CreateUnbounded<byte>();
        public List<byte[]> Sent { get; } = [];
        public Func<byte[], Task>? OnSend { get; set; }
        public void Enqueue(byte[] bytes) { foreach (byte value in bytes) _bytes.Writer.TryWrite(value); }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            byte value = await _bytes.Reader.ReadAsync(ct);
            buffer.Span[0] = value;
            return 1;
        }
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            byte[] packet = data.ToArray();
            Sent.Add(packet);
            if (OnSend != null) await OnSend(packet);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class LrzszFactAttribute : FactAttribute
{
    public LrzszFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("KEITERM_LRZSZ_RECEIVER")))
            Skip = "设置 KEITERM_LRZSZ_RECEIVER 为真实 lrz/rz 可执行文件路径以运行协议互通测试";
    }
}

public sealed class TerminalModemInteropTests
{
    [LrzszFact]
    public Task ZModemInteroperatesWithLrzsz() => VerifyReceiverAsync(false);

    [LrzszFact]
    public Task YModemInteroperatesWithLrzsz() => VerifyReceiverAsync(true);

    [LrzszFact]
    public Task ZModemUploaderRunsReceiverAndWaitsForExit() => VerifyUploaderAsync(TerminalUploadProtocol.ZModem);

    [LrzszFact]
    public Task YModemUploaderRunsReceiverAndWaitsForExit() => VerifyUploaderAsync(TerminalUploadProtocol.YModem);

    [LrzszFact]
    public Task ZModemCancellationStopsReceiverAndRestoresChannel() => VerifyUploaderAsync(TerminalUploadProtocol.ZModem, cancel: true);

    [LrzszFact]
    public Task YModemCancellationStopsReceiverAndRestoresChannel() => VerifyUploaderAsync(TerminalUploadProtocol.YModem, cancel: true);

    private static async Task VerifyUploaderAsync(TerminalUploadProtocol protocol, bool cancel = false)
    {
        if (OperatingSystem.IsWindows()) return;
        string directory = TerminalModemTransferTests.CreateTemporaryDirectory();
        // 实际 shell 执行含单引号、空格、美元符的路径，验证两层 ShellQuote 与起止标记。
        string output = Path.Combine(directory, "received ' C $(literal)");
        Directory.CreateDirectory(output);
        try
        {
            string receiver = Environment.GetEnvironmentVariable("KEITERM_LRZSZ_RECEIVER")!;
            foreach (string name in new[] { "rz", "rb" })
            {
                string launcher = Path.Combine(directory, name);
                await File.WriteAllTextAsync(launcher,
                    $"#!/bin/sh\nexec {ScpRemoteFileSystem.ShellQuote(receiver)} {(name == "rb" ? "--ymodem" : "--zmodem")} \"$@\"\n");
                File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            string full = Path.Combine(directory, "空 格.bin");
            byte[] contents = Enumerable.Range(0, 24_509).Select(index => (byte)(index * 79 + index / 131)).ToArray();
            await File.WriteAllBytesAsync(full, contents);
            ProcessStartInfo start = new("/bin/sh")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment["PATH"] = directory + ":" + Environment.GetEnvironmentVariable("PATH");
            using Process process = Process.Start(start)!;
            _ = process.StandardError.ReadToEndAsync();
            await using ShellProcessSession session = new(process);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            try
            {
                IProgress<TerminalUploadProgress>? progress = cancel ? new CancelAfterFirstBlock(timeout) : null;
                Task upload = new TerminalFileUploader().UploadAsync(session, [full], protocol, output, progress, timeout.Token);
                if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
                else await upload;
                Assert.True(session.ChannelCompleted);
                if (!cancel) Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Combine(output, Path.GetFileName(full))));
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class CancelAfterFirstBlock(CancellationTokenSource cancellation) : IProgress<TerminalUploadProgress>
    {
        public void Report(TerminalUploadProgress value)
        {
            if (value.BytesTransferred > 0) cancellation.Cancel();
        }
    }

    private static async Task VerifyReceiverAsync(bool ymodem)
    {
        string directory = TerminalModemTransferTests.CreateTemporaryDirectory();
        string output = Path.Combine(directory, "received");
        Directory.CreateDirectory(output);
        try
        {
            string full = Path.Combine(directory, "空 格.bin");
            string empty = Path.Combine(directory, "empty.bin");
            byte[] contents = Enumerable.Range(0, 300_513).Select(index => (byte)(index * 73 + index / 251)).ToArray();
            await File.WriteAllBytesAsync(full, contents);
            await File.WriteAllBytesAsync(empty, []);
            ProcessStartInfo start = new(Environment.GetEnvironmentVariable("KEITERM_LRZSZ_RECEIVER")!)
            {
                WorkingDirectory = output,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(ymodem ? "--ymodem" : "--zmodem");
            start.ArgumentList.Add("-b");
            start.ArgumentList.Add("-y");
            using Process process = Process.Start(start)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            await using ProcessChannel channel = new(process);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            try
            {
                if (ymodem) await new YModemSender(channel).SendAsync([full, empty], null, timeout.Token);
                else await new ZModemSender(channel).SendAsync([full, empty], null, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await error);
                Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Combine(output, Path.GetFileName(full))));
                Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(output, "empty.bin")));
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ProcessChannel(Process process) : ITerminalBinaryChannel
    {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => process.StandardOutput.BaseStream.ReadAsync(buffer, ct);
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            await process.StandardInput.BaseStream.WriteAsync(data, ct);
            await process.StandardInput.BaseStream.FlushAsync(ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ShellProcessSession(Process process) : ITerminalSession, ITerminalBinaryTransferSession, ITerminalBinaryChannel
    {
        private bool _firstSend = true;
        public bool ChannelCompleted { get; private set; }
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => !process.HasExited;
        public event Action<byte[]>? OutputReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => SendAsync(data, ct);
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<ITerminalBinaryChannel> AcquireBinaryChannelAsync(CancellationToken ct = default)
            => Task.FromResult<ITerminalBinaryChannel>(this);
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => process.StandardOutput.BaseStream.ReadAsync(buffer, ct);
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (_firstSend)
            {
                // 管道 shell 没有 PTY 行规程；模拟 PTY 对 Ctrl-U 与回车的处理，再执行真正的命令。
                _firstSend = false;
                data = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data.Span).TrimStart('\u0015').Replace('\r', '\n'));
            }
            await process.StandardInput.BaseStream.WriteAsync(data, ct);
            await process.StandardInput.BaseStream.FlushAsync(ct);
        }
        public ValueTask CompleteAsync() { ChannelCompleted = true; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
