using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.ViewModels;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Kei.Term.Infrastructure.Vault;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Ssh.Services;
using Microsoft.Data.Sqlite;
using Renci.SshNet;
using Xunit;

namespace Kei.Term.Tests;

// 代理/防火墙：往返、v4 迁移、会话编辑器下拉、跳板选择器排除、SOCKS5 只包最外层、拨号前失败
public class ProxyFirewallTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"keiterm_proxy_{Guid.NewGuid():N}.db");

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
    public async Task ProxyJson_RoundTripsThreeKinds_AndCorruptJsonDoesNotThrowOnTreeLoad()
    {
        var repo = new SqliteTreeRepository(ConnStr);
        await repo.InitializeAsync();

        var none = new SessionNode { Name = "direct", Host = "direct.example" };
        var viaProxy = new SessionNode { Name = "via-proxy", Host = "proxy.example", ProxyProfileId = Guid.NewGuid() };
        var viaSession = new SessionNode { Name = "via-session", Host = "jump.example", JumpHostSessionId = Guid.NewGuid() };
        await repo.SaveNodeAsync(none);
        await repo.SaveNodeAsync(viaProxy);
        await repo.SaveNodeAsync(viaSession);

        var loaded = (await repo.GetAllNodesAsync()).OfType<SessionNode>().ToDictionary(s => s.Name);
        Assert.Null(loaded["direct"].ProxyProfileId);
        Assert.Null(loaded["direct"].JumpHostSessionId);
        Assert.Equal(viaProxy.ProxyProfileId, loaded["via-proxy"].ProxyProfileId);
        Assert.Null(loaded["via-proxy"].JumpHostSessionId);
        Assert.Equal(viaSession.JumpHostSessionId, loaded["via-session"].JumpHostSessionId);
        Assert.Null(loaded["via-session"].ProxyProfileId);

        // 仓储只写 proxy_json，旧列保持空，避免两列各说各话
        using (var conn = new SqliteConnection(ConnStr))
        {
            await conn.OpenAsync();
            var jumpColumn = await conn.ExecuteScalarAsync<string?>(
                "SELECT jump_host_id FROM session_details WHERE node_id = @id;",
                new { id = viaSession.Id.ToString() });
            var proxyJson = await conn.ExecuteScalarAsync<string?>(
                "SELECT proxy_json FROM session_details WHERE node_id = @id;",
                new { id = viaSession.Id.ToString() });
            Assert.True(string.IsNullOrEmpty(jumpColumn));
            Assert.Contains("session", proxyJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(viaSession.JumpHostSessionId!.Value.ToString(), proxyJson, StringComparison.OrdinalIgnoreCase);

            await conn.ExecuteAsync(
                "UPDATE session_details SET proxy_json = @json WHERE node_id = @id;",
                new { json = "{not-json", id = none.Id.ToString() });
        }

        var afterCorrupt = await repo.GetAllNodesAsync();
        var direct = Assert.IsType<SessionNode>(Assert.Single(afterCorrupt, n => n.Id == none.Id));
        Assert.Null(direct.ProxyProfileId);
        Assert.Null(direct.JumpHostSessionId);
    }

    [Fact]
    public async Task ProxyConfigJson_RoundTripsThreeTypes_AndSkipsCorruptRows()
    {
        var repo = new SqliteProxyRepository(ConnStr);
        await repo.InitializeAsync();

        var socks = new ProxyProfile
        {
            Name = "local-socks",
            SortOrder = 1,
            Config = new Socks5ProxyConfig("127.0.0.1", 2080)
        };
        var http = new ProxyProfile
        {
            Name = "local-http",
            SortOrder = 2,
            Config = new HttpProxyConfig("127.0.0.1", 8118)
        };
        var sessionId = Guid.NewGuid();
        var session = new ProxyProfile
        {
            Name = "via-bastion",
            SortOrder = 0,
            Config = new SessionProxyConfig(sessionId)
        };
        await repo.SaveAsync(socks);
        await repo.SaveAsync(http);
        await repo.SaveAsync(session);

        var loaded = await repo.GetAllAsync();
        Assert.Equal([session.Id, socks.Id, http.Id], loaded.Select(p => p.Id));
        var socksLoaded = Assert.IsType<Socks5ProxyConfig>(loaded.Single(p => p.Id == socks.Id).Config);
        Assert.Equal("127.0.0.1", socksLoaded.Host);
        Assert.Equal(2080, socksLoaded.Port);
        var httpLoaded = Assert.IsType<HttpProxyConfig>(loaded.Single(p => p.Id == http.Id).Config);
        Assert.Equal(8118, httpLoaded.Port);
        var sessionLoaded = Assert.IsType<SessionProxyConfig>(loaded.Single(p => p.Id == session.Id).Config);
        Assert.Equal(sessionId, sessionLoaded.SessionId);

        using (var conn = new SqliteConnection(ConnStr))
        {
            await conn.OpenAsync();
            var now = DateTime.UtcNow.ToString("O");
            await conn.ExecuteAsync(@"
                INSERT INTO proxies (id, name, sort_order, config_json, created_at, updated_at)
                VALUES (@id, @name, 9, @json, @now, @now);",
                new { id = Guid.NewGuid().ToString(), name = "broken", json = "{bad", now });
            await conn.ExecuteAsync(@"
                INSERT INTO proxies (id, name, sort_order, config_json, created_at, updated_at)
                VALUES (@id, @name, 8, @json, @now, @now);",
                new { id = Guid.NewGuid().ToString(), name = "unknown", json = """{"type":"socks4","host":"h","port":1}""", now });
            await conn.ExecuteAsync(@"
                INSERT INTO proxies (id, name, sort_order, config_json, created_at, updated_at)
                VALUES (@id, @name, 7, @json, @now, @now);",
                new { id = Guid.NewGuid().ToString(), name = "bad-port", json = """{"type":"socks5","host":"h","port":0}""", now });
        }

        var after = await repo.GetAllAsync();
        Assert.Equal(3, after.Count);
        Assert.DoesNotContain(after, p => p.Name is "broken" or "unknown" or "bad-port");
    }

    [Fact]
    public async Task V4_MigratesJumpHostIdToSessionKind_AndSaveClearsOldColumn()
    {
        var nodeId = Guid.NewGuid();
        var jumpId = Guid.NewGuid();
        var now = "2026-01-01T00:00:00.0000000Z";
        using (var conn = new SqliteConnection(ConnStr))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(@"
                CREATE TABLE tree_nodes (
                    id TEXT PRIMARY KEY NOT NULL, parent_id TEXT, node_type INTEGER NOT NULL, name TEXT NOT NULL,
                    description TEXT, sort_order INTEGER NOT NULL DEFAULT 0, protocol TEXT NOT NULL DEFAULT 'ssh',
                    is_expanded INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE identities (
                    id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL, description TEXT, username TEXT,
                    methods_json TEXT NOT NULL DEFAULT '[]', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE session_details (
                    node_id TEXT PRIMARY KEY NOT NULL, host TEXT NOT NULL, port INTEGER, username TEXT, identity_id TEXT,
                    terminal_type TEXT NOT NULL DEFAULT 'xterm-256color', startup_script TEXT, jump_host_id TEXT,
                    env_vars_json TEXT, terminal_profile_id TEXT, file_transfer_protocol INTEGER NOT NULL DEFAULT 0,
                    sftp_mode INTEGER NOT NULL DEFAULT 0, options_json TEXT,
                    FOREIGN KEY(node_id) REFERENCES tree_nodes(id) ON DELETE CASCADE);
                INSERT INTO tree_nodes (id, node_type, name, created_at, updated_at)
                    VALUES (@id, 1, 'web', @now, @now);
                INSERT INTO session_details (node_id, host, jump_host_id) VALUES (@id, 'web.example', @jump);
                PRAGMA user_version = 3;
            ", new { id = nodeId.ToString(), jump = jumpId.ToString(), now });
        }

        var factory = new SqliteConnectionFactory(ConnStr);
        var version = await SchemaMigrator.MigrateAsync(factory);
        Assert.Equal(SchemaMigrator.LatestVersion, version);

        using (var conn = await factory.OpenAsync())
        {
            var proxyJson = await conn.ExecuteScalarAsync<string>(
                "SELECT proxy_json FROM session_details WHERE node_id = @id;",
                new { id = nodeId.ToString() });
            Assert.Contains("session", proxyJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(jumpId.ToString(), proxyJson, StringComparison.OrdinalIgnoreCase);
            // 迁移不删旧列，回填后旧值仍在，直到下一次保存
            var stillThere = await conn.ExecuteScalarAsync<string>(
                "SELECT jump_host_id FROM session_details WHERE node_id = @id;",
                new { id = nodeId.ToString() });
            Assert.Equal(jumpId.ToString(), stillThere);
        }

        var repo = new SqliteTreeRepository(factory);
        var loaded = Assert.IsType<SessionNode>(await repo.GetNodeByIdAsync(nodeId));
        Assert.Equal(jumpId, loaded.JumpHostSessionId);
        Assert.Null(loaded.ProxyProfileId);
        loaded.Name = "web-saved";
        await repo.SaveNodeAsync(loaded);

        using (var conn = await factory.OpenAsync())
        {
            var jumpColumn = await conn.ExecuteScalarAsync<string?>(
                "SELECT jump_host_id FROM session_details WHERE node_id = @id;",
                new { id = nodeId.ToString() });
            var proxyJson = await conn.ExecuteScalarAsync<string?>(
                "SELECT proxy_json FROM session_details WHERE node_id = @id;",
                new { id = nodeId.ToString() });
            Assert.True(string.IsNullOrEmpty(jumpColumn));
            Assert.Contains(jumpId.ToString(), proxyJson, StringComparison.OrdinalIgnoreCase);
        }

        var reloaded = Assert.IsType<SessionNode>(await repo.GetNodeByIdAsync(nodeId));
        Assert.Equal(jumpId, reloaded.JumpHostSessionId);
        Assert.Equal("web-saved", reloaded.Name);
    }

    [Fact]
    public void SessionEditor_DropdownDoesNotListSessionNames_AndOrdersProxies()
    {
        var bastion = new SessionNode { Name = "bastion-unique", Host = "10.0.0.1", Username = "root" };
        var other = new SessionNode { Name = "other-unique", Host = "10.0.0.2" };
        var later = new ProxyProfile { Name = "later", SortOrder = 2, Config = new Socks5ProxyConfig("127.0.0.1", 1080) };
        var first = new ProxyProfile { Name = "first", SortOrder = 1, Config = new HttpProxyConfig("127.0.0.1", 8118) };

        var vm = new SessionEditViewModel(null, null, [], jumpCandidates: [bastion, other], proxies: [later, first]);

        Assert.Equal(["无", "first", "later", "选择会话…", "新建…"], vm.FirewallOptions.Select(o => o.DisplayName).ToArray());
        Assert.DoesNotContain(vm.FirewallOptions, o => o.DisplayName.Contains("bastion-unique", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.FirewallOptions, o => o.DisplayName.Contains("other-unique", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.FirewallOptions, o => o.Id == bastion.Id || o.Id == other.Id);
    }

    [Fact]
    public void SessionEditor_SelectingProxy_WritesKindProxy_AndClearsJump()
    {
        var proxy = new ProxyProfile { Name = "socks", SortOrder = 0, Config = new Socks5ProxyConfig("10.1.1.1", 1080) };
        var existing = new SessionNode { Name = "web", Host = "web.example", JumpHostSessionId = Guid.NewGuid() };
        var vm = new SessionEditViewModel(existing, null, [], jumpCandidates: [existing], proxies: [proxy]);

        vm.SelectedFirewall = vm.FirewallOptions.Single(o => o.Id == proxy.Id);
        var saved = vm.ApplyToModel(existing);

        Assert.Equal(proxy.Id, saved.ProxyProfileId);
        Assert.Null(saved.JumpHostSessionId);
    }

    [Fact]
    public async Task SessionEditor_ChoosingSession_WritesKindSession_CancelRestoresPrevious()
    {
        var proxy = new ProxyProfile { Name = "socks", SortOrder = 0, Config = new Socks5ProxyConfig("10.1.1.1", 1080) };
        var picked = new SessionNode { Name = "bastion", Host = "10.0.0.1", Username = "root", Port = 22 };
        var existing = new SessionNode { Name = "web", Host = "web.example", ProxyProfileId = proxy.Id };
        var vm = new SessionEditViewModel(existing, null, [], jumpCandidates: [existing, picked], proxies: [proxy]);
        var before = vm.SelectedFirewall;
        vm.PickSessionAsync = _ => Task.FromResult<SessionNode?>(null);

        await vm.ChooseSessionAsync();

        Assert.Equal(before?.Id, vm.SelectedFirewall?.Id);
        Assert.Equal(proxy.Id, vm.ApplyToModel(existing).ProxyProfileId);

        vm.PickSessionAsync = _ => Task.FromResult<SessionNode?>(picked);
        await vm.ChooseSessionAsync();

        Assert.Equal(picked.Id, vm.SelectedFirewall?.Id);
        Assert.Contains("bastion", vm.SelectedFirewall?.DisplayName, StringComparison.Ordinal);
        var saved = vm.ApplyToModel(existing);
        Assert.Equal(picked.Id, saved.JumpHostSessionId);
        Assert.Null(saved.ProxyProfileId);
        Assert.DoesNotContain(vm.FirewallOptions.Where(o => o.Kind != FirewallChoiceKind.ChosenSession), o => o.Id == picked.Id);
    }

    [Fact]
    public void SessionEditor_SelectingNone_ClearsProxyAndJump()
    {
        var jump = new SessionNode { Name = "bastion", Host = "10.0.0.1" };
        var existing = new SessionNode { Name = "web", Host = "web.example", JumpHostSessionId = jump.Id };
        var vm = new SessionEditViewModel(existing, null, [], jumpCandidates: [existing, jump]);

        vm.SelectedFirewall = vm.FirewallOptions.Single(o => o.Kind == FirewallChoiceKind.None);
        var saved = vm.ApplyToModel(existing);

        Assert.Null(saved.JumpHostSessionId);
        Assert.Null(saved.ProxyProfileId);
    }

    [Fact]
    public void SessionEditor_DeletedProxyAndSessionIds_AreKeptAndShownDeleted()
    {
        var deletedProxy = Guid.NewGuid();
        var deletedSession = Guid.NewGuid();
        var other = new SessionNode { Name = "other-unique", Host = "other.example" };
        var proxyCase = new SessionNode { Name = "web", Host = "web.example", ProxyProfileId = deletedProxy };
        var sessionCase = new SessionNode { Name = "app", Host = "app.example", JumpHostSessionId = deletedSession };

        var proxyVm = new SessionEditViewModel(proxyCase, null, [], jumpCandidates: [other], proxies: []);
        Assert.Equal(deletedProxy, proxyVm.SelectedFirewall?.Id);
        Assert.Equal("已删除", proxyVm.SelectedFirewall?.DisplayName);
        Assert.DoesNotContain(proxyVm.FirewallOptions, o => o.Id == other.Id);
        Assert.Equal(deletedProxy, proxyVm.ApplyToModel(proxyCase).ProxyProfileId);
        Assert.Null(proxyVm.ApplyToModel(proxyCase).JumpHostSessionId);

        var sessionVm = new SessionEditViewModel(sessionCase, null, [], jumpCandidates: [other]);
        Assert.Equal(deletedSession, sessionVm.SelectedFirewall?.Id);
        Assert.Equal("已删除", sessionVm.SelectedFirewall?.DisplayName);
        Assert.Equal(deletedSession, sessionVm.ApplyToModel(sessionCase).JumpHostSessionId);
        Assert.Null(sessionVm.ApplyToModel(sessionCase).ProxyProfileId);
    }

    [Fact]
    public void SessionPicker_UnselectableSetMatchesJumpHostPicker_AndNodesStayInTree()
    {
        var editing = new SessionNode { Name = "self", Host = "self.example" };
        var a = new SessionNode { Name = "a", Host = "a.example" };
        var b = new SessionNode { Name = "b", Host = "b.example", JumpHostSessionId = a.Id };
        a.JumpHostSessionId = b.Id;
        var ok = new SessionNode { Name = "ok", Host = "ok.example" };
        var folder = new FolderNode { Name = "folder", IsExpanded = true, Children = [editing, a, b, ok] };
        foreach (var session in new[] { editing, a, b, ok })
        {
            session.ParentId = folder.Id;
        }

        var sessions = new SessionNode[] { editing, a, b, ok };
        var picker = new SessionPickerViewModel([folder], editing.Id, sessions);
        var expected = JumpHostPicker.Selectable(editing.Id, sessions).Select(s => s.Id).OrderBy(id => id).ToArray();
        var actual = sessions.Where(picker.IsSelectable).Select(s => s.Id).OrderBy(id => id).ToArray();

        Assert.Equal(expected, actual);
        Assert.False(picker.IsSelectable(editing));
        Assert.False(picker.IsSelectable(a));
        Assert.False(picker.IsSelectable(folder));
        Assert.True(picker.IsSelectable(ok));

        var visible = Flatten(picker.Roots).Select(n => n.Id).ToHashSet();
        Assert.Contains(editing.Id, visible);
        Assert.Contains(a.Id, visible);
        Assert.Contains(b.Id, visible);
        Assert.Contains(ok.Id, visible);

        picker.SelectedNode = a;
        Assert.False(picker.CanConfirm);
        picker.SelectedNode = folder;
        Assert.False(picker.CanConfirm);
        picker.SelectedNode = ok;
        Assert.True(picker.CanConfirm);
    }

    [Fact]
    public void SessionPicker_FilterKeepsAncestorFoldersExpanded_AndHidesNonMatches()
    {
        var folder = new FolderNode { Name = "prod", IsExpanded = false };
        var hit = new SessionNode { Name = "web-1", Host = "10.0.0.9", ParentId = folder.Id };
        var miss = new SessionNode { Name = "db", Host = "web-1.internal", ParentId = folder.Id };
        folder.Children = [hit, miss];
        var picker = new SessionPickerViewModel([folder], Guid.NewGuid(), [hit, miss]);

        picker.FilterText = "web-1";

        var visibleFolder = Assert.IsType<FolderNode>(Assert.Single(picker.Roots));
        Assert.True(visibleFolder.IsExpanded);
        Assert.Contains(visibleFolder.Children, n => n.Id == hit.Id);
        Assert.DoesNotContain(visibleFolder.Children, n => n.Id == miss.Id);
        // 只匹配名称，不因主机名命中把 db 留在树上
        Assert.False(folder.IsExpanded);
    }

    [Fact]
    public void BuildConnectionInfo_AppliesSocks5OnlyOnOutermostDirectDial()
    {
        var auth = new AuthenticationMethod[] { new PasswordAuthenticationMethod("ops", "secret") };
        var target = new SshTarget("bastion.example", 22, "ops", auth, "10.8.0.1", 2080);
        var options = new SshClientOptions(TimeSpan.FromSeconds(15), TimeSpan.Zero, null);

        var direct = SshJumpChain.BuildConnectionInfo(target, "bastion.example", 22, options);
        Assert.Equal(ProxyTypes.Socks5, direct.ProxyType);
        Assert.Equal("10.8.0.1", direct.ProxyHost);
        Assert.Equal(2080, direct.ProxyPort);
        Assert.Equal(string.Empty, direct.ProxyUsername);
        Assert.Equal(string.Empty, direct.ProxyPassword);

        var forwarded = SshJumpChain.BuildConnectionInfo(target, SshJumpChain.IPAddressLoopback, 43210, options);
        Assert.Equal(ProxyTypes.None, forwarded.ProxyType);
        Assert.Equal(SshJumpChain.IPAddressLoopback, forwarded.Host);
        Assert.Equal(43210, forwarded.Port);
    }

    [Fact]
    public async Task Orchestrator_SessionExit_PutsHopSocks5OnThatHop_NotOnLaterHopsOrTarget()
    {
        var socks = new ProxyProfile
        {
            Name = "local",
            Config = new Socks5ProxyConfig("10.8.0.1", 2080)
        };
        var proxies = new InMemoryProxies([socks]);
        var outer = new SessionNode { Name = "outer", Host = "outer.example", Username = "jump", ProxyProfileId = socks.Id };
        var mid = new SessionNode { Name = "mid", Host = "mid.example", Username = "mid", JumpHostSessionId = outer.Id };
        var host = new ProxyHost();
        host.Sessions[outer.Id] = outer;
        host.Sessions[mid.Id] = mid;
        var factory = new RecordingFactory();
        var ui = new ScriptedInteraction();
        var settings = new FixedSettingsService(new AppSettings { PreferSystemAgent = true });
        var orchestrator = CreateOrchestrator(factory, ui, settings, proxies);

        await orchestrator.ConnectAsync(
            new ConnectionRequest(Config(jump: mid.Id), UseIdentity: false, Password("p1")),
            host);

        var call = Assert.Single(factory.Calls);
        Assert.Equal(2, call.Options.JumpHosts.Count);
        Assert.Equal("outer.example", call.Options.JumpHosts[0].Config.Host);
        Assert.Equal("10.8.0.1", call.Options.JumpHosts[0].Socks5Host);
        Assert.Equal(2080, call.Options.JumpHosts[0].Socks5Port);
        Assert.True(string.IsNullOrEmpty(call.Options.JumpHosts[1].Socks5Host));
        Assert.True(string.IsNullOrEmpty(call.Options.Socks5Host));
        Assert.Equal("mid.example", call.Options.JumpHosts[1].Config.Host);
    }

    [Fact]
    public async Task Orchestrator_HttpAndDeleted_FailBeforeDial_WithDistinctErrors()
    {
        var http = new ProxyProfile { Name = "http", Config = new HttpProxyConfig("127.0.0.1", 8118) };
        var proxies = new InMemoryProxies([http]);
        var factory = new RecordingFactory();
        var ui = new ScriptedInteraction();
        var settings = new FixedSettingsService(new AppSettings());
        var orchestrator = CreateOrchestrator(factory, ui, settings, proxies);
        var host = new ProxyHost();

        await orchestrator.ConnectAsync(
            new ConnectionRequest(Config(proxy: http.Id), UseIdentity: false),
            host);

        Assert.Empty(factory.Calls);
        Assert.Empty(host.Tabs);
        var httpError = Assert.Single(ui.Notifications).Message;
        Assert.Contains("尚未接入拨号", httpError, StringComparison.Ordinal);
        Assert.DoesNotContain("已删除", httpError, StringComparison.Ordinal);

        ui.Notifications.Clear();
        await orchestrator.ConnectAsync(
            new ConnectionRequest(Config(proxy: Guid.NewGuid()), UseIdentity: false),
            host);

        Assert.Empty(factory.Calls);
        var deletedProxy = Assert.Single(ui.Notifications).Message;
        Assert.Contains("已删除", deletedProxy, StringComparison.Ordinal);
        Assert.DoesNotContain("尚未接入", deletedProxy, StringComparison.Ordinal);

        ui.Notifications.Clear();
        await orchestrator.ConnectAsync(
            new ConnectionRequest(Config(jump: Guid.NewGuid()), UseIdentity: false, Password("p1")),
            host);

        Assert.Empty(factory.Calls);
        var deletedSession = Assert.Single(ui.Notifications).Message;
        Assert.Contains("已删除", deletedSession, StringComparison.Ordinal);
        Assert.DoesNotContain("尚未接入", deletedSession, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxySettingsRows_FormatSessionEndpoint_AndDeleted()
    {
        var bastion = new SessionNode { Name = "bastion", Host = "10.0.0.1", Username = "root", Port = 2222 };
        var repo = new InMemoryProxies(
        [
            new ProxyProfile { Name = "jump", SortOrder = 0, Config = new SessionProxyConfig(bastion.Id) },
            new ProxyProfile { Name = "gone", SortOrder = 1, Config = new SessionProxyConfig(Guid.NewGuid()) },
            new ProxyProfile { Name = "socks", SortOrder = 2, Config = new Socks5ProxyConfig("127.0.0.1", 2080) }
        ]);
        var page = new ProxySettingsPage(repo, () => [bastion]);

        await page.ReloadAsync();

        Assert.Equal("bastion (root@10.0.0.1:2222)", page.Rows[0].Host);
        Assert.Equal(string.Empty, page.Rows[0].Port);
        Assert.Equal("已删除", page.Rows[1].Host);
        Assert.Equal(string.Empty, page.Rows[1].Port);
        Assert.Equal("127.0.0.1", page.Rows[2].Host);
        Assert.Equal("2080", page.Rows[2].Port);
    }

    [Fact]
    public async Task Socks5ConfigJson_RoundTripsUsername_AndSerializedStringOmitsPassword()
    {
        var repo = new SqliteProxyRepository(ConnStr);
        await repo.InitializeAsync();

        var named = new ProxyProfile
        {
            Name = "auth-socks",
            SortOrder = 0,
            Config = new Socks5ProxyConfig("10.8.0.1", 2080, "proxy-user")
        };
        var blankUser = new ProxyProfile
        {
            Name = "blank-user",
            SortOrder = 1,
            Config = new Socks5ProxyConfig("10.8.0.2", 1080, "   ")
        };
        await repo.SaveAsync(named);
        await repo.SaveAsync(blankUser);

        var loaded = Assert.IsType<Socks5ProxyConfig>((await repo.GetByIdAsync(named.Id))!.Config);
        Assert.Equal("proxy-user", loaded.Username);
        Assert.Equal("10.8.0.1", loaded.Host);
        Assert.Equal(2080, loaded.Port);

        using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        var namedJson = await conn.ExecuteScalarAsync<string>(
            "SELECT config_json FROM proxies WHERE id = @id;",
            new { id = named.Id.ToString() });
        var blankJson = await conn.ExecuteScalarAsync<string>(
            "SELECT config_json FROM proxies WHERE id = @id;",
            new { id = blankUser.Id.ToString() });

        Assert.Contains("proxy-user", namedJson, StringComparison.Ordinal);
        Assert.Contains("username", namedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", namedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", blankJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", blankJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProxySecretStore_RoundTripsPassword_AndNullClears()
    {
        var repo = new SqliteProxyRepository(ConnStr);
        await repo.InitializeAsync();
        var proxy = new ProxyProfile
        {
            Name = "socks",
            Config = new Socks5ProxyConfig("10.8.0.1", 1080, "proxy-user")
        };
        await repo.SaveAsync(proxy);

        var vault = VaultTestDb.CreateVault(ConnStr);
        IProxySecretStore store = vault;
        await store.SetPasswordAsync(proxy.Id, "s3cret-proxy");

        Assert.Equal("s3cret-proxy", await store.GetPasswordAsync(proxy.Id));
        Assert.True(await store.HasPasswordAsync(proxy.Id));

        using (var conn = new SqliteConnection(ConnStr))
        {
            await conn.OpenAsync();
            var json = await conn.ExecuteScalarAsync<string>(
                "SELECT config_json FROM proxies WHERE id = @id;",
                new { id = proxy.Id.ToString() });
            Assert.DoesNotContain("s3cret-proxy", json, StringComparison.Ordinal);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        }

        await store.SetPasswordAsync(proxy.Id, null);
        Assert.False(await store.HasPasswordAsync(proxy.Id));
        Assert.Null(await store.GetPasswordAsync(proxy.Id));
    }

    [Fact]
    public async Task SetMasterPassword_RewrapsProxySecret_SoItStillDecrypts()
    {
        var repo = new SqliteProxyRepository(ConnStr);
        await repo.InitializeAsync();
        var proxy = new ProxyProfile { Name = "socks", Config = new Socks5ProxyConfig("10.8.0.1", 1080) };
        await repo.SaveAsync(proxy);

        var vault = VaultTestDb.CreateVault(ConnStr);
        IProxySecretStore store = vault;
        await store.SetPasswordAsync(proxy.Id, "s3cret-proxy");
        await vault.SetMasterPasswordAsync("master-one");

        (string algorithm, byte[] firstBlob) = await ReadProxySecretAsync();
        Assert.Equal("XCHACHA20-POLY1305", algorithm);
        Assert.DoesNotContain("s3cret-proxy", Encoding.UTF8.GetString(firstBlob));

        vault.Lock();
        await vault.UnlockAsync("master-one", false);
        Assert.Equal("s3cret-proxy", await store.GetPasswordAsync(proxy.Id));

        // 换主密码只换盐并重新包装同一个 Vault Key，条目密文字节不变
        await vault.SetMasterPasswordAsync("master-two");
        (_, byte[] secondBlob) = await ReadProxySecretAsync();
        Assert.Equal(firstBlob, secondBlob);

        vault.Lock();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.UnlockAsync("master-one", false));
        await vault.UnlockAsync("master-two", false);
        Assert.Equal("s3cret-proxy", await store.GetPasswordAsync(proxy.Id));

        async Task<(string Algorithm, byte[] Blob)> ReadProxySecretAsync()
        {
            using var conn = new SqliteConnection(ConnStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT secrets_blob, encryption_algorithm FROM proxy_secrets WHERE proxy_id = $id;";
            cmd.Parameters.AddWithValue("$id", proxy.Id.ToString());
            using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(1), (byte[])reader["secrets_blob"]);
        }
    }

    [Fact]
    public void BuildConnectionInfo_PassesSocks5Credentials_AndOmitsThemOnLoopback()
    {
        var auth = new AuthenticationMethod[] { new PasswordAuthenticationMethod("ops", "secret") };
        var target = new SshTarget(
            "bastion.example",
            22,
            "ops",
            auth,
            "10.8.0.1",
            2080,
            "proxy-user",
            "s3cret-proxy");
        var options = new SshClientOptions(TimeSpan.FromSeconds(15), TimeSpan.Zero, null);

        var direct = SshJumpChain.BuildConnectionInfo(target, "bastion.example", 22, options);
        Assert.Equal(ProxyTypes.Socks5, direct.ProxyType);
        Assert.Equal("proxy-user", direct.ProxyUsername);
        Assert.Equal("s3cret-proxy", direct.ProxyPassword);

        var forwarded = SshJumpChain.BuildConnectionInfo(target, SshJumpChain.IPAddressLoopback, 43210, options);
        Assert.Equal(ProxyTypes.None, forwarded.ProxyType);
        Assert.Equal(SshJumpChain.IPAddressLoopback, forwarded.Host);
        Assert.True(string.IsNullOrEmpty(forwarded.ProxyUsername));
        Assert.True(string.IsNullOrEmpty(forwarded.ProxyPassword));
    }

    [Fact]
    public async Task LegacySocks5Json_LoadsWithoutUsername_AndDialUsesEmptyCredentials()
    {
        var repo = new SqliteProxyRepository(ConnStr);
        await repo.InitializeAsync();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("O");
        using (var conn = new SqliteConnection(ConnStr))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(@"
                INSERT INTO proxies (id, name, sort_order, config_json, created_at, updated_at)
                VALUES (@id, 'legacy', 0, @json, @now, @now);",
                new { id = id.ToString(), json = """{"type":"socks5","host":"10.1.1.1","port":1080}""", now });
        }

        var loaded = Assert.IsType<Socks5ProxyConfig>((await repo.GetByIdAsync(id))!.Config);
        Assert.Equal("10.1.1.1", loaded.Host);
        Assert.Equal(1080, loaded.Port);
        Assert.True(string.IsNullOrEmpty(loaded.Username));

        var auth = new AuthenticationMethod[] { new PasswordAuthenticationMethod("ops", "secret") };
        var target = new SshTarget("bastion.example", 22, "ops", auth, loaded.Host, loaded.Port, loaded.Username, null);
        var info = SshJumpChain.BuildConnectionInfo(
            target,
            "bastion.example",
            22,
            new SshClientOptions(TimeSpan.FromSeconds(15), TimeSpan.Zero, null));
        Assert.Equal(ProxyTypes.Socks5, info.ProxyType);
        Assert.Equal(string.Empty, info.ProxyUsername);
        Assert.Equal(string.Empty, info.ProxyPassword);
    }

    [Fact]
    public async Task ProxyEditor_EmptyPasswordBox_DoesNotClearSavedPassword()
    {
        var existing = new ProxyProfile
        {
            Name = "socks",
            Config = new Socks5ProxyConfig("10.1.1.1", 1080, "alice")
        };
        var vm = new ProxyEditViewModel(existing, hasSavedPassword: true);
        Assert.Equal(string.Empty, vm.Password);
        Assert.Equal("alice", vm.Username);
        Assert.Equal("已设置，留空不改", vm.PasswordPlaceholder);
        Assert.True(vm.ShowClearPassword);

        var store = new RecordingProxySecrets();
        await vm.ApplyPasswordAsync(store);
        Assert.Empty(store.Writes);

        vm.ClearSavedPassword = true;
        await vm.ApplyPasswordAsync(store);
        var cleared = Assert.Single(store.Writes);
        Assert.Equal(existing.Id, cleared.Id);
        Assert.Null(cleared.Password);
    }

    [Fact]
    public async Task Orchestrator_Socks5Credentials_LandOnOptionsAndHop_NotOnLaterHops()
    {
        var socks = new ProxyProfile
        {
            Name = "local",
            Config = new Socks5ProxyConfig("10.8.0.1", 2080, "proxy-user")
        };
        var store = new FixedProxySecrets(socks.Id, "s3cret-proxy");
        var proxies = new InMemoryProxies([socks]);
        var outer = new SessionNode { Name = "outer", Host = "outer.example", Username = "jump", ProxyProfileId = socks.Id };
        var host = new ProxyHost();
        host.Sessions[outer.Id] = outer;
        var factory = new RecordingFactory();
        var ui = new ScriptedInteraction();
        var settings = new FixedSettingsService(new AppSettings { PreferSystemAgent = true });
        var orchestrator = CreateOrchestrator(factory, ui, settings, proxies, store);

        await orchestrator.ConnectAsync(
            new ConnectionRequest(Config(proxy: socks.Id), UseIdentity: false, Password("p1")),
            host);

        var direct = Assert.Single(factory.Calls);
        Assert.Equal("proxy-user", direct.Options.Socks5Username);
        Assert.Equal("s3cret-proxy", direct.Options.Socks5Password);
        Assert.Equal("10.8.0.1", direct.Options.Socks5Host);

        factory.Calls.Clear();
        await orchestrator.ConnectAsync(
            new ConnectionRequest(Config(jump: outer.Id), UseIdentity: false, Password("p1")),
            host);

        var viaHop = Assert.Single(factory.Calls);
        Assert.Equal("proxy-user", viaHop.Options.JumpHosts[0].Socks5Username);
        Assert.Equal("s3cret-proxy", viaHop.Options.JumpHosts[0].Socks5Password);
        Assert.True(string.IsNullOrEmpty(viaHop.Options.Socks5Password));
        Assert.True(string.IsNullOrEmpty(viaHop.Options.Socks5Username));
    }

    private static List<TreeNodeBase> Flatten(IEnumerable<TreeNodeBase> nodes)
    {
        var list = new List<TreeNodeBase>();
        foreach (var node in nodes)
        {
            list.Add(node);
            if (node is FolderNode folder)
            {
                list.AddRange(Flatten(folder.Children));
            }
        }

        return list;
    }

    private static ConnectionOrchestrator CreateOrchestrator(
        RecordingFactory factory,
        ScriptedInteraction ui,
        FixedSettingsService settings,
        IProxyRepository proxies,
        IProxySecretStore? proxySecrets = null)
    {
        var vault = new VaultSessionService(new PlainVault(), new PlainVault(), settings, () => ui);
        var collector = new AuthMaterialCollector(new InMemoryIdentities(), settings, vault, () => ui);
        return new ConnectionOrchestrator(
            factory,
            collector,
            null,
            settings,
            () => ui,
            action => action(),
            proxies: proxies,
            proxySecrets: proxySecrets);
    }

    private static ResolvedSessionConfig Config(Guid? jump = null, Guid? proxy = null) => new(
        Guid.NewGuid(),
        "target",
        "target.example",
        22,
        "ops",
        null,
        "xterm-256color",
        null,
        jump,
        new Dictionary<string, string>(),
        ProxyProfileId: proxy);

    private static MaterializedAuthMethod Password(string value)
        => new(AuthMaterialKind.Password, new SecretPayload { Password = value });

    private sealed class RecordingFactory : ISshSessionFactory
    {
        public List<SessionCall> Calls { get; } = [];

        public Task<ISshSession> CreateSessionAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
        {
            Calls.Add(new SessionCall(config, methods, options ?? SshConnectOptions.Default));
            return Task.FromResult<ISshSession>(new IdleSession());
        }

        public Task<IRemoteFileSystem> CreateFileSystemAsync(
            ResolvedSessionConfig config,
            IReadOnlyList<MaterializedAuthMethod> methods,
            ISshSession? activeSession = null,
            SshConnectOptions? options = null,
            CancellationToken ct = default)
            => Task.FromResult<IRemoteFileSystem>(null!);
    }

    private sealed record SessionCall(
        ResolvedSessionConfig Config,
        IReadOnlyList<MaterializedAuthMethod> Methods,
        SshConnectOptions Options);

    private sealed class IdleSession : ISshSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public bool IsConnected => true;
        public object? UnderlyingClient => null;
        public event Action<byte[]>? OutputReceived { add { } remove { } }
        public event Action<Exception?>? Disconnected { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProxyHost : IConnectionHost
    {
        public List<object> Tabs { get; } = [];
        public Dictionary<Guid, SessionNode> Sessions { get; } = [];
        public IConnectionTarget OpenTab(ResolvedSessionConfig config)
        {
            var tab = new IdleTarget();
            Tabs.Add(tab);
            return tab;
        }

        public SessionNode? FindSession(Guid id) => Sessions.GetValueOrDefault(id);
    }

    private sealed class IdleTarget : IConnectionTarget
    {
        public bool IsDisposed => false;
        public void AttachSession(ITerminalSession session) { }
        public Task DetachSessionAsync() => Task.CompletedTask;
        public void MarkConnected() { }
        public void ReportError(string message) { }
        public Task AttachFileSystemAsync(IRemoteFileSystem fileSystem) => Task.CompletedTask;
        public Task ResetForReconnectAsync(ResolvedSessionConfig config) => Task.CompletedTask;
    }

    private sealed class InMemoryProxies : IProxyRepository
    {
        private readonly Dictionary<Guid, ProxyProfile> _items;

        public InMemoryProxies(IEnumerable<ProxyProfile> items)
        {
            _items = items.ToDictionary(p => p.Id);
        }

        public Task<IReadOnlyList<ProxyProfile>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProxyProfile>>(_items.Values.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToList());

        public Task<ProxyProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_items.GetValueOrDefault(id));

        public Task SaveAsync(ProxyProfile proxy, CancellationToken ct = default)
        {
            _items[proxy.Id] = proxy;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class PlainVault : IVaultManager, IVaultSecretStore
    {
        public bool IsUnlocked => true;
        public bool IsPlainMode => true;
        public Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryAutoUnlockAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default) => Task.CompletedTask;
        public void Lock() { }
        public Task<Dictionary<string, SecretPayload>> GetSecretsAsync(Guid identityId, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, SecretPayload>());
        public Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteSecretsAsync(Guid identityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingProxySecrets : IProxySecretStore
    {
        public List<(Guid Id, string? Password)> Writes { get; } = [];

        public Task<string?> GetPasswordAsync(Guid proxyId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task SetPasswordAsync(Guid proxyId, string? password, CancellationToken ct = default)
        {
            Writes.Add((proxyId, password));
            return Task.CompletedTask;
        }

        public Task<bool> HasPasswordAsync(Guid proxyId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class FixedProxySecrets : IProxySecretStore
    {
        private readonly Guid _id;
        private readonly string _password;

        public FixedProxySecrets(Guid id, string password)
        {
            _id = id;
            _password = password;
        }

        public Task<string?> GetPasswordAsync(Guid proxyId, CancellationToken ct = default)
            => Task.FromResult(proxyId == _id ? _password : null);

        public Task SetPasswordAsync(Guid proxyId, string? password, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> HasPasswordAsync(Guid proxyId, CancellationToken ct = default)
            => Task.FromResult(proxyId == _id);
    }

    private sealed class InMemoryIdentities : IIdentityRepository
    {
        public Task<IReadOnlyList<Identity>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Identity>>([]);
        public Task<Identity?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Identity?>(null);
        public Task SaveAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
