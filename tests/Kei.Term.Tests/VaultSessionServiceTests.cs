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
    public async Task GetPayload_WhenUserCancelsUnlock_CancelsAndStaysLocked()
    {
        var (vault, session) = await CreateLockedAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => session.GetSecretPayloadAsync(Guid.NewGuid()));
        Assert.False(vault.IsUnlocked);
        Assert.Equal(1, _ui.MasterPasswordPrompts);
    }

    [Fact]
    public async Task SaveLibrarySecret_WhenUnlockCancelled_ReturnsFalseWithoutWriting()
    {
        var (vault, session) = await CreateLockedAsync();
        Guid secretId = Guid.NewGuid();

        bool saved = await session.SaveLibrarySecretAsync(
            new VaultSecret { Id = secretId, Name = "pw", Kind = SecretKind.Password },
            new SecretPayload { Password = "test" });

        Assert.False(saved);
        Assert.False(vault.IsUnlocked);
        await vault.UnlockAsync("correct", rememberOnThisDevice: false);
        Assert.Null(await vault.GetPayloadAsync(secretId));
    }

    [Fact]
    public async Task ListLibrarySecrets_WhileLocked_DoesNotPromptNorUnlock()
    {
        var (vault, session) = await CreateLockedAsync();

        Assert.Empty(await session.ListLibrarySecretsAsync());
        Assert.False(vault.IsUnlocked);
        Assert.Equal(0, _ui.MasterPasswordPrompts);
    }

    [Fact]
    public async Task SaveAndGetLibrarySecret_KeepsEntriesSeparate_AndUnlocksOnce()
    {
        var (_, session) = await CreateLockedAsync();
        _ui.MasterPasswords.Enqueue("correct");
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        Assert.True(await session.SaveLibrarySecretAsync(
            new VaultSecret { Id = first, Name = "pw", Kind = SecretKind.Password },
            new SecretPayload { Password = "p1" }));
        Assert.True(await session.SaveLibrarySecretAsync(
            new VaultSecret { Id = second, Name = "pp", Kind = SecretKind.Passphrase },
            new SecretPayload { Passphrase = "k2" }));

        Assert.Equal("p1", (await session.GetSecretPayloadAsync(first))!.Password);
        Assert.Equal("k2", (await session.GetSecretPayloadAsync(second))!.Passphrase);
        // 解锁一次后整个会话内不再追问主密码
        Assert.Equal(1, _ui.MasterPasswordPrompts);
    }

    [Fact]
    public async Task SavePrivateKeySecret_ComputesFingerprintAndKeyType()
    {
        var (_, session) = await CreateLockedAsync();
        _ui.MasterPasswords.Enqueue("correct");
        Guid secretId = Guid.NewGuid();

        Assert.True(await session.SaveLibrarySecretAsync(
            new VaultSecret { Id = secretId, Name = "key", Kind = SecretKind.SshPrivateKey },
            new SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh }));

        VaultSecret saved = Assert.Single(await session.ListLibrarySecretsAsync());
        Assert.False(string.IsNullOrEmpty(saved.Fingerprint));
        Assert.Equal("ssh-ed25519", saved.KeyType);
    }

    [Fact]
    public async Task DeleteLibrarySecret_WhenUnlockCancelled_ReturnsFalse()
    {
        var (vault, session) = await CreateLockedAsync();

        Assert.False(await session.DeleteLibrarySecretAsync(Guid.NewGuid()));
        Assert.False(vault.IsUnlocked);
    }

    // 惰性补算的并发最小缓解：只写元数据、且基于库内最新条目，不得用旧快照覆盖并发的改名或重写载荷
    [Fact]
    public async Task Backfill_StaleSnapshot_DoesNotClobberRenamedEntryOrPayload()
    {
        await VaultTestDb.CreateAsync(ConnStr, _identityId);
        var vault = VaultTestDb.CreateVault(ConnStr);
        await vault.SetMasterPasswordAsync("correct");
        var session = new VaultSessionService(vault, vault, _settings, () => _ui);
        Guid secretId = Guid.NewGuid();
        // 迁移产出式旧条目：只落载荷、无指纹
        await vault.SaveAsync(
            new VaultSecret { Id = secretId, Name = "旧名", Kind = SecretKind.SshPrivateKey },
            new SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh });

        // 调用方捕获的快照（名称=旧名、无指纹）——模拟 LoadAsync 列出的旧数据
        IReadOnlyList<VaultSecret> snapshot = await session.ListLibrarySecretsAsync();
        // 并发：补算前先改名（只改元数据），并取加密基线
        Assert.True(await session.SaveLibrarySecretAsync(
            new VaultSecret { Id = secretId, Name = "新名", Kind = SecretKind.SshPrivateKey }, null));
        var before = await VaultTestDb.ReadVaultSecretRowAsync(ConnStr, secretId);

        await session.BackfillLibraryMetadataAsync(snapshot);

        VaultSecret afterMeta = Assert.Single(await session.ListLibrarySecretsAsync());
        Assert.Equal("新名", afterMeta.Name);
        Assert.False(string.IsNullOrEmpty(afterMeta.Fingerprint));
        Assert.Equal("ssh-ed25519", afterMeta.KeyType);

        // 载荷密文未被补算重写（仍复用改名时的封套）
        var after = await VaultTestDb.ReadVaultSecretRowAsync(ConnStr, secretId);
        Assert.Equal(before.blob, after.blob);
        Assert.Equal(before.nonce, after.nonce);
        Assert.Equal(before.tag, after.tag);
        Assert.Equal(PrivateKeyFormatTests.PlainOpenSsh, (await vault.GetPayloadAsync(secretId))!.PrivateKeyContent);
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

    [Fact]
    public async Task CreateLibrarySecretFromDraft_PasswordDraft_PersistsPasswordEntry()
    {
        var (vault, session) = await CreateLockedAsync();
        _ui.MasterPasswords.Enqueue("correct");
        var draft = new VaultSecretDraft("测试密码材料", false, null, null, "pass123");

        var secret = await session.CreateLibrarySecretFromDraftAsync(draft);

        Assert.NotNull(secret);
        Assert.Equal("测试密码材料", secret.Name);
        Assert.Equal(SecretKind.Password, secret.Kind);

        var list = await session.ListLibrarySecretsAsync();
        Assert.Contains(list, s => s.Id == secret.Id && s.Name == "测试密码材料");
        var payload = await vault.GetPayloadAsync(secret.Id);
        Assert.Equal("pass123", payload?.Password);
    }

    [Fact]
    public async Task CreateLibrarySecretFromDraft_PrivateKeyDraft_ComputesFingerprintAndPersists()
    {
        var (vault, session) = await CreateLockedAsync();
        _ui.MasterPasswords.Enqueue("correct");
        var draft = new VaultSecretDraft("测试私钥材料", true, PrivateKeyFormatTests.PlainOpenSsh, null, null);

        var secret = await session.CreateLibrarySecretFromDraftAsync(draft);

        Assert.NotNull(secret);
        Assert.Equal("测试私钥材料", secret.Name);
        Assert.Equal(SecretKind.SshPrivateKey, secret.Kind);

        var list = await session.ListLibrarySecretsAsync();
        var saved = Assert.Single(list, s => s.Id == secret.Id);
        Assert.False(string.IsNullOrEmpty(saved.Fingerprint));
        Assert.Equal("ssh-ed25519", saved.KeyType);
    }
}
