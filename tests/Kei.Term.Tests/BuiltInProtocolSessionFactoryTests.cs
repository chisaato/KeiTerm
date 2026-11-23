using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Sync;
using Kei.Term.Transport.Go;

namespace Kei.Term.Tests;

public sealed class BuiltInProtocolSessionFactoryTests
{
    [Fact]
    public async Task RefreshWithNoEngineSelection_InheritsGlobalEngine()
    {
        await using var fixture = await Fixture.CreateAsync();
        int starts = 0;
        var factory = fixture.Factory(NullInteractionService.Instance, new GoProtocolFactory(_ =>
        {
            starts++;
            throw new IOException("global Go discovery reached");
        }));
        var global = new Kei.Term.Core.Settings.AppSettings { TransportEngine = TransportEngine.GoCore };
        var vm = new Kei.Term.App.ViewModels.SessionEditViewModel(null, null, [], global, null, null, null, null, null, null, null, factory);
        vm.SelectedTransportEngine = null;
        await vm.RefreshSerialPortsCommand.ExecuteAsync(null);
        Assert.Equal(1, starts);
        Assert.Equal(Strings.Get("SessionEdit.Serial.DiscoveryError"), vm.SerialPortDiscoveryError);
    }
    [Fact]
    public async Task CatalogUsesRealBclNamesAndExplicitGoRefreshOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        int starts = 0;
        var go = new GoProtocolFactory(_ => { starts++; throw new IOException("discovery unavailable"); });
        var factory = fixture.Factory(NullInteractionService.Instance, go);
        var names = await factory.GetPortNamesAsync(TransportEngine.BuiltIn);
        Assert.Equal(SerialPort.GetPortNames().Order(), names.Order());
        await Assert.ThrowsAsync<NotSupportedException>(() => factory.GetPortNamesAsync((TransportEngine)999));
        var vm = new Kei.Term.App.ViewModels.SessionEditViewModel(null, null, [], new(), null, null, null, null, null, null, null, factory);
        vm.SelectedConnectionProtocol = SessionProtocols.Serial;
        vm.CurrentScopePortName = "/dev/manual";
        vm.SelectedTransportEngine = vm.TransportEngineOptions.First(o => o.Value == TransportEngine.GoCore);
        Assert.Equal(0, starts);
        await vm.RefreshSerialPortsCommand.ExecuteAsync(null);
        Assert.Equal(1, starts);
        Assert.Equal(Strings.Get("SessionEdit.Serial.DiscoveryError"), vm.SerialPortDiscoveryError);
        Assert.Equal("/dev/manual", vm.CurrentScopePortName);
        vm.SelectedTransportEngine = vm.TransportEngineOptions.First(o => o.Value == TransportEngine.BuiltIn);
        await vm.RefreshSerialPortsCommand.ExecuteAsync(null);
        Assert.Empty(vm.SerialPortDiscoveryError);
        Assert.Equal(names.Order(), vm.SerialPortNames.Order());
    }
    [Fact]
    public async Task GoCreationIsLazyAndProviderFailureNeverFallsBackToBuiltIn()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var providerStarts = 0;
        var go = new GoProtocolFactory(_ =>
        {
            Interlocked.Increment(ref providerStarts);
            throw new InvalidOperationException("provider unavailable");
        });
        var factory = fixture.Factory(NullInteractionService.Instance, go);
        Assert.True(factory.Supports(SessionProtocols.Telnet, TransportEngine.GoCore));
        Assert.True(factory.Supports(SessionProtocols.Serial, TransportEngine.GoCore));
        var config = fixture.Config with { Protocol = SessionProtocols.Telnet, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        await using var session = await factory.CreateSessionAsync(config, TransportEngine.GoCore);
        Assert.Equal(0, providerStarts);
        Assert.False(session.IsConnected);
        Assert.False(listener.Pending());
        Assert.Equal(config.SessionId, session.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync());
        Assert.Equal(1, providerStarts);
        Assert.False(listener.Pending());
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task GoSerialUsesSameBindingAndResolutionBeforeAnyProviderIsStarted()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var pty = await PtyPair.StartAsync();
        var providerStarts = 0;
        var go = new GoProtocolFactory(_ =>
        {
            Interlocked.Increment(ref providerStarts);
            throw new InvalidOperationException("provider unavailable");
        });
        await fixture.Devices.SaveOverrideAsync(fixture.Config.SessionId, "serial", DeviceConfigurationTarget.ForOperatingSystem("linux"), Fields(new { portName = pty.First, baudRate = 57600 }));
        var config = fixture.Config with { Serial = new SerialOptions { PortName = "", BaudRate = 0 } };
        var interaction = ConfirmationProxy.Create((_, port, _) => Task.FromResult(port == pty.First));
        await using var session = await fixture.Factory(interaction, go).CreateSessionAsync(config, TransportEngine.GoCore);
        Assert.Equal(config.SessionId, session.SessionId);
        Assert.Equal(0, providerStarts);
        Assert.False(IsOpenInThisProcess(pty.First));
        var saved = Assert.Single(await fixture.Devices.GetOverridesAsync(config.SessionId, "serial"), r => r.Target.TargetDeviceId == fixture.Id.DeviceId);
        Assert.Equal(new[] { "portName" }, saved.Fields.Keys);
        Assert.Equal(pty.First, saved.Fields["portName"].GetString());
        await using var builtIn = await fixture.Factory(NullInteractionService.Instance).CreateSessionAsync(config, TransportEngine.BuiltIn);
        await builtIn.ConnectAsync();
        Assert.True(builtIn.IsConnected);
        Assert.Equal(0, providerStarts);
    }

    [Fact]
    public async Task TelnetIsNotDialedByFactoryAndUsesRealProtocolSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var factory = fixture.Factory(NullInteractionService.Instance);
        Assert.True(factory.Supports(SessionProtocols.Telnet, TransportEngine.BuiltIn));
        Assert.True(factory.Supports(SessionProtocols.Serial, TransportEngine.BuiltIn));
        Assert.False(factory.Supports(SessionProtocols.Telnet, TransportEngine.GoCore));
        Assert.False(factory.Supports(SessionProtocols.Serial, TransportEngine.GoCore));
        Assert.False(factory.Supports(SessionProtocols.Ssh, TransportEngine.BuiltIn));
        Assert.False(factory.Supports(SessionProtocols.Telnet, (TransportEngine)999));
        var config = fixture.Config with { Protocol = SessionProtocols.Telnet, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        await Assert.ThrowsAsync<NotSupportedException>(() => factory.CreateSessionAsync(config, TransportEngine.GoCore));
        await Assert.ThrowsAsync<NotSupportedException>(() => factory.CreateSessionAsync(fixture.Config, TransportEngine.GoCore));
        Assert.Empty(await fixture.Devices.GetOverridesAsync(config.SessionId, "serial"));
        await using var session = await factory.CreateSessionAsync(config, TransportEngine.BuiltIn);
        Assert.False(session.IsConnected);
        Assert.False(listener.Pending());
        Assert.Equal(config.SessionId, session.SessionId);
        var output = Channel.CreateUnbounded<byte>();
        session.OutputReceived += data => { foreach (var b in data) output.Writer.TryWrite(b); };
        await session.ConnectAsync();
        using var peer = await listener.AcceptTcpClientAsync();
        await peer.GetStream().WriteAsync(new byte[] { 255, 251, 1, 72 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal((byte)72, await output.Reader.ReadAsync(timeout.Token));
        var bytes = new byte[3];
        await peer.GetStream().ReadExactlyAsync(bytes, timeout.Token);
        Assert.Equal(new byte[] { 255, 253, 1 }, bytes);
        await session.SendInputAsync(new byte[] { 255, 13 });
        bytes = new byte[4];
        await peer.GetStream().ReadExactlyAsync(bytes, timeout.Token);
        Assert.Equal(new byte[] { 255, 255, 13, 0 }, bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerialConfirmationDoesNotOpenPortAndCancellationNeverPersists(bool cancelToken)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var pty = await PtyPair.StartAsync();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = ConfirmationProxy.Create((name, port, _) =>
        {
            Assert.Equal(fixture.Config.SessionName, name);
            Assert.Equal(pty.First, port);
            asked.TrySetResult();
            return answer.Task;
        });
        using var cancellation = new CancellationTokenSource();
        var creation = fixture.Factory(interaction).CreateSessionAsync(fixture.Config with { Serial = new SerialOptions { PortName = pty.First } }, TransportEngine.BuiltIn, cancellation.Token);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(IsOpenInThisProcess(pty.First));
        Assert.Empty(await fixture.Devices.GetOverridesAsync(fixture.Config.SessionId, "serial"));
        if (cancelToken) cancellation.Cancel();
        answer.TrySetResult(cancelToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        Assert.False(IsOpenInThisProcess(pty.First));
        Assert.Empty(await fixture.Devices.GetOverridesAsync(fixture.Config.SessionId, "serial"));
    }

    [Fact]
    public async Task SerialConfirmationPreservesExplicitFieldsAndNextConnectionNeedsNoPrompt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var pty = await PtyPair.StartAsync();
        var target = DeviceConfigurationTarget.ForDevice(fixture.Id.EnsureCreated());
        await fixture.Devices.SaveOverrideAsync(fixture.Config.SessionId, "serial", DeviceConfigurationTarget.ForOperatingSystem("linux"), Fields(new { portName = pty.First, baudRate = 115200 }));
        await fixture.Devices.SaveOverrideAsync(fixture.Config.SessionId, "serial", target, Fields(new { baudRate = 57600 }));
        var interaction = ConfirmationProxy.Create((_, port, _) => Task.FromResult(port == pty.First));
        // 缺失基底端口由 OS 层补齐，不在覆盖解析前拒绝可修复的基底。
        var config = fixture.Config with { Serial = new SerialOptions { PortName = "", BaudRate = 0 } };
        await using (var session = await fixture.Factory(interaction).CreateSessionAsync(config, TransportEngine.BuiltIn))
        {
            Assert.False(session.IsConnected);
            Assert.Equal(config.SessionId, session.SessionId);
            Assert.False(IsOpenInThisProcess(pty.First));
            var saved = Assert.Single(await fixture.Devices.GetOverridesAsync(config.SessionId, "serial"), r => r.Target == target);
            Assert.Equal(new[] { "baudRate", "portName" }, saved.Fields.Keys.Order().ToArray());
            Assert.Equal(57600, saved.Fields["baudRate"].GetInt32());
            Assert.Equal(pty.First, saved.Fields["portName"].GetString());
            await session.ConnectAsync();
            using var peer = new SerialPort(pty.Second) { ReadTimeout = 1000, WriteTimeout = 1000 };
            peer.Open();
            var output = Channel.CreateUnbounded<byte>();
            session.OutputReceived += data => { foreach (var b in data) output.Writer.TryWrite(b); };
            peer.Write(new byte[] { 65, 255 }, 0, 2);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.Equal((byte)65, await output.Reader.ReadAsync(timeout.Token));
            Assert.Equal((byte)255, await output.Reader.ReadAsync(timeout.Token));
            await session.SendInputAsync(new byte[] { 66, 254 });
            Assert.Equal(new[] { 66, 254 }, await Task.Run(() => new[] { peer.ReadByte(), peer.ReadByte() }));
        }
        // SerialPort 持有 TIOCEXCL；释放后由真实 OS 读取保留在 PTY 上的波特率。
        var start = new ProcessStartInfo("stty") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-F"); start.ArgumentList.Add(pty.First); start.ArgumentList.Add("speed");
        using var stty = Process.Start(start)!;
        string speed = (await stty.StandardOutput.ReadToEndAsync()).Trim();
        string error = await stty.StandardError.ReadToEndAsync();
        await stty.WaitForExitAsync();
        Assert.True(stty.ExitCode == 0, error);
        Assert.Equal("57600", speed);
        // 默认拒绝交互依然能创建并真实打开：已保存绑定不会再次确认。
        await using var next = await fixture.Factory(NullInteractionService.Instance).CreateSessionAsync(config, TransportEngine.BuiltIn);
        await next.ConnectAsync();
        Assert.True(next.IsConnected);
    }

    [Fact]
    public async Task RecreatedInstallationDoesNotClaimOldBinding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var oldTarget = DeviceConfigurationTarget.ForDevice(fixture.Id.EnsureCreated());
        await fixture.Devices.SaveOverrideAsync(fixture.Config.SessionId, "serial", oldTarget, Fields(new { portName = "/dev/old-installation" }));
        fixture.Id.RecreateAsNewDevice();
        var config = fixture.Config with { Serial = new SerialOptions { PortName = "/dev/new-candidate" } };
        var interaction = ConfirmationProxy.Create((_, port, _) =>
        {
            Assert.Equal("/dev/new-candidate", port);
            return Task.FromResult(false);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Factory(interaction).CreateSessionAsync(config, TransportEngine.BuiltIn));
        var record = Assert.Single(await fixture.Devices.GetOverridesAsync(config.SessionId, "serial"));
        Assert.Equal(oldTarget, record.Target);
        Assert.Equal("/dev/old-installation", record.Fields["portName"].GetString());
    }

    [Fact]
    public async Task IncompleteSerialImportCannotBecomeAnImplicitDefaultConnection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var factory = fixture.Factory(NullInteractionService.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => factory.CreateSessionAsync(fixture.Config with { Serial = null }, TransportEngine.BuiltIn));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => factory.CreateSessionAsync(fixture.Config with { Serial = new SerialOptions { PortName = "" } }, TransportEngine.BuiltIn));
        Assert.Empty(await fixture.Devices.GetOverridesAsync(fixture.Config.SessionId, "serial"));
    }

    private static Dictionary<string, JsonElement> Fields(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static bool IsOpenInThisProcess(string port)
    {
        var realPath = File.ResolveLinkTarget(port, true)!.FullName;
        return Directory.EnumerateFiles("/proc/self/fd").Any(fd =>
        {
            try { return File.ResolveLinkTarget(fd, true)?.FullName == realPath; }
            catch (IOException) { return false; }
        });
    }

    public class ConfirmationProxy : DispatchProxy
    {
        private Func<string, string, CancellationToken, Task<bool>> _confirm = null!;
        public static IInteractionService Create(Func<string, string, CancellationToken, Task<bool>> confirm)
        {
            var proxy = Create<IInteractionService, ConfirmationProxy>();
            ((ConfirmationProxy)proxy)._confirm = confirm;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "PromptConfirmSerialBindingAsync"
            ? _confirm((string)args![0]!, (string)args[1]!, (CancellationToken)args[2]!)
            : throw new InvalidOperationException("Unexpected interaction: " + method?.Name);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine("/tmp/opencode", "keiterm-factory-" + Guid.NewGuid().ToString("N"));
        public PersistentDeviceId Id { get; private set; } = null!;
        public DeviceConfigurationService Devices { get; private set; } = null!;
        public ResolvedSessionConfig Config { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture._directory);
            fixture.Id = new PersistentDeviceId(Path.Combine(fixture._directory, "device-id"));
            var db = new SqliteConnectionFactory($"Data Source={Path.Combine(fixture._directory, "db")};Pooling=False", fixture.Id);
            var tree = new SqliteTreeRepository(db);
            await tree.InitializeAsync();
            var node = new SessionNode { Name = "device", Protocol = SessionProtocols.Serial };
            await tree.SaveNodeAsync(node);
            fixture.Devices = new(new SqliteDeviceConfigurationStore(db), fixture.Id);
            fixture.Config = new(node.Id, node.Name, "", 0, "", null, "vt100", null, null, new Dictionary<string, string>(), Protocol: SessionProtocols.Serial);
            return fixture;
        }
        public BuiltInProtocolSessionFactory Factory(IInteractionService interaction, GoProtocolFactory? go = null) => new(Devices, Id, () => interaction, "linux", go);
        public ValueTask DisposeAsync() { Directory.Delete(_directory, true); return ValueTask.CompletedTask; }
    }

    private sealed class PtyPair : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine("/tmp/opencode", "keiterm-factory-pty-" + Guid.NewGuid().ToString("N"));
        private Process _process = null!;
        public string First => Path.Combine(_directory, "first");
        public string Second => Path.Combine(_directory, "second");
        public static async Task<PtyPair> StartAsync()
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此测试仅验证 Linux PTY，不代表串口硬件验证。");
            var pair = new PtyPair();
            Directory.CreateDirectory(pair._directory);
            var start = new ProcessStartInfo("socat") { UseShellExecute = false };
            start.ArgumentList.Add("pty,raw,echo=0,link=" + pair.First);
            start.ArgumentList.Add("pty,raw,echo=0,link=" + pair.Second);
            pair._process = Process.Start(start)!;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (!File.Exists(pair.First) || !File.Exists(pair.Second)) await Task.Delay(10, timeout.Token);
                return pair;
            }
            catch { await pair.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill();
            await _process.WaitForExitAsync();
            _process.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}
