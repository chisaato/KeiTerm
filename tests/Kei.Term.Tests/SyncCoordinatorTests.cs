using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.Core.Models;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Sync;
using Kei.Term.Infrastructure.Vault;
using Xunit;

namespace Kei.Term.Tests;

public sealed class SyncCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keiterm-sync-" + Guid.NewGuid().ToString("N"));
    private const string Passphrase = "同步口令 e2ee";

    private async Task<DeviceFixture> CreateDeviceAsync(string name)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var device = new PersistentDeviceId(Path.Combine(directory, "device-id"));
        var db = new SqliteConnectionFactory($"Data Source={Path.Combine(directory, "db.sqlite")};Pooling=False", device);
        var tree = new SqliteTreeRepository(db);
        await tree.InitializeAsync();
        var vault = VaultTestDb.CreateVault(db);
        await vault.SetMasterPasswordAsync("vault-password");
        await vault.SavePassphraseAsync(Passphrase.AsMemory());
        var store = new SqliteSyncSnapshotStore(db);
        return new DeviceFixture(db, tree, vault, device, store, new SyncCoordinator(store, new AgeSyncPackage(), vault, device, vault));
    }

    // 往返/并发用例模拟用户选择合并及确认精确预览；无交互门禁由 Safety 用例独立验证。
    private static async Task<SyncResult> SyncConfirmedAsync(SyncCoordinator coordinator, ISyncLocation location)
    {
        var result = await coordinator.SyncAsync(location);
        while (result.Confirmation is { } preview)
            result = await coordinator.SyncApprovedAsync(location, new(preview.Token));
        return result;
    }

    private ISyncLocation Location => new LocalFolderSyncLocation(Path.Combine(_root, "shared"));

    [Fact]
    public async Task TwoDevices_RoundTripConfigurationChangesMovesAndDeletion_KeepRemoteStamp()
    {
        var a = await CreateDeviceAsync("a");
        var b = await CreateDeviceAsync("b");
        Assert.Null(a.Device.DeviceId);
        var folder = new FolderNode { Name = "folder" };
        var session = new SessionNode { Name = "server", ParentId = folder.Id, Host = "example.org", Port = 2222,
            Username = "alice", StartupScript = "echo hi", EnvironmentVariables = new() { ["LANG"] = "zh_CN.UTF-8" },
            IdentityId = Guid.NewGuid(), ProxyProfileId = Guid.NewGuid(), TerminalProfileId = "profile",
            Overrides = new() { ConnectTimeoutSeconds = 42 } };
        await a.Tree.SaveNodesAsync([folder, session]);
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        Assert.True((await SyncConfirmedAsync(b.Coordinator, Location)).Success);
        var imported = Assert.IsType<SessionNode>(await b.Tree.GetNodeByIdAsync(session.Id));
        Assert.Equal("example.org", imported.Host);
        Assert.Equal(2222, imported.Port);
        Assert.Equal("alice", imported.Username);
        Assert.Equal(session.IdentityId, imported.IdentityId);
        Assert.Equal(session.ProxyProfileId, imported.ProxyProfileId);
        Assert.Equal("profile", imported.TerminalProfileId);
        Assert.Equal(42, imported.Overrides.ConnectTimeoutSeconds);
        Assert.Equal("zh_CN.UTF-8", imported.EnvironmentVariables["LANG"]);
        var stampA = (await a.Store.ExportAsync()).Single(e => e.EntityId == session.Id.ToString());
        var stampB = (await b.Store.ExportAsync()).Single(e => e.EntityId == session.Id.ToString());
        Assert.Equal(stampA.UpdatedAtUtc, stampB.UpdatedAtUtc);
        Assert.Equal(stampA.DeviceId, stampB.DeviceId);
        Assert.Equal(a.Device.DeviceId, new PersistentDeviceId(Path.Combine(_root, "a", "device-id")).DeviceId);
        Assert.NotEqual(a.Device.DeviceId, b.Device.DeviceId);

        imported.Name = "renamed";
        imported.Host = "new.example.org";
        await b.Tree.SaveNodeAsync(imported);
        await b.Tree.MoveNodeAsync(session.Id, null, 7);
        Assert.True((await SyncConfirmedAsync(b.Coordinator, Location)).Success);
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        var changed = Assert.IsType<SessionNode>(await a.Tree.GetNodeByIdAsync(session.Id));
        Assert.Equal("new.example.org", changed.Host);
        Assert.Equal("renamed", changed.Name);
        Assert.Null(changed.ParentId);
        Assert.Equal(7, changed.SortOrder);
        await a.Tree.DeleteNodeAsync(session.Id);
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        Assert.True((await SyncConfirmedAsync(b.Coordinator, Location)).Success);
        Assert.Null(await b.Tree.GetNodeByIdAsync(session.Id));
        // 空闲轮次不重写远端，导入不会把远端版本变为本机新修改。
        byte[] before = (await Location.ReadAsync())!.Content;
        Assert.True((await SyncConfirmedAsync(b.Coordinator, Location)).Success);
        Assert.Equal(before, (await Location.ReadAsync())!.Content);
    }

    [Fact]
    public async Task DeleteFolder_ConcurrentNewerChildEditSurvivesAtRoot_OlderSiblingDeleted()
    {
        var a = await CreateDeviceAsync("a");
        var b = await CreateDeviceAsync("b");
        var folder = new FolderNode { Name = "folder" };
        var child = new SessionNode { ParentId = folder.Id, Name = "child", Host = "old" };
        var sibling = new SessionNode { ParentId = folder.Id, Name = "sibling", Host = "deleted" };
        await a.Tree.SaveNodesAsync([folder, child, sibling]);
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        Assert.True((await SyncConfirmedAsync(b.Coordinator, Location)).Success);
        await a.Tree.DeleteNodeAsync(folder.Id);
        var edited = Assert.IsType<SessionNode>(await b.Tree.GetNodeByIdAsync(child.Id));
        edited.Host = "new";
        await b.Tree.SaveNodeAsync(edited);
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        Assert.True((await SyncConfirmedAsync(b.Coordinator, Location)).Success);
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        Assert.Null(await a.Tree.GetNodeByIdAsync(folder.Id));
        Assert.Null(await a.Tree.GetNodeByIdAsync(sibling.Id));
        var survivor = Assert.IsType<SessionNode>(await a.Tree.GetNodeByIdAsync(child.Id));
        Assert.Equal("new", survivor.Host);
        Assert.Null(survivor.ParentId);
    }

    [Fact]
    public async Task EqualTimestamps_OrdinalDeviceIdWinsOnBothDevices()
    {
        var a = await CreateDeviceAsync("a");
        var b = await CreateDeviceAsync("b");
        a.Device.EnsureCreated();
        b.Device.EnsureCreated();
        Guid id = Guid.NewGuid();
        await a.Tree.SaveNodeAsync(new SessionNode { Id = id, Name = "a", Host = "a" });
        await b.Tree.SaveNodeAsync(new SessionNode { Id = id, Name = "b", Host = "b" });
        string instant = DateTime.UtcNow.ToString("O");
        foreach (DeviceFixture device in new[] { a, b })
        {
            using var conn = await device.Db.OpenAsync();
            await conn.ExecuteAsync("UPDATE tree_nodes SET updated_at = @instant;", new { instant });
        }
        string winner = StringComparer.Ordinal.Compare(a.Device.DeviceId, b.Device.DeviceId) > 0 ? "a" : "b";
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        Assert.True((await SyncConfirmedAsync(b.Coordinator, Location)).Success);
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        Assert.Equal(winner, Assert.IsType<SessionNode>(await a.Tree.GetNodeByIdAsync(id)).Host);
        Assert.Equal(winner, Assert.IsType<SessionNode>(await b.Tree.GetNodeByIdAsync(id)).Host);
    }

    [Fact]
    public async Task WrongPassphraseAndLockedVault_DoNotChangeLocalDataOrRemote()
    {
        var a = await CreateDeviceAsync("a");
        var b = await CreateDeviceAsync("b");
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "remote" });
        await b.Tree.SaveNodeAsync(new FolderNode { Name = "local" });
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        var before = JsonSerializer.Serialize(await b.Store.ExportAsync());
        var remoteBefore = (await Location.ReadAsync())!.Content;
        await b.Vault.SavePassphraseAsync("wrong".AsMemory());
        var result = await b.Coordinator.SyncAsync(Location);
        Assert.False(result.Success);
        Assert.Equal("同步口令不正确", result.Message);
        Assert.Equal(before, JsonSerializer.Serialize(await b.Store.ExportAsync()));
        Assert.Equal(remoteBefore, (await Location.ReadAsync())!.Content);
        b.Vault.Lock();
        result = await b.Coordinator.SyncAsync(Location);
        Assert.Equal("保险库已锁定", result.Message);
        Assert.Equal(before, JsonSerializer.Serialize(await b.Store.ExportAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverwriteAfterUpload_RetriesWholeRound_PreservesBothDevices(bool conditional)
    {
        var a = await CreateDeviceAsync("a");
        var b = await CreateDeviceAsync("b");
        var nodeA = new FolderNode { Name = "A" };
        var nodeB = new FolderNode { Name = "B" };
        await a.Tree.SaveNodeAsync(nodeA);
        await b.Tree.SaveNodeAsync(nodeB);
        var bSnapshot = new SyncSnapshot(1, b.Device.EnsureCreated(), DateTime.UtcNow, await b.Store.ExportAsync());
        byte[] otherBytes = new AgeSyncPackage().Encrypt(JsonSerializer.SerializeToUtf8Bytes(bSnapshot), Passphrase);
        var racing = new RacingLocation(Location, otherBytes, conditional, always: false);
        Assert.True((await SyncConfirmedAsync(a.Coordinator, racing)).Success);
        Assert.True(racing.WriteCount >= 2);
        Assert.True((await SyncConfirmedAsync(b.Coordinator, Location)).Success);
        Assert.NotNull(await b.Tree.GetNodeByIdAsync(nodeA.Id));
        Assert.NotNull(await b.Tree.GetNodeByIdAsync(nodeB.Id));
        Assert.NotNull(await a.Tree.GetNodeByIdAsync(nodeB.Id));
    }

    [Fact]
    public async Task PersistentOverwrite_StopsAtThreeAttempts()
    {
        var a = await CreateDeviceAsync("a");
        var b = await CreateDeviceAsync("b");
        var node = new FolderNode { Name = "A" };
        await a.Tree.SaveNodeAsync(node);
        // 已完成首次初始化，单独验证持续覆盖时三轮上限。
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        node.Name = "A-edited";
        await a.Tree.SaveNodeAsync(node);
        var racing = new RacingLocation(Location, new AgeSyncPackage().Encrypt(
            JsonSerializer.SerializeToUtf8Bytes(new SyncSnapshot(1, b.Device.EnsureCreated(), DateTime.UtcNow, [])), Passphrase), false, always: true);
        var result = await a.Coordinator.SyncAsync(racing);
        Assert.False(result.Success);
        Assert.Contains("3 次", result.Message);
        Assert.Equal(3, racing.WriteCount);
        Assert.Single(await a.Tree.GetAllNodesAsync());
    }

    [Fact]
    public async Task NewerSnapshotFormat_IsRejectedBeforeLocalImport()
    {
        var a = await CreateDeviceAsync("a");
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "local" });
        await Location.WriteAsync(new AgeSyncPackage().Encrypt(JsonSerializer.SerializeToUtf8Bytes(
            new SyncSnapshot(999, "future", DateTime.UtcNow, [])), Passphrase));
        var result = await a.Coordinator.SyncAsync(Location);
        Assert.False(result.Success);
        Assert.Contains("升级", result.Message);
        Assert.Equal("local", Assert.Single(await a.Tree.GetAllNodesAsync()).Name);
    }

    [Fact]
    public async Task SuccessfulSync_PurgesOnlyTombstonesOlderThan180Days()
    {
        var a = await CreateDeviceAsync("a");
        var old = new FolderNode { Name = "old" };
        var recent = new FolderNode { Name = "recent" };
        await a.Tree.SaveNodesAsync([old, recent]);
        await a.Tree.DeleteNodeAsync(old.Id);
        await a.Tree.DeleteNodeAsync(recent.Id);
        using (var conn = await a.Db.OpenAsync())
            await conn.ExecuteAsync("UPDATE tree_nodes SET deleted_at = @date, updated_at = @date WHERE id = @id;",
                new { date = DateTime.UtcNow.AddDays(-181).ToString("O"), id = old.Id.ToString() });
        Assert.True((await SyncConfirmedAsync(a.Coordinator, Location)).Success);
        var entities = await a.Store.ExportAsync();
        Assert.DoesNotContain(entities, e => e.EntityId == old.Id.ToString());
        Assert.Contains(entities, e => e.EntityId == recent.Id.ToString() && e.DeletedAtUtc != null);
    }

    [Fact]
    public async Task TruncatedPackage_IsReadableFailureAndDoesNotChangeLocal()
    {
        var a = await CreateDeviceAsync("a");
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "local" });
        byte[] valid = new AgeSyncPackage().Encrypt(JsonSerializer.SerializeToUtf8Bytes(
            new SyncSnapshot(1, "remote", DateTime.UtcNow, [])), Passphrase);
        await Location.WriteAsync(valid[..^8]);
        string before = JsonSerializer.Serialize(await a.Store.ExportAsync());
        var result = await a.Coordinator.SyncAsync(Location);
        Assert.False(result.Success);
        Assert.Contains("同步包无法读取", result.Message);
        Assert.Equal(before, JsonSerializer.Serialize(await a.Store.ExportAsync()));
    }

    [Fact]
    public async Task Import_RechecksLocalEditsMadeAfterExport()
    {
        var a = await CreateDeviceAsync("a");
        var session = new SessionNode { Name = "session", Host = "initial" };
        await a.Tree.SaveNodeAsync(session);
        var earlier = await a.Store.ExportAsync();
        session.Host = "edited-during-network";
        await a.Tree.SaveNodeAsync(session);
        var imported = await a.Store.ImportAsync(earlier);
        Assert.Equal("edited-during-network", Assert.IsType<SessionNode>(await a.Tree.GetNodeByIdAsync(session.Id)).Host);
        Assert.Equal("edited-during-network", imported.Single().Data.Deserialize<SyncTreeData>()!.Session!.Host);
    }

    [Fact]
    public async Task ImportFailure_RollsBackAllEntities()
    {
        var a = await CreateDeviceAsync("a");
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "first" });
        var entities = await a.Store.ExportAsync();
        var data = entities[0].Data.Deserialize<SyncTreeData>()!;
        data.Node.Name = "must rollback";
        var changed = entities[0] with { Data = JsonSerializer.SerializeToElement(data) };
        var invalid = changed with { EntityId = Guid.NewGuid().ToString() };
        await Assert.ThrowsAsync<JsonException>(() => a.Store.ImportAsync([changed, invalid]));
        Assert.Equal("first", Assert.Single(await a.Tree.GetAllNodesAsync()).Name);
    }

    [Fact]
    public async Task Passphrase_StaysInLocalVault_AcrossRestartAndMasterPasswordChanges()
    {
        var a = await CreateDeviceAsync("a");
        using (var conn = await a.Db.OpenAsync())
        {
            byte[] blob = await conn.QuerySingleAsync<byte[]>("SELECT secrets_blob FROM local_vault_secrets;");
            Assert.DoesNotContain(Passphrase, System.Text.Encoding.UTF8.GetString(blob));
        }
        var restarted = VaultTestDb.CreateVault(a.Db);
        await restarted.TryAutoUnlockAsync();
        await restarted.UnlockAsync("vault-password", false);
        Assert.Equal(Passphrase, new string((await restarted.GetPassphraseAsync())!));
        await restarted.SetMasterPasswordAsync("new-vault-password");
        restarted.Lock();
        await restarted.UnlockAsync("new-vault-password", false);
        Assert.Equal(Passphrase, new string((await restarted.GetPassphraseAsync())!));
        await restarted.RemoveMasterPasswordAsync();
        Assert.Equal(Passphrase, new string((await restarted.GetPassphraseAsync())!));
        await restarted.SetMasterPasswordAsync("third-password");
        Assert.Equal(Passphrase, new string((await restarted.GetPassphraseAsync())!));
        Assert.Empty((await a.Store.ExportAsync()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed record DeviceFixture(SqliteConnectionFactory Db, SqliteTreeRepository Tree, InternalVaultManager Vault,
        PersistentDeviceId Device, SqliteSyncSnapshotStore Store, SyncCoordinator Coordinator);

    // 包装真实目录，仅在竞争窗口注入另一设备已加密的完整快照。
    private sealed class RacingLocation(ISyncLocation inner, byte[] competing, bool conditional, bool always) : ISyncLocation
    {
        public int WriteCount { get; private set; }
        public bool SupportsConditionalWrites => conditional;
        public Task TestConnectionAsync(CancellationToken ct = default) => inner.TestConnectionAsync(ct);
        public Task<SyncObject?> ReadAsync(CancellationToken ct = default) => inner.ReadAsync(ct);
        public async Task WriteAsync(byte[] content, string? expectedVersionTag = null, CancellationToken ct = default)
        {
            WriteCount++;
            if (always || WriteCount == 1)
            {
                if (!conditional) await inner.WriteAsync(content, expectedVersionTag, ct);
                await inner.WriteAsync(competing, null, ct);
                if (conditional) throw new SyncWriteConflictException();
            }
            else await inner.WriteAsync(content, expectedVersionTag, ct);
        }
        public Task<IReadOnlyList<SyncHistoryEntry>> ListHistoryAsync(CancellationToken ct = default) => inner.ListHistoryAsync(ct);
        public Task<byte[]?> ReadHistoryAsync(string name, CancellationToken ct = default) => inner.ReadHistoryAsync(name, ct);
        public Task WriteHistoryAsync(string name, byte[] content, CancellationToken ct = default) => inner.WriteHistoryAsync(name, content, ct);
        public Task DeleteHistoryAsync(string name, CancellationToken ct = default) => inner.DeleteHistoryAsync(name, ct);
    }
}
