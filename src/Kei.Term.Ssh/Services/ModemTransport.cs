namespace Kei.Term.Ssh.Services;

using Kei.Term.Core.Abstractions;

// 单字节读取保留流中剩余数据；每次协议应答共享一个截止时间，噪声不能无限延长超时。
internal sealed class ModemTransport(ITerminalBinaryChannel channel)
{
    private readonly byte[] _one = new byte[1];
    internal TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => channel.SendAsync(data, ct);

    public async Task<T> ReadResponseAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ResponseTimeout);
        try { return await read(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Modem 接收端未响应"); }
    }

    public async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        if (await channel.ReadAsync(_one, ct) == 0) throw new EndOfStreamException("终端连接在文件传输期间关闭");
        return _one[0];
    }

    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}
