using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.Core.Models;
using Kei.Term.Infrastructure.Storage;
using Xunit;

namespace Kei.Term.Tests;

public class SqliteProfilePersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SqliteProfilePersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_profile_test_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task SaveNodeAsync_WithTerminalProfileId_PersistsAndReadsBack()
    {
        var repo = new SqliteTreeRepository(_connectionString);
        await repo.InitializeAsync();

        var profileId = "builtin-term-monokai";
        var session = new SessionNode
        {
            Id = Guid.NewGuid(),
            Name = "Server with Custom Profile",
            Host = "192.168.1.100",
            Port = 22,
            Username = "root",
            TerminalProfileId = profileId
        };

        await repo.SaveNodeAsync(session);

        var loaded = await repo.GetNodeByIdAsync(session.Id) as SessionNode;
        Assert.NotNull(loaded);
        Assert.Equal(session.Name, loaded.Name);
        Assert.Equal(profileId, loaded.TerminalProfileId);
    }

    [Fact]
    public async Task SaveNodeAsync_WithNullTerminalProfileId_PersistsAsNull()
    {
        var repo = new SqliteTreeRepository(_connectionString);
        await repo.InitializeAsync();

        var session = new SessionNode
        {
            Id = Guid.NewGuid(),
            Name = "Server with Default Profile",
            Host = "192.168.1.101",
            Port = 22,
            Username = "root",
            TerminalProfileId = null
        };

        await repo.SaveNodeAsync(session);

        var loaded = await repo.GetNodeByIdAsync(session.Id) as SessionNode;
        Assert.NotNull(loaded);
        Assert.Null(loaded.TerminalProfileId);
    }

    [Fact]
    public async Task SaveNodeAsync_UpdateTerminalProfileId_UpdatesPersistedValue()
    {
        var repo = new SqliteTreeRepository(_connectionString);
        await repo.InitializeAsync();

        var session = new SessionNode
        {
            Id = Guid.NewGuid(),
            Name = "Updatable Session",
            Host = "192.168.1.102",
            Port = 22,
            TerminalProfileId = "builtin-term-dracula"
        };

        await repo.SaveNodeAsync(session);

        var loaded1 = await repo.GetNodeByIdAsync(session.Id) as SessionNode;
        Assert.NotNull(loaded1);
        Assert.Equal("builtin-term-dracula", loaded1.TerminalProfileId);

        // 更新为另一个 profile id
        session.TerminalProfileId = "builtin-term-solarized-dark";
        await repo.SaveNodeAsync(session);

        var loaded2 = await repo.GetNodeByIdAsync(session.Id) as SessionNode;
        Assert.NotNull(loaded2);
        Assert.Equal("builtin-term-solarized-dark", loaded2.TerminalProfileId);

        // 更新为 null 回退全局
        session.TerminalProfileId = null;
        await repo.SaveNodeAsync(session);

        var loaded3 = await repo.GetNodeByIdAsync(session.Id) as SessionNode;
        Assert.NotNull(loaded3);
        Assert.Null(loaded3.TerminalProfileId);
    }

    [Fact]
    public async Task Overrides_RoundTrip_NullMeansInherit()
    {
        var repo = new SqliteTreeRepository(_connectionString);
        await repo.InitializeAsync();
        var explicitSession = new SessionNode
        {
            Name = "a",
            Host = "h",
            Overrides = new SessionOverrides { FollowRemoteTitle = false, CwdFollow = CwdFollowMode.OnceOnOpen }
        };
        var inheriting = new SessionNode { Name = "b", Host = "h" };

        await repo.SaveNodeAsync(explicitSession);
        await repo.SaveNodeAsync(inheriting);

        var loadedExplicit = Assert.IsType<SessionNode>(await repo.GetNodeByIdAsync(explicitSession.Id));
        Assert.False(loadedExplicit.Overrides.FollowRemoteTitle);
        Assert.Equal(CwdFollowMode.OnceOnOpen, loadedExplicit.Overrides.CwdFollow);

        var loadedInheriting = Assert.IsType<SessionNode>(await repo.GetNodeByIdAsync(inheriting.Id));
        Assert.Null(loadedInheriting.Overrides.FollowRemoteTitle);
        Assert.Null(loadedInheriting.Overrides.CwdFollow);
    }

    [Fact]
    public async Task CorruptOverridesJson_DoesNotBreakTreeLoad()
    {
        var factory = new SqliteConnectionFactory(_connectionString);
        var repo = new SqliteTreeRepository(factory);
        await repo.InitializeAsync();
        var session = new SessionNode { Name = "a", Host = "h" };
        await repo.SaveNodeAsync(session);
        using (var conn = await factory.OpenAsync())
        {
            // 模拟更新版本写入的未知枚举值
            await conn.ExecuteAsync("UPDATE session_details SET options_json = '{\"CwdFollow\":\"Teleport\"}';");
        }

        var all = await repo.GetAllNodesAsync();

        var loaded = Assert.IsType<SessionNode>(Assert.Single(all));
        Assert.Null(loaded.Overrides.CwdFollow);
    }

    [Fact]
    public async Task SaveNodesAsync_IsAllOrNothing()
    {
        var repo = new SqliteTreeRepository(_connectionString);
        await repo.InitializeAsync();
        var existing = new SessionNode { Name = "keep", Host = "old" };
        await repo.SaveNodeAsync(existing);

        existing.Host = "new";
        // 父节点不存在 → 外键失败；前一个节点的修改也必须回滚
        var broken = new SessionNode { Name = "broken", Host = "h", ParentId = Guid.NewGuid() };

        await Assert.ThrowsAnyAsync<Exception>(() => repo.SaveNodesAsync([existing, broken]));

        var reloaded = await repo.GetAllNodesAsync();
        Assert.Equal("old", Assert.IsType<SessionNode>(Assert.Single(reloaded)).Host);
    }
}
