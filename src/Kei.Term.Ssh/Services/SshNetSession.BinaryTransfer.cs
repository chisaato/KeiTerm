namespace Kei.Term.Ssh.Services;

using System.Threading.Channels;
using Kei.Term.Core.Abstractions;
using Renci.SshNet;

public partial class SshNetSession : ITerminalBinaryTransferSession
{
    private readonly SemaphoreSlim _binaryGate = new(1, 1);
    private readonly object _outputGate = new();
    private BinaryChannel? _binaryChannel;

    // SSH.NET 的公开 API 无法复用已有 SshClient 的 SFTP subsystem；同一拨号器保留主机校验和现有跳板链。
    internal Task<SftpClient> ConnectUploadSftpAsync(CancellationToken ct)
        => _dialer.ConnectClientAsync(info => new SftpClient(info), ct);

    public async Task<ITerminalBinaryChannel> AcquireBinaryChannelAsync(CancellationToken ct = default)
    {
        await _binaryGate.WaitAsync(ct);
        BinaryChannel channel;
        lock (_outputGate)
        {
            if (!IsConnected)
            {
                _binaryGate.Release();
                throw new IOException("终端连接已断开");
            }

            channel = new(this);
            _binaryChannel = channel;
        }
        try
        {
            // 键盘输入可能已入队但尚未写出；独占后先排空旧输入，再启动接收命令。
            await QueueInputAsync(ReadOnlyMemory<byte>.Empty, ct, throwOnFailure: true, flushOnly: true);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    private void DispatchOutput(byte[] chunk)
    {
        lock (_outputGate)
        {
            if (_binaryChannel is { } channel)
            {
                channel.Receive(chunk);
            }
            else
            {
                OutputReceived?.Invoke(chunk);
            }
        }
    }

    private void CompleteBinaryChannel(Exception? error)
    {
        lock (_outputGate)
        {
            _binaryChannel?.Complete(error);
        }
    }

    private sealed class BinaryChannel(SshNetSession owner) : ITerminalBinaryChannel
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private ReadOnlyMemory<byte> _remaining;
        private int _disposed;

        public void Receive(byte[] data) => _output.Writer.TryWrite(data);
        public void Complete(Exception? error) => _output.Writer.TryComplete(error);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty) return 0;
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            while (_remaining.IsEmpty)
            {
                if (!await _output.Reader.WaitToReadAsync(ct)) return 0;
                if (_output.Reader.TryRead(out byte[]? chunk)) _remaining = chunk;
            }

            int count = Math.Min(buffer.Length, _remaining.Length);
            _remaining[..count].CopyTo(buffer);
            _remaining = _remaining[count..];
            return count;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!owner.IsConnected) throw new IOException("终端连接已断开");
            return owner.QueueInputAsync(data, ct, throwOnFailure: true);
        }

        public ValueTask CompleteAsync()
        {
            Release(replayOutput: true);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Release(replayOutput: false);
            return ValueTask.CompletedTask;
        }

        private void Release(bool replayOutput)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                lock (owner._outputGate)
                {
                    owner._binaryChannel = null;
                    Complete(null);
                    if (replayOutput)
                    {
                        if (!_remaining.IsEmpty) owner.OutputReceived?.Invoke(_remaining.ToArray());
                        while (_output.Reader.TryRead(out byte[]? pending)) owner.OutputReceived?.Invoke(pending);
                    }
                }
            }
            finally { owner._binaryGate.Release(); }
        }
    }
}
