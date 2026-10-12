namespace Kei.Term.Ssh.Services;

using System.Globalization;
using System.Text;
using Kei.Term.Core.Abstractions;

// SCP sink 协议只使用当前 SSH 客户端，不复制认证材料或重建未经校验的连接。
internal static class ScpTerminalSender
{
    public static async Task SendAsync(
        Stream input, Stream output, IReadOnlyList<string> paths,
        IProgress<TerminalUploadProgress>? progress, CancellationToken ct)
    {
        await ReadAckAsync(output, ct);
        byte[] buffer = new byte[64 * 1024];
        foreach (string path in paths)
        {
            await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = file.Length;
            string metadata = $"C0644 {length.ToString(CultureInfo.InvariantCulture)} {Path.GetFileName(path)}\n";
            await input.WriteAsync(Encoding.UTF8.GetBytes(metadata), ct);
            await input.FlushAsync(ct);
            await ReadAckAsync(output, ct);
            long sent = 0;
            progress?.Report(new(path, sent, length));
            while (sent < length)
            {
                int count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), ct);
                if (count == 0) throw new IOException("上传期间本地文件被截断");
                await input.WriteAsync(buffer.AsMemory(0, count), ct);
                sent += count;
                progress?.Report(new(path, sent, length));
            }
            await input.WriteAsync(new byte[] { 0 }, ct);
            await input.FlushAsync(ct);
            await ReadAckAsync(output, ct);
        }
    }

    private static async Task ReadAckAsync(Stream output, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        byte[] one = new byte[1];
        await output.ReadExactlyAsync(one, timeout.Token);
        if (one[0] == 0) return;
        if (one[0] is not (1 or 2)) throw new IOException("SCP 接收端响应无效");
        List<byte> message = [];
        while (message.Count < 4096)
        {
            await output.ReadExactlyAsync(one, timeout.Token);
            if (one[0] == '\n') break;
            message.Add(one[0]);
        }
        throw new IOException($"SCP 接收端拒绝上传：{Encoding.UTF8.GetString(message.ToArray())}");
    }
}
