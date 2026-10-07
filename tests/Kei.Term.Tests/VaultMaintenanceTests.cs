using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

public class VaultMaintenanceTests
{
    [Fact]
    public async Task PasswordRotation_PreservesIdentityAndProxySecrets_AfterRestart()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Vault.SetMasterPasswordAsync("first-password");
        await fixture.SaveSecretsAsync();

        await fixture.Vault.SetMasterPasswordAsync("second-password");
        fixture.Vault.Lock();
        InternalVaultManager restarted = new(fixture.Database);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.UnlockAsync("first-password", false));
        await restarted.UnlockAsync("second-password", false);
        await fixture.AssertSecretsAsync(restarted);
    }

    [Fact]
    public async Task LockedPasswordRotation_RejectsChange_AndPreservesOldPasswordAndSecrets()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Vault.SetMasterPasswordAsync("first-password");
        await fixture.SaveSecretsAsync();
        fixture.Vault.Lock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Vault.SetMasterPasswordAsync("second-password"));

        InternalVaultManager restarted = new(fixture.Database);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.UnlockAsync("second-password", false));
        await restarted.UnlockAsync("first-password", false);
        await fixture.AssertSecretsAsync(restarted);
    }

    [Fact]
    public async Task FirstPassword_RemovesLiveAndDeletedPlaintext_FromDatabaseAndWal()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        // 保持连接打开，避免连接关闭触发的自动 checkpoint 掩盖实际清理行为。
        using SqliteConnection keeper = await fixture.Database.OpenAsync();
        await fixture.SaveSecretsAsync();
        Guid deletedId = Guid.NewGuid();
        await new SqliteIdentityRepository(fixture.Database).SaveAsync(new Identity { Id = deletedId, Name = "deleted" });
        string oldSecret = "deleted-secret-marker-91d0f39d" + new string('x', 32_000);
        await fixture.Vault.SaveSecretsAsync(deletedId, new Dictionary<string, SecretPayload>
        {
            ["old"] = new() { PrivateKeyContent = oldSecret }
        });
        await keeper.ExecuteAsync("PRAGMA wal_checkpoint(FULL);");
        await fixture.Vault.DeleteSecretsAsync(deletedId);
        Assert.True(fixture.FilesContain(Fixture.Password));
        Assert.True(fixture.FilesContain("deleted-secret-marker-91d0f39d"));

        await fixture.Vault.SetMasterPasswordAsync("new-password");

        Assert.False(fixture.FilesContain(Fixture.Password));
        Assert.False(fixture.FilesContain(Fixture.PrivateKey));
        Assert.False(fixture.FilesContain(Fixture.ProxyPassword));
        Assert.False(fixture.FilesContain("deleted-secret-marker-91d0f39d"));
        await fixture.AssertSecretsAsync(fixture.Vault);
    }

    [Fact]
    public async Task BusyCleanup_ReportsCommittedPassword_AndRestartCompletesCleanup()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SaveSecretsAsync();
        using SqliteConnection readerConnection = await fixture.Database.OpenAsync();
        using (SqliteTransaction readTransaction = readerConnection.BeginTransaction(deferred: true))
        {
            // 固定旧快照：允许 WAL 写入和密钥事务提交，但阻止历史帧截断。
            await readerConnection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM identity_secrets;", transaction: readTransaction);
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Vault.SetMasterPasswordAsync("new-password"));
            Assert.Contains("主密码已保存", error.Message);
            Assert.False(fixture.Vault.IsPlainMode);
            Assert.True(fixture.Vault.IsUnlocked);
            await fixture.AssertSecretsAsync(fixture.Vault);
            Assert.True(fixture.FilesContain(Fixture.Password));
        }

        fixture.Vault.Lock();
        InternalVaultManager restarted = new(fixture.Database);
        Assert.False(await restarted.TryAutoUnlockAsync());
        Assert.False(fixture.FilesContain(Fixture.Password));
        Assert.False(fixture.FilesContain(Fixture.PrivateKey));
        Assert.False(fixture.FilesContain(Fixture.ProxyPassword));
        await restarted.UnlockAsync("new-password", false);
        await fixture.AssertSecretsAsync(restarted);
    }

    [Fact]
    public async Task IdentityManager_CleanupFailureRefreshesEncryptedStateAndNotifiesUser()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SaveSecretsAsync();
        ScriptedInteraction interaction = new();
        IdentityManagerViewModel model = new(new SqliteIdentityRepository(fixture.Database), fixture.Vault, fixture.Vault)
        {
            Interaction = interaction,
            VaultSetupDialogAsync = () => Task.FromResult(new VaultSetupResult(VaultSetupChoice.SetMasterPassword, "new-password"))
        };
        await model.LoadAsync();
        Assert.True(model.IsPlainMode);
        bool plainModeNotified = false;
        model.PropertyChanged += (_, args) => plainModeNotified |= args.PropertyName == nameof(model.IsPlainMode);

        using SqliteConnection reader = await fixture.Database.OpenAsync();
        using (SqliteTransaction transaction = reader.BeginTransaction(deferred: true))
        {
            await reader.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM identity_secrets;", transaction: transaction);
            await model.SetMasterPasswordCommand.ExecuteAsync(null);
        }

        Assert.False(model.IsPlainMode);
        Assert.True(model.CanLockVault);
        Assert.True(plainModeNotified);
        Assert.Contains("主密码已保存", Assert.Single(interaction.Notifications).Message);
        fixture.Vault.Lock();
        await fixture.Vault.UnlockAsync("new-password", false);
        await fixture.AssertSecretsAsync(fixture.Vault);
    }

    [Fact]
    public async Task FailedIdentityRewrap_RollsBackPasswordAndOtherSecrets()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Vault.SetMasterPasswordAsync("first-password");
        await fixture.SaveSecretsAsync();
        using (SqliteConnection connection = await fixture.Database.OpenAsync())
        {
            await connection.ExecuteAsync("UPDATE identity_secrets SET secrets_blob = zeroblob(48);");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Vault.SetMasterPasswordAsync("second-password"));

        fixture.Vault.Lock();
        InternalVaultManager restarted = new(fixture.Database);
        await restarted.UnlockAsync("first-password", false);
        Assert.Equal(Fixture.ProxyPassword, await restarted.GetPasswordAsync(fixture.ProxyId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.UnlockAsync("second-password", false));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Password = "identity-password-marker-c0c57b72";
        public const string PrivateKey = "identity-private-key-marker-816f91f1";
        public const string ProxyPassword = "proxy-password-marker-51bd0947";
        public required string Directory { get; init; }
        public required SqliteConnectionFactory Database { get; init; }
        public required InternalVaultManager Vault { get; init; }
        public required Guid IdentityId { get; init; }
        public required Guid ProxyId { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "keiterm_vault_maintenance_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            SqliteConnectionFactory database = new($"Data Source={Path.Combine(directory, "vault.db")}");
            SqliteIdentityRepository identities = new(database);
            await identities.InitializeAsync();
            Identity identity = new() { Name = "identity" };
            await identities.SaveAsync(identity);
            ProxyProfile proxy = new() { Name = "proxy", Config = new Socks5ProxyConfig("localhost", 1080) };
            await new SqliteProxyRepository(database).SaveAsync(proxy);
            return new Fixture
            {
                Directory = directory, Database = database, Vault = new InternalVaultManager(database),
                IdentityId = identity.Id, ProxyId = proxy.Id
            };
        }

        public async Task SaveSecretsAsync()
        {
            await Vault.SaveSecretsAsync(IdentityId, new Dictionary<string, SecretPayload>
            {
                ["password"] = new() { Password = Password },
                ["private-key"] = new() { PrivateKeyContent = PrivateKey, Passphrase = "key-passphrase" }
            });
            await Vault.SetPasswordAsync(ProxyId, ProxyPassword);
        }

        public async Task AssertSecretsAsync(InternalVaultManager vault)
        {
            Dictionary<string, SecretPayload> secrets = await vault.GetSecretsAsync(IdentityId);
            Assert.Equal(Password, secrets["password"].Password);
            Assert.Equal(PrivateKey, secrets["private-key"].PrivateKeyContent);
            Assert.Equal("key-passphrase", secrets["private-key"].Passphrase);
            Assert.Equal(ProxyPassword, await vault.GetPasswordAsync(ProxyId));
        }

        public bool FilesContain(string marker)
            => System.IO.Directory.GetFiles(Directory).Any(path => Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(marker, StringComparison.Ordinal));

        public ValueTask DisposeAsync()
        {
            Vault.Lock();
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
