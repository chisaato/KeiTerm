namespace Kei.Term.Ssh.Services;

using System.Globalization;
using System.Text;
using Kei.Term.Core.Abstractions;

internal sealed class ZModemSender(ITerminalBinaryChannel channel)
{
    private readonly ModemTransport _transport = new(channel);

    public async Task SendAsync(IReadOnlyList<string> paths, IProgress<TerminalUploadProgress>? progress, CancellationToken ct)
    {
        ZModemCodec codec = new(_transport);
        // rz 启动即发 ZRINIT；先读取，避免在 PTY 尚未切到 raw mode 时发送二进制数据。
        ZModemHeader ready = await ReadRelevantAsync(codec, ct);
        if (ready.Type != 1) throw new IOException("Z Modem 接收端未返回 ZRINIT");
        if ((ready.Position & 0x80000000u) != 0) throw new NotSupportedException("Z Modem 不支持 7 位 ASCII 传输通道");
        int receiveBuffer = (int)(ready.Position & 0xffff);
        int blockSize = receiveBuffer == 0 ? 1024 : Math.Min(1024, receiveBuffer);
        foreach (string path in paths)
        {
            await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > uint.MaxValue) throw new IOException("Z Modem 文件大小不能超过 4 GiB");
            uint length = (uint)file.Length;
            byte[] info = Encoding.UTF8.GetBytes($"{Path.GetFileName(path)}\0{length.ToString(CultureInfo.InvariantCulture)}\0");
            if (info.Length > 1024) throw new IOException("Z Modem 文件名过长");
            ZModemHeader response = default;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                // ZCBIN 保持二进制内容；接收端使用 -y，由其管理覆盖语义。
                await codec.SendHeaderAsync(4, 0x01000000, true, ct);
                await codec.SendDataAsync(info, ZModemCodec.Zcrcw, ct);
                response = await ReadRelevantAsync(codec, ct);
                if (response.Type is 9 or 5) break; // ZRPOS / ZSKIP
                if (attempt == 9) throw new IOException("Z Modem 文件元数据未被确认");
            }
            if (response.Type == 5) throw new IOException($"Z Modem 接收端跳过了文件：{Path.GetFileName(path)}");
            uint position = response.Position;
            if (position > length) throw new IOException("Z Modem 接收端请求的文件偏移无效");
            progress?.Report(new(path, position, length));
            byte[] buffer = new byte[blockSize];
            int retries = 0;
            while (true)
            {
                while (position < length)
                {
                    file.Position = position;
                    int count = (int)Math.Min((uint)buffer.Length, length - position);
                    await file.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                    await codec.SendHeaderAsync(10, position, true, ct); // ZDATA
                    // 每帧以 ZCRCW 确认，兼容不能重叠 I/O 的接收器和有限接收缓冲区。
                    await codec.SendDataAsync(buffer.AsMemory(0, count), ZModemCodec.Zcrcw, ct);
                    response = await ReadRelevantAsync(codec, ct);
                    uint end = position + (uint)count;
                    if (response.Type == 3 && response.Position == end)
                    {
                        position = end;
                        retries = 0;
                        progress?.Report(new(path, position, length));
                    }
                    else if (response.Type == 9 && response.Position <= length)
                    {
                        if (++retries > 10) throw new IOException("Z Modem 接收端重复要求重新传输");
                        position = response.Position;
                    }
                    else if (response.Type == 6 || response.Type == 3)
                    {
                        if (++retries > 10) throw new IOException("Z Modem 数据包未被正确确认");
                    }
                    else throw new IOException("Z Modem 数据传输应答无效");
                }
                // 零字节文件仍需发 ZDATA，建立接收器的文件写入状态。
                if (length == 0)
                {
                    await codec.SendHeaderAsync(10, 0, true, ct);
                    await codec.SendDataAsync(ReadOnlyMemory<byte>.Empty, ZModemCodec.Zcrce, ct);
                }
                await codec.SendHeaderAsync(11, length, false, ct); // ZEOF
                response = await ReadRelevantAsync(codec, ct);
                if (response.Type == 1) break;
                if (response.Type != 9 || response.Position > length || ++retries > 10)
                    throw new IOException("Z Modem 文件结束确认失败");
                position = response.Position;
            }
        }
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await codec.SendHeaderAsync(8, 0, false, ct); // ZFIN
            ZModemHeader response = await ReadRelevantAsync(codec, ct);
            if (response.Type != 8) continue;
            await _transport.SendAsync("OO"u8.ToArray(), ct);
            return;
        }
        throw new IOException("Z Modem 批次结束确认失败");
    }

    private static async Task<ZModemHeader> ReadRelevantAsync(ZModemCodec codec, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            ZModemHeader response = await codec.ReadHeaderAsync(ct);
            if (response.Type == 14) // ZCHALLENGE 的随机值必须原样回传，不能触发任意 shell 命令。
            {
                await codec.SendHeaderAsync(3, response.Position, false, ct);
                continue;
            }
            if (response.Type is 7 or 12 or 16)
                throw new IOException("远端中止了 Z Modem 上传");
            return response;
        }
        throw new IOException("Z Modem 接收端连续挑战失败");
    }
}
