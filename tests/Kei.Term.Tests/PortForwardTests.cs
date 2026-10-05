using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Ssh.Services;
using Microsoft.Data.Sqlite;
using Renci.SshNet;
using Xunit;

namespace Kei.Term.Tests;

// 端口转发：v5 只加表、三种模式往返、重复监听拒绝、会话删除级联、目标客户端上启动并在释放后停止
public class PortForwardTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_pf_{Guid.NewGuid():N}.db");

    private string ConnStr => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Fact]
    public async Task V5_OnExistingV4Database_AddsPortForwards_AndLeavesProxiesUntouched()
    {
        var proxyId = Guid.NewGuid();
        var configJson = """{"type":"socks5","host":"127.0.0.1","port":2080}""";
        using (var conn = new SqliteConnection(ConnStr))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(@"
                CREATE TABLE proxies (
                    id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    config_json TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
                INSERT INTO proxies (id, name, sort_order, config_json, created_at, updated_at)
                    VALUES (@id, 'local-socks', 0, @json, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                PRAGMA user_version = 4;
            ", new { id = proxyId.ToString(), json = configJson });
        }

        var version = await SchemaMigrator.MigrateAsync(new SqliteConnectionFactory(ConnStr));
        // v4 之后还会跑后续迁移；这里只要求升到当前最新，且不改 proxies。
        Assert.Equal(SchemaMigrator.LatestVersion, version);

        using var check = new SqliteConnection(ConnStr);
        await check.OpenAsync();
        var tables = (await check.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table';")).ToHashSet();
        Assert.Contains("port_forwards", tables);

        var proxy = await check.QuerySingleAsync<(string Name, string ConfigJson)>(
            "SELECT name, config_json FROM proxies WHERE id = @id;",
            new { id = proxyId.ToString() });
        Assert.Equal("local-socks", proxy.Name);
        Assert.Equal(configJson, proxy.ConfigJson);

        var proxyColumns = (await check.QueryAsync<string>("SELECT name FROM pragma_table_info('proxies');")).ToHashSet();
        Assert.Equal(
            ["id", "name", "sort_order", "config_json", "created_at", "updated_at"],
            proxyColumns);
        Assert.DoesNotContain("port_forwards", proxyColumns);
    }

    [Fact]
    public async Task Save_RoundTripsThreeModes_DynamicDestinationIsEmpty()
    {
        var sessionId = await SeedSessionAsync();
        var repo = new SqlitePortForwardRepository(ConnStr);

        var local = new PortForward
        {
            SessionId = sessionId,
            Name = "db",
            Mode = PortForwardMode.Local,
            BindAddress = "127.0.0.1",
            ListenPort = 15432,
            DestinationHost = "10.0.0.8",
            DestinationPort = 5432
        };
        var remote = new PortForward
        {
            SessionId = sessionId,
            Name = "web",
            Mode = PortForwardMode.Remote,
            BindAddress = "0.0.0.0",
            ListenPort = 8080,
            DestinationHost = "127.0.0.1",
            DestinationPort = 80
        };
        var dynamic = new PortForward
        {
            SessionId = sessionId,
            Name = "socks",
            Mode = PortForwardMode.Dynamic,
            BindAddress = "127.0.0.1",
            ListenPort = 1080,
            DestinationHost = "should-be-cleared",
            DestinationPort = 9
        };
        await repo.SaveAsync(local);
        await repo.SaveAsync(remote);
        await repo.SaveAsync(dynamic);

        var loaded = await repo.GetBySessionAsync(sessionId);
        Assert.Equal(3, loaded.Count);

        var localLoaded = loaded.Single(f => f.Id == local.Id);
        Assert.Equal(PortForwardMode.Local, localLoaded.Mode);
        Assert.Equal("127.0.0.1", localLoaded.BindAddress);
        Assert.Equal(15432, localLoaded.ListenPort);
        Assert.Equal("10.0.0.8", localLoaded.DestinationHost);
        Assert.Equal(5432, localLoaded.DestinationPort);

        var remoteLoaded = loaded.Single(f => f.Id == remote.Id);
        Assert.Equal(PortForwardMode.Remote, remoteLoaded.Mode);
        Assert.Equal("0.0.0.0", remoteLoaded.BindAddress);
        Assert.Equal(8080, remoteLoaded.ListenPort);
        Assert.Equal("127.0.0.1", remoteLoaded.DestinationHost);
        Assert.Equal(80, remoteLoaded.DestinationPort);

        var dynamicLoaded = loaded.Single(f => f.Id == dynamic.Id);
        Assert.Equal(PortForwardMode.Dynamic, dynamicLoaded.Mode);
        Assert.Equal(1080, dynamicLoaded.ListenPort);
        Assert.True(string.IsNullOrEmpty(dynamicLoaded.DestinationHost));
        Assert.Null(dynamicLoaded.DestinationPort);
    }

    [Fact]
    public async Task Save_DuplicateListenOnSameSession_FailsWithOccupiedMessage()
    {
        var sessionId = await SeedSessionAsync();
        var repo = new SqlitePortForwardRepository(ConnStr);
        await repo.SaveAsync(new PortForward
        {
            SessionId = sessionId,
            Mode = PortForwardMode.Local,
            BindAddress = "127.0.0.1",
            ListenPort = 20022,
            DestinationHost = "10.0.0.1",
            DestinationPort = 22
        });

        var conflict = await Assert.ThrowsAsync<PortForwardConflictException>(() => repo.SaveAsync(new PortForward
        {
            SessionId = sessionId,
            Mode = PortForwardMode.Local,
            BindAddress = "127.0.0.1",
            ListenPort = 20022,
            DestinationHost = "10.0.0.2",
            DestinationPort = 22
        }));
        Assert.Contains("占用", conflict.Message, StringComparison.Ordinal);

        // 同端口不同模式可以并存
        await repo.SaveAsync(new PortForward
        {
            SessionId = sessionId,
            Mode = PortForwardMode.Dynamic,
            BindAddress = "127.0.0.1",
            ListenPort = 20022
        });
        Assert.Equal(2, (await repo.GetBySessionAsync(sessionId)).Count);
    }

    [Fact]
    public async Task DeleteSession_CascadesPortForwards()
    {
        var sessionId = await SeedSessionAsync();
        var repo = new SqlitePortForwardRepository(ConnStr);
        await repo.SaveAsync(new PortForward
        {
            SessionId = sessionId,
            Mode = PortForwardMode.Local,
            BindAddress = "127.0.0.1",
            ListenPort = 19090,
            DestinationHost = "10.1.1.1",
            DestinationPort = 90
        });

        var tree = new SqliteTreeRepository(ConnStr);
        await tree.DeleteNodeAsync(sessionId);

        Assert.Empty(await repo.GetBySessionAsync(sessionId));
    }

    [Fact]
    public async Task Dialer_StartsForwardsOnTargetClient_NotJumpClient_AndStopsOnDispose()
    {
        using var sshd = new LocalSshd();
        var factory = new SshSessionFactory();
        var hops = new List<SshHop> { new(sshd.Config("hop"), sshd.KeyAuth()) };
        int localPort = FreePort();
        int dynamicPort = FreePort();
        var forwards = new List<PortForward>
        {
            new()
            {
                Name = "local-db",
                Mode = PortForwardMode.Local,
                BindAddress = "127.0.0.1",
                ListenPort = localPort,
                DestinationHost = "127.0.0.1",
                DestinationPort = 9
            },
            new()
            {
                Name = "dyn",
                Mode = PortForwardMode.Dynamic,
                BindAddress = "127.0.0.1",
                ListenPort = dynamicPort
            }
        };
        var options = new SshConnectOptions
        {
            HostKeyVerifier = new AcceptAllHostKeys(),
            JumpHosts = hops,
            PortForwards = forwards
        };

        var session = (SshNetSession)await factory.CreateSessionAsync(sshd.Config("target"), sshd.KeyAuth(), options);
        await session.ConnectAsync();

        var target = Assert.IsType<SshClient>(session.UnderlyingClient);
        var started = target.ForwardedPorts.Where(p => p.IsStarted).ToList();
        Assert.Contains(started, p => p is ForwardedPortLocal local && local.BoundPort == (uint)localPort);
        Assert.Contains(started, p => p is ForwardedPortDynamic dynamic && dynamic.BoundPort == (uint)dynamicPort);

        var jumpClients = session.JumpChain!.Clients;
        Assert.NotEmpty(jumpClients);
        Assert.DoesNotContain(jumpClients.SelectMany(c => c.ForwardedPorts), p =>
            p is ForwardedPortLocal local && local.BoundPort == (uint)localPort);
        Assert.DoesNotContain(jumpClients.SelectMany(c => c.ForwardedPorts), p =>
            p is ForwardedPortDynamic dynamic && dynamic.BoundPort == (uint)dynamicPort);

        var held = started.ToList();
        await session.DisposeAsync();
        Assert.All(held, p => Assert.False(p.IsStarted));
    }

    [Fact]
    public void SessionEditor_AddsPortsCategory_WithoutRemovingFirewallRow()
    {
        var vm = new SessionEditViewModel(null, null, []);
        Assert.Contains(vm.Categories, c => Equals(c.Page, "Ports"));
        Assert.Contains(vm.Categories, c => Equals(c.Page, "Connection"));
        Assert.Contains(vm.FirewallOptions, o => o.Kind == FirewallChoiceKind.None);
        Assert.Contains(vm.FirewallOptions, o => o.Kind == FirewallChoiceKind.ChooseSession);
    }

    private async Task<Guid> SeedSessionAsync()
    {
        var factory = new SqliteConnectionFactory(ConnStr);
        await SchemaMigrator.MigrateAsync(factory);
        var id = Guid.NewGuid();
        var node = new SessionNode { Id = id, Name = "web", Host = "web.example" };
        await new SqliteTreeRepository(factory).SaveNodeAsync(node);
        return id;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class AcceptAllHostKeys : IHostKeyVerifier
    {
        public Task<IReadOnlyList<string>> GetKnownKeyTypesAsync(string host, int port, System.Threading.CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<HostKeyCheckOutcome> VerifyAsync(PresentedHostKey presented, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new HostKeyCheckOutcome(true, new HostKeyEvaluation(HostKeyVerdict.Trusted, presented, []), null));
    }

    // 只听回环的 sshd，用来断言转发对象状态，不访问外网
    private sealed class LocalSshd : IDisposable
    {
        private const string SshdPath = "/usr/sbin/sshd";
        private readonly Process _process;
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"keiterm_pf_sshd_{Guid.NewGuid():N}");
        public int Port { get; }
        public string User { get; } = Environment.UserName;
        public string ClientPrivateKey { get; }

        public LocalSshd()
        {
            System.IO.Directory.CreateDirectory(Directory);
            System.IO.Directory.CreateDirectory("/run/sshd");
            string hostKey = Path.Combine(Directory, "host_ed25519");
            string clientKey = Path.Combine(Directory, "client_ed25519");
            Run("ssh-keygen", $"-q -t ed25519 -N \"\" -f {hostKey}");
            Run("ssh-keygen", $"-q -t ed25519 -N \"\" -f {clientKey}");
            File.Copy(clientKey + ".pub", Path.Combine(Directory, "authorized_keys"));
            ClientPrivateKey = File.ReadAllText(clientKey);
            Port = FreePort();
            string config = Path.Combine(Directory, "sshd_config");
            File.WriteAllText(config, $"""
                Port {Port}
                ListenAddress 127.0.0.1
                HostKey {hostKey}
                PidFile {Path.Combine(Directory, "sshd.pid")}
                AuthorizedKeysFile {Path.Combine(Directory, "authorized_keys")}
                PasswordAuthentication no
                KbdInteractiveAuthentication no
                PubkeyAuthentication yes
                PermitRootLogin prohibit-password
                StrictModes no
                UsePAM no
                AllowTcpForwarding yes
                """);
            _process = Process.Start(new ProcessStartInfo(SshdPath, $"-D -e -f {config}")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            })!;
            WaitForPort(Port);
        }

        public ResolvedSessionConfig Config(string name) => new(
            Guid.NewGuid(), name, "127.0.0.1", Port, User, null, "xterm-256color", null, null,
            new Dictionary<string, string>());

        public IReadOnlyList<MaterializedAuthMethod> KeyAuth() =>
        [
            new MaterializedAuthMethod(AuthMaterialKind.PrivateKey, new SecretPayload { PrivateKeyContent = ClientPrivateKey })
        ];

        public void Dispose()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
            catch
            {
                // 进程可能已退出
            }

            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
        }

        private static void Run(string file, string args)
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardError = true })!;
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"{file} 退出码 {p.ExitCode}: {p.StandardError.ReadToEnd()}");
            }
        }

        private static void WaitForPort(int port)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var tcp = new TcpClient();
                    tcp.Connect(IPAddress.Loopback, port);
                    return;
                }
                catch
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            throw new TimeoutException($"sshd 未在 {port} 监听");
        }
    }
}
