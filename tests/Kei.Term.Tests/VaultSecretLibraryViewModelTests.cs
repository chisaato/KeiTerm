using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kei.Term.Tests;

// 材料库窗口 ViewModel：列表元数据（含未解锁）、删除引用保护、无引用筛选、惰性指纹补算。
// 用真实 InternalVaultManager + SqliteIdentityRepository，headless 派发到 UI 会话执行。
public class VaultSecretLibraryViewModelTests : IDisposable
{
    private readonly string _dbPath = VaultTestDb.NewPath();
    private readonly ScriptedInteraction _ui = new();
    private readonly FixedSettingsService _settings = new();
    private InternalVaultManager _vault = null!;
    private SqliteIdentityRepository _identities = null!;
    private VaultSessionService _session = null!;
    private VaultSecretLibraryViewModel _vm = null!;

    private string ConnStr => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private async Task<VaultSecretLibraryViewModel> CreateAsync()
    {
        await VaultTestDb.CreateAsync(ConnStr, Guid.NewGuid());
        _vault = VaultTestDb.CreateVault(ConnStr);
        _identities = new SqliteIdentityRepository(ConnStr);
        _session = new VaultSessionService(_vault, _vault, _settings, () => _ui);
        _vm = new VaultSecretLibraryViewModel(_session, _identities, _ui);
        return _vm;
    }

    private Task SaveReferencedByIdentityAsync(Guid secretId, string identityName, string kind)
    {
        AuthMethodEntry method = kind == SecretKind.SshPrivateKey
            ? new VaultPrivateKeyMethod { SecretId = secretId }
            : new VaultPasswordMethod { SecretId = secretId };
        return _identities.SaveAsync(new Identity { Id = Guid.NewGuid(), Name = identityName, Methods = [method] });
    }

    [Fact]
    public Task Delete_ReferencedSecret_IsRejectedWithIdentityNames() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        Guid secretId = Guid.NewGuid();
        await _vault.SaveAsync(
            new VaultSecret { Id = secretId, Name = "共享私钥", Kind = SecretKind.SshPrivateKey },
            new SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh });
        await SaveReferencedByIdentityAsync(secretId, "身份甲", SecretKind.SshPrivateKey);
        await SaveReferencedByIdentityAsync(secretId, "身份乙", SecretKind.Password);

        await vm.LoadAsync();
        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        Assert.Equal(2, item.ReferenceCount);

        await vm.DeleteAsync(item);

        // 条目仍在：引用保护拒绝了删除，通知里列出引用它的身份名
        Assert.Single(vm.Items);
        Assert.NotNull(await _vault.GetPayloadAsync(secretId));
        (string title, string message) = Assert.Single(_ui.Notifications);
        Assert.Contains("身份甲", message);
        Assert.Contains("身份乙", message);
        Assert.NotEqual(string.Empty, title);
    });

    // RF2 补充：文件私钥持久化口令条目（Kind=passphrase、Id=方法 Id）被引用时同样拒绝删除
    [Fact]
    public Task Delete_ReferencedFileKeyPassphrase_IsRejected() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        Guid methodId = Guid.NewGuid();
        await _vault.SaveAsync(
            new VaultSecret { Id = methodId, Name = "文件私钥口令", Kind = SecretKind.Passphrase },
            new SecretPayload { Passphrase = "pp" });
        await _identities.SaveAsync(new Identity
        {
            Id = Guid.NewGuid(),
            Name = "持钥身份",
            Methods = [new FilePrivateKeyMethod { Id = methodId, PassphraseMode = PassphrasePersistence.Persistent }],
        });

        await vm.LoadAsync();
        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        Assert.Equal(1, item.ReferenceCount);
        Assert.Equal(SecretKind.Passphrase, item.Secret.Kind);

        await vm.DeleteAsync(item);

        // 文件私钥口令条目按方法 Id 被引用：删除被拒绝、条目保留、通知列出引用身份
        Assert.Single(vm.Items);
        Assert.NotNull(await _vault.GetPayloadAsync(methodId));
        (_, string message) = Assert.Single(_ui.Notifications);
        Assert.Contains("持钥身份", message);
    });

    [Fact]
    public Task Delete_UnreferencedSecret_RemovesEntry() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        Guid secretId = Guid.NewGuid();
        await _vault.SaveAsync(
            new VaultSecret { Id = secretId, Name = "孤立密码", Kind = SecretKind.Password },
            new SecretPayload { Password = "p" });

        await vm.LoadAsync();
        await vm.DeleteAsync(Assert.Single(vm.Items));

        Assert.Empty(vm.Items);
        Assert.Null(await _vault.GetPayloadAsync(secretId));
    });

    // I3：删除确认后等待解锁期间新增引用——解锁完成后需重新扫描引用并拒绝删除
    [Fact]
    public Task Delete_WhenReferenceAddedDuringUnlock_IsRejected() => HeadlessAvalonia.RunAsync(async () =>
    {
        await VaultTestDb.CreateAsync(ConnStr, Guid.NewGuid());
        _vault = VaultTestDb.CreateVault(ConnStr);
        await _vault.SetMasterPasswordAsync("pw");
        _identities = new SqliteIdentityRepository(ConnStr);
        _session = new VaultSessionService(_vault, _vault, _settings, () => _ui);
        _vm = new VaultSecretLibraryViewModel(_session, _identities, _ui);

        Guid secretId = Guid.NewGuid();
        await _vault.SaveAsync(
            new VaultSecret { Id = secretId, Name = "待删密码", Kind = SecretKind.Password },
            new SecretPayload { Password = "p" });
        _vault.Lock();

        // 删除开始时无引用；主密码框等待输入期间，另一路径新增了对该材料的引用
        _ui.VaultPromptHandler = async _ =>
        {
            await SaveReferencedByIdentityAsync(secretId, "新引用身份", SecretKind.Password);
            return new VaultUnlockResponse(VaultUnlockKind.Password, "pw");
        };

        await _vm.LoadAsync();
        VaultSecretListItemViewModel item = Assert.Single(_vm.Items);
        Assert.Equal(0, item.ReferenceCount);

        await _vm.DeleteAsync(item);

        // 解锁成功后二次扫描发现引用：删除被拒绝，条目与载荷保留
        Assert.True(_vault.IsUnlocked);
        Assert.Single(_vm.Items);
        Assert.NotNull(await _vault.GetPayloadAsync(secretId));
        (_, string message) = Assert.Single(_ui.Notifications);
        Assert.Contains("新引用身份", message);
    });

    [Fact]
    public Task UnreferencedFilter_ShowsOnlyUnreferenced() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        Guid referenced = Guid.NewGuid();
        Guid free = Guid.NewGuid();
        await _vault.SaveAsync(
            new VaultSecret { Id = referenced, Name = "被引用", Kind = SecretKind.Password },
            new SecretPayload { Password = "a" });
        await _vault.SaveAsync(
            new VaultSecret { Id = free, Name = "无引用", Kind = SecretKind.Password },
            new SecretPayload { Password = "b" });
        await SaveReferencedByIdentityAsync(referenced, "身份甲", SecretKind.Password);

        await vm.LoadAsync();
        Assert.Equal(2, vm.Items.Count);

        vm.UnreferencedOnly = true;
        VaultSecretListItemViewModel only = Assert.Single(vm.Items);
        Assert.Equal(free, only.Id);

        vm.UnreferencedOnly = false;
        Assert.Equal(2, vm.Items.Count);
    });

    [Fact]
    public Task List_WhenLocked_StillShowsMetadata() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        await _vault.SetMasterPasswordAsync("pw");
        Guid secretId = Guid.NewGuid();
        await _vault.SaveAsync(
            new VaultSecret { Id = secretId, Name = "库中密码", Kind = SecretKind.Password, Fingerprint = "fp:1" },
            new SecretPayload { Password = "x" });
        _vault.Lock();
        Assert.False(_vault.IsUnlocked);

        await vm.LoadAsync();

        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        Assert.Equal("库中密码", item.Name);
        // 未解锁也能列出元数据，且不弹主密码框、不解锁
        Assert.Equal(0, _ui.MasterPasswordPrompts);
        Assert.False(_vault.IsUnlocked);
    });

    // RF1 补充：未经解锁时经身份管理器「材料库」入口打开窗口——窗口照常打开并列出元数据，
    // 全程不弹主密码框、不解锁（覆盖真实打开窗口的命令路径，而非直接调 LoadAsync）
    [Fact]
    public Task OpenLibraryWindow_WhenLocked_DoesNotPromptMasterPassword() => HeadlessAvalonia.RunAsync(async () =>
    {
        await VaultTestDb.CreateAsync(ConnStr, Guid.NewGuid());
        _vault = VaultTestDb.CreateVault(ConnStr);
        await _vault.SetMasterPasswordAsync("pw");
        await _vault.SaveAsync(
            new VaultSecret { Id = Guid.NewGuid(), Name = "库中条目", Kind = SecretKind.Password },
            new SecretPayload { Password = "x" });
        _vault.Lock();
        _identities = new SqliteIdentityRepository(ConnStr);
        _session = new VaultSessionService(_vault, _vault, _settings, () => _ui);
        var manager = new IdentityManagerViewModel(_identities, _vault) { Interaction = _ui };
        manager.ConfigureVaultSession(_session);

        await manager.OpenVaultSecretLibraryCommand.ExecuteAsync(null);

        Assert.Equal(1, _ui.SecretLibraryOpenedCount);
        VaultSecretLibraryViewModel opened = Assert.IsType<VaultSecretLibraryViewModel>(_ui.LastSecretLibraryViewModel);
        Assert.Single(opened.Items);
        Assert.Equal(0, _ui.MasterPasswordPrompts);
        Assert.False(_vault.IsUnlocked);
    });

    [Fact]
    public Task Backfill_WhenUnlocked_ComputesMissingFingerprint() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        Guid secretId = Guid.NewGuid();
        // 模拟迁移产出的旧条目：只落载荷、无指纹元数据（绕过 VaultSessionService 的计算路径）
        await _vault.SaveAsync(
            new VaultSecret { Id = secretId, Name = "旧私钥", Kind = SecretKind.SshPrivateKey },
            new SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh });

        await vm.LoadAsync();

        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        Assert.False(string.IsNullOrEmpty(item.Fingerprint));
        Assert.Equal("ssh-ed25519", item.Secret.KeyType);
    });

    [Fact]
    public Task Backfill_WhenLocked_DoesNotPromptNorCompute() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        await _vault.SetMasterPasswordAsync("pw");
        Guid secretId = Guid.NewGuid();
        await _vault.SaveAsync(
            new VaultSecret { Id = secretId, Name = "旧私钥", Kind = SecretKind.SshPrivateKey },
            new SecretPayload { PrivateKeyContent = PrivateKeyFormatTests.PlainOpenSsh });
        _vault.Lock();

        await vm.LoadAsync();

        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        Assert.Equal(string.Empty, item.Fingerprint);
        Assert.Equal(0, _ui.MasterPasswordPrompts);
        Assert.False(_vault.IsUnlocked);
    });

    [Fact]
    public Task CreatePassword_FromDraft_AddsEntry() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        _ui.NewSecretDrafts.Enqueue(new VaultSecretDraft("网站密码", false, null, null, "s3cret"));

        await vm.CreatePasswordAsync();

        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        Assert.Equal("网站密码", item.Name);
        Assert.Equal(SecretKind.Password, item.Secret.Kind);
        Assert.Equal("s3cret", (await _vault.GetPayloadAsync(item.Id))!.Password);
        Assert.False(_ui.NewSecretPrompts[0]);
    });

    // RF5 补充：明文模式新建材料——vault_secrets 行按明文落库（encryption_algorithm=PLAIN、无 nonce/tag）
    [Fact]
    public Task CreatePassword_InPlainMode_WritesPlaintextRow() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        Assert.True(_vault.IsPlainMode);
        _ui.NewSecretDrafts.Enqueue(new VaultSecretDraft("明文密码", false, null, null, "p"));

        await vm.CreatePasswordAsync();

        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        var row = await VaultTestDb.ReadVaultSecretRawAsync(ConnStr, item.Id);
        Assert.Equal("PLAIN", row.algorithm);
        Assert.False(row.hasNonce);
        Assert.False(row.hasTag);
    });

    [Fact]
    public Task CreatePrivateKey_FromDraft_PersistsPayloadAndFingerprint() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        _ui.NewSecretDrafts.Enqueue(new VaultSecretDraft("导入私钥", true, PrivateKeyFormatTests.PlainOpenSsh, null, null));

        await vm.CreatePrivateKeyAsync();

        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        Assert.Equal(SecretKind.SshPrivateKey, item.Secret.Kind);
        Assert.False(string.IsNullOrEmpty(item.Fingerprint));
        Assert.Equal("ssh-ed25519", item.Secret.KeyType);
        Assert.Equal(PrivateKeyFormatTests.PlainOpenSsh, (await _vault.GetPayloadAsync(item.Id))!.PrivateKeyContent);
        Assert.True(_ui.NewSecretPrompts[0]);
    });

    [Fact]
    public Task Rename_UpdatesEntryName() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        await _vault.SaveAsync(
            new VaultSecret { Id = Guid.NewGuid(), Name = "旧名称", Kind = SecretKind.Password },
            new SecretPayload { Password = "p" });
        await vm.LoadAsync();
        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        _ui.TextAnswers.Enqueue("新名称");

        await vm.RenameAsync(item);

        Assert.Equal("新名称", Assert.Single(vm.Items).Name);
        // 只改元数据：载荷仍可读
        Assert.Equal("p", (await _vault.GetPayloadAsync(item.Id))!.Password);
    });

    [Fact]
    public Task Rename_KeepsPayloadMaterializable() => HeadlessAvalonia.RunAsync(async () =>
    {
        VaultSecretLibraryViewModel vm = await CreateAsync();
        Guid secretId = Guid.NewGuid();
        await _vault.SaveAsync(
            new VaultSecret { Id = secretId, Name = "旧名称", Kind = SecretKind.Password },
            new SecretPayload { Password = "p" });
        await vm.LoadAsync();
        VaultSecretListItemViewModel item = Assert.Single(vm.Items);
        _ui.TextAnswers.Enqueue("改名后");

        await vm.RenameAsync(item);

        Assert.Equal("改名后", Assert.Single(vm.Items).Name);

        // 重命名只改元数据：引用该条目的身份方法仍能物化出原载荷
        SecretPayload? payload = await _vault.GetPayloadAsync(secretId);
        Assert.Equal("p", payload!.Password);
        var context = new AuthMaterializerContext
        {
            ReadPrivateKeyFileAsync = (_, _) => Task.FromResult<FileKeyReadResult?>(null),
            GetSessionPassphrase = _ => null,
            GetVaultSecret = id => id == secretId ? payload : null,
            PromptPassphraseAsync = (_, _) => Task.FromResult<PassphrasePromptResult?>(null),
        };
        MaterializedAuthMethod? material = await AuthMaterializer.MaterializeAsync(
            new VaultPasswordMethod { SecretId = secretId }, "ops", context);

        Assert.NotNull(material);
        Assert.Equal(AuthMaterialKind.Password, material!.Kind);
        Assert.Equal("p", material.Secret!.Password);
    });
}
