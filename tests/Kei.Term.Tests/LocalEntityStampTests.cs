using Dapper;
using Kei.Term.App.Services;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Transfers;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;

namespace Kei.Term.Tests;

public sealed class LocalEntityStampTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keiterm-stamps-" + Guid.NewGuid().ToString("N"));
    private readonly Device _device = new();
    private SqliteConnectionFactory Factory => new("Data Source=" + Path.Combine(_directory, "data.db"), _device);

    public LocalEntityStampTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private sealed class Device : ICurrentDeviceId { public string? DeviceId { get; set; } = "device-a"; }

    [Fact]
    public async Task SubtreeDeletion_TombstonesEveryChild_AndPurgeDoesNotDeleteLiveData()
    {
        await SchemaMigrator.MigrateAsync(Factory);
        var tree = new SqliteTreeRepository(Factory);
        var root = new FolderNode { Name = "root" };
        var folder = new FolderNode { Name = "folder", ParentId = root.Id };
        var session = new SessionNode { Name = "session", Host = "host", ParentId = folder.Id };
        var live = new SessionNode { Name = "live", Host = "other" };
        await tree.SaveNodesAsync([root, folder, session, live]);
        var forwards = new SqlitePortForwardRepository(Factory);
        await forwards.SaveAsync(new PortForward { SessionId = session.Id, Mode = PortForwardMode.Dynamic, ListenPort = 1080 });
        var metadata = new SqliteSessionMetadataRepository(Factory);
        await metadata.SetTransferProbeAsync(session.Id, new TransferProbeResult(RemotePlatform.Linux, [], [], DateTime.UtcNow));
        await tree.DeleteNodeAsync(root.Id);
        Assert.Equal(live.Id, Assert.Single(await tree.GetAllNodesAsync()).Id);
        Assert.Null(await tree.GetNodeByIdAsync(session.Id));
        Assert.Null(await metadata.GetTransferProbeAsync(session.Id));
        Assert.Empty(await forwards.GetBySessionAsync(session.Id));
        using var conn = await Factory.OpenAsync();
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tree_nodes WHERE deleted_at IS NOT NULL AND device_id = 'device-a' AND revision = 2"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM session_details WHERE deleted_at IS NOT NULL"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM port_forwards WHERE deleted_at IS NOT NULL"));
        var tracker = new SqliteLocalChangeTracker(Factory);
        Assert.NotNull(Assert.Single(await tracker.GetAllAsync()).DeletedAtUtc);
        await tracker.PurgeTombstonesBeforeAsync(DateTime.UtcNow.AddDays(-1));
        Assert.Equal(4, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tree_nodes"));
        await tracker.PurgeTombstonesBeforeAsync(DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tree_nodes"));
        Assert.Empty(await tracker.GetAllAsync());
        Assert.Equal(live.Id, Assert.Single(await tree.GetAllNodesAsync()).Id);
    }

    [Fact]
    public async Task EditorDeletion_TombstonesPathsAndAssociations_AndCanBeRecreated()
    {
        await SchemaMigrator.MigrateAsync(Factory);
        var repo = new SqliteExternalEditorRepository(Factory);
        var editor = new ExternalEditor { Name = "code" };
        editor.Paths.Add(new ExternalEditorPath(Guid.NewGuid(), editor.Id, "linux", "/usr/bin/code"));
        await repo.SaveEditorAsync(editor);
        var rule = new FileAssociationRule { EditorId = editor.Id, Pattern = "*.txt" };
        await repo.SaveAssociationAsync(rule);
        await repo.DeleteEditorAsync(editor.Id);
        Assert.Empty(await repo.GetAllEditorsAsync());
        Assert.Empty(await repo.GetAllAssociationsAsync());
        Assert.Null(await repo.GetEditorByIdAsync(editor.Id));
        using var conn = await Factory.OpenAsync();
        foreach (var table in new[] { "external_editors", "external_editor_paths", "file_associations" })
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE deleted_at IS NOT NULL"));
        await repo.SaveEditorAsync(editor);
        Assert.Single((await repo.GetEditorByIdAsync(editor.Id))!.Paths);
        Assert.Empty(await repo.GetAllAssociationsAsync());
        await repo.SaveAssociationAsync(rule);
        Assert.Single(await repo.GetAllAssociationsAsync());
    }

    [Fact]
    public async Task SecretsWrite_StampsOwner_AndDeletionHidesButRetainsSecrets()
    {
        await SchemaMigrator.MigrateAsync(Factory);
        var identities = new SqliteIdentityRepository(Factory);
        var identity = new Identity { Name = "id" };
        await identities.SaveAsync(identity);
        var proxies = new SqliteProxyRepository(Factory);
        var proxy = new ProxyProfile { Name = "proxy", Config = new Socks5ProxyConfig("localhost", 1080) };
        await proxies.SaveAsync(proxy);
        var vault = new Kei.Term.Infrastructure.Vault.InternalVaultManager(Factory);
        using var conn = await Factory.OpenAsync();
        await conn.ExecuteAsync("UPDATE identities SET updated_at = '2000-01-01T00:00:00.0000000Z'; UPDATE proxies SET updated_at = '2000-01-01T00:00:00.0000000Z';");
        _device.DeviceId = "device-b";
        await vault.SaveSecretsAsync(identity.Id, new() { ["password"] = new SecretPayload { Password = "test" } });
        await vault.SetPasswordAsync(proxy.Id, "test");
        foreach (var table in new[] { "identities", "proxies" })
        {
            Assert.Equal(2, await conn.ExecuteScalarAsync<int>($"SELECT revision FROM {table}"));
            Assert.Equal("device-b", await conn.ExecuteScalarAsync<string>($"SELECT device_id FROM {table}"));
            Assert.NotEqual("2000-01-01T00:00:00.0000000Z", await conn.ExecuteScalarAsync<string>($"SELECT updated_at FROM {table}"));
        }
        await identities.DeleteAsync(identity.Id);
        await proxies.DeleteAsync(proxy.Id);
        Assert.Empty(await vault.GetSecretsAsync(identity.Id));
        Assert.Null(await vault.GetPasswordAsync(proxy.Id));
        Assert.False(await vault.HasPasswordAsync(proxy.Id));
        Assert.Empty(await proxies.GetAllAsync());
        Assert.Null(await proxies.GetByIdAsync(proxy.Id));
        foreach (var table in new[] { "identity_secrets", "proxy_secrets" })
            Assert.Equal(1, await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE deleted_at IS NOT NULL"));
    }

    [Fact]
    public async Task SettingsAndProfiles_TrackChangedItemsOnly_AndSurviveRestartWithoutRestamping()
    {
        await SchemaMigrator.MigrateAsync(Factory);
        var tracker = new SqliteLocalChangeTracker(Factory);
        var path = Path.Combine(_directory, "settings.json");
        var settings = new JsonSettingsService(path, tracker);
        await settings.LoadSettingsAsync();
        var old = (await tracker.GetAllAsync()).ToDictionary(s => s.EntityId);
        await settings.CommitFontSizeAsync(18);
        settings.Current.FileTransfer.VerifyHashEnabled = false;
        await settings.SaveSettingsAsync(settings.Current);
        var changed = (await tracker.GetAllAsync()).ToDictionary(s => s.EntityId);
        Assert.Equal(old["FontSize"].Revision + 1, changed["FontSize"].Revision);
        Assert.Equal(old["FileTransfer.VerifyHashEnabled"].Revision + 1, changed["FileTransfer.VerifyHashEnabled"].Revision);
        Assert.Equal(old["FontFamily"], changed["FontFamily"]);
        var restarted = new JsonSettingsService(path, tracker);
        await restarted.LoadSettingsAsync();
        Assert.Equal(changed.OrderBy(p => p.Key), (await tracker.GetAllAsync()).ToDictionary(s => s.EntityId).OrderBy(p => p.Key));
        var profiles = new ProfileManagerService(settings, _directory, tracker: tracker);
        await profiles.InitializeAsync();
        var profile = new TerminalProfile { Id = "custom", Name = "test" };
        profiles.AddCustomTerminalProfile(profile);
        await profiles.CommitAndSaveProfilesAsync();
        var stamp = Assert.Single(await tracker.GetAllAsync(), s => s.EntityType == "profile");
        Assert.Null(stamp.DeletedAtUtc);
        var reopened = new ProfileManagerService(settings, _directory, tracker: tracker);
        await reopened.InitializeAsync();
        Assert.Equal(stamp, Assert.Single(await tracker.GetAllAsync(), s => s.EntityType == "profile"));
        reopened.RemoveCustomTerminalProfile(profile.Id);
        await reopened.CommitAndSaveProfilesAsync();
        var deleted = Assert.Single(await tracker.GetAllAsync(), s => s.EntityType == "profile");
        Assert.NotNull(deleted.DeletedAtUtc);
        Assert.Equal(stamp.Revision + 1, deleted.Revision);
    }

    [Fact]
    public async Task Metadata_TracksKeysWithoutTouchingSessionRevision_AndMissingSessionDoesNotCreateStamp()
    {
        await SchemaMigrator.MigrateAsync(Factory);
        var tree = new SqliteTreeRepository(Factory);
        var session = new SessionNode { Name = "host", Host = "host" };
        await tree.SaveNodeAsync(session);
        var repo = new SqliteSessionMetadataRepository(Factory);
        await repo.SetTransferProbeAsync(session.Id, new TransferProbeResult(RemotePlatform.Linux, [], [], DateTime.UtcNow));
        var tracker = new SqliteLocalChangeTracker(Factory);
        var stamp = Assert.Single(await tracker.GetAllAsync());
        Assert.Equal(session.Id + "/transferProbe", stamp.EntityId);
        Assert.Equal("device-a", stamp.DeviceId);
        Assert.Null(stamp.DeletedAtUtc);
        await repo.ClearTransferProbeAsync(session.Id);
        Assert.NotNull(Assert.Single(await tracker.GetAllAsync()).DeletedAtUtc);
        Assert.False(await repo.SetTransferProbeAsync(Guid.NewGuid(), new TransferProbeResult(RemotePlatform.Linux, [], [], DateTime.UtcNow)));
        Assert.Single(await tracker.GetAllAsync());
        using var conn = await Factory.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT revision FROM tree_nodes"));
        await tree.MoveNodeAsync(session.Id, null, 5);
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT revision FROM tree_nodes"));
    }

    [Fact]
    public async Task PortForwardDeletion_ReleasesListenAddressWithoutRemovingTombstone()
    {
        await SchemaMigrator.MigrateAsync(Factory);
        var session = new SessionNode { Name = "host", Host = "host" };
        await new SqliteTreeRepository(Factory).SaveNodeAsync(session);
        var repo = new SqlitePortForwardRepository(Factory);
        var forward = new PortForward { SessionId = session.Id, Mode = PortForwardMode.Dynamic, ListenPort = 1080 };
        await repo.SaveAsync(forward);
        await repo.DeleteAsync(forward.Id);
        await repo.SaveAsync(new PortForward { SessionId = session.Id, Mode = PortForwardMode.Dynamic, ListenPort = 1080 });
        Assert.Single(await repo.GetBySessionAsync(session.Id));
        using var conn = await Factory.OpenAsync();
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM port_forwards"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM port_forwards WHERE deleted_at IS NOT NULL AND revision = 2 AND device_id = 'device-a' AND updated_at = deleted_at"));
    }

    [Fact]
    public async Task PurgeOldParent_DoesNotCascadeDeleteANewerChild()
    {
        await SchemaMigrator.MigrateAsync(Factory);
        var tree = new SqliteTreeRepository(Factory);
        var folder = new FolderNode { Name = "folder" };
        var session = new SessionNode { Name = "session", Host = "host", ParentId = folder.Id };
        await tree.SaveNodesAsync([folder, session]);
        await tree.DeleteNodeAsync(folder.Id);
        using var conn = await Factory.OpenAsync();
        await conn.ExecuteAsync("UPDATE tree_nodes SET deleted_at = '2000-01-01T00:00:00.0000000Z' WHERE id = @id", new { id = folder.Id.ToString() });
        await new SqliteLocalChangeTracker(Factory).PurgeTombstonesBeforeAsync(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tree_nodes"));
        Assert.Null(await conn.ExecuteScalarAsync<string>("SELECT parent_id FROM tree_nodes"));
        Assert.Equal(session.Id.ToString(), await conn.ExecuteScalarAsync<string>("SELECT id FROM tree_nodes"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM session_details"));
    }

    [Fact]
    public async Task UpgradeV8_BackfillsExistingMetadataKeys_WithoutChangingSessionRevision()
    {
        using (var conn = await Factory.OpenAsync())
        {
            foreach (var migration in SchemaMigrations.All.Where(m => m.Version <= 8))
            {
                using var tx = conn.BeginTransaction();
                await migration.Apply(conn, tx, default);
                await conn.ExecuteAsync("PRAGMA user_version = " + migration.Version, transaction: tx);
                tx.Commit();
            }
            await conn.ExecuteAsync(@"
                INSERT INTO tree_nodes(id,node_type,name,created_at,updated_at,revision) VALUES ('legacy',1,'legacy','2026-01-01T00:00:00.0000000Z','2026-01-02T00:00:00.0000000Z',4);
                INSERT INTO session_details(node_id,host,metadata_json) VALUES ('legacy','host','{""one"":1,""two"":true}');");
        }
        await SchemaMigrator.MigrateAsync(Factory);
        var stamps = await new SqliteLocalChangeTracker(Factory).GetAllAsync();
        Assert.Equal(new[] { "legacy/one", "legacy/two" }, stamps.Select(s => s.EntityId).OrderBy(id => id));
        Assert.All(stamps, s => Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), s.UpdatedAtUtc));
        using var check = await Factory.OpenAsync();
        Assert.Equal(4, await check.ExecuteScalarAsync<int>("SELECT revision FROM tree_nodes"));
    }

    [Fact]
    public async Task KnownHost_NaturalKeyDeduplicatesIncludingTombstone_AndDeletedPatternIsNotRead()
    {
        await SchemaMigrator.MigrateAsync(Factory);
        var repo = new SqliteKnownHostRepository(Factory);
        var entry = new KnownHostEntry { Host = "host", Port = 22, KeyType = "ssh-ed25519", PublicKeyBase64 = "key", FingerprintSha256 = "fingerprint" };
        await repo.SaveAsync(entry);
        await repo.DeleteAsync(entry.Id);
        Assert.Empty(await repo.GetAllAsync());
        Assert.Empty(await repo.GetCandidatesAsync("host", 22));
        using var conn = await Factory.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM known_hosts WHERE deleted_at IS NOT NULL"));
        entry.Id = Guid.NewGuid();
        await repo.SaveAsync(entry);
        Assert.Single(await repo.GetAllAsync());
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM known_hosts"));
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>("SELECT revision FROM known_hosts"));
    }
}
