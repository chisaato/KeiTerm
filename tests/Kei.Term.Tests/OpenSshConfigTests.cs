using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

public class OpenSshConfigTests : IDisposable
{
    private const string SampleConfig = """
        # 全局默认
        ServerAliveInterval 30

        Host bastion
            HostName bastion.example.com
            User jump
            Port 2200
            IdentityFile ~/.ssh/jump_ed25519

        Host web db
            HostName %h.internal.example
            ProxyJump bastion

        Host legacy
            HostName 10.0.0.9
            ProxyJump admin@gw1.example:2222,gw2.example

        Host tunnel
            ProxyCommand nc -X 5 -x proxy:1080 %h %p

        Match exec "test -f /tmp/x"
            User should-not-apply

        Host *.internal.example
            User deploy

        Host *
            User fallback
            IdentityFile ~/.ssh/id_ed25519
            IdentitiesOnly yes
        """;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_sshcfg_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Fact]
    public void Resolve_FirstValueWins_AcrossMatchingBlocks()
    {
        var blocks = OpenSshConfigParser.Parse(SampleConfig);

        var bastion = OpenSshConfigParser.Resolve(blocks, "bastion");
        Assert.Equal("bastion.example.com", bastion.HostName);
        Assert.Equal(2200, bastion.Port);
        Assert.Equal("jump", bastion.User);
        // IdentityFile 累加：自身块 + Host *
        Assert.Equal(new[] { "~/.ssh/jump_ed25519", "~/.ssh/id_ed25519" }, bastion.IdentityFiles);
        Assert.True(bastion.IdentitiesOnly);

        // %h 展开为别名。Host 模式按别名（而非 HostName）匹配：web 不命中 *.internal.example，
        // Match 块整体跳过 → User 取 Host * 的值

        var web = OpenSshConfigParser.Resolve(blocks, "web");
        Assert.Equal("web.internal.example", web.HostName);
        Assert.Equal("fallback", web.User);
        Assert.Equal(new[] { "bastion" }, web.ProxyJumps);
    }

    [Fact]
    public void Parse_KeyEqualsValueAndQuotes_AndInclude()
    {
        var blocks = OpenSshConfigParser.Parse(
            """
            Include extra.conf
            Host quoted
                IdentityFile="/keys/my key"
            """,
            _ => ["Host included\n  HostName inc.example\n"]);

        Assert.Equal(new[] { "/keys/my key" }, OpenSshConfigParser.Resolve(blocks, "quoted").IdentityFiles);
        Assert.Equal("inc.example", OpenSshConfigParser.Resolve(blocks, "included").HostName);
    }

    [Theory]
    [InlineData("host", null, "host", null)]
    [InlineData("user@host:2222", "user", "host", 2222)]
    [InlineData("user@[fe80::1]:22", "user", "fe80::1", 22)]
    [InlineData("fe80::1", null, "fe80::1", null)]
    public void ParseJumpSpec(string spec, string? user, string host, int? port)
    {
        var parsed = OpenSshConfigParser.ParseJumpSpec(spec);
        Assert.Equal(user, parsed.User);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
    }

    [Fact]
    public async Task Import_CreatesSessionsIdentitiesAndJumpChains_Idempotently()
    {
        var connStr = $"Data Source={_dbPath}";
        var tree = new SqliteTreeRepository(connStr);
        await tree.InitializeAsync();
        var identities = new SqliteIdentityRepository(connStr);
        var importer = new OpenSshConfigImporter(tree, identities);

        var summary = await importer.ImportAsync(SampleConfig, p => p.Replace("~", "/home/me"));

        var nodes = await tree.GetAllNodesAsync();
        var folder = Assert.Single(nodes.OfType<FolderNode>(), f => f.Name == OpenSshConfigImporter.FolderName);
        var sessions = nodes.OfType<SessionNode>().ToDictionary(s => s.Name);

        // bastion, web(db 为别名), legacy, tunnel + 两个临时跳板
        Assert.Equal(6, summary.SessionsImported);
        Assert.All(sessions.Values, s => Assert.Equal(folder.Id, s.ParentId));
        Assert.Equal("别名: db", sessions["web"].Description);
        Assert.Equal(sessions["bastion"].Id, sessions["web"].JumpHostSessionId);

        // legacy: ProxyJump admin@gw1:2222,gw2 → legacy 经 gw2，gw2 经 gw1
        var gw1 = sessions["admin@gw1.example:2222"];
        var gw2 = sessions["gw2.example"];
        Assert.Equal("admin", gw1.Username);
        Assert.Equal(2222, gw1.Port);
        Assert.Equal(gw2.Id, sessions["legacy"].JumpHostSessionId);
        Assert.Equal(gw1.Id, gw2.JumpHostSessionId);
        Assert.Null(gw1.JumpHostSessionId);

        // 链可被连接期解析器正确展开（由外到内）
        var chain = JumpChainResolver.Resolve(sessions["legacy"], id => sessions.Values.FirstOrDefault(s => s.Id == id));
        Assert.Equal(new[] { gw1.Id, gw2.Id }, chain.Select(n => n.Id));

        Assert.Contains(summary.Warnings, w => w.StartsWith("tunnel", StringComparison.Ordinal));

        // 身份：IdentitiesOnly=yes 时不插入 Agent 方法，路径经宿主展开
        var bastionIdentity = await identities.GetByIdAsync(sessions["bastion"].IdentityId!.Value);
        Assert.NotNull(bastionIdentity);
        Assert.All(bastionIdentity!.Methods, m => Assert.IsType<FilePrivateKeyMethod>(m));
        Assert.Equal(
            new[] { "/home/me/.ssh/jump_ed25519", "/home/me/.ssh/id_ed25519" },
            bastionIdentity.Methods.Cast<FilePrivateKeyMethod>().Select(m => m.KeyFilePath));
        // 相同 IdentityFile 组合的主机共享同一身份
        Assert.Equal(sessions["legacy"].IdentityId, sessions["web"].IdentityId);

        var again = await importer.ImportAsync(SampleConfig, p => p);
        Assert.Equal(0, again.SessionsImported);
        Assert.Equal(4, again.SessionsSkipped);
        Assert.Equal(nodes.Count, (await tree.GetAllNodesAsync()).Count);
    }
}
