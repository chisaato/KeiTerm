using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels.Settings;
using Kei.Term.Core.Sync;
using Kei.Term.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace Kei.Term.Tests;

public sealed class SyncLocationConfigurationSafetyTests
{
    [Theory]
    [InlineData("webdav", "ftp://example.com/dav/")]
    [InlineData("s3", "ftp://example.com")]
    [InlineData("s3", "http://[")]
    public async Task InvalidDraftPreservesSavedConfigurationAndBothVaultCredentials(string kind, string address)
    {
        string root = Path.Combine(Path.GetTempPath(), "keiterm-sync-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var vault = VaultTestDb.CreateVault($"Data Source={Path.Combine(root, "vault.db")}");
            await vault.SaveWebDavPasswordAsync("original-dav");
            await vault.SaveS3SecretKeyAsync("original-s3");
            SyncLocationSettingsStore.Save(root, new SyncLocationSettings
            {
                Kind = SyncLocationKinds.WebDav,
                WebDavUrl = "https://example.com/valid/",
                WebDavUser = "original-user",
            });
            string saved = File.ReadAllText(Path.Combine(root, SyncLocationSettingsStore.FileName));
            using var scheduler = new SyncScheduler(new SystemSyncClock(), new FileSyncLocationSource(root, vault),
                vault, new SyncChangeSignal(), (_, _) => throw new InvalidOperationException("不应同步"));
            var page = new SyncSettingsPage(root, scheduler, vault, vault)
            {
                WebDavUrl = address,
                WebDavUser = "new-user",
                WebDavPassword = "replacement-dav",
                S3Endpoint = address,
                S3Bucket = "bucket",
                S3AccessKeyId = "access",
                S3SecretKey = "replacement-s3",
            };
            page.SelectedKind = page.LocationKinds.Single(option => option.Id == kind);

            await page.TestConnectionCommand.ExecuteAsync(null);

            Assert.NotEqual("同步位置连接成功", page.Status);
            Assert.False(page.IsBusy);
            Assert.Equal(saved, File.ReadAllText(Path.Combine(root, SyncLocationSettingsStore.FileName)));
            Assert.Equal("original-dav", await vault.GetWebDavPasswordAsync());
            Assert.Equal("original-s3", await vault.GetS3SecretKeyAsync());
            Assert.Equal("replacement-dav", page.WebDavPassword);
            Assert.Equal("replacement-s3", page.S3SecretKey);
            Assert.Equal("https://example.com/valid/", SyncLocationSettingsStore.Load(root).WebDavUrl);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
