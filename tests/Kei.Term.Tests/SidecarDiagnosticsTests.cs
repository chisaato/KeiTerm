using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Transport.Go;
using Xunit;

namespace Kei.Term.Tests;

public class SidecarDiagnosticsTests
{
    [Fact]
    public async Task RealDaemonKilled_EndsPendingCall_ThenExplicitDialCreatesNewGeneration_AndDisposeIsExpected()
    {
        if (!OperatingSystem.IsLinux()) return;
        string binary = Path.Combine(AppContext.BaseDirectory, "kei-trans-daemon");
        Assert.True(File.Exists(binary), "聚焦测试需要构建的真实 daemon");
        var events = new ConcurrentQueue<SidecarDiagnostic>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var manager = new GoSidecarProcessManager(binary, events.Enqueue);
        try
        {
            var client = await manager.GetClientAsync();
            var started = Assert.Single(events, e => e.Kind == "process-started");
            var pending = client.CallAsync("session.probeHostKey", new
            {
                host = "127.0.0.1", port = ((IPEndPoint)listener.LocalEndpoint).Port, user = "test"
            });
            using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var process = Process.GetProcessById(started.ProcessId!.Value);
            process.Kill();
            await process.WaitForExitAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(client.IsConnected);
            var replacement = await manager.GetClientAsync();
            Assert.NotSame(client, replacement);
            await replacement.CallAsync("daemon.ping").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, events.Count(e => e.Kind == "process-started"));
            Assert.Contains(events, e => e.Kind == "process-exited" && e.ProcessId == started.ProcessId && e.ExitCode == 137 && !e.Expected);
            await manager.DisposeAsync();
            Assert.Contains(events, e => e.Kind == "process-exited" && e.ConnectionId == replacement.ConnectionId && e.Expected);
            Assert.Contains(events, e => e.Kind == "ipc-ended" && e.ConnectionId == replacement.ConnectionId && e.Expected);
            Assert.DoesNotContain(events, e => e.ConnectionId == replacement.ConnectionId && e.Kind == "ipc-ended" && !e.Expected);
            Assert.All(events.Where(e => e.Kind == "process-started"), e =>
            {
                Assert.NotNull(e.Endpoint);
                Assert.False(File.Exists(e.Endpoint));
                Assert.False(Directory.Exists(Path.GetDirectoryName(e.Endpoint)));
            });
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task StartupExit_ReportsExactCodePathAndSafeOutput_AndCanDialAgain()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "keiterm-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string binary = Path.Combine(directory, "daemon");
        var events = new ConcurrentQueue<SidecarDiagnostic>();
        await File.WriteAllTextAsync(binary, "#!/bin/sh\nprintf 'password=never-log-this private-key-secret\\n' >&2\nprintf 'terminal-payload-secret\\n'\nexit 23\n");
        File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await using var manager = new GoSidecarProcessManager(binary, events.Enqueue);
            for (int i = 0; i < 2; i++)
            {
                var error = await Assert.ThrowsAsync<IOException>(() => manager.GetClientAsync());
                Assert.Contains("23", error.Message);
            }
            Assert.Equal(2, events.Count(e => e.Kind == "process-started" && e.BinaryPath == binary));
            Assert.Equal(2, events.Count(e => e.Kind == "process-exited" && e.ExitCode == 23 && !e.Expected));
            Assert.Contains(events, e => e.Kind == "process-output" && e.Reason == "stderr");
            Assert.Contains(events, e => e.Kind == "process-output" && e.Reason == "stdout");
            string log = JsonSerializer.Serialize(events);
            Assert.DoesNotContain("never-log-this", log);
            Assert.DoesNotContain("private-key-secret", log);
            Assert.DoesNotContain("terminal-payload-secret", log);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IpcEnd_CompletesPendingRpcAndDisconnectsAllSessions_ButDisposeIsExpected(bool dispose, bool closeFirst)
    {
        var events = new ConcurrentQueue<SidecarDiagnostic>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var socket = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var peer = await accepting;
            await using var client = new GoIpcClient(socket.GetStream(), events.Enqueue);
            var wire = peer.GetStream();
            var config = SessionConfigBuilder.Build(new SessionNode { Host = "test" }, new AppSettings());
            async Task<GoSshSession> Open(uint streamId)
            {
                var request = client.CallAsync("test.open", new { password = "rpc-secret" });
                byte[] header = new byte[9];
                await wire.ReadExactlyAsync(header);
                byte[] payload = new byte[BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5))];
                await wire.ReadExactlyAsync(payload);
                using var doc = JsonDocument.Parse(payload);
                await Frame(wire, 0, 5, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = doc.RootElement.GetProperty("id").GetInt64(), result = new { streamId } }));
                await request;
                var session = new GoSshSession(config, _ => Task.FromResult((client, streamId.ToString(), client.GetStream(streamId))));
                await session.ConnectAsync();
                return session;
            }
            await using var first = await Open(1);
            await using var second = await Open(2);
            var firstEnd = new TaskCompletionSource<Exception?>();
            var secondEnd = new TaskCompletionSource<Exception?>();
            first.Disconnected += e => firstEnd.TrySetResult(e);
            second.Disconnected += e => secondEnd.TrySetResult(e);
            // 单连接 Close 不应结束共享 IPC 或另一个连接。
            if (closeFirst)
            {
                await Frame(wire, 1, 3, []);
                Assert.Null(await firstEnd.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(client.IsConnected);
                Assert.True(second.IsConnected);
            }
            var pending = client.CallAsync("test.wait", new { privateKey = "key-secret" });
            if (dispose) await client.DisposeAsync();
            else peer.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            var failure = await secondEnd.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose) Assert.Null(failure);
            else Assert.IsAssignableFrom<IOException>(failure);
            if (!closeFirst) Assert.IsAssignableFrom<IOException>(await firstEnd.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(second.IsConnected);
            Assert.Contains(events, e => e.Kind == "ipc-ended" && e.Expected == dispose);
            Assert.Contains(events, e => e.Kind == "stream-ended" && e.StreamId == 1);
            Assert.DoesNotContain("rpc-secret", JsonSerializer.Serialize(events));
            Assert.DoesNotContain("key-secret", JsonSerializer.Serialize(events));
        }
        finally { listener.Stop(); }
    }

    private static async Task Frame(Stream wire, uint id, byte type, byte[] payload)
    {
        byte[] header = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(header, id);
        header[4] = type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), (uint)payload.Length);
        await wire.WriteAsync(header);
        await wire.WriteAsync(payload);
    }
}
