using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.App.Services;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Settings;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;
using Xunit;

namespace Kei.Term.Tests;

public sealed partial class FullSyncSnapshotTests
{
    private const string RecoveryChildEnvironment = "KEITERM_04_RECOVERY_CHILD";

    private static SyncFileRecovery Recovery(Device device) => new(device.Db,
        Path.Combine(device.Directory, "settings.json"), Path.Combine(device.Directory, "profiles.json"));

    private static async Task<List<SyncEntity>> IncomingFilesAsync(Device device)
    {
        await device.Settings.CommitFontSizeAsync(15);
        device.Profiles.AddCustomTerminalProfile(new() { Id = "recovery-profile", Name = "before" });
        await device.Profiles.CommitAndSaveProfilesAsync();
        string path = Path.Combine(device.Directory, "settings.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["WindowSize"] = "local-window-must-survive-replay";
        await File.WriteAllTextAsync(path, document.ToJsonString());
        var entities = await device.Store.ExportAsync();
        var font = entities.Single(e => e.EntityType == "setting" && e.EntityId == "FontSize") with
        { UpdatedAtUtc = DateTime.UtcNow.AddHours(1), Data = JsonSerializer.SerializeToElement(37) };
        var profile = entities.Single(e => e.EntityType == "profile" && e.EntityId == "recovery-profile") with
        {
            UpdatedAtUtc = DateTime.UtcNow.AddHours(1),
            Data = JsonSerializer.SerializeToElement(new SqliteSyncSnapshotStore.ProfileData("terminalProfiles",
                JsonSerializer.SerializeToElement(new TerminalProfile { Id = "recovery-profile", Name = "after" })))
        };
        return [font, profile];
    }

    private static async Task<long> JournalCountAsync(Device device)
    {
        using var conn = await device.Db.OpenAsync();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sync_file_journal;");
    }

    private static async Task AssertRestartedFilesAsync(Device device, IReadOnlyList<SyncEntity> expected, bool committed)
    {
        // 新实例走真实服务加载/记账入口；不能靠只检查文件字节漏掉重启重新盖章。
        var tracker = new SqliteLocalChangeTracker(device.Db);
        var stampsBefore = await tracker.GetAllAsync();
        var settings = new JsonSettingsService(Path.Combine(device.Directory, "settings.json"), tracker);
        await settings.LoadSettingsAsync();
        var profiles = new ProfileManagerService(settings, device.Directory, tracker: tracker);
        await profiles.InitializeAsync();
        Assert.Equal(committed ? 37 : 15, settings.Current.FontSize);
        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(device.Directory, "settings.json")));
        Assert.Equal("local-window-must-survive-replay", file.RootElement.GetProperty("WindowSize").GetString());
        Assert.Equal(committed ? "after" : "before", profiles.AllTerminalProfiles.Single(p => p.Id == "recovery-profile").Name);
        Assert.Equal(stampsBefore, await tracker.GetAllAsync());
        if (committed)
        {
            var entities = await new SqliteSyncSnapshotStore(device.Db, device.Vault,
                Path.Combine(device.Directory, "settings.json"), Path.Combine(device.Directory, "profiles.json")).ExportAsync();
            foreach (var entity in expected)
                Assert.Equal(JsonSerializer.Serialize(entity), JsonSerializer.Serialize(entities.Single(e => e.EntityType == entity.EntityType && e.EntityId == entity.EntityId)));
        }
    }

    [Theory]
    [InlineData("BeforeCommit")]
    [InlineData("AfterCommit")]
    [InlineData("AfterSettingsFile")]
    [InlineData("AfterProfilesFile")]
    [InlineData("BeforeJournalClear")]
    public async Task RecoveryAfterProcessInterruption_KeepsFilesAndVersionsConsistent(string checkpoint)
    {
        string? child = Environment.GetEnvironmentVariable(RecoveryChildEnvironment);
        if (child != null)
        {
            await RunRecoveryChildAsync(child);
            return;
        }
        var device = await CreateAsync("crash");
        var incoming = await IncomingFilesAsync(device);
        await InterruptChildAsync(device, incoming, checkpoint, replay: false);
        bool committed = checkpoint != "BeforeCommit";
        Assert.Equal(committed ? 1 : 0, await JournalCountAsync(device));
        Assert.Equal(SchemaMigrator.LatestVersion, await Recovery(device).PrepareStartupAsync());
        await AssertRestartedFilesAsync(device, incoming, committed);
        Assert.Equal(0, await JournalCountAsync(device));
        // Program 和 App 的两次调用，以及再次正常重启，都不得改变文件版本。
        await Recovery(device).PrepareStartupAsync();
        await AssertRestartedFilesAsync(device, incoming, committed);
    }

    [Fact]
    public async Task RecoveryInterruptedAgain_ReplaysIdempotentlyWithoutDuplicatingProfiles()
    {
        var device = await CreateAsync("replay");
        var incoming = await IncomingFilesAsync(device);
        device.Store.RecoveryCheckpoint = point => { if (point == SyncFileRecoveryCheckpoint.AfterCommit) throw new IOException("提交后中断"); };
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => device.Store.ImportAsync(incoming));
        await InterruptChildAsync(device, incoming, "AfterProfilesFile", replay: true);
        Assert.Equal(1, await JournalCountAsync(device));
        await Recovery(device).PrepareStartupAsync();
        await AssertRestartedFilesAsync(device, incoming, committed: true);
        Assert.Equal(0, await JournalCountAsync(device));
    }

    [Fact]
    public async Task FileFailure_BlocksLoadsSavesAndSync_UntilSuccessfulStartupRecovery_NoFalseImportedEvent()
    {
        var device = await CreateAsync("failed");
        var incoming = await IncomingFilesAsync(device);
        string settingsPath = Path.Combine(device.Directory, "settings.json");
        int imported = 0;
        device.Store.Imported += () => { imported++; return Task.CompletedTask; };
        device.Store.RecoveryCheckpoint = point =>
        {
            if (point != SyncFileRecoveryCheckpoint.AfterCommit) return;
            File.Move(settingsPath, settingsPath + ".backup");
            Directory.CreateDirectory(settingsPath);
        };
        var failure = await Assert.ThrowsAsync<SyncFileRecoveryException>(() => device.Store.ImportAsync(incoming));
        Assert.Contains("已提交", failure.Message);
        Assert.Contains("待恢复", failure.Message);
        Assert.Equal(0, imported);
        var stamps = await new SqliteLocalChangeTracker(device.Db).GetAllAsync();
        using var conn = await device.Db.OpenAsync();
        string journal = await conn.QuerySingleAsync<string>("SELECT payload FROM sync_file_journal;");
        Assert.DoesNotContain("local-window-must-survive-replay", journal);
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => Recovery(device).PrepareStartupAsync());
        var tracker = new SqliteLocalChangeTracker(device.Db);
        var settings = new JsonSettingsService(settingsPath, tracker);
        var profiles = new ProfileManagerService(settings, device.Directory, tracker: tracker);
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => settings.LoadSettingsAsync());
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => settings.SaveSettingsAsync(new AppSettings { FontSize = 99 }));
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => profiles.InitializeAsync());
        device.Profiles.AddCustomTerminalProfile(new() { Id = "stale-draft", Name = "must-not-write" });
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => device.Profiles.CommitAndSaveProfilesAsync());
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => device.Store.ExportAsync());
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => device.Store.ImportAsync(incoming));
        var result = await device.Coordinator.SyncAsync(Location);
        Assert.False(result.Success);
        Assert.Contains("待恢复", result.Message);
        Assert.Null(await Location.ReadAsync());
        Assert.Equal(journal, await conn.QuerySingleAsync<string>("SELECT payload FROM sync_file_journal;"));
        Assert.Equal(stamps, await tracker.GetAllAsync());
        Directory.Delete(settingsPath);
        File.Move(settingsPath + ".backup", settingsPath);
        await Recovery(device).PrepareStartupAsync();
        await AssertRestartedFilesAsync(device, incoming, committed: true);
        Assert.Equal(0, imported);
    }

    [Fact]
    public async Task JournalContainsOnlyFileOperations_AndCannotChooseRemotePaths()
    {
        var source = await CreateAsync("source");
        var target = await CreateAsync("target");
        await PopulateAsync(source);
        var entities = await source.Store.ExportAsync();
        string victim = Path.Combine(_root, "never-written.json");
        entities.Add(new("setting", "TargetPath", DateTime.UtcNow, "source", null, JsonSerializer.SerializeToElement(victim)));
        target.Store.RecoveryCheckpoint = point => { if (point == SyncFileRecoveryCheckpoint.AfterCommit) throw new IOException("中断"); };
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => target.Store.ImportAsync(entities));
        using var conn = await target.Db.OpenAsync();
        string payload = await conn.QuerySingleAsync<string>("SELECT payload FROM sync_file_journal;");
        var operations = JsonSerializer.Deserialize<List<SyncEntity>>(payload)!;
        Assert.All(operations, e => Assert.True(e.EntityType is "setting" or "profile"));
        Assert.DoesNotContain("Secret", payload);
        await Recovery(target).PrepareStartupAsync();
        Assert.False(File.Exists(victim));
        Assert.True(File.Exists(Path.Combine(target.Directory, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target.Directory, "profiles.json")));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("[{\"EntityType\":\"identity\",\"EntityId\":\"path\",\"Data\":{}}]")]
    public async Task CorruptJournal_FailsClosedWithoutDiscardingRecoveryRecord(string payload)
    {
        var device = await CreateAsync("corrupt");
        using var conn = await device.Db.OpenAsync();
        await conn.ExecuteAsync("INSERT INTO sync_file_journal(id,payload) VALUES(1,@payload);", new { payload });
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => Recovery(device).PrepareStartupAsync());
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => device.Settings.LoadSettingsAsync());
        Assert.Equal(payload, await conn.QuerySingleAsync<string>("SELECT payload FROM sync_file_journal;"));
    }

    [Fact]
    public async Task NoJournalStartupAndV10Upgrade_AreIdempotentAndDoNotRewriteFiles()
    {
        var device = await CreateAsync("normal");
        await IncomingFilesAsync(device);
        var stamps = await new SqliteLocalChangeTracker(device.Db).GetAllAsync();
        string path = Path.Combine(device.Directory, "settings.json");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using (var conn = await device.Db.OpenAsync())
            await conn.ExecuteAsync("DROP TABLE sync_file_journal; PRAGMA user_version=10;");
        Assert.Equal(11, await Recovery(device).PrepareStartupAsync());
        Assert.Equal(11, await Recovery(device).PrepareStartupAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(stamps, await new SqliteLocalChangeTracker(device.Db).GetAllAsync());
        Assert.Equal(0, await JournalCountAsync(device));
        var fresh = new SqliteConnectionFactory($"Data Source={Path.Combine(_root, "fresh.sqlite")};Pooling=False");
        await new SyncFileRecovery(fresh, Path.Combine(_root, "fresh-settings.json"), Path.Combine(_root, "fresh-profiles.json")).PrepareStartupAsync();
        Assert.False(File.Exists(Path.Combine(_root, "fresh-settings.json")));
        Assert.False(File.Exists(Path.Combine(_root, "fresh-profiles.json")));
    }

    [Fact]
    public async Task ProductionStartupPreparation_RestoresBeforeNativeMenuPreRead_AndImportNotifiesOnlyAfterClearingJournal()
    {
        string directory = Path.Combine(_root, "startup");
        await Task.Run(() => AppPaths.PrepareStorage(directory));
        var factory = new SqliteConnectionFactory($"Data Source={Path.Combine(directory, "keiterm.db")};Pooling=False");
        string path = Path.Combine(directory, "settings.json");
        var tracker = new SqliteLocalChangeTracker(factory);
        var settings = new JsonSettingsService(path, tracker);
        await settings.LoadSettingsAsync();
        await settings.SaveSettingsAsync(settings.Current);
        var store = new SqliteSyncSnapshotStore(factory, settingsPath: path, profilesPath: Path.Combine(directory, "profiles.json"));
        var incoming = (await store.ExportAsync()).Single(e => e.EntityType == "setting" && e.EntityId == "UseNativeGlobalMenu") with
        { UpdatedAtUtc = DateTime.UtcNow.AddHours(1), Data = JsonSerializer.SerializeToElement(false) };
        store.RecoveryCheckpoint = point => { if (point == SyncFileRecoveryCheckpoint.AfterCommit) throw new IOException("模拟中断"); };
        await Assert.ThrowsAsync<SyncFileRecoveryException>(() => store.ImportAsync([incoming]));
        await Task.Run(() => AppPaths.PrepareStorage(directory));
        await Task.Run(() => AppPaths.PrepareStorage(directory));
        var options = await Task.Run(() => NativeMenuSettings.CreateX11Options(path));
        Assert.False(options.UseDBusMenu);
        store.RecoveryCheckpoint = null;
        bool notified = false;
        store.Imported += async () =>
        {
            using var conn = await factory.OpenAsync();
            Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sync_file_journal;"));
            await settings.ReloadSynchronizedAsync();
            notified = true;
        };
        await store.ImportAsync([incoming with { UpdatedAtUtc = incoming.UpdatedAtUtc.AddHours(1), Data = JsonSerializer.SerializeToElement(true) }]);
        Assert.True(notified);
        Assert.True(settings.Current.UseNativeGlobalMenu);
    }

    private static async Task RunRecoveryChildAsync(string directory)
    {
        string checkpoint = Environment.GetEnvironmentVariable("KEITERM_04_CHECKPOINT")!;
        bool replay = Environment.GetEnvironmentVariable("KEITERM_04_REPLAY") == "1";
        var factory = new SqliteConnectionFactory($"Data Source={Path.Combine(directory, "db.sqlite")};Pooling=False");
        void Interrupt(SyncFileRecoveryCheckpoint point)
        {
            if (point.ToString() != checkpoint) return;
            File.WriteAllText(Path.Combine(directory, "child-ready"), checkpoint);
            Thread.Sleep(Timeout.Infinite);
        }
        if (replay)
            await new SyncFileRecovery(factory, Path.Combine(directory, "settings.json"), Path.Combine(directory, "profiles.json")) { Checkpoint = Interrupt }.PrepareStartupAsync();
        else
        {
            var store = new SqliteSyncSnapshotStore(factory, settingsPath: Path.Combine(directory, "settings.json"), profilesPath: Path.Combine(directory, "profiles.json")) { RecoveryCheckpoint = Interrupt };
            var entities = JsonSerializer.Deserialize<List<SyncEntity>>(await File.ReadAllTextAsync(Path.Combine(directory, "incoming-files.json")))!;
            await store.ImportAsync(entities);
        }
    }

    private static async Task InterruptChildAsync(Device device, List<SyncEntity> incoming, string checkpoint, bool replay)
    {
        await File.WriteAllTextAsync(Path.Combine(device.Directory, "incoming-files.json"), JsonSerializer.Serialize(incoming));
        string ready = Path.Combine(device.Directory, "child-ready");
        if (File.Exists(ready)) File.Delete(ready);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(FullSyncSnapshotTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=Kei.Term.Tests.FullSyncSnapshotTests.RecoveryAfterProcessInterruption_KeepsFilesAndVersionsConsistent");
        start.Environment[RecoveryChildEnvironment] = device.Directory;
        start.Environment["KEITERM_04_CHECKPOINT"] = checkpoint;
        start.Environment["KEITERM_04_REPLAY"] = replay ? "1" : "0";
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(ready))
            {
                if (process.HasExited) Assert.Fail("子进程未到达故障边界：" + await stdout + await stderr);
                await Task.Delay(10, timeout.Token);
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Task.WhenAll(stdout, stderr);
        }
    }
}
