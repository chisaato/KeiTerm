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
    public async Task FileKey_RepeatedCancelledAttempts_DoNotReusePreviouslyCollectedKey()
    {
        using KeyFixture fixture = await CreateLockedKeyAsync(vaultKey: false);
        ConnectionRequest request = new(fixture.Config, UseIdentity: true);

        await fixture.Orchestrator.ConnectAsync(request, _host);
        await fixture.Orchestrator.ConnectAsync(request, _host);

        Assert.Equal(2, _ui.MasterPasswordPrompts);
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
            fixture.Identity.Methods[0].Id));

        await fixture.Orchestrator.ConnectAsync(new ConnectionRequest(fixture.Config, UseIdentity: true), _host);

        Assert.Equal(2, _ui.MasterPasswordPrompts);
        Assert.False(fixture.Vault.IsUnlocked);
        Assert.Single(_factory.SessionCalls);
        Assert.Equal("key rejected", Assert.Single(_host.Tabs).Error);
        Assert.False(_host.Tabs[0].Connected);
    }

    private async Task<KeyFixture> CreateLockedKeyAsync(bool vaultKey, PassphrasePersistence mode = PassphrasePersistence.AlwaysAsk)
    {
        string dbPath = VaultTestDb.NewPath();
        string keyPath = Path.Combine(Path.GetTempPath(), $"keiterm_test_key_{Guid.NewGuid():N}");
        string connectionString = $"Data Source={dbPath}";
        Guid identityId = Guid.NewGuid();
        // 仅复用公开的非敏感测试密钥，绝不读取用户真实凭据。
        await File.WriteAllTextAsync(keyPath, PrivateKeyFormatTests.PlainOpenSsh);
        await VaultTestDb.CreateAsync(connectionString, identityId);
        AuthMethodEntry method = vaultKey
            ? new VaultPrivateKeyMethod()
            : new FilePrivateKeyMethod { KeyFilePath = keyPath, PassphraseMode = mode };
        var identity = new Identity { Id = identityId, Name = "Key only", Username = "ops", Methods = [method] };
        var identities = new SqliteIdentityRepository(connectionString);
        await identities.SaveAsync(identity);
        var vault = new InternalVaultManager(connectionString);
        await vault.SetMasterPasswordAsync("correct");
        if (vaultKey)
        {
            await vault.SaveSecretsAsync(identityId, new Dictionary<string, SecretPayload>
            {
                [method.Id.ToString()] = new() { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh }
            });
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
