using System;
using System.IO;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

public class VaultSessionServiceTests : IDisposable
{
    private readonly string _dbPath = VaultTestDb.NewPath();
    private readonly Guid _identityId = Guid.NewGuid();
    private readonly ScriptedInteraction _ui = new();
    private readonly FixedSettingsService _settings = new(new AppSettings { LockTimeoutMinutes = 5 });

    private string ConnStr => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    // 已设主密码并锁定的加密 Vault
    private async Task<(InternalVaultManager Vault, VaultSessionService Session)> CreateLockedAsync()
    {
        await VaultTestDb.CreateAsync(ConnStr, _identityId);
        var vault = VaultTestDb.CreateVault(ConnStr);
        await vault.SetMasterPasswordAsync("correct");
        vault.Lock();
        return (vault, new VaultSessionService(vault, vault, _settings, () => _ui));
    }

    [Fact]
    public async Task EnsureUnlocked_RepromptsOnWrongPassword_UntilCorrect()
    {
        var (vault, session) = await CreateLockedAsync();
        _ui.MasterPasswords.Enqueue("wrong");
        _ui.MasterPasswords.Enqueue("correct");

        Assert.True(await session.EnsureUnlockedAsync());
        Assert.Equal(2, _ui.MasterPasswordPrompts);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task LoadSecrets_WhenUserCancelsUnlock_CancelsAndStaysLocked()
    {
        var (vault, session) = await CreateLockedAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => session.LoadIdentitySecretsAsync(_identityId));
        Assert.False(vault.IsUnlocked);
        Assert.Equal(1, _ui.MasterPasswordPrompts);
    }

    [Fact]
    public async Task PersistSecret_WhenUnlockCancelled_CancelsWithoutWriting()
    {
        var (vault, session) = await CreateLockedAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            session.PersistSecretAsync(_identityId, Guid.NewGuid(), new SecretPayload { Passphrase = "test" }));

        Assert.False(vault.IsUnlocked);
        await vault.UnlockAsync("correct", rememberOnThisDevice: false);
        Assert.Empty(await vault.GetSecretsAsync(_identityId));
    }

    [Fact]
    public async Task KeyInfoPreview_WhenUnlockCancelled_ReturnsNoPreviewAndStaysLocked()
    {
        var (vault, session) = await CreateLockedAsync();

        Assert.Null(await session.GetVaultKeyInfoAsync(_identityId, Guid.NewGuid()));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task PersistSecret_MergesIntoExistingIdentityBundle()
    {
        var (_, session) = await CreateLockedAsync();
        _ui.MasterPasswords.Enqueue("correct");
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        await session.PersistSecretAsync(_identityId, first, new SecretPayload { Password = "p1" });
        await session.PersistSecretAsync(_identityId, second, new SecretPayload { Passphrase = "k2" });

        var secrets = await session.LoadIdentitySecretsAsync(_identityId);
        Assert.Equal("p1", secrets[first.ToString()].Password);
        Assert.Equal("k2", secrets[second.ToString()].Passphrase);
        // 解锁一次后整个会话内不再追问主密码
        Assert.Equal(1, _ui.MasterPasswordPrompts);
    }

    [Theory]
    [InlineData(PassphrasePersistence.SessionOnly, true, true)]
    [InlineData(PassphrasePersistence.SessionOnly, false, false)]
    [InlineData(PassphrasePersistence.AlwaysAsk, true, false)]
    public async Task PromptPassphrase_CachesOnlySessionOnlyRemembered(PassphrasePersistence mode, bool remember, bool cached)
    {
        var (_, session) = await CreateLockedAsync();
        var method = new FilePrivateKeyMethod { PassphraseMode = mode };
        _ui.Passphrases.Enqueue(new PassphrasePromptResult("secret", remember));

        await session.PromptPassphraseAsync(method);

        Assert.Equal(cached ? "secret" : null, session.GetSessionPassphrase(method.Id));
    }

    [Fact]
    public async Task AutoLock_OnlyAfterIdleThreshold_AndClearsPassphraseCache()
    {
        var (vault, session) = await CreateLockedAsync();
        _ui.MasterPasswords.Enqueue("correct");
        await session.EnsureUnlockedAsync();
        var method = new FilePrivateKeyMethod { PassphraseMode = PassphrasePersistence.SessionOnly };
        _ui.Passphrases.Enqueue(new PassphrasePromptResult("secret", true));
        await session.PromptPassphraseAsync(method);

        DateTime start = session.LastAccessUtc;
        Assert.False(session.AutoLockIfIdle(start.AddMinutes(4)));
        Assert.True(vault.IsUnlocked);

        Assert.True(session.AutoLockIfIdle(start.AddMinutes(5)));
        Assert.False(vault.IsUnlocked);
        Assert.Null(session.GetSessionPassphrase(method.Id));
    }

    [Fact]
    public async Task AutoLock_DisabledWhenTimeoutIsZero()
    {
        var (_, session) = await CreateLockedAsync();
        _ui.MasterPasswords.Enqueue("correct");
        await session.EnsureUnlockedAsync();
        _settings.Current.LockTimeoutMinutes = 0;

        Assert.False(session.AutoLockIfIdle(DateTime.UtcNow.AddDays(1)));
    }
}
