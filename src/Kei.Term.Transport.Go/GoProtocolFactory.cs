using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;

namespace Kei.Term.Transport.Go;

// 仅在显式选择 Go 且 ConnectAsync 时启动共享 Sidecar。
public sealed class GoProtocolFactory(Func<CancellationToken, Task<GoIpcClient>> clientProvider)
{
    public GoProtocolFactory(GoIpcClient client) : this(_ => Task.FromResult(client)) { }

    public async Task<IReadOnlyList<string>> ListSerialPortsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = await clientProvider(ct).ConfigureAwait(false);
        var result = await client.CallAsync("serial.listPorts", new { }, ct).WaitAsync(ct).ConfigureAwait(false);
        return result.GetProperty("names").EnumerateArray().Select(p => p.GetString()!).ToArray();
    }

    public ITerminalSession CreateTelnet(Guid id, TelnetConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        if (options.Port is < 1 or > 65535 || options.Timeout <= TimeSpan.Zero || options.Timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.TerminalType) || options.TerminalType.Length > 255 ||
            options.TerminalType.Any(c => c is < ' ' or > '~')) throw new ArgumentException("TerminalType must be printable ASCII.", nameof(options));
        return new GoProtocolTerminalSession(id, clientProvider, "telnet.connect", new
        {
            host = options.Host, port = options.Port, terminalType = options.TerminalType,
            timeoutMilliseconds = (int)Math.Ceiling(options.Timeout.TotalMilliseconds)
        }, canResize: true);
    }

    public ITerminalSession CreateSerial(Guid id, SerialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new GoSerialTerminalSession(id, clientProvider, new
        {
            portName = options.PortName, baudRate = options.BaudRate, dataBits = options.DataBits,
            parity = options.Parity.ToString(), stopBits = options.StopBits.ToString(), flowControl = options.FlowControl.ToString(),
            dtrEnable = options.DtrEnable, rtsEnable = options.RtsEnable,
            readTimeoutMilliseconds = options.ReadTimeoutMilliseconds, writeTimeoutMilliseconds = options.WriteTimeoutMilliseconds
        }, canSetRts: options.FlowControl != SerialFlowControl.RtsCts);
    }
}
