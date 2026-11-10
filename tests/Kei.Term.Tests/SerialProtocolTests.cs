using System.Diagnostics;
using System.IO.Ports;
using System.Text.Json;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Infrastructure.Protocols;

namespace Kei.Term.Tests;

public sealed class SerialProtocolTests
{
    [UnprivilegedLinuxSerialFact]
    public async Task Pty_RealPermissionDenialReportsAccessDenied_AndRestoredPortCanReopen()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        await using var pty = await PtyPair.StartAsync();
        // 仅修改本测试刚创建的专属 PTY slave，不触碰用户串口设备。
        string slave = File.ResolveLinkTarget(pty.First, returnFinalTarget: true)!.FullName;
        UnixFileMode original = File.GetUnixFileMode(slave);
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First });
        await session.ConnectAsync();
        await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            File.SetUnixFileMode(slave, UnixFileMode.None);
            var error = await Assert.ThrowsAsync<SerialConnectionException>(() => session.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(SerialConnectionFailure.AccessDenied, error.Failure);
            Assert.NotNull(error.InnerException);
            Assert.False(session.IsConnected);
        }
        finally { File.SetUnixFileMode(slave, original); }

        await session.ConnectAsync();
        using var peer = new SerialPort(pty.Second) { ReadTimeout = 1000, WriteTimeout = 1000 };
        peer.Open();
        await session.SendInputAsync(new byte[] { 87 });
        Assert.Equal(87, await Task.Run(peer.ReadByte).WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Fact]
    public void ConfigurationFields_RoundTripUsesWhitelistAndStringEnums()
    {
        var options = new SerialOptions
        {
            PortName = "/dev/ttyUSB0", BaudRate = 115200, DataBits = 7,
            Parity = SerialParity.Even, StopBits = SerialStopBits.Two,
            FlowControl = SerialFlowControl.XonXoff, DtrEnable = true, RtsEnable = false,
            ReadTimeoutMilliseconds = 234, WriteTimeoutMilliseconds = 567
        };
        var fields = SerialConfigurationFields.FromOptions(options);
        Assert.Equal(new[] { "baudRate", "dataBits", "dtrEnable", "flowControl", "parity", "portName", "rtsEnable", "stopBits" }, fields.Keys.Order().ToArray());
        Assert.Equal("Even", fields["parity"].GetString());
        Assert.Equal("Two", fields["stopBits"].GetString());
        Assert.Equal("XonXoff", fields["flowControl"].GetString());
        var defaults = new SerialOptions { PortName = "default", ReadTimeoutMilliseconds = 234, WriteTimeoutMilliseconds = 567 };
        Assert.Equal(options, SerialConfigurationFields.Apply(defaults, fields));
        var minimal = SerialConfigurationFields.FromOptions(new SerialOptions { PortName = "default" });
        Assert.Equal(6, minimal.Count);
        Assert.False(minimal.ContainsKey("dtrEnable"));
        Assert.False(minimal.ContainsKey("rtsEnable"));
    }

    [Fact]
    public void ConfigurationFields_PartialOverridesPreserveDefaultsAndNeverApplyTimeouts()
    {
        var defaults = new SerialOptions { PortName = "default", Parity = SerialParity.Odd, DtrEnable = true, ReadTimeoutMilliseconds = 321 };
        var fields = new Dictionary<string, JsonElement>
        {
            ["baudRate"] = JsonSerializer.SerializeToElement(57600),
            ["readTimeoutMilliseconds"] = JsonSerializer.SerializeToElement(999),
            ["futureField"] = JsonSerializer.SerializeToElement("ignored")
        };
        Assert.Equal(defaults with { BaudRate = 57600 }, SerialConfigurationFields.Apply(defaults, fields));
        Assert.Equal(defaults, SerialConfigurationFields.Apply(defaults, new Dictionary<string, JsonElement>()));
    }

    [Theory]
    [InlineData("flowControl", "\"rtscts\"")]
    [InlineData("flowControl", "\"1\"")]
    [InlineData("flowControl", "1")]
    [InlineData("parity", "\"Unknown\"")]
    [InlineData("stopBits", "\"one\"")]
    [InlineData("dtrEnable", "null")]
    [InlineData("baudRate", "\"9600\"")]
    public void ConfigurationFields_RejectMalformedExplicitValues(string key, string json)
    {
        var fields = new Dictionary<string, JsonElement> { [key] = JsonSerializer.Deserialize<JsonElement>(json) };
        Assert.ThrowsAny<ArgumentException>(() => SerialConfigurationFields.Apply(new SerialOptions { PortName = "default" }, fields));
    }

    [Fact]
    public void ConfigurationFields_ReuseOptionsValidationAndSupportEveryDeclaredEnum()
    {
        foreach (var flow in Enum.GetValues<SerialFlowControl>())
        foreach (var parity in Enum.GetValues<SerialParity>())
        foreach (var stop in Enum.GetValues<SerialStopBits>())
        {
            var options = new SerialOptions { PortName = "port", FlowControl = flow, Parity = parity, StopBits = stop };
            Assert.Equal(options, SerialConfigurationFields.Apply(new SerialOptions { PortName = "default" }, SerialConfigurationFields.FromOptions(options)));
        }
        var invalidFields = SerialConfigurationFields.FromOptions(new SerialOptions { PortName = "port", BaudRate = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => SerialConfigurationFields.Apply(new SerialOptions { PortName = "default" }, invalidFields));
        var fields = SerialConfigurationFields.FromOptions(new SerialOptions { PortName = "port", FlowControl = SerialFlowControl.RtsCts });
        Assert.Throws<ArgumentException>(() => SerialConfigurationFields.Apply(new SerialOptions { PortName = "port", RtsEnable = true }, fields));
        fields = new Dictionary<string, JsonElement> { ["dataBits"] = JsonSerializer.SerializeToElement(9) };
        Assert.Throws<ArgumentOutOfRangeException>(() => SerialConfigurationFields.Apply(new SerialOptions { PortName = "port" }, fields));
    }

    [Fact]
    public void ConfigurationFields_EmptyGenericPortCanBeCompletedByDeviceOverride()
    {
        var generic = new SerialOptions { PortName = "", BaudRate = 115200 };
        var fields = SerialConfigurationFields.FromOptions(generic);
        Assert.Equal("", fields["portName"].GetString());
        fields["portName"] = JsonSerializer.SerializeToElement("/dev/ttyUSB0");
        Assert.Equal(generic with { PortName = "/dev/ttyUSB0" }, SerialConfigurationFields.Apply(generic, fields));
        Assert.Throws<ArgumentNullException>(() => SerialConfigurationFields.FromOptions(null!));
    }

    [Fact]
    public async Task SessionConstructorPreservesConfiguredIdentity()
    {
        var id = Guid.NewGuid();
        await using var session = new SerialTerminalSession(id, new SerialOptions { PortName = "port" });
        Assert.Equal(id, session.SessionId);
        await using var generated = new SerialTerminalSession(new SerialOptions { PortName = "port" });
        Assert.NotEqual(Guid.Empty, generated.SessionId);
    }

    [Fact]
    public async Task Pty_ReadWrite_CloseAndReopen_AreBounded()
    {
        await using var pty = await PtyPair.StartAsync();
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First });
        using var peer = new SerialPort(pty.Second) { ReadTimeout = 1000, WriteTimeout = 1000 };
        peer.Open();
        var output = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collected = new List<byte>();
        session.OutputReceived += bytes =>
        {
            collected.AddRange(bytes);
            if (collected.Count >= 3) output.TrySetResult(collected.ToArray());
        };
        var disconnected = new List<Exception?>();
        session.Disconnected += error => disconnected.Add(error);
        await session.ConnectAsync();
        Assert.False(session.Capabilities.CanResize);
        Assert.False(session.Capabilities.HasFileSystem);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ResizeTerminalAsync(80, 24, 0, 0));
        peer.Write(new byte[] { 65, 0, 255 }, 0, 3);
        Assert.Equal(new byte[] { 65, 0, 255 }, await output.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await session.SendInputAsync(new byte[] { 66, 0, 254 });
        var received = await Task.Run(() => new[] { peer.ReadByte(), peer.ReadByte(), peer.ReadByte() });
        Assert.Equal(new[] { 66, 0, 254 }, received);
        await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(session.IsConnected);
        Assert.Single(disconnected);
        Assert.Null(disconnected[0]);
        await session.ConnectAsync();
        await session.SendInputAsync(new byte[] { 67 });
        Assert.Equal(67, await Task.Run(peer.ReadByte));
        await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, disconnected.Count);
    }

    [Fact]
    public async Task Pty_CancellationDoesNotDisconnect_AndIdleDisposeStopsReader()
    {
        await using var pty = await PtyPair.StartAsync();
        var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(cancelled.Token));
        Assert.False(session.IsConnected);
        await session.ConnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendInputAsync(new byte[] { 1 }, cancelled.Token));
        Assert.True(session.IsConnected);
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(session.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ConnectAsync());
    }

    [Fact]
    public async Task Pty_PeerLossNotifiesErrorExactlyOnce()
    {
        await using var pty = await PtyPair.StartAsync();
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First });
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        session.Disconnected += error => { Interlocked.Increment(ref count); ended.TrySetResult(error); };
        await session.ConnectAsync();
        pty.Stop();
        Assert.NotNull(await ended.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(session.IsConnected);
        await session.DisconnectAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Pty_RtsCtsRejectsManualRts_AndUnsupportedDtrIsExplicit()
    {
        await using var pty = await PtyPair.StartAsync();
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First, FlowControl = SerialFlowControl.RtsCts });
        await session.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetRtsAsync(true));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.SetDtrAsync(true));
        Assert.True(session.IsConnected);
    }

    [Theory]
    [InlineData(SerialFlowControl.None)]
    [InlineData(SerialFlowControl.XonXoff)]
    [InlineData(SerialFlowControl.RtsCts)]
    public async Task Pty_FlowControlModesOpenWithoutSilentFallback(SerialFlowControl flow)
    {
        await using var pty = await PtyPair.StartAsync();
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First, FlowControl = flow });
        await session.ConnectAsync();
        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task MissingPortPreservesNativeFailure_AndInvalidOptionsFailBeforeOpen()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SerialTerminalSession(new SerialOptions { PortName = "unused", BaudRate = 0 }));
        Assert.Throws<ArgumentException>(() => new SerialTerminalSession(new SerialOptions { PortName = "unused", FlowControl = SerialFlowControl.RtsCts, RtsEnable = true }));
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = "/dev/keiterm-nonexistent-port" });
        var error = await Assert.ThrowsAsync<SerialConnectionException>(() => session.ConnectAsync());
        Assert.Equal(SerialConnectionFailure.DeviceNotFound, error.Failure);
        Assert.NotNull(error.InnerException);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Pty_CancelInFlightWriteIsBoundedAndDoesNotReportTransportFailure()
    {
        await using var pty = await PtyPair.StartAsync();
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First });
        await session.ConnectAsync();
        var notifications = 0;
        session.Disconnected += _ => Interlocked.Increment(ref notifications);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.SendInputAsync(new byte[8 * 1024 * 1024], cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(session.IsConnected);
        Assert.Equal(0, notifications);
        await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Pty_AlreadyOpenedPortReportsBusyWithoutDisturbingOwner()
    {
        await using var pty = await PtyPair.StartAsync();
        await using var first = new SerialTerminalSession(new SerialOptions { PortName = pty.First });
        await using var second = new SerialTerminalSession(new SerialOptions { PortName = pty.First });
        await first.ConnectAsync();
        var error = await Assert.ThrowsAsync<SerialConnectionException>(() => second.ConnectAsync());
        Assert.Equal(SerialConnectionFailure.DeviceBusy, error.Failure);
        Assert.True(first.IsConnected);
        Assert.False(second.IsConnected);
    }

    [Fact]
    public async Task Pty_WriteTimeoutDisconnectsExactlyOnce()
    {
        await using var pty = await PtyPair.StartAsync();
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First, WriteTimeoutMilliseconds = 100 });
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        session.Disconnected += error => { Interlocked.Increment(ref count); ended.TrySetResult(error); };
        await session.ConnectAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => session.SendInputAsync(new byte[8 * 1024 * 1024]));
        Assert.IsType<TimeoutException>(await ended.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await session.DisconnectAsync();
        Assert.False(session.IsConnected);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Pty_CloseDuringWriteIsBoundedAndRemainsNormal()
    {
        await using var pty = await PtyPair.StartAsync();
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First });
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += error => ended.TrySetResult(error);
        await session.ConnectAsync();
        var write = session.SendInputAsync(new byte[8 * 1024 * 1024]);
        await Task.Delay(30);
        await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        // 已进入同步写的调用可能超时，尚在排队的调用得到已关闭；关闭通知仍是正常退出。
        var error = await Record.ExceptionAsync(() => write);
        Assert.True(error is TimeoutException or InvalidOperationException);
        Assert.Null(await ended.Task);
    }

    [Fact]
    public async Task Pty_ManualRtsUnsupportedIsExplicit_AndBreakStateCanBeReleased()
    {
        await using var pty = await PtyPair.StartAsync();
        await using var session = new SerialTerminalSession(new SerialOptions { PortName = pty.First });
        await session.ConnectAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => session.SetRtsAsync(true));
        // Linux PTY 支持设置 BreakState，但这不验证真实电气信号或 Break 时长。
        await session.SetBreakStateAsync(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendInputAsync(new byte[] { 1 }));
        await session.SetBreakStateAsync(false);
        await session.SendInputAsync(new byte[] { 2 });
        Assert.True(session.IsConnected);
    }

    private sealed class PtyPair : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _directory;
        public string First => Path.Combine(_directory, "first");
        public string Second => Path.Combine(_directory, "second");
        private PtyPair(string directory, Process process) => (_directory, _process) = (directory, process);

        public static async Task<PtyPair> StartAsync()
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("此测试需要 Linux 与真实 socat PTY；不替代真机验证。");
            var directory = Path.Combine("/tmp/opencode", "keiterm-serial-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo("socat") { UseShellExecute = false };
            start.ArgumentList.Add("pty,raw,echo=0,link=" + Path.Combine(directory, "first"));
            start.ArgumentList.Add("pty,raw,echo=0,link=" + Path.Combine(directory, "second"));
            var pair = new PtyPair(directory, Process.Start(start)!);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (!File.Exists(pair.First) || !File.Exists(pair.Second))
                    await Task.Delay(20, timeout.Token);
                return pair;
            }
            catch { await pair.DisposeAsync(); throw; }
        }

        public void Stop() { if (!_process.HasExited) _process.Kill(); }
        public async ValueTask DisposeAsync()
        {
            Stop();
            await _process.WaitForExitAsync();
            _process.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}

public sealed class UnprivilegedLinuxSerialFactAttribute : FactAttribute
{
    public UnprivilegedLinuxSerialFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "真实 chmod 权限拒绝用例需要 Linux PTY。";
            return;
        }
        // /proc 的 Uid 字段依次为 real/effective/saved/fs；root 可绕过 chmod，不能冒报通过。
        string effectiveUid = File.ReadLines("/proc/self/status").First(line => line.StartsWith("Uid:", StringComparison.Ordinal))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[2];
        if (effectiveUid == "0") Skip = "有效 UID 为 root，会绕过 chmod 000；需以普通用户验证真实 AccessDenied。";
    }
}
