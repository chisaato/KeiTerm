using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Sync;
using Kei.Term.Infrastructure.Vault;
using Xunit;

namespace Kei.Term.Tests;

public sealed class DeviceConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keiterm-devices-" + Guid.NewGuid().ToString("N"));
    private sealed record Installation(PersistentDeviceId Id, SqliteConnectionFactory Db, SqliteTreeRepository Tree,
        DeviceConfigurationService Config, SqliteSyncSnapshotStore Sync);
    private async Task<Installation> OpenAsync(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var id = new PersistentDeviceId(Path.Combine(dir, "device-id"));
        var db = new SqliteConnectionFactory($"Data Source={Path.Combine(dir, "db")};Pooling=False", id);
        var tree = new SqliteTreeRepository(db);
        await tree.InitializeAsync();
        return new(id, db, tree, new(new SqliteDeviceConfigurationStore(db), id), new(db));
    }
    private static Dictionary<string, JsonElement> Fields(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [Fact]
    public async Task LocalResolution_Fieldwise_Restart_DeleteAndNewInstallation()
    {
        var a = await OpenAsync("a");
        var session = new SessionNode { Name = "serial" };
        await a.Tree.SaveNodeAsync(session);
        await a.Config.SaveOverrideAsync(session.Id, "serial", DeviceConfigurationTarget.ForOperatingSystem("linux"), Fields(new { baudRate = 115200 }));
        string oldId = a.Id.DeviceId!;
        Assert.False(string.IsNullOrWhiteSpace(oldId));
        await a.Config.SaveOverrideAsync(session.Id, "serial", DeviceConfigurationTarget.ForDevice(oldId), Fields(new { portName = "/dev/ttyUSB0" }));
        var restarted = await OpenAsync("a");
        var defaults = Fields(new { baudRate = 9600, dataBits = 8 });
        var resolved = await restarted.Config.ResolveAsync(session.Id, "serial", "linux", defaults);
        Assert.Equal(115200, resolved["baudRate"].GetInt32());
        Assert.Equal(8, resolved["dataBits"].GetInt32());
        Assert.Equal("/dev/ttyUSB0", resolved["portName"].GetString());
        var explicitRecords = await restarted.Config.GetOverridesAsync(session.Id, "serial");
        Assert.Equal(new[] { "baudRate" }, explicitRecords.Single(r => r.Target.OperatingSystem == "linux").Fields.Keys);
        Assert.Equal(new[] { "portName" }, explicitRecords.Single(r => r.Target.TargetDeviceId == oldId).Fields.Keys);
        await restarted.Config.SaveOverrideAsync(session.Id, "serial", DeviceConfigurationTarget.ForDevice(oldId), Fields(new { }));
        Assert.False((await restarted.Config.ResolveAsync(session.Id, "serial", "linux", defaults)).ContainsKey("portName"));
        await restarted.Config.DeleteOverrideAsync(session.Id, "serial", DeviceConfigurationTarget.ForOperatingSystem("linux"));
        Assert.DoesNotContain(await restarted.Config.GetOverridesAsync(session.Id, "serial"), r => r.Target.OperatingSystem == "linux");
        Assert.Equal(9600, (await restarted.Config.ResolveAsync(session.Id, "serial", "linux", defaults))["baudRate"].GetInt32());
        await restarted.Config.SaveOverrideAsync(session.Id, "serial", DeviceConfigurationTarget.ForDevice(oldId), Fields(new { portName = "COM4" }));
        Assert.NotEqual(oldId, restarted.Id.RecreateAsNewDevice());
        Assert.False((await restarted.Config.ResolveAsync(session.Id, "serial", "linux", defaults)).ContainsKey("portName"));
        Assert.Contains(await restarted.Config.GetOverridesAsync(session.Id, "serial"), r => r.Target.TargetDeviceId == oldId && r.Fields["portName"].GetString() == "COM4");
        Assert.Equal(restarted.Id.DeviceId, (await OpenAsync("a")).Id.DeviceId);
    }

    [Fact]
    public async Task IndependentOfflineRecords_TombstonesAndParentDeletion_DoNotResurrect()
    {
        var a = await OpenAsync("a");
        var b = await OpenAsync("b");
        var session = new SessionNode { Name = "serial" };
        await a.Tree.SaveNodeAsync(session);
        await b.Sync.ImportAsync(await a.Sync.ExportAsync());
        a.Id.EnsureCreated(); b.Id.EnsureCreated();
        var ta = DeviceConfigurationTarget.ForDevice(a.Id.DeviceId!);
        var tb = DeviceConfigurationTarget.ForDevice(b.Id.DeviceId!);
        await a.Config.SaveOverrideAsync(session.Id, "serial", ta, Fields(new { portName = "COM1" }));
        await b.Config.SaveOverrideAsync(session.Id, "serial", tb, Fields(new { portName = "COM2" }));
        await a.Sync.ImportAsync(await b.Sync.ExportAsync());
        await b.Sync.ImportAsync(await a.Sync.ExportAsync());
        Assert.Equal("COM1", (await a.Config.ResolveAsync(session.Id, "serial", "windows", Fields(new { })))["portName"].GetString());
        Assert.Equal("COM2", (await b.Config.ResolveAsync(session.Id, "serial", "windows", Fields(new { })))["portName"].GetString());
        var stale = await a.Sync.ExportAsync();
        await a.Config.DeleteOverrideAsync(session.Id, "serial", ta);
        await b.Sync.ImportAsync(await a.Sync.ExportAsync());
        await b.Sync.ImportAsync(stale);
        Assert.Contains(await b.Sync.ExportAsync(), e => e.EntityType == "device-configuration" && e.DeletedAtUtc != null);
        await b.Tree.DeleteNodeAsync(session.Id);
        await a.Sync.ImportAsync(await b.Sync.ExportAsync());
        await a.Sync.ImportAsync(stale);
        Assert.Empty(await a.Config.ResolveAsync(session.Id, "serial", "windows", Fields(new { })));
        Assert.All((await a.Sync.ExportAsync()).Where(e => e.EntityType == "device-configuration"), e => Assert.NotNull(e.DeletedAtUtc));
    }

    [Fact]
    public async Task SecretFields_AreRejectedAtPublicConfigurationEntry()
    {
        var a = await OpenAsync("a");
        var session = new SessionNode { Name = "serial" };
        await a.Tree.SaveNodeAsync(session);
        await Assert.ThrowsAsync<ArgumentException>(() => a.Config.SaveOverrideAsync(session.Id, "serial", DeviceConfigurationTarget.ForOperatingSystem("linux"), Fields(new { password = "plaintext" })));
    }

    [Fact]
    public async Task SameRecordConflict_UsesTimestampThenSourceDevice_NotTargetDevice()
    {
        var a = await OpenAsync("a");
        var b = await OpenAsync("b");
        var session = new SessionNode { Name = "serial" };
        await a.Tree.SaveNodeAsync(session);
        await b.Sync.ImportAsync(await a.Sync.ExportAsync());
        var target = DeviceConfigurationTarget.ForOperatingSystem("linux");
        await a.Config.SaveOverrideAsync(session.Id, "serial", target, Fields(new { baudRate = 9600 }));
        await b.Config.SaveOverrideAsync(session.Id, "serial", target, Fields(new { baudRate = 115200 }));
        var ea = (await a.Sync.ExportAsync()).Single(e => e.EntityType == "device-configuration");
        var eb = (await b.Sync.ExportAsync()).Single(e => e.EntityType == "device-configuration");
        DateTime stamp = DateTime.UtcNow.AddDays(1);
        ea = ea with { UpdatedAtUtc = stamp, DeviceId = "a" };
        eb = eb with { UpdatedAtUtc = stamp, DeviceId = "z" };
        await a.Sync.ImportAsync([ea, eb]);
        await b.Sync.ImportAsync([eb, ea]);
        Assert.Equal(115200, (await a.Config.ResolveAsync(session.Id, "serial", "linux", Fields(new { })))["baudRate"].GetInt32());
        Assert.Equal(115200, (await b.Config.ResolveAsync(session.Id, "serial", "linux", Fields(new { })))["baudRate"].GetInt32());
        await a.Sync.ImportAsync([ea with { UpdatedAtUtc = stamp.AddSeconds(1) }]);
        Assert.Equal(9600, (await a.Config.ResolveAsync(session.Id, "serial", "linux", Fields(new { })))["baudRate"].GetInt32());
    }

    [Fact]
    public async Task SyncEntry_ReadsV1_WritesV2_RejectsFutureBeforeImportOrUpload()
    {
        var a = await OpenAsync("a");
        var session = new SessionNode { Name = "v1-session" };
        await a.Tree.SaveNodeAsync(session);
        var vault = VaultTestDb.CreateVault(a.Db);
        await vault.SetMasterPasswordAsync("local-master");
        await vault.SavePassphraseAsync("sync-password".AsMemory());
        var package = new AgeSyncPackage();
        var location = new LocalFolderSyncLocation(Path.Combine(_root, "shared"));
        var legacy = new SyncSnapshot(1, "legacy", DateTime.UtcNow, await a.Sync.ExportAsync());
        await location.WriteAsync(package.Encrypt(JsonSerializer.SerializeToUtf8Bytes(legacy), "sync-password".AsSpan()));
        var coordinator = new SyncCoordinator(a.Sync, package, vault, a.Id, vault);
        async Task<SyncResult> SyncAsync()
        {
            var result = await coordinator.SyncAsync(location);
            while (result.Confirmation is { } preview) result = await coordinator.SyncApprovedAsync(location, new(preview.Token));
            return result;
        }
        Assert.True((await SyncAsync()).Success);
        await a.Config.SaveOverrideAsync(session.Id, "serial", DeviceConfigurationTarget.ForDevice(a.Id.DeviceId!), Fields(new { portName = "COM3" }));
        Assert.True((await SyncAsync()).Success);
        var outgoing = JsonSerializer.Deserialize<SyncSnapshot>(package.Decrypt((await location.ReadAsync())!.Content, "sync-password".AsSpan()))!;
        Assert.Equal(2, outgoing.FormatVersion);
        Assert.Contains(outgoing.Entities, e => e.EntityType == "device-configuration");
        var before = JsonSerializer.Serialize(await a.Sync.ExportAsync());
        var future = outgoing with { FormatVersion = 3, Entities = [] };
        var existing = (await location.ReadAsync())!;
        await location.WriteAsync(package.Encrypt(JsonSerializer.SerializeToUtf8Bytes(future), "sync-password".AsSpan()), existing.VersionTag);
        byte[] remoteBefore = (await location.ReadAsync())!.Content;
        var rejected = await coordinator.SyncAsync(location);
        Assert.False(rejected.Success);
        Assert.Contains("升级", rejected.Message);
        Assert.Equal(before, JsonSerializer.Serialize(await a.Sync.ExportAsync()));
        Assert.Equal(remoteBefore, (await location.ReadAsync())!.Content);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
