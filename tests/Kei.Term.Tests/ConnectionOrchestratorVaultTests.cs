using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Renci.SshNet.Common;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

public partial class ConnectionOrchestratorTests
{
    [Theory]
    [InlineData(PassphrasePersistence.AlwaysAsk)]
    [InlineData(PassphrasePersistence.SessionOnly)]
    [InlineData(PassphrasePersistence.Persistent)]
    public async Task FileKey_CancelMasterPassword_DoesNotPassConfiguredKeyToSsh(PassphrasePersistence mode)
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey: false, mode);

        await fixture.Orchestrator.ConnectAsync(new ConnectionRequest(fixture.Config, UseIdentity: true), _host);

        Assert.False(_factory.SessionCalls.Any(call => call.Materials.Any(m => m.Kind == AuthMaterialKind.PrivateKey)),
            "取消主密码后，仍把已配置的文件私钥交给了 SSH 工厂。");
        Assert.Empty(_factory.SessionCalls);
        Assert.Empty(_host.Tabs);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.False(fixture.Vault.IsUnlocked);
        Assert.Equal(1, _ui.MasterPasswordPrompts);
    }

    [Fact]
    public async Task VaultKey_CancelMasterPassword_DoesNotDialWithOtherAuthentication()
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey: true);

        await fixture.Orchestrator.ConnectAsync(new ConnectionRequest(fixture.Config, UseIdentity: true), _host);

        Assert.Empty(_factory.SessionCalls);
        Assert.Empty(_host.Tabs);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.False(fixture.Vault.IsUnlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyIdentity_CorrectMasterPassword_ConnectsUsingConfiguredKey(bool vaultKey)
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey);
        _ui.MasterPasswords.Enqueue("correct");

        await fixture.Orchestrator.ConnectAsync(new ConnectionRequest(fixture.Config, UseIdentity: true), _host);

        MaterializedAuthMethod material = Assert.Single(Assert.Single(_factory.SessionCalls).Materials);
        Assert.Equal(AuthMaterialKind.PrivateKey, material.Kind);
        Assert.Equal(PrivateKeyFormatTests.PlainOpenSsh, material.Secret!.PrivateKeyContent);
        Assert.True(Assert.Single(_host.Tabs).Connected);
        Assert.True(fixture.Vault.IsUnlocked);
        Assert.Empty(_ui.AuthPromptUsernames);
        await _factory.WaitForFileSystemAsync();
    }

    [Fact]
    public async Task FileKey_WrongMasterPasswordThenCancel_DoesNotDial()
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey: false);
        _ui.MasterPasswords.Enqueue("wrong");

        await fixture.Orchestrator.ConnectAsync(new ConnectionRequest(fixture.Config, UseIdentity: true), _host);

        Assert.Equal(2, _ui.MasterPasswordPrompts);
        Assert.False(fixture.Vault.IsUnlocked);
        Assert.Empty(_factory.SessionCalls);
        Assert.Empty(_host.Tabs);
    }

    [Fact]
    public async Task FileKey_ReconnectCancelled_DoesNotResetTabOrReusePreloadedKey()
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey: false);
        var existing = new FakeTarget();
        var preloaded = new MaterializedAuthMethod(AuthMaterialKind.PrivateKey,
            new SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh });

        await fixture.Orchestrator.ConnectAsync(
            new ConnectionRequest(fixture.Config, UseIdentity: true, preloaded, existing), _host);

        Assert.Empty(existing.Resets);
        Assert.Null(existing.AttachedSession);
        Assert.Empty(_factory.SessionCalls);
        Assert.Empty(_host.Tabs);
    }

    [Fact]
    public async Task JumpFileKey_CancelMasterPassword_DoesNotDialAnyHop()
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey: false);
        var jump = new SessionNode
        {
            Name = "key-only jump", Host = "jump.example", Username = "ops", IdentityId = fixture.Identity.Id
        };
        _host.Sessions[jump.Id] = jump;

        await fixture.Orchestrator.ConnectAsync(
            new ConnectionRequest(Config(jump: jump.Id), UseIdentity: false, Password("target password")), _host);

        Assert.Equal(1, _ui.MasterPasswordPrompts);
        Assert.Empty(_factory.SessionCalls);
        Assert.Empty(_host.Tabs);
        Assert.Empty(_ui.AuthPromptUsernames);
    }

    [Fact]
    public async Task FileKey_MissingConfiguredFile_DoesNotFallBackToPassword()
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey: false);
        File.Delete(fixture.KeyPath);
        _ui.MasterPasswords.Enqueue("correct");

        await fixture.Orchestrator.ConnectAsync(new ConnectionRequest(fixture.Config, UseIdentity: true), _host);

        Assert.Empty(_factory.SessionCalls);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.Empty(_host.Tabs);
        Assert.Single(_ui.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyOnlyIdentity_AuthenticationRejected_DoesNotPromptForOtherMethods(bool vaultKey)
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey);
        _ui.MasterPasswords.Enqueue("correct");
        _factory.Outcomes.Enqueue(new SshAuthenticationException("key rejected"));
        // 即使预置了密码回答，也不能把它当作未配置的回退方式使用。
        _ui.AuthResults.Enqueue(PasswordPrompt("unconfigured password"));

        await fixture.Orchestrator.ConnectAsync(new ConnectionRequest(fixture.Config, UseIdentity: true), _host);

        Assert.Single(_factory.SessionCalls);
        Assert.Empty(_ui.AuthPromptUsernames);
        Assert.Equal("key rejected", Assert.Single(_host.Tabs).Error);
        Assert.False(_host.Tabs[0].Connected);
    }

    [Fact]
    public async Task VaultUnlockCancelledDuringRetry_DoesNotReuseOriginalMaterials()
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey: true);
        fixture.Identity.Methods.Add(new VaultPasswordMethod());
        await new SqliteIdentityRepository($"Data Source={fixture.DbPath}").SaveAsync(fixture.Identity);
        _ui.MasterPasswords.Enqueue("correct");
        _factory.Outcomes.Enqueue(new SshAuthenticationException("key rejected"));
        _factory.OnCreate = fixture.Session.Lock;
        _ui.AuthResults.Enqueue(new AuthPromptResult(AuthPromptMethod.PublicKeyVault, "ops", null, null, null,
            ((VaultPrivateKeyMethod)fixture.Identity.Methods[0]).SecretId));

        await fixture.Orchestrator.ConnectAsync(new ConnectionRequest(fixture.Config, UseIdentity: true), _host);

        Assert.Equal(2, _ui.MasterPasswordPrompts);
        Assert.False(fixture.Vault.IsUnlocked);
        Assert.Single(_factory.SessionCalls);
        Assert.Equal("key rejected", Assert.Single(_host.Tabs).Error);
        Assert.False(_host.Tabs[0].Connected);
    }

    [Fact]
    public async Task SharedVaultPrivateKey_BothIdentitiesMaterializeSameKey_AndRebindingOneLeavesOtherIntact()
    {
        string dbPath = VaultTestDb.NewPath();
        string connectionString = $"Data Source={dbPath}";
        try
        {
            Guid firstId = Guid.NewGuid();
            Guid secondId = Guid.NewGuid();
            Guid sharedSecretId = Guid.NewGuid();
            await VaultTestDb.CreateAsync(connectionString, firstId);
            var identities = new SqliteIdentityRepository(connectionString);
            await identities.SaveAsync(new Identity { Id = secondId, Name = "second" });

            var first = new Identity
            {
                Id = firstId,
                Name = "first",
                Username = "ops",
                Methods = [new VaultPrivateKeyMethod { SecretId = sharedSecretId }],
            };
            var second = new Identity
            {
                Id = secondId,
                Name = "second",
                Username = "ops",
                Methods = [new VaultPrivateKeyMethod { SecretId = sharedSecretId }],
            };
            await identities.SaveAsync(first);
            await identities.SaveAsync(second);

            var vault = VaultTestDb.CreateVault(connectionString);
            await vault.SetMasterPasswordAsync("correct");
            await vault.SaveAsync(
                new VaultSecret { Id = sharedSecretId, Name = "shared", Kind = SecretKind.SshPrivateKey },
                new SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh });
            vault.Lock();

            var session = new VaultSessionService(vault, vault, _settings, () => _ui);
            var collector = new AuthMaterialCollector(identities, _settings, session, () => _ui);
            var orchestrator = new ConnectionOrchestrator(_factory, collector, null, _settings, () => _ui, action => action());

            // 两个身份共享同一条目：都物化出相同私钥
            _ui.MasterPasswords.Enqueue("correct");
            await orchestrator.ConnectAsync(new ConnectionRequest(Config() with { IdentityId = firstId }, UseIdentity: true), _host);
            MaterializedAuthMethod firstMaterial = Assert.Single(Assert.Single(_factory.SessionCalls).Materials);
            Assert.Equal(PrivateKeyFormatTests.PlainOpenSsh, firstMaterial.Secret!.PrivateKeyContent);

            await orchestrator.ConnectAsync(new ConnectionRequest(Config() with { IdentityId = secondId }, UseIdentity: true), _host);
            MaterializedAuthMethod secondMaterial = Assert.Single(Assert.Single(_factory.SessionCalls.Skip(1)).Materials);
            Assert.Equal(firstMaterial.Secret!.PrivateKeyContent, secondMaterial.Secret!.PrivateKeyContent);

            // 编辑第一个身份改绑到另一条目，第二个身份的引用与材料不受影响
            Guid otherSecretId = Guid.NewGuid();
            await vault.SaveAsync(
                new VaultSecret { Id = otherSecretId, Name = "other", Kind = SecretKind.SshPrivateKey },
                new SecretPayload { PrivateKeyContent = "OTHER-KEY" });
            first.Methods = [new VaultPrivateKeyMethod { Id = first.Methods[0].Id, SecretId = otherSecretId }];
            await identities.SaveAsync(first);

            await orchestrator.ConnectAsync(new ConnectionRequest(Config() with { IdentityId = secondId }, UseIdentity: true), _host);
            MaterializedAuthMethod thirdMaterial = Assert.Single(Assert.Single(_factory.SessionCalls.Skip(2)).Materials);
            Assert.Equal(PrivateKeyFormatTests.PlainOpenSsh, thirdMaterial.Secret!.PrivateKeyContent);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(dbPath + suffix);
            }
        }
    }

    // 文件私钥持久化口令：已存在条目（含迁移产物）只更新载荷，保留其名称与类型
    [Fact]
    public async Task PersistedPassphrase_DoesNotOverwriteExistingEntryNameOrKind()
    {
        string dbPath = VaultTestDb.NewPath();
        string connectionString = $"Data Source={dbPath}";
        string? keyPath = null;
        try
        {
            Guid identityId = Guid.NewGuid();
            await VaultTestDb.CreateAsync(connectionString, identityId);
            FileKeyFixture fixture = await CreateLockedFileKeyAsync(connectionString, identityId, existingEntryName: "自定义名称");
            keyPath = fixture.KeyPath;
            var collector = new AuthMaterialCollector(fixture.Identities, _settings, fixture.Session, () => _ui);
            _ui.MasterPasswords.Enqueue("correct");
            _ui.Passphrases.Enqueue(new PassphrasePromptResult("pp", true));

            CollectedAuth? collected = await collector.CollectTargetAsync(
                new ConnectionRequest(Config() with { IdentityId = identityId }, UseIdentity: true));

            Assert.NotNull(collected);
            Guid entryId = ((FilePrivateKeyMethod)fixture.Identity.Methods[0]).Id;
            VaultSecret entry = Assert.Single(await fixture.Vault.ListAsync());
            Assert.Equal(entryId, entry.Id);
            Assert.Equal("自定义名称", entry.Name);
            Assert.Equal(SecretKind.Passphrase, entry.Kind);
            Assert.Equal("pp", (await fixture.Vault.GetPayloadAsync(entryId))!.Passphrase);
        }
        finally
        {
            CleanupDb(dbPath, keyPath);
        }
    }

    // 文件私钥持久化口令：无既有条目时按命名规则新建 Passphrase 条目
    [Fact]
    public async Task PersistedPassphrase_CreatesNewPassphraseEntry()
    {
        string dbPath = VaultTestDb.NewPath();
        string connectionString = $"Data Source={dbPath}";
        string? keyPath = null;
        try
        {
            Guid identityId = Guid.NewGuid();
            await VaultTestDb.CreateAsync(connectionString, identityId);
            FileKeyFixture fixture = await CreateLockedFileKeyAsync(connectionString, identityId, existingEntryName: null);
            keyPath = fixture.KeyPath;
            var collector = new AuthMaterialCollector(fixture.Identities, _settings, fixture.Session, () => _ui);
            _ui.MasterPasswords.Enqueue("correct");
            _ui.Passphrases.Enqueue(new PassphrasePromptResult("pp", true));

            Assert.NotNull(await collector.CollectTargetAsync(
                new ConnectionRequest(Config() with { IdentityId = identityId }, UseIdentity: true)));

            Guid entryId = ((FilePrivateKeyMethod)fixture.Identity.Methods[0]).Id;
            VaultSecret entry = Assert.Single(await fixture.Vault.ListAsync());
            Assert.Equal(entryId, entry.Id);
            Assert.Equal(SecretKind.Passphrase, entry.Kind);
            Assert.Contains("FileKey", entry.Name);
            Assert.Equal("pp", (await fixture.Vault.GetPayloadAsync(entryId))!.Passphrase);
        }
        finally
        {
            CleanupDb(dbPath, keyPath);
        }
    }

    // 持久化口令回写时若解锁被取消：中止本次材料收集，不得静默继续
    [Fact]
    public async Task PersistedPassphrase_WhenSaveUnlockCancelled_ThrowsInsteadOfContinuing()
    {
        string dbPath = VaultTestDb.NewPath();
        string connectionString = $"Data Source={dbPath}";
        string? keyPath = null;
        try
        {
            Guid identityId = Guid.NewGuid();
            await VaultTestDb.CreateAsync(connectionString, identityId);
            FileKeyFixture fixture = await CreateLockedFileKeyAsync(connectionString, identityId, existingEntryName: null);
            keyPath = fixture.KeyPath;
            var collector = new AuthMaterialCollector(fixture.Identities, _settings, fixture.Session, () => _ui);
            _ui.MasterPasswords.Enqueue("correct");
            _ui.Passphrases.Enqueue(new PassphrasePromptResult("pp", true));
            // 弹出口令后锁定 Vault：回写口令时再次需要解锁且用户取消
            _ui.PassphrasePromptHook = fixture.Session.Lock;

            await Assert.ThrowsAsync<OperationCanceledException>(() => collector.CollectTargetAsync(
                new ConnectionRequest(Config() with { IdentityId = identityId }, UseIdentity: true)));
            Assert.Empty(await fixture.Vault.ListAsync());
        }
        finally
        {
            CleanupDb(dbPath, keyPath);
        }
    }

    private async Task<FileKeyFixture> CreateLockedFileKeyAsync(string connectionString, Guid identityId, string? existingEntryName)
    {
        string keyPath = Path.Combine(Path.GetTempPath(), $"keiterm_test_key_{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(keyPath, PrivateKeyFormatTests.EncryptedOpenSsh);
        var method = new FilePrivateKeyMethod { KeyFilePath = keyPath, PassphraseMode = PassphrasePersistence.Persistent };
        var identity = new Identity { Id = identityId, Name = "FileKey", Username = "ops", Methods = [method] };
        var identities = new SqliteIdentityRepository(connectionString);
        await identities.SaveAsync(identity);

        var vault = VaultTestDb.CreateVault(connectionString);
        await vault.SetMasterPasswordAsync("correct");
        if (existingEntryName != null)
        {
            // 条目 Id = 方法 Id，载荷不含口令 => 持久化查询未命中，走弹窗后回写
            await vault.SaveAsync(
                new VaultSecret { Id = method.Id, Name = existingEntryName, Kind = SecretKind.Passphrase },
                new SecretPayload());
        }

        vault.Lock();
        var session = new VaultSessionService(vault, vault, _settings, () => _ui);
        return new FileKeyFixture(vault, session, identities, identity, keyPath);
    }

    private static void CleanupDb(string dbPath, string? keyPath)
    {
        SqliteConnection.ClearAllPools();
        if (keyPath != null)
        {
            File.Delete(keyPath);
        }

        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(dbPath + suffix);
        }
    }

    private sealed record FileKeyFixture(
        InternalVaultManager Vault,
        VaultSessionService Session,
        SqliteIdentityRepository Identities,
        Identity Identity,
        string KeyPath);

    private async Task<KeyFixture> CreateLockedKeyAsync(bool vaultKey, PassphrasePersistence mode = PassphrasePersistence.AlwaysAsk)
    {
        string dbPath = VaultTestDb.NewPath();
        string keyPath = Path.Combine(Path.GetTempPath(), $"keiterm_test_key_{Guid.NewGuid():N}");
        string connectionString = $"Data Source={dbPath}";
        Guid identityId = Guid.NewGuid();
        // 仅复用公开的非敏感测试密钥，绝不读取用户真实凭据。
        await File.WriteAllTextAsync(keyPath, PrivateKeyFormatTests.PlainOpenSsh);
        await VaultTestDb.CreateAsync(connectionString, identityId);
        Guid secretId = Guid.NewGuid();
        AuthMethodEntry method = vaultKey
            ? new VaultPrivateKeyMethod { SecretId = secretId }
            : new FilePrivateKeyMethod { KeyFilePath = keyPath, PassphraseMode = mode };
        var identity = new Identity { Id = identityId, Name = "Key only", Username = "ops", Methods = [method] };
        var identities = new SqliteIdentityRepository(connectionString);
        await identities.SaveAsync(identity);
        var vault = VaultTestDb.CreateVault(connectionString);
        await vault.SetMasterPasswordAsync("correct");
        if (vaultKey)
        {
            await vault.SaveAsync(
                new VaultSecret { Id = secretId, Name = "key", Kind = SecretKind.SshPrivateKey },
                new SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh });
        }
        vault.Lock();
        var session = new VaultSessionService(vault, vault, _settings, () => _ui);
        var collector = new AuthMaterialCollector(identities, _settings, session, () => _ui);
        var orchestrator = new ConnectionOrchestrator(_factory, collector, null, _settings, () => _ui, action => action());
        return new KeyFixture(dbPath, keyPath, vault, session, identity,
            Config() with { IdentityId = identityId }, orchestrator);
    }

    private sealed record KeyFixture(
        string DbPath,
        string KeyPath,
        InternalVaultManager Vault,
        VaultSessionService Session,
        Identity Identity,
        ResolvedSessionConfig Config,
        ConnectionOrchestrator Orchestrator) : IDisposable
    {
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(KeyPath);
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(DbPath + suffix);
            }
        }
    }
}
