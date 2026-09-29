using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Ssh.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

// 需要真实 OpenSSH 服务端的集成测试：默认跳过，设置 KEITERM_SSHD_TESTS=1 且本机有 /usr/sbin/sshd 时运行
public sealed class SshdFactAttribute : FactAttribute
{
    public SshdFactAttribute()
    {
        if (!SshdFixture.IsEnabled)
        {
            Skip = "需要 /usr/sbin/sshd 与 ssh-keygen，并设置环境变量 KEITERM_SSHD_TESTS=1";
        }
    }
}

// 在临时目录拉起仅监听回环的 sshd（公钥认证、允许 TCP 转发），测试结束后清理
public sealed class SshdFixture : IDisposable
{
    private const string SshdPath = "/usr/sbin/sshd";
    private readonly Process? _process;

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("KEITERM_SSHD_TESTS") == "1"
        && File.Exists(SshdPath)
        && OperatingSystem.IsLinux();

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"keiterm_sshd_{Guid.NewGuid():N}");
    public int Port { get; }
    public string User { get; } = Environment.UserName;
    public string ClientPrivateKey { get; } = string.Empty;
    public string HostPublicKeyBase64 { get; } = string.Empty;

    public SshdFixture()
    {
        if (!IsEnabled)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        // 特权分离目录：部分发行版未预建
        System.IO.Directory.CreateDirectory("/run/sshd");

        string hostKey = Path.Combine(Directory, "host_ed25519");
        string clientKey = Path.Combine(Directory, "client_ed25519");
        Run("ssh-keygen", $"-q -t ed25519 -N \"\" -f {hostKey}");
        Run("ssh-keygen", $"-q -t ed25519 -N \"\" -f {clientKey}");
        File.Copy(clientKey + ".pub", Path.Combine(Directory, "authorized_keys"));

        ClientPrivateKey = File.ReadAllText(clientKey);
        HostPublicKeyBase64 = File.ReadAllText(hostKey + ".pub").Split(' ')[1];
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
            Subsystem sftp internal-sftp
            """);

        _process = Process.Start(new ProcessStartInfo(SshdPath, $"-D -e -f {config}")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        });
        WaitForPort(Port);
    }

    public ResolvedSessionConfig Config(string name = "sshd", Guid? jumpHost = null) => new(
        Guid.NewGuid(),
        name,
        "127.0.0.1",
        Port,
        User,
        null,
        "xterm-256color",
        null,
        jumpHost,
        new Dictionary<string, string>());

    public IReadOnlyList<MaterializedAuthMethod> KeyAuth() =>
    [
        new MaterializedAuthMethod(AuthMaterialKind.PrivateKey, new SecretPayload { PrivateKeyContent = ClientPrivateKey })
    ];

    public void Dispose()
    {
        try
        {
            _process?.Kill(entireProcessTree: true);
            _process?.WaitForExit(2000);
        }
        catch
        {
            // 进程可能已退出
        }

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private static void Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {args} 退出码 {p.ExitCode}");
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void WaitForPort(int port)
    {
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }

        throw new TimeoutException("sshd 未能在 5 秒内开始监听");
    }
}

public class SshIntegrationTests : IClassFixture<SshdFixture>, IDisposable
{
    private readonly SshdFixture _sshd;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_it_{Guid.NewGuid():N}.db");

    public SshIntegrationTests(SshdFixture sshd)
    {
        _sshd = sshd;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private async Task<(SqliteKnownHostRepository Repo, HostKeyTrustService Trust)> NewTrustAsync(HostKeyPolicy policy)
    {
        var repo = new SqliteKnownHostRepository($"Data Source={_dbPath}");
        await repo.InitializeAsync();
        return (repo, new HostKeyTrustService(repo, () => policy));
    }

    private static async Task<string> CollectOutputAsync(ISshSession session, string marker, TimeSpan timeout)
    {
        var buffer = new StringBuilder();
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutputReceived += chunk =>
        {
            lock (buffer)
            {
                buffer.Append(Encoding.UTF8.GetString(chunk));
                if (buffer.ToString().Contains(marker, StringComparison.Ordinal))
                {
                    found.TrySetResult();
                }
            }
        };

        await Task.WhenAny(found.Task, Task.Delay(timeout));
        lock (buffer)
        {
            return buffer.ToString();
        }
    }

    [SshdFact]
    public async Task FirstConnect_RecordsHostKey_ThenTrusts()
    {
        var (repo, trust) = await NewTrustAsync(HostKeyPolicy.AcceptNew);
        var factory = new SshSessionFactory();
        var options = new SshConnectOptions { HostKeyValidator = trust.VerifyAsync, KeepAliveInterval = TimeSpan.FromSeconds(5) };

        await using (var first = await factory.CreateSessionAsync(_sshd.Config(), _sshd.KeyAuth(), options))
        {
            await first.ConnectAsync();
            Assert.True(first.IsConnected);
        }

        var entry = Assert.Single(await repo.GetAllAsync());
        Assert.Equal(_sshd.HostPublicKeyBase64, entry.PublicKeyBase64);
        Assert.Equal(_sshd.Port, entry.Port);

        await using var second = await factory.CreateSessionAsync(_sshd.Config(), _sshd.KeyAuth(), options);
        await second.ConnectAsync();
        Assert.True(second.IsConnected);
        Assert.NotNull((await repo.GetAllAsync()).Single().LastSeenAt);
    }

    [SshdFact]
    public async Task ChangedHostKey_IsBlockedWithTypedException()
    {
        var (repo, trust) = await NewTrustAsync(HostKeyPolicy.Ask);
        // 预置一把"旧"密钥，模拟服务器密钥被替换 / 中间人
        await repo.SaveAsync(new KnownHostEntry
        {
            Host = "127.0.0.1",
            Port = _sshd.Port,
            KeyType = "ssh-ed25519",
            PublicKeyBase64 = "AAAAC3NzaC1lZDI1NTE5AAAAINNOCjYfYNvN56zScDjq/dDohnNs4wdauZTYtC54QykV",
            FingerprintSha256 = "SHA256:lMz65d+0OmzZYYTRXqePVuIzF8njDm0SKmPbs1feAXY"
        });

        var factory = new SshSessionFactory();
        await using var session = await factory.CreateSessionAsync(
            _sshd.Config(),
            _sshd.KeyAuth(),
            new SshConnectOptions { HostKeyValidator = trust.VerifyAsync });

        var ex = await Assert.ThrowsAsync<HostKeyRejectedException>(() => session.ConnectAsync());
        Assert.Equal(HostKeyVerdict.Changed, ex.Outcome.Evaluation.Verdict);
        Assert.False(session.IsConnected);
    }

    [SshdFact]
    public async Task JumpChain_TwoHops_RunsStartupScript_AndSftpBorrowsChain()
    {
        var (repo, trust) = await NewTrustAsync(HostKeyPolicy.AcceptNew);
        var factory = new SshSessionFactory();
        // 两跳都指向同一个 sshd：本机 → hop1 → hop2 → 目标，验证逐跳转发与逻辑地址校验
        var hops = new List<SshHop>
        {
            new(_sshd.Config("hop1"), _sshd.KeyAuth()),
            new(_sshd.Config("hop2"), _sshd.KeyAuth())
        };
        var options = new SshConnectOptions { HostKeyValidator = trust.VerifyAsync, JumpHosts = hops };
        var target = _sshd.Config("target") with { StartupScript = "echo KEI_MARK_$((40+2))" };

        await using var session = await factory.CreateSessionAsync(target, _sshd.KeyAuth(), options);
        var outputTask = CollectOutputAsync(session, "KEI_MARK_42", TimeSpan.FromSeconds(10));
        await session.ConnectAsync();

        // 算术展开的结果只可能来自远端 shell 执行，而非输入回显
        Assert.Contains("KEI_MARK_42", await outputTask);

        // 主机密钥以逻辑地址（而非 127.0.0.1 临时转发口）登记：全链路只有一条记录
        var entry = Assert.Single(await repo.GetAllAsync());
        Assert.Equal(_sshd.Port, entry.Port);

        await using IRemoteFileSystem fs = await factory.CreateFileSystemAsync(target, _sshd.KeyAuth(), session, options);
        await fs.ConnectAsync();
        var root = await fs.ListDirectoryAsync("/");
        Assert.Contains(root, item => item.Name == "etc" && item.IsDirectory);
    }

    [SshdFact]
    public async Task Scp_MaliciousFileName_IsNotExecutedByRemoteShell()
    {
        var (_, trust) = await NewTrustAsync(HostKeyPolicy.AcceptNew);
        var factory = new SshSessionFactory();
        var config = _sshd.Config() with { FileTransferProtocol = FileTransferProtocol.Scp };
        string workDir = Path.Combine(_sshd.Directory, "scp");
        System.IO.Directory.CreateDirectory(workDir);
        string canary = Path.Combine(workDir, "pwned");
        string evil = Path.Combine(workDir, $"it's $(touch {canary}) `touch {canary}`");

        await using IRemoteFileSystem fs = await factory.CreateFileSystemAsync(
            config,
            _sshd.KeyAuth(),
            options: new SshConnectOptions { HostKeyValidator = trust.VerifyAsync });
        await fs.ConnectAsync();
        await fs.CreateDirectoryAsync(evil);

        Assert.True(System.IO.Directory.Exists(evil));
        Assert.False(File.Exists(canary));

        await fs.DeleteAsync(evil, isDirectory: true);
        Assert.False(System.IO.Directory.Exists(evil));
        Assert.False(File.Exists(canary));
    }
}
