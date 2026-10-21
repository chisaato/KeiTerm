using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Sync;
using Kei.Term.Infrastructure.Vault;
using Xunit;

namespace Kei.Term.Tests;

public sealed class SyncSafetyThresholdTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keiterm-safety-threshold-" + Guid.NewGuid().ToString("N"));
    private sealed record Device(SqliteSyncSnapshotStore Store, SqliteTreeRepository Tree, InternalVaultManager Vault, SyncCoordinator Sync);
    private ISyncLocation Location => new LocalFolderSyncLocation(Path.Combine(_root, "shared"));

    private async Task<Device> CreateAsync(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        var id = new PersistentDeviceId(Path.Combine(path, "device"));
        var db = new SqliteConnectionFactory($"Data Source={Path.Combine(path, "db")};Pooling=False", id);
        var tree = new SqliteTreeRepository(db);
        await tree.InitializeAsync();
        var vault = VaultTestDb.CreateVault(db);
        await vault.SetMasterPasswordAsync("master-password");
        await vault.SavePassphraseAsync("sync-password".AsMemory());
        var store = new SqliteSyncSnapshotStore(db, vault);
        return new(store, tree, vault, new(store, new AgeSyncPackage(), vault, id, vault));
    }

    private async Task ConfirmJoinAsync(Device d)
    {
        var result = await d.Sync.SyncAsync(Location);
        if (result.Confirmation is { } preview) result = await d.Sync.SyncApprovedAsync(Location, new(preview.Token));
        Assert.True(result.Success, result.Message);
    }

    [Theory]
    [InlineData(10, 3, false)]
    [InlineData(10, 4, true)]
    [InlineData(200, 50, false)]
    [InlineData(200, 51, true)]
    public async Task StrictThresholdCountsOnlyLocalLiveEntities(int total, int delete, bool needsConfirmation)
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        var nodes = Enumerable.Range(0, total).Select(i => new FolderNode { Name = "n" + i }).ToList();
        await a.Tree.SaveNodesAsync(nodes);
        await ConfirmJoinAsync(a);
        await ConfirmJoinAsync(b);
        foreach (var node in nodes.Take(delete)) await a.Tree.DeleteNodeAsync(node.Id);
        await ConfirmJoinAsync(a);
        var result = await b.Sync.SyncAsync(Location);
        Assert.Equal(needsConfirmation, result.Confirmation != null);
        Assert.Equal(!needsConfirmation, result.Success);
        Assert.Equal(needsConfirmation ? total : total - delete, (await b.Store.ExportAsync()).Count(e => e.DeletedAtUtc == null));
        if (needsConfirmation)
        {
            Assert.Equal(delete, result.Confirmation!.DeletedCount);
            Assert.Equal(total, result.Confirmation.LocalLiveCount);
            result = await b.Sync.SyncApprovedAsync(Location, new(result.Confirmation.Token));
            Assert.True(result.Success, result.Message);
            Assert.Equal(total - delete, (await b.Store.ExportAsync()).Count(e => e.DeletedAtUtc == null));
        }
    }

    [Fact]
    public async Task FirstJoinRemoteReplacementRequiresSeparateDeletionConfirmation()
    {
        var a = await CreateAsync("a");
        var b = await CreateAsync("b");
        await a.Tree.SaveNodeAsync(new FolderNode { Name = "remote" });
        await b.Tree.SaveNodeAsync(new FolderNode { Name = "local" });
        await ConfirmJoinAsync(a);
        var join = (await b.Sync.SyncAsync(Location)).Confirmation!;
        var result = await b.Sync.SyncApprovedAsync(Location, new(join.Token, SyncJoinChoice.Remote));
        Assert.Equal(SyncConfirmationKind.BulkDeletion, result.Confirmation!.Kind);
        Assert.Equal("local", Assert.Single(await b.Tree.GetAllNodesAsync()).Name);
        result = await b.Sync.SyncApprovedAsync(Location, new(result.Confirmation.Token));
        Assert.True(result.Success, result.Message);
        Assert.Equal("remote", Assert.Single(await b.Tree.GetAllNodesAsync()).Name);
    }

    [Fact]
    public async Task GuardedImportRejectsConcurrentLocalEditWithoutDeletingIt()
    {
        var a = await CreateAsync("a");
        var node = new FolderNode { Name = "original" };
        await a.Tree.SaveNodeAsync(node);
        var preview = await a.Store.ExportAsync();
        var deletion = preview.Select(e => e with { UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1), DeletedAtUtc = DateTime.UtcNow.AddMinutes(1) }).ToList();
        node.Name = "edited-after-preview";
        await a.Tree.SaveNodeAsync(node);
        await Assert.ThrowsAsync<SyncPreviewChangedException>(() => a.Store.ImportGuardedAsync(deletion, preview));
        Assert.Equal("edited-after-preview", (await a.Tree.GetNodeByIdAsync(node.Id))!.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
