namespace Kei.Term.Ssh.Services;

using System.Buffers.Binary;
using System.Text;

internal readonly record struct ZModemHeader(byte Type, uint Position);

// 协议依据 Chuck Forsberg 的公开 ZMODEM specification；CRC/转义与终端解码完全分离。
internal sealed class ZModemCodec(ModemTransport transport)
{
    internal const byte Zdle = 24, Zcrce = 104, Zcrcw = 107;

    public Task SendHeaderAsync(byte type, uint position, bool binary, CancellationToken ct)
        => transport.SendAsync(EncodeHeader(type, position, binary), ct);

    public Task SendDataAsync(ReadOnlyMemory<byte> payload, byte ending, CancellationToken ct)
        => transport.SendAsync(EncodeData(payload.Span, ending), ct);

    internal static byte[] EncodeHeader(byte type, uint position, bool binary)
    {
        byte[] contents = new byte[7];
        contents[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(contents.AsSpan(1), position);
        ushort crc = ModemTransport.Crc16(contents.AsSpan(0, 5));
        contents[5] = (byte)(crc >> 8);
        contents[6] = (byte)crc;
        if (!binary)
        {
            string hex = Convert.ToHexString(contents).ToLowerInvariant();
            return Encoding.ASCII.GetBytes($"**\u0018B{hex}\r\n{(type is 3 or 8 ? "" : "\x11")}");
        }
        List<byte> bytes = [42, Zdle, 65];
        foreach (byte value in contents) Escape(bytes, value);
        return bytes.ToArray();
    }

    internal static byte[] EncodeData(ReadOnlySpan<byte> payload, byte ending)
    {
        byte[] checkedData = new byte[payload.Length + 1];
        payload.CopyTo(checkedData);
        checkedData[^1] = ending;
        ushort crc = ModemTransport.Crc16(checkedData);
        List<byte> bytes = new(payload.Length * 2 + 8);
        foreach (byte value in payload) Escape(bytes, value);
        bytes.Add(Zdle);
        bytes.Add(ending);
        Escape(bytes, (byte)(crc >> 8));
        Escape(bytes, (byte)crc);
        if (ending == Zcrcw) bytes.Add(17);
        return bytes.ToArray();
    }

    private static void Escape(List<byte> bytes, byte value)
    {
        // 总是转义控制字符，可满足 ESCCTL 接收器；CR 与高位 DLE/XON/XOFF 也必须转义。
        if ((value & 0x60) == 0 || value is 0x7f or 0xff)
        {
            bytes.Add(Zdle);
            bytes.Add(value is 0x7f ? (byte)108 : value is 0xff ? (byte)109 : (byte)(value ^ 64));
        }
        else bytes.Add(value);
    }

    public Task<ZModemHeader> ReadHeaderAsync(CancellationToken ct) => transport.ReadResponseAsync(async token =>
    {
        int cancellationCount = 0;
        while (true)
        {
            byte value = await transport.ReadByteAsync(token);
            if (value == Zdle)
            {
                if (++cancellationCount >= 5) throw new IOException("远端取消了 Z Modem 上传");
                continue;
            }
            cancellationCount = 0;
            if (value != 42) continue;
            do { value = await transport.ReadByteAsync(token); } while (value == 42);
            if (value != Zdle) continue;
            byte format = await transport.ReadByteAsync(token);
            if (format is not (65 or 66 or 67)) continue;
            byte[] contents = new byte[format == 67 ? 9 : 7];
            for (int i = 0; i < contents.Length; i++)
                contents[i] = format == 66 ? await ReadHexAsync(token) : await ReadEscapedAsync(token);
            bool valid = format == 67
                ? ModemTransport.Crc32(contents.AsSpan(0, 5)) == BinaryPrimitives.ReadUInt32LittleEndian(contents.AsSpan(5))
                : ModemTransport.Crc16(contents.AsSpan(0, 5)) == BinaryPrimitives.ReadUInt16BigEndian(contents.AsSpan(5));
            if (!valid)
            {
                await SendHeaderAsync(6, 0, false, token); // ZNAK 请求重新发送损坏的应答。
                continue;
            }
            // HEX 的 CR/LF 是帧的一部分；消费它，不能把结尾字符留到 shell/下一次应答。
            if (format == 66)
            {
                byte ending = (byte)((await transport.ReadByteAsync(token)) & 127);
                if (ending == 13) ending = (byte)((await transport.ReadByteAsync(token)) & 127);
                if (ending != 10) throw new IOException("Z Modem HEX 帧结尾无效");
                if (contents[0] is not (3 or 8))
                {
                    if (await transport.ReadByteAsync(token) != 17) throw new IOException("Z Modem HEX 帧缺少 XON");
                }
            }
            return new ZModemHeader(contents[0], BinaryPrimitives.ReadUInt32LittleEndian(contents.AsSpan(1)));
        }
    }, ct);

    private async Task<byte> ReadHexAsync(CancellationToken ct)
    {
        int high = HexDigit((byte)((await transport.ReadByteAsync(ct)) & 127));
        int low = HexDigit((byte)((await transport.ReadByteAsync(ct)) & 127));
        if (high < 0 || low < 0) throw new IOException("Z Modem HEX 帧数据无效");
        return (byte)(high * 16 + low);
    }

    private static int HexDigit(byte value) => value is >= 48 and <= 57 ? value - 48
        : value is >= 97 and <= 102 ? value - 97 + 10 : -1;

    private async Task<byte> ReadEscapedAsync(CancellationToken ct)
    {
        byte value;
        do { value = await transport.ReadByteAsync(ct); } while (value is 17 or 19 or 145 or 147);
        if (value != Zdle) return value;
        do { value = await transport.ReadByteAsync(ct); } while (value is 17 or 19 or 145 or 147);
        return value switch
        {
            108 => 127,
            109 => 255,
            _ when (value & 0x60) == 0x40 => (byte)(value ^ 64),
            _ => throw new IOException("Z Modem 二进制帧转义无效")
        };
    }
}
