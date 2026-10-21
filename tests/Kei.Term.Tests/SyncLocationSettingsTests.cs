using System;
using System.IO;
using System.Threading.Tasks;
using Kei.Term.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Tests;

public sealed class SyncLocationSettingsTests
{
    [Fact]
    public void LegacyTextFileLoadsAsLocalDirectoryAndSaveReplacesIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keiterm-sync-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, SyncLocationSettingsStore.LegacyFileName), "/tmp/old-sync\n");
            var loaded = SyncLocationSettingsStore.Load(directory);
            Assert.Equal(SyncLocationKinds.LocalFolder, loaded.Kind);
            Assert.Equal("/tmp/old-sync", loaded.Directory);

            loaded.Kind = SyncLocationKinds.WebDav;
            loaded.WebDavUrl = "https://cloud.example/dav/";
            loaded.WebDavUser = "ada";
            loaded.S3Provider = "aliyun";
            SyncLocationSettingsStore.Save(directory, loaded);

            Assert.False(File.Exists(Path.Combine(directory, SyncLocationSettingsStore.LegacyFileName)));
            var json = File.ReadAllText(Path.Combine(directory, SyncLocationSettingsStore.FileName));
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            var again = SyncLocationSettingsStore.Load(directory);
            Assert.Equal("https://cloud.example/dav/", again.WebDavUrl);
            Assert.Equal("ada", again.WebDavUser);
            Assert.True(again.WebDavConditionalWrites);
            Assert.False(again.UseS3ConditionalWrites);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void S3ConditionalWriteDefaultsFollowProviderUnlessOverridden()
    {
        Assert.False(S3ConditionalWritePolicy.IsEnabled("aliyun"));
        Assert.False(S3ConditionalWritePolicy.IsEnabled("tencent-cos"));
        Assert.False(S3ConditionalWritePolicy.IsEnabled("OSS"));
        Assert.True(S3ConditionalWritePolicy.IsEnabled("minio"));
        Assert.True(S3ConditionalWritePolicy.IsEnabled("aws"));
        Assert.True(S3ConditionalWritePolicy.IsEnabled(null));

        var settings = new SyncLocationSettings { S3Provider = "aliyun", S3ConditionalWrites = true };
        Assert.True(settings.UseS3ConditionalWrites);
    }

    [Fact]
    public async Task FactoryLocalFolderRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keiterm-factory-" + Guid.NewGuid().ToString("N"));
        var location = SyncLocationFactory.Create(new SyncLocationSettings { Directory = directory }, null, null);
        await location.WriteAsync([9, 8]);
        Assert.Equal(new byte[] { 9, 8 }, (await location.ReadAsync())!.Content);
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task RemoteHistoryNamesAreRejectedBeforeAnyNetworkCall()
    {
        using var webDav = new WebDavSyncLocation("http://127.0.0.1:9/dav/", "user", "secret", conditionalWrites: true);
        using var s3 = new S3SyncLocation("http://127.0.0.1:9", "us-east-1", "bucket", "prefix", "access", "secret", true, false);
        Assert.True(webDav.SupportsConditionalWrites);
        Assert.False(s3.SupportsConditionalWrites);
        await Assert.ThrowsAsync<ArgumentException>(() => webDav.WriteHistoryAsync("../escape.age", [1]));
        await Assert.ThrowsAsync<ArgumentException>(() => s3.WriteHistoryAsync("../escape.age", [1]));
    }

    [Fact]
    public async Task VaultStoresWebDavPasswordAndS3SecretSeparately()
    {
        string db = VaultTestDb.NewPath();
        try
        {
            var vault = VaultTestDb.CreateVault($"Data Source={db}");
            Assert.Null(await vault.GetWebDavPasswordAsync());
            Assert.Null(await vault.GetS3SecretKeyAsync());
            await vault.SaveWebDavPasswordAsync("dav-secret");
            await vault.SaveS3SecretKeyAsync("s3-secret");
            Assert.Equal("dav-secret", await vault.GetWebDavPasswordAsync());
            Assert.Equal("s3-secret", await vault.GetS3SecretKeyAsync());
            Assert.Null(await vault.GetPassphraseAsync());
            await vault.SetMasterPasswordAsync("vault-password");
            vault.Lock();
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.GetWebDavPasswordAsync());
            var restarted = VaultTestDb.CreateVault($"Data Source={db}");
            await restarted.UnlockAsync("vault-password", false);
            Assert.Equal("dav-secret", await restarted.GetWebDavPasswordAsync());
            Assert.Equal("s3-secret", await restarted.GetS3SecretKeyAsync());
            await restarted.SetMasterPasswordAsync("new-password");
            restarted.Lock();
            await restarted.UnlockAsync("new-password", false);
            Assert.Equal("dav-secret", await restarted.GetWebDavPasswordAsync());
            Assert.Equal("s3-secret", await restarted.GetS3SecretKeyAsync());
            await Assert.ThrowsAsync<ArgumentException>(() => restarted.SaveS3SecretKeyAsync(""));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(db + suffix)) File.Delete(db + suffix);
        }
    }
}
