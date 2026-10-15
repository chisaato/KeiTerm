namespace Kei.Term.Ssh.Services;

using System.Globalization;
using System.Text;
using Kei.Term.Core.Abstractions;

// YMODEM batch / CRC-16：block 0 元数据、1 KiB 数据包、NAK 重传、双 EOT 和空 block 0 结束批次。
internal sealed class YModemSender(ITerminalBinaryChannel channel)
{
    private const byte Ack = 6, Nak = 21, Can = 24, RequestCrc = 67;
    private readonly ModemTransport _transport = new(channel);

    public async Task SendAsync(IReadOnlyList<string> paths, IProgress<TerminalUploadProgress>? progress, CancellationToken ct)
    {
        await WaitForAsync(RequestCrc, ct);
        foreach (string path in paths)
        {
            await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = file.Length;
            byte[] info = Encoding.UTF8.GetBytes($"{Path.GetFileName(path)}\0{length.ToString(CultureInfo.InvariantCulture)}\0");
            if (info.Length > 1024) throw new IOException("Y Modem 文件名过长");
            await SendBlockAsync(0, info, info.Length <= 128 ? 128 : 1024, 0, ct);
            await WaitForAsync(RequestCrc, ct);
            long sent = 0;
            byte block = 1;
            byte[] buffer = new byte[1024];
            progress?.Report(new(path, sent, length));
            while (sent < length)
            {
                int count = (int)Math.Min(buffer.Length, length - sent);
                await file.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                await SendBlockAsync(block, buffer.AsMemory(0, count), 1024, 26, ct);
                block = unchecked((byte)(block + 1));
                sent += count;
                progress?.Report(new(path, sent, length));
            }

            // 接收器可直接 ACK 首个 EOT，或先 NAK 再 ACK 第二个 EOT，两种都遵循标准。
            for (int attempt = 0; ; attempt++)
            {
                if (attempt >= 10) throw new IOException("Y Modem 文件结束确认失败");
                await _transport.SendAsync(new byte[] { 4 }, ct);
                byte response = await ReadControlAsync(ct);
                if (response == Ack) break;
            }
            await WaitForAsync(RequestCrc, ct);
        }
        await SendBlockAsync(0, ReadOnlyMemory<byte>.Empty, 128, 0, ct);
    }

    private async Task SendBlockAsync(byte number, ReadOnlyMemory<byte> data, int blockSize, byte padding, CancellationToken ct)
    {
        byte[] packet = new byte[blockSize + 5];
        packet[0] = blockSize == 128 ? (byte)1 : (byte)2;
        packet[1] = number;
        packet[2] = (byte)~number;
        packet.AsSpan(3, blockSize).Fill(padding);
        data.Span.CopyTo(packet.AsSpan(3));
        ushort crc = ModemTransport.Crc16(packet.AsSpan(3, blockSize));
        packet[^2] = (byte)(crc >> 8);
        packet[^1] = (byte)crc;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await _transport.SendAsync(packet, ct);
            try
            {
                if (await ReadControlAsync(ct) == Ack) return;
            }
            catch (TimeoutException) when (attempt < 9) { }
        }
        throw new IOException("Y Modem 数据包重复发送后仍未确认");
    }

    private async Task WaitForAsync(byte expected, CancellationToken ct)
    {
        byte control = await ReadControlAsync(ct);
        if (control != expected) throw new IOException("Y Modem 接收端握手无效（需要 CRC 模式）");
    }

    private Task<byte> ReadControlAsync(CancellationToken ct) => _transport.ReadResponseAsync(async token =>
    {
        while (true)
        {
            byte value = await _transport.ReadByteAsync(token);
            if (value == Can)
            {
                if (await _transport.ReadByteAsync(token) == Can) throw new IOException("远端取消了 Y Modem 上传");
                continue;
            }
            if (value is Ack or Nak or RequestCrc) return value;
        }
    }, ct);
}
