using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.App.Services;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Sync;
using Kei.Term.Core.Transfers;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Sync;
using Kei.Term.Infrastructure.Vault;
using Xunit;

namespace Kei.Term.Tests;

public sealed partial class FullSyncSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keiterm-full-sync-" + Guid.NewGuid().ToString("N"));
    private const string Passphrase = "shared-sync-passphrase";
    // 测试输入独立于生产排除清单，删漏清单中的任何类别都会泄露这些可识别值。
    private static readonly string[] LocalOnlySettings = ["WindowPosition", "WindowSize", "WindowX", "WindowY", "WindowWidth", "WindowHeight",
        "RecentDirectories", "LastLocalDirectory", "LastRemoteDirectory", "QuickUnlock", "Sync", "DeviceId", "sync-location"];
    private ISyncLocation Location => new LocalFolderSyncLocation(Path.Combine(_root, "shared"));
    private sealed record Device(string Directory, SqliteConnectionFactory Db, InternalVaultManager Vault, SqliteTreeRepository Tree,
        JsonSettingsService Settings, ProfileManagerService Profiles, SqliteSyncSnapshotStore Store, SyncCoordinator Coordinator);
    private sealed record Content(Identity Identity, ProxyProfile Proxy, SessionNode Session, ExternalEditor Editor, FileAssociationRule Association, PortForward Forward, KnownHostEntry Host);

    private async Task<Device> CreateAsync(string name, SyncChangeSignal? changes = null)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var id = new PersistentDeviceId(Path.Combine(dir, "sync-device-id.txt"));
        id.EnsureCreated();
        var db = new SqliteConnectionFactory($"Data Source={Path.Combine(dir, "db.sqlite")};Pooling=False", id, changes);
        await new SqliteTreeRepository(db).InitializeAsync();
        var vault = VaultTestDb.CreateVault(db);
        await vault.SetMasterPasswordAsync("different-vault-password-" + name);
        await vault.SavePassphraseAsync(Passphrase.AsMemory());
        var tracker = new SqliteLocalChangeTracker(db);
        string settingsPath = Path.Combine(dir, "settings.json");
        var settings = new JsonSettingsService(settingsPath, tracker);
        await settings.LoadSettingsAsync();
        var profiles = new ProfileManagerService(settings, dir, tracker: tracker);
        await profiles.InitializeAsync();
        var tree = new SqliteTreeRepository(db, profileExists: profiles.HasCompleteProfileReferences, defaultIdentityId: () => settings.Current.DefaultIdentityId);
        var store = new SqliteSyncSnapshotStore(db, vault, settingsPath, Path.Combine(dir, "profiles.json"));
        store.Imported += async () =>
        {
            await settings.ReloadSynchronizedAsync();
            await profiles.ReloadSynchronizedAsync();
        };
        return new(dir, db, vault, tree, settings, profiles, store, new(store, new AgeSyncPackage(), vault, id, vault));
    }

    private async Task SyncAsync(Device device)
    {
        var result = await device.Coordinator.SyncAsync(Location);
        // 这些往返用例模拟用户明确选择合并，并确认当前精确的删除预览；门禁本身由 Safety 用例验证。
        while (result.Confirmation is { } preview)
            result = await device.Coordinator.SyncApprovedAsync(Location, new(preview.Token));
        Assert.True(result.Success, result.Message);
    }

    private static async Task<Content> PopulateAsync(Device device)
    {
        var identity = new Identity { Name = "identity", Username = "alice", Methods = [new VaultPasswordMethod(), new VaultPrivateKeyMethod()] };
        await new SqliteIdentityRepository(device.Db).SaveAsync(identity);
        await device.Vault.SaveSecretsAsync(identity.Id, new() { ["password"] = new SecretPayload { Password = "password-a", PrivateKeyContent = "private-key-a", Passphrase = "key-passphrase-a" } });
        var proxy = new ProxyProfile { Name = "proxy", Config = new Socks5ProxyConfig("proxy.example", 1080, "alice") };
        await new SqliteProxyRepository(device.Db).SaveAsync(proxy);
        await device.Vault.SetPasswordAsync(proxy.Id, "proxy-password-a");
        device.Profiles.AddCustomTerminalProfile(new TerminalProfile { Id = "terminal", Name = "terminal-a" });
        device.Profiles.AddCustomGuiProfile(new GuiProfile { Id = "gui", Name = "gui-a" });
        await device.Profiles.CommitAndSaveProfilesAsync();
        var session = new SessionNode { Name = "session", Host = "host.example", IdentityId = identity.Id, ProxyProfileId = proxy.Id, TerminalProfileId = "terminal" };
        await device.Tree.SaveNodeAsync(session);
        var forward = new PortForward { SessionId = session.Id, Name = "forward", ListenPort = 12345, DestinationHost = "localhost", DestinationPort = 80 };
        await new SqlitePortForwardRepository(device.Db).SaveAsync(forward);
        var editor = new ExternalEditor { Name = "editor", ArgumentsTemplate = "--wait {path}" };
        editor.Paths = [new(Guid.NewGuid(), editor.Id, "linux", "/usr/bin/code"), new(Guid.NewGuid(), editor.Id, "windows", "C:\\Code.exe")];
        var editors = new SqliteExternalEditorRepository(device.Db);
        await editors.SaveEditorAsync(editor);
        var association = new FileAssociationRule { EditorId = editor.Id, Pattern = "*.txt", Priority = 20 };
        await editors.SaveAssociationAsync(association);
        var host = new KnownHostEntry { Host = "host.example", Port = 22, KeyType = "ssh-ed25519", PublicKeyBase64 = "a2V5", FingerprintSha256 = "SHA256:test", Comment = "trusted-a" };
        await new SqliteKnownHostRepository(device.Db).SaveAsync(host);
        await new SqliteSessionMetadataRepository(device.Db).SetTransferProbeAsync(session.Id, new(RemotePlatform.Linux, [TransferHashAlgorithm.Sha256], [], DateTime.UtcNow));
        device.Settings.Current.FontSize = 19;
        await device.Settings.SaveSettingsAsync(device.Settings.Current);
        return new(identity, proxy, session, editor, association, forward, host);
    }

    [Fact]
    public async Task EveryEntityAndSecrets_RoundTripBetweenDifferentVaultKeys_AndIdleSyncKeepsVersions()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        var data = await PopulateAsync(a);
        await SyncAsync(a);
        await SyncAsync(b);
        Assert.Equal("alice", (await new SqliteIdentityRepository(b.Db).GetByIdAsync(data.Identity.Id))!.Username);
        var secrets = await b.Vault.GetSecretsAsync(data.Identity.Id);
        Assert.Equal("password-a", secrets["password"].Password);
        Assert.Equal("private-key-a", secrets["password"].PrivateKeyContent);
        Assert.Equal("key-passphrase-a", secrets["password"].Passphrase);
        Assert.Equal("proxy-password-a", await b.Vault.GetPasswordAsync(data.Proxy.Id));
        Assert.Equal("proxy.example", Assert.IsType<Socks5ProxyConfig>((await new SqliteProxyRepository(b.Db).GetByIdAsync(data.Proxy.Id))!.Config).Host);
        Assert.Equal(80, Assert.Single(await new SqlitePortForwardRepository(b.Db).GetBySessionAsync(data.Session.Id)).DestinationPort);
        var editor = Assert.Single(await new SqliteExternalEditorRepository(b.Db).GetAllEditorsAsync());
        Assert.Equal("--wait {path}", editor.ArgumentsTemplate);
        Assert.Equal("/usr/bin/code", editor.GetEffectivePath("linux"));
        Assert.Equal("C:\\Code.exe", editor.GetEffectivePath("windows"));
        Assert.Equal("*.txt", Assert.Single(await new SqliteExternalEditorRepository(b.Db).GetAllAssociationsAsync()).Pattern);
        Assert.Equal("trusted-a", Assert.Single(await new SqliteKnownHostRepository(b.Db).GetAllAsync()).Comment);
        Assert.Equal(RemotePlatform.Linux, (await new SqliteSessionMetadataRepository(b.Db).GetTransferProbeAsync(data.Session.Id))!.Platform);
        Assert.Equal(19, b.Settings.Current.FontSize);
        Assert.Equal("terminal-a", b.Profiles.AllTerminalProfiles.Single(p => p.Id == "terminal").Name);
        Assert.Equal("gui-a", b.Profiles.AllGuiProfiles.Single(p => p.Id == "gui").Name);
        Assert.False(Assert.IsType<SessionNode>(await b.Tree.GetNodeByIdAsync(data.Session.Id)).IsConfigurationIncomplete);
        using var ca = await a.Db.OpenAsync();
        using var cb = await b.Db.OpenAsync();
        Assert.NotEqual(await ca.QuerySingleAsync<byte[]>("SELECT secrets_blob FROM identity_secrets;"), await cb.QuerySingleAsync<byte[]>("SELECT secrets_blob FROM identity_secrets;"));
        byte[] package = (await Location.ReadAsync())!.Content;
        Assert.DoesNotContain("private-key-a", Encoding.UTF8.GetString(package));
        string before = JsonSerializer.Serialize(await b.Store.ExportAsync());
        await SyncAsync(b);
        Assert.Equal(before, JsonSerializer.Serialize(await b.Store.ExportAsync()));
        Assert.Equal(package, (await Location.ReadAsync())!.Content);
        // 仅修改秘密也必须作为所属实体的新版本到达另一设备。
        await b.Vault.SaveSecretsAsync(data.Identity.Id, new() { ["password"] = new SecretPayload { Password = "password-b" } });
        await b.Vault.SetPasswordAsync(data.Proxy.Id, "proxy-password-b");
        await SyncAsync(b);
        await SyncAsync(a);
        Assert.Equal("password-b", (await a.Vault.GetSecretsAsync(data.Identity.Id))["password"].Password);
        Assert.Equal("proxy-password-b", await a.Vault.GetPasswordAsync(data.Proxy.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllEntityConflicts_UseNewerTimeOrOrdinalDeviceTieBreak(bool tie)
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        var content = await PopulateAsync(a);
        await SyncAsync(a);
        await SyncAsync(b);
        // 每一种实体都产生不同内容；精确时间由数据库设置，不用延时猜测时钟。
        foreach (var device in new[] { a, b })
        {
            string marker = device == a ? "a" : "b";
            using var conn = await device.Db.OpenAsync();
            await conn.ExecuteAsync("UPDATE identities SET name=@marker; UPDATE proxies SET name=@marker; UPDATE port_forwards SET name=@marker; UPDATE external_editors SET name=@marker; UPDATE file_associations SET pattern=@marker; UPDATE known_hosts SET comment=@marker; UPDATE tree_nodes SET name=@marker;", new { marker });
            await device.Vault.SetPasswordAsync(content.Proxy.Id, marker);
            await new SqliteSessionMetadataRepository(device.Db).SetTransferProbeAsync(content.Session.Id, new(device == a ? RemotePlatform.Linux : RemotePlatform.Windows, [], [], DateTime.UtcNow));
            device.Settings.Current.FontSize = device == a ? 21 : 22;
            await device.Settings.SaveSettingsAsync(device.Settings.Current);
            var terminal = device.Profiles.AllTerminalProfiles.Single(p => p.Id == "terminal");
            terminal.Name = marker;
            var gui = device.Profiles.AllGuiProfiles.Single(p => p.Id == "gui");
            gui.Name = marker;
            await device.Profiles.CommitAndSaveProfilesAsync();
            string time = new DateTime(2030, 1, device == b && !tie ? 2 : 1, 0, 0, 0, DateTimeKind.Utc).ToString("O");
            foreach (string table in new[] { "tree_nodes", "identities", "proxies", "port_forwards", "external_editors", "file_associations", "known_hosts", "local_entity_stamps" })
                await conn.ExecuteAsync($"UPDATE {table} SET updated_at=@time,device_id=@marker;", new { time, marker });
        }
        await SyncAsync(a);
        await SyncAsync(b);
        await SyncAsync(a);
        foreach (var device in new[] { a, b })
        {
            Assert.Equal("b", (await new SqliteIdentityRepository(device.Db).GetByIdAsync(content.Identity.Id))!.Name);
            Assert.Equal("b", (await new SqliteProxyRepository(device.Db).GetByIdAsync(content.Proxy.Id))!.Name);
            Assert.Equal("b", await device.Vault.GetPasswordAsync(content.Proxy.Id));
            Assert.Equal("b", Assert.Single(await new SqlitePortForwardRepository(device.Db).GetBySessionAsync(content.Session.Id)).Name);
            Assert.Equal("b", Assert.Single(await new SqliteExternalEditorRepository(device.Db).GetAllEditorsAsync()).Name);
            Assert.Equal("b", Assert.Single(await new SqliteExternalEditorRepository(device.Db).GetAllAssociationsAsync()).Pattern);
            Assert.Equal("b", Assert.Single(await new SqliteKnownHostRepository(device.Db).GetAllAsync()).Comment);
            Assert.Equal(RemotePlatform.Windows, (await new SqliteSessionMetadataRepository(device.Db).GetTransferProbeAsync(content.Session.Id))!.Platform);
            Assert.Equal(22, device.Settings.Current.FontSize);
            Assert.Equal("b", device.Profiles.AllTerminalProfiles.Single(p => p.Id == "terminal").Name);
            Assert.Equal("b", device.Profiles.AllGuiProfiles.Single(p => p.Id == "gui").Name);
        }
    }

    [Fact]
    public async Task SettingsMergeByDottedPath_LocalOnlyValuesNeverLeaveOrOverwriteDevice_ExpansionIsLocal()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        var folder = new FolderNode { Name = "folder", IsExpanded = true };
        await a.Tree.SaveNodeAsync(folder);
        await SyncAsync(a);
        await SyncAsync(b);
        Assert.False((await b.Tree.GetNodeByIdAsync(folder.Id))!.IsExpanded);
        a.Settings.Current.UiTheme = "System";
        await a.Settings.SaveSettingsAsync(a.Settings.Current);
        b.Settings.Current.FileTransfer.PollingIntervalSeconds = 17;
        await b.Settings.SaveSettingsAsync(b.Settings.Current);
        foreach (var device in new[] { a, b })
        {
            string path = Path.Combine(device.Directory, "settings.json");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var tracker = new SqliteLocalChangeTracker(device.Db);
            foreach (string setting in LocalOnlySettings)
            {
                json[setting] = device == a ? "local-a-secret" : "local-b-secret";
                await tracker.TrackAsync("setting", setting);
            }
            await File.WriteAllTextAsync(path, json.ToJsonString());
            foreach (string file in new[] { "window-state.json", "recent-directories.json", "quick-unlock.txt", "sync-location.txt", "sync-device-id.txt", "sync-status.txt" })
                await File.WriteAllTextAsync(Path.Combine(device.Directory, file), "machine-only-credential");
        }
        await SyncAsync(a);
        await SyncAsync(b);
        await SyncAsync(a);
        Assert.Equal("System", b.Settings.Current.UiTheme);
        Assert.Equal(17, a.Settings.Current.FileTransfer.PollingIntervalSeconds);
        Assert.True((await a.Tree.GetNodeByIdAsync(folder.Id))!.IsExpanded);
        var edit = (await b.Tree.GetNodeByIdAsync(folder.Id))!;
        edit.Name = "renamed";
        await b.Tree.SaveNodeAsync(edit);
        await SyncAsync(b);
        await SyncAsync(a);
        Assert.True((await a.Tree.GetNodeByIdAsync(folder.Id))!.IsExpanded);
        var bytes = new AgeSyncPackage().Decrypt((await Location.ReadAsync())!.Content, Passphrase);
        string plain = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("local-a-secret", plain);
        Assert.DoesNotContain("local-b-secret", plain);
        Assert.DoesNotContain("machine-only-credential", plain);
        Assert.DoesNotContain(Passphrase, plain);
        foreach (string table in SyncLocalOnly.DatabaseTables) Assert.DoesNotContain(table, plain);
        Assert.DoesNotContain("IsExpanded", plain);
        Assert.DoesNotContain("is_expanded", plain);
        foreach (var device in new[] { a, b })
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(device.Directory, "settings.json")))!;
            foreach (string setting in LocalOnlySettings) Assert.Equal(device == a ? "local-a-secret" : "local-b-secret", json[setting]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task KnownHostsMergeByNaturalKey_NotRandomDatabaseId_DeletionPropagates()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        foreach (var device in new[] { a, b })
            await new SqliteKnownHostRepository(device.Db).SaveAsync(new() { Host = "HOST.example", Port = 22, KeyType = "ssh-ed25519", PublicKeyBase64 = "a2V5", FingerprintSha256 = "SHA256:test" });
        await SyncAsync(a);
        await SyncAsync(b);
        await SyncAsync(a);
        Assert.Single(await new SqliteKnownHostRepository(a.Db).GetAllAsync());
        var host = Assert.Single(await new SqliteKnownHostRepository(b.Db).GetAllAsync());
        await new SqliteKnownHostRepository(b.Db).DeleteAsync(host.Id);
        await SyncAsync(b);
        await SyncAsync(a);
        Assert.Empty(await new SqliteKnownHostRepository(a.Db).GetAllAsync());
    }

    [Fact]
    public async Task DanglingReferencesAreRetainedAndMarked_DeletedEntitiesAndProfilesPropagate()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        var data = await PopulateAsync(a);
        await SyncAsync(a);
        await SyncAsync(b);
        // 远端身份删除不覆盖 B 上较新的会话引用。
        await new SqliteIdentityRepository(a.Db).DeleteAsync(data.Identity.Id);
        await new SqliteProxyRepository(a.Db).DeleteAsync(data.Proxy.Id);
        await new SqliteExternalEditorRepository(a.Db).DeleteEditorAsync(data.Editor.Id);
        await new SqlitePortForwardRepository(a.Db).DeleteAsync(data.Forward.Id);
        await new SqliteSessionMetadataRepository(a.Db).ClearTransferProbeAsync(data.Session.Id);
        a.Profiles.RemoveCustomTerminalProfile("terminal");
        a.Profiles.RemoveCustomGuiProfile("gui");
        await a.Profiles.CommitAndSaveProfilesAsync();
        var local = Assert.IsType<SessionNode>(await b.Tree.GetNodeByIdAsync(data.Session.Id));
        await b.Tree.SaveNodeAsync(local);
        var jump = new SessionNode { Name = "dangling jump", Host = "host", JumpHostSessionId = Guid.NewGuid() };
        await b.Tree.SaveNodeAsync(jump);
        await SyncAsync(a);
        await SyncAsync(b);
        var imported = Assert.IsType<SessionNode>(await b.Tree.GetNodeByIdAsync(local.Id));
        Assert.Equal(local.IdentityId, imported.IdentityId);
        Assert.Equal(local.ProxyProfileId, imported.ProxyProfileId);
        Assert.Equal("terminal", imported.TerminalProfileId);
        Assert.True(imported.IsConfigurationIncomplete);
        Assert.Contains("配置不完整", imported.ConfigurationWarning);
        Assert.True(Assert.IsType<SessionNode>(await b.Tree.GetNodeByIdAsync(jump.Id)).IsConfigurationIncomplete);
        Assert.Empty(await new SqliteIdentityRepository(b.Db).GetAllAsync());
        Assert.Empty(await b.Vault.GetSecretsAsync(data.Identity.Id));
        Assert.Null(await b.Vault.GetPasswordAsync(data.Proxy.Id));
        Assert.Empty(await new SqliteExternalEditorRepository(b.Db).GetAllEditorsAsync());
        Assert.Empty(await new SqliteExternalEditorRepository(b.Db).GetAllAssociationsAsync());
        Assert.Empty(await new SqlitePortForwardRepository(b.Db).GetBySessionAsync(local.Id));
        Assert.Null(await new SqliteSessionMetadataRepository(b.Db).GetTransferProbeAsync(local.Id));
        Assert.DoesNotContain(b.Profiles.AllTerminalProfiles, p => p.Id == "terminal");
        Assert.DoesNotContain(b.Profiles.AllGuiProfiles, p => p.Id == "gui");
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("default-identity")]
    [InlineData("proxy")]
    [InlineData("proxy-jump")]
    [InlineData("jump")]
    [InlineData("terminal")]
    [InlineData("gui")]
    public async Task EachDanglingReferenceIndependentlyMarksSessionWithoutChangingReference(string kind)
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        Guid missing = Guid.NewGuid();
        var session = new SessionNode { Name = "dangling", Host = "host" };
        switch (kind)
        {
            case "identity": session.IdentityId = missing; break;
            case "default-identity":
                a.Settings.Current.DefaultIdentityId = missing;
                await a.Settings.SaveSettingsAsync(a.Settings.Current);
                break;
            case "proxy": session.ProxyProfileId = missing; break;
            case "proxy-jump":
                var proxy = new ProxyProfile { Config = new SessionProxyConfig(missing) };
                await new SqliteProxyRepository(a.Db).SaveAsync(proxy);
                session.ProxyProfileId = proxy.Id;
                break;
            case "jump": session.JumpHostSessionId = missing; break;
            case "terminal": session.TerminalProfileId = "missing-terminal"; break;
            case "gui":
                a.Settings.Current.ActiveGuiProfileId = "missing-gui";
                await a.Settings.SaveSettingsAsync(a.Settings.Current);
                break;
        }
        await a.Tree.SaveNodeAsync(session);
        await SyncAsync(a);
        await SyncAsync(b);
        var read = Assert.IsType<SessionNode>(await b.Tree.GetNodeByIdAsync(session.Id));
        Assert.True(read.IsConfigurationIncomplete);
        Assert.True(Assert.IsType<SessionNode>(Assert.Single(await b.Tree.GetAllNodesAsync())).IsConfigurationIncomplete);
        Assert.Equal(session.IdentityId, read.IdentityId);
        Assert.Equal(session.ProxyProfileId, read.ProxyProfileId);
        Assert.Equal(session.JumpHostSessionId, read.JumpHostSessionId);
        Assert.Equal(session.TerminalProfileId, read.TerminalProfileId);
    }

    [Fact]
    public async Task MetadataKeysAndProfilePreferencesMergeIndependently()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        await a.Profiles.CommitAndSaveProfilesAsync();
        await b.Profiles.CommitAndSaveProfilesAsync();
        var session = new SessionNode { Name = "session", Host = "host" };
        await a.Tree.SaveNodeAsync(session);
        await SyncAsync(a);
        await SyncAsync(b);
        await new SqliteSessionMetadataRepository(a.Db).SetTransferProbeAsync(session.Id, new(RemotePlatform.Linux, [], [], DateTime.UtcNow));
        // 现有业务 API 仅有探测键；用真实内容列与公开记账接口模拟另一个插件的服务器元数据键。
        using (var conn = await b.Db.OpenAsync())
            await conn.ExecuteAsync("UPDATE session_details SET metadata_json = '{\"capability\":{\"enabled\":true}}' WHERE node_id=@id;", new { id = session.Id.ToString() });
        await new SqliteLocalChangeTracker(b.Db).TrackAsync("metadata", session.Id + "/capability");
        a.Profiles.AddCustomGuiProfile(new GuiProfile { Id = "custom-gui", Name = "gui" });
        a.Profiles.SetActiveGuiProfile("custom-gui");
        await a.Profiles.CommitAndSaveProfilesAsync();
        b.Profiles.SetTabBarSettings(new() { Placement = TabPlacement.Bottom });
        await b.Profiles.CommitAndSaveProfilesAsync();
        await SyncAsync(a);
        await SyncAsync(b);
        await SyncAsync(a);
        foreach (var device in new[] { a, b })
        {
            Assert.Equal(RemotePlatform.Linux, (await new SqliteSessionMetadataRepository(device.Db).GetTransferProbeAsync(session.Id))!.Platform);
            using var conn = await device.Db.OpenAsync();
            var metadata = JsonNode.Parse((await conn.QuerySingleAsync<string>("SELECT metadata_json FROM session_details;")))!;
            Assert.True(metadata["capability"]!["enabled"]!.GetValue<bool>());
            Assert.Equal("custom-gui", device.Profiles.ActiveGuiProfile.Id);
            Assert.Equal(TabPlacement.Bottom, device.Profiles.TabBarSettings.Placement);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
