using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.Core.Models;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Sync;
using Xunit;

namespace Kei.Term.Tests;

public sealed partial class FullSyncSnapshotTests
{
    private async Task<Device> SafetyDeviceAsync(string name)
    {
        var d = await CreateAsync(name);
        return d with { Coordinator = new(d.Store, new AgeSyncPackage(), d.Vault,
            new PersistentDeviceId(Path.Combine(d.Directory, "sync-device-id.txt")), d.Vault) };
    }

    private async Task JoinSafetyAsync(Device d, SyncJoinChoice choice = SyncJoinChoice.Merge)
    {
        var result = await d.Coordinator.SyncAsync(Location);
        while (result.Confirmation is { } preview)
            result = await d.Coordinator.SyncApprovedAsync(Location, new(preview.Token, choice));
        Assert.True(result.Success, result.Message);
    }

    [Theory]
    [InlineData(SyncJoinChoice.Merge)]
    [InlineData(SyncJoinChoice.Remote)]
    [InlineData(SyncJoinChoice.Local)]
    public async Task Safety_FirstJoinStopsBeforeImport_ThreeChoicesPropagate(SyncJoinChoice choice)
    {
        var a = await SafetyDeviceAsync("a");
        var b = await SafetyDeviceAsync("b");
        var remote = new FolderNode { Name = "remote-only" };
        var local = new FolderNode { Name = "local-only" };
        await a.Tree.SaveNodeAsync(remote);
        await b.Tree.SaveNodeAsync(local);
        Assert.True((await a.Coordinator.SyncAsync(Location)).Success);
        Assert.True(await a.Vault.HasJoinedAsync());
        string before = JsonSerializer.Serialize(await b.Store.ExportAsync());
        byte[] remoteBefore = (await Location.ReadAsync())!.Content;
        var result = await b.Coordinator.SyncAsync(Location);
        Assert.Equal(SyncConfirmationKind.FirstJoin, result.Confirmation!.Kind);
        Assert.Equal(before, JsonSerializer.Serialize(await b.Store.ExportAsync()));
        Assert.Equal(remoteBefore, (await Location.ReadAsync())!.Content);
        Assert.Empty(await b.Coordinator.ListBackupsAsync());
        await JoinSafetyAsync(b, choice);
        await JoinSafetyAsync(a);
        foreach (var d in new[] { a, b })
        {
            Assert.Equal(choice != SyncJoinChoice.Local, await d.Tree.GetNodeByIdAsync(remote.Id) != null);
            Assert.Equal(choice != SyncJoinChoice.Remote, await d.Tree.GetNodeByIdAsync(local.Id) != null);
        }
    }

    [Fact]
    public async Task Safety_FirstJoinOldPreviewCannotAuthorizeChangedRemote()
    {
        var a = await SafetyDeviceAsync("a");
        var b = await SafetyDeviceAsync("b");
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "one" });
        await JoinSafetyAsync(a);
        var preview = (await b.Coordinator.SyncAsync(Location)).Confirmation!;
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "two" });
        await JoinSafetyAsync(a);
        var result = await b.Coordinator.SyncApprovedAsync(Location, new(preview.Token, SyncJoinChoice.Remote));
        Assert.Equal(SyncConfirmationKind.FirstJoin, result.Confirmation!.Kind);
        Assert.NotEqual(preview.Token, result.Confirmation.Token);
        Assert.DoesNotContain(await b.Store.ExportAsync(), e => e.EntityType == "tree");
    }

    [Fact]
    public async Task Safety_BulkDeletionStopsAndReconfirmsRemoteAndLocalChanges()
    {
        var a = await SafetyDeviceAsync("a");
        var b = await SafetyDeviceAsync("b");
        var nodes = Enumerable.Range(0, 170).Select(i => new FolderNode { Name = "node-" + i }).ToList();
        await a.Tree.SaveNodesAsync(nodes);
        await JoinSafetyAsync(a);
        await JoinSafetyAsync(b);
        foreach (var node in nodes.Take(51)) await a.Tree.DeleteNodeAsync(node.Id);
        await JoinSafetyAsync(a);
        var result = await b.Coordinator.SyncAsync(Location);
        var preview = Assert.IsType<SyncConfirmation>(result.Confirmation);
        Assert.Equal(SyncConfirmationKind.BulkDeletion, preview.Kind);
        Assert.Equal(51, preview.DeletedCount);
        Assert.NotNull(await b.Tree.GetNodeByIdAsync(nodes[0].Id));
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "remote-changed" });
        await JoinSafetyAsync(a);
        result = await b.Coordinator.SyncApprovedAsync(Location, new(preview.Token));
        Assert.NotEqual(preview.Token, result.Confirmation!.Token);
        preview = result.Confirmation;
        await b.Tree.SaveNodeAsync(new FolderNode { Name = "local-changed" });
        result = await b.Coordinator.SyncApprovedAsync(Location, new(preview.Token));
        Assert.NotEqual(preview.Token, result.Confirmation!.Token);
        preview = result.Confirmation;
        // 即便新结果已低于自动删除阈值，也不能把旧预览的许可套到新结果上。
        foreach (var node in nodes.Take(3)) await a.Tree.SaveNodeAsync(node);
        await JoinSafetyAsync(a);
        result = await b.Coordinator.SyncApprovedAsync(Location, new(preview.Token));
        Assert.Equal(48, result.Confirmation!.DeletedCount);
        Assert.NotEqual(preview.Token, result.Confirmation.Token);
        result = await b.Coordinator.SyncApprovedAsync(Location, new(result.Confirmation.Token));
        Assert.True(result.Success, result.Message);
        Assert.Null(await b.Tree.GetNodeByIdAsync(nodes[3].Id));
        Assert.NotEmpty(await b.Coordinator.ListBackupsAsync());
    }

    [Fact]
    public async Task Safety_RestoreLocalDeletesAbsentEntities_RestoresSecretsAndKeepsLocalOnly()
    {
        var a = await SafetyDeviceAsync("a");
        var b = await SafetyDeviceAsync("b");
        var original = await PopulateAsync(a);
        await JoinSafetyAsync(a);
        await JoinSafetyAsync(b);
        await a.Vault.SaveBackupAsync(await a.Store.ExportAsync());
        string backup = (await a.Coordinator.ListBackupsAsync())[0].Id;
        var extra = new FolderNode { Name = "not-in-backup" };
        await a.Tree.SaveNodeAsync(extra);
        await a.Tree.DeleteNodeAsync(original.Session.Id);
        await a.Vault.SetPasswordAsync(original.Proxy.Id, "changed");
        await a.Vault.SaveSecretsAsync(original.Identity.Id, new() { ["password"] = new() { Password = "changed-secret" } });
        // 本机专属叶子不在备份内，恢复也不能覆盖。
        string path = Path.Combine(a.Directory, "settings.json");
        var file = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        file["WindowX"] = 8765;
        await File.WriteAllTextAsync(path, file.ToJsonString());
        var restored = await a.Coordinator.RestoreLocalAsync(backup);
        Assert.True(restored.Success, restored.Message);
        Assert.Null(await a.Tree.GetNodeByIdAsync(extra.Id));
        Assert.NotNull(await a.Tree.GetNodeByIdAsync(original.Session.Id));
        Assert.Equal("proxy-password-a", await a.Vault.GetPasswordAsync(original.Proxy.Id));
        Assert.Equal("private-key-a", (await a.Vault.GetSecretsAsync(original.Identity.Id))["password"].PrivateKeyContent);
        Assert.Equal(8765, System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!["WindowX"]!.GetValue<int>());
        await JoinSafetyAsync(a);
        await JoinSafetyAsync(b);
        Assert.Null(await b.Tree.GetNodeByIdAsync(extra.Id));
        Assert.NotNull(await b.Tree.GetNodeByIdAsync(original.Session.Id));
        Assert.Equal("proxy-password-a", await b.Vault.GetPasswordAsync(original.Proxy.Id));
        Assert.Equal("password-a", (await b.Vault.GetSecretsAsync(original.Identity.Id))["password"].Password);
    }

    [Fact]
    public async Task Safety_HistoryRotatesFiveAndRestoresAbsentEntities()
    {
        var a = await SafetyDeviceAsync("a");
        var b = await SafetyDeviceAsync("b");
        var node = new FolderNode { Name = "original" };
        await a.Tree.SaveNodeAsync(node);
        await JoinSafetyAsync(a);
        byte[] original = (await Location.ReadAsync())!.Content;
        var extra = new FolderNode { Name = "extra" };
        await a.Tree.SaveNodeAsync(extra);
        await JoinSafetyAsync(a);
        var history = Assert.Single(await Location.ListHistoryAsync());
        Assert.Equal(original, await Location.ReadHistoryAsync(history.Name));
        var result = await a.Coordinator.RestoreHistoryAsync(Location, history.Name);
        Assert.True(result.Success, result.Message);
        Assert.Null(await a.Tree.GetNodeByIdAsync(extra.Id));
        await JoinSafetyAsync(a);
        await JoinSafetyAsync(b);
        Assert.NotNull(await b.Tree.GetNodeByIdAsync(node.Id));
        Assert.Null(await b.Tree.GetNodeByIdAsync(extra.Id));
        for (int i = 0; i < 6; i++)
        {
            node.Name = "version-" + i;
            await a.Tree.SaveNodeAsync(node);
            await JoinSafetyAsync(a);
        }
        Assert.Equal(5, (await Location.ListHistoryAsync()).Count);
        foreach (var entry in await Location.ListHistoryAsync())
            Assert.DoesNotContain("version-", Encoding.UTF8.GetString((await Location.ReadHistoryAsync(entry.Name))!));
    }

    [Fact]
    public async Task Safety_BackupsRetainTen_StayEncryptedAcrossVaultKeyRotation_ExplicitRemovalRequired()
    {
        var a = await SafetyDeviceAsync("a");
        await PopulateAsync(a);
        var entities = await a.Store.ExportAsync();
        for (int i = 0; i < 12; i++) await a.Vault.SaveBackupAsync(entities);
        var backups = await a.Coordinator.ListBackupsAsync();
        Assert.Equal(10, backups.Count);
        using (var conn = await a.Db.OpenAsync())
        {
            Assert.DoesNotContain("PLAIN", await conn.QueryAsync<string>("SELECT encryption_algorithm FROM local_vault_secrets;"));
            foreach (byte[] blob in await conn.QueryAsync<byte[]>("SELECT secrets_blob FROM local_vault_secrets;"))
            {
                Assert.DoesNotContain("private-key-a", Encoding.UTF8.GetString(blob));
                Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(blob));
            }
        }
        await a.Vault.SetMasterPasswordAsync("new-master-password");
        Assert.True(SyncCoordinator.SameEntities(entities, await a.Vault.ReadBackupAsync(backups[0].Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Vault.RemoveMasterPasswordAsync());
        Assert.True(SyncCoordinator.SameEntities(entities, await a.Vault.ReadBackupAsync(backups[0].Id)));
        await a.Vault.DeleteSyncBackupsAsync();
        await a.Vault.RemoveMasterPasswordAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Vault.SaveBackupAsync(entities));
    }

    [Fact]
    public async Task Safety_ChangePassphraseOldFails_HistoryCleared_NewDeviceCanJoin()
    {
        var a = await SafetyDeviceAsync("a");
        var b = await SafetyDeviceAsync("b");
        var node = new FolderNode { Name = "before" };
        await a.Tree.SaveNodeAsync(node);
        await JoinSafetyAsync(a);
        await JoinSafetyAsync(b);
        node.Name = "after";
        await b.Tree.SaveNodeAsync(node);
        await JoinSafetyAsync(b);
        Assert.NotEmpty(await Location.ListHistoryAsync());
        var result = await a.Coordinator.ChangePassphraseAsync(Location, "new-sync-password".AsMemory());
        Assert.True(result.Success, result.Message);
        Assert.Equal("after", (await a.Tree.GetNodeByIdAsync(node.Id))!.Name);
        Assert.Empty(await Location.ListHistoryAsync());
        Assert.Null(await a.Vault.GetPendingPassphraseAsync());
        result = await b.Coordinator.SyncAsync(Location);
        Assert.False(result.Success);
        Assert.Equal("同步口令不正确", result.Message);
        await b.Vault.SavePassphraseAsync("new-sync-password".AsMemory());
        Assert.True((await b.Coordinator.SyncAsync(Location)).Success);
    }

    [Fact]
    public async Task Safety_PassphraseUploadNetworkUncertaintyKeepsCredentialsAndResumesAfterRestart()
    {
        var a = await SafetyDeviceAsync("a");
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "data" });
        await JoinSafetyAsync(a);
        var result = await a.Coordinator.ChangePassphraseAsync(new WriteThenFailLocation(Location), "pending-new".AsMemory());
        Assert.False(result.Success);
        Assert.Equal(Passphrase, new string((await a.Vault.GetPassphraseAsync())!));
        Assert.Equal("pending-new", new string((await a.Vault.GetPendingPassphraseAsync())!));
        Assert.Empty(await a.Coordinator.ListBackupsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Vault.RemoveMasterPasswordAsync());
        var restarted = new SyncCoordinator(a.Store, new AgeSyncPackage(), a.Vault, new PersistentDeviceId(Path.Combine(a.Directory, "sync-device-id.txt")), a.Vault);
        Assert.False((await restarted.SyncAsync(Location)).Success);
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "edited-while-pending" });
        result = await restarted.ResumePassphraseChangeAsync(Location);
        Assert.True(result.Success, result.Message);
        Assert.Null(await a.Vault.GetPendingPassphraseAsync());
        Assert.True((await restarted.SyncAsync(Location)).Success);
        var b = await SafetyDeviceAsync("b");
        await b.Vault.SavePassphraseAsync("pending-new".AsMemory());
        await JoinSafetyAsync(b);
        Assert.Contains(await b.Store.ExportAsync(), e => e.EntityType == "tree" && e.Data.GetRawText().Contains("edited-while-pending", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Safety_HistoryCleanupFailureIsNotSuccess_AndPreservesOldHistoryCredential()
    {
        var a = await SafetyDeviceAsync("a");
        var node = new FolderNode { Name = "historical" };
        await a.Tree.SaveNodeAsync(node);
        await JoinSafetyAsync(a);
        node.Name = "current";
        await a.Tree.SaveNodeAsync(node);
        await JoinSafetyAsync(a);
        var history = Assert.Single(await Location.ListHistoryAsync());
        var result = await a.Coordinator.ChangePassphraseAsync(new WriteThenFailLocation(Location, false, true), "new-password".AsMemory());
        Assert.False(result.Success);
        Assert.NotEmpty(await Location.ListHistoryAsync());
        Assert.Equal("new-password", new string((await a.Vault.GetPassphraseAsync())!));
        Assert.Equal(Passphrase, new string((await a.Vault.GetPreviousPassphraseAsync())!));
        Assert.Equal("new-password", new string((await a.Vault.GetPendingPassphraseAsync())!));
        // 当前主包已经新口令，但未清完的旧历史仍可用保留的旧凭据恢复。
        result = await a.Coordinator.RestoreHistoryAsync(Location, history.Name);
        Assert.True(result.Success, result.Message);
        Assert.Equal("historical", (await a.Tree.GetNodeByIdAsync(node.Id))!.Name);
        result = await a.Coordinator.ResumePassphraseChangeAsync(Location);
        Assert.True(result.Success, result.Message);
        Assert.Empty(await Location.ListHistoryAsync());
        Assert.Null(await a.Vault.GetPreviousPassphraseAsync());
    }

    [Fact]
    public async Task Safety_SchedulerFirstJoinNeverImportsWithoutManualApproval()
    {
        var a = await SafetyDeviceAsync("a");
        var b = await SafetyDeviceAsync("b");
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "must-wait-for-choice" });
        await JoinSafetyAsync(a);
        string before = JsonSerializer.Serialize(await b.Store.ExportAsync());
        var clock = new ReviewClock();
        using var scheduler = new SyncScheduler(clock, new ReviewLocationSource(Location), b.Vault, new SyncChangeSignal(), b.Coordinator.SyncAsync);
        scheduler.Start();
        int epoch = await clock.ParkedAfterAsync(0);
        Assert.Contains("首次加入", scheduler.Status);
        Assert.Equal(before, JsonSerializer.Serialize(await b.Store.ExportAsync()));
        clock.Advance(SyncScheduler.Interval);
        await clock.ParkedAfterAsync(epoch);
        Assert.Equal(before, JsonSerializer.Serialize(await b.Store.ExportAsync()));
        await JoinSafetyAsync(b);
        Assert.Contains(await b.Store.ExportAsync(), e => e.EntityType == "tree");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Safety_MasterPasswordRequiredBeforeInitializationOrJoinAndBackground(bool remoteExists)
    {
        var a = await SafetyDeviceAsync("a");
        if (remoteExists)
        {
            await a.Tree.SaveNodeAsync(new FolderNode { Name = "remote-data" });
            await JoinSafetyAsync(a);
        }
        var b = await SafetyDeviceAsync("b");
        await b.Tree.SaveNodeAsync(new FolderNode { Name = "untouched-local" });
        // 模拟已保存口令的旧设备后来去掉主密码；不能靠缺少口令或没有远端来挡住同步。
        await b.Vault.RemoveMasterPasswordAsync();
        string before = JsonSerializer.Serialize(await b.Store.ExportAsync());
        byte[]? remoteBefore = (await Location.ReadAsync())?.Content;
        var result = await b.Coordinator.SyncAsync(Location);
        Assert.False(result.Success);
        Assert.Null(result.Confirmation);
        Assert.Contains("先为保险库设置主密码", result.Message);
        var saveError = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Vault.SavePassphraseAsync("replacement".AsMemory()));
        Assert.Contains("先为保险库设置主密码", saveError.Message);
        Assert.Equal(Passphrase, new string((await b.Vault.GetPassphraseAsync())!));
        var clock = new ReviewClock();
        using (var scheduler = new SyncScheduler(clock, new ReviewLocationSource(Location), b.Vault, new SyncChangeSignal(), b.Coordinator.SyncAsync))
        {
            scheduler.Start();
            await clock.ParkedAfterAsync(0);
            Assert.Contains("先为保险库设置主密码", scheduler.Status);
            Assert.Equal(before, JsonSerializer.Serialize(await b.Store.ExportAsync()));
            Assert.Equal(remoteBefore, (await Location.ReadAsync())?.Content);
            Assert.Empty(await b.Coordinator.ListBackupsAsync());
        }
        await b.Vault.SetMasterPasswordAsync("enabled-master-password");
        await JoinSafetyAsync(b);
        Assert.NotNull(await Location.ReadAsync());
        Assert.True(await b.Vault.HasJoinedAsync());
    }

    [Fact]
    public async Task Safety_PlainVaultStillSupportsOrdinaryWork_UnconfiguredSchedulerIsQuiet_EnablingVmExplainsRequirement()
    {
        var b = await SafetyDeviceAsync("b");
        await b.Vault.DeletePassphraseAsync();
        await b.Vault.RemoveMasterPasswordAsync();
        var identity = new Kei.Term.Core.Vault.Identity { Name = "plain-user", Methods = [new Kei.Term.Core.Vault.VaultPasswordMethod()] };
        await new SqliteIdentityRepository(b.Db).SaveAsync(identity);
        await b.Vault.SaveSecretsAsync(identity.Id, new() { ["password"] = new() { Password = "normal-plain-mode-secret" } });
        Assert.Equal("normal-plain-mode-secret", (await b.Vault.GetSecretsAsync(identity.Id))["password"].Password);
        b.Settings.Current.FontSize = 27;
        await b.Settings.SaveSettingsAsync(b.Settings.Current);
        Assert.Equal(27, (await b.Store.ExportAsync()).Single(e => e.EntityType == "setting" && e.EntityId == "FontSize").Data.GetInt32());
        var clock = new ReviewClock();
        using var scheduler = new SyncScheduler(clock, new Kei.Term.App.Services.FileSyncLocationSource(b.Directory, b.Vault), b.Vault, new SyncChangeSignal(), b.Coordinator.SyncAsync);
        scheduler.Start();
        await clock.ParkedAfterAsync(0);
        Assert.Equal("", scheduler.Status);
        var page = new Kei.Term.App.ViewModels.Settings.SyncSettingsPage(b.Directory, scheduler, b.Vault, b.Vault, b.Coordinator)
        { DirectoryPath = Path.Combine(_root, "unused-shared"), Passphrase = "not-saved-in-plain-mode" };
        await page.SavePassphraseCommand.ExecuteAsync(null);
        Assert.Contains("先为保险库设置主密码", page.Status);
        Assert.Null(await b.Vault.GetPassphraseAsync());
        await page.SyncNowCommand.ExecuteAsync(null);
        Assert.Contains("先为保险库设置主密码", page.Status);
        Assert.False(File.Exists(Path.Combine(b.Directory, "sync-location.json")));
        Assert.False(Directory.Exists(page.DirectoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Safety_DisableKeepsLocalDataAndStorageCredentials(bool deleteRemote)
    {
        var a = await SafetyDeviceAsync("a");
        var node = new FolderNode { Name = "kept" };
        await a.Tree.SaveNodeAsync(node);
        await JoinSafetyAsync(a);
        node.Name = "kept-too";
        await a.Tree.SaveNodeAsync(node);
        await JoinSafetyAsync(a);
        await a.Vault.SaveWebDavPasswordAsync("storage-password");
        string before = JsonSerializer.Serialize(await a.Store.ExportAsync());
        var result = await a.Coordinator.DisableAsync(Location, deleteRemote);
        Assert.True(result.Success, result.Message);
        Assert.Equal(before, JsonSerializer.Serialize(await a.Store.ExportAsync()));
        Assert.Null(await a.Vault.GetPassphraseAsync());
        Assert.Equal("storage-password", await a.Vault.GetWebDavPasswordAsync());
        Assert.Equal(deleteRemote, await Location.ReadAsync() == null);
        Assert.Equal(deleteRemote, (await Location.ListHistoryAsync()).Count == 0);
        Assert.False((await a.Coordinator.SyncAsync(Location)).Success);
    }

    [Fact]
    public async Task Safety_RestoreCannotBypassPendingRedoJournal()
    {
        var a = await SafetyDeviceAsync("a");
        await a.Vault.SaveBackupAsync(await a.Store.ExportAsync());
        string id = (await a.Coordinator.ListBackupsAsync())[0].Id;
        using (var conn = await a.Db.OpenAsync())
            await conn.ExecuteAsync("INSERT INTO sync_file_journal(id,payload) VALUES(1,'corrupt-pending');");
        var result = await a.Coordinator.RestoreLocalAsync(id);
        Assert.False(result.Success);
        Assert.Contains("待恢复", result.Message);
        using (var conn = await a.Db.OpenAsync())
            Assert.Equal("corrupt-pending", await conn.QuerySingleAsync<string>("SELECT payload FROM sync_file_journal;"));
    }

    private sealed class WriteThenFailLocation(ISyncLocation inner, bool failUpload = true, bool failHistory = false) : ISyncLocation
    {
        public bool SupportsConditionalWrites => inner.SupportsConditionalWrites;
        public Task TestConnectionAsync(CancellationToken ct = default) => inner.TestConnectionAsync(ct);
        public Task<SyncObject?> ReadAsync(CancellationToken ct = default) => inner.ReadAsync(ct);
        public async Task WriteAsync(byte[] content, string? expectedVersionTag = null, CancellationToken ct = default)
        {
            await inner.WriteAsync(content, expectedVersionTag, ct);
            if (failUpload) throw new IOException("connection lost after accepted upload");
        }
        public Task<IReadOnlyList<SyncHistoryEntry>> ListHistoryAsync(CancellationToken ct = default) => inner.ListHistoryAsync(ct);
        public Task<byte[]?> ReadHistoryAsync(string name, CancellationToken ct = default) => inner.ReadHistoryAsync(name, ct);
        public Task WriteHistoryAsync(string name, byte[] content, CancellationToken ct = default) => inner.WriteHistoryAsync(name, content, ct);
        public Task DeleteHistoryAsync(string name, CancellationToken ct = default) => failHistory
            ? throw new IOException("history cleanup unavailable") : inner.DeleteHistoryAsync(name, ct);
    }
}
