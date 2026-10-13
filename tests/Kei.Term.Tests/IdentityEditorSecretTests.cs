using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Views;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kei.Term.Tests;

public class IdentityEditorSecretTests
{
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = VaultTestDb.NewPath();
        public string ConnectionString => $"Data Source={Path};Pooling=False";
        public InternalVaultManager Vault { get; private set; } = null!;
        public SqliteIdentityRepository Repository { get; private set; } = null!;
        public VaultSessionService Session { get; private set; } = null!;
        public FixedSettingsService Settings { get; } = new(new AppSettings());
        public ScriptedInteraction Interaction { get; } = new();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await VaultTestDb.CreateAsync(fixture.ConnectionString, Guid.NewGuid());
            fixture.Vault = new(fixture.ConnectionString);
            fixture.Repository = new(fixture.ConnectionString);
            fixture.Session = new VaultSessionService(fixture.Vault, fixture.Vault, fixture.Settings, () => fixture.Interaction);
            return fixture;
        }

        public void Dispose()
        {
            Vault?.Lock();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(Path + suffix))
                {
                    try { File.Delete(Path + suffix); } catch { }
                }
            }
        }
    }

    [Fact]
    public Task Editor_SelectExistingSecret_BindsSecretIdOnSave() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Guid secretId = Guid.NewGuid();
        var secret = new VaultSecret
        {
            Id = secretId,
            Name = "生产环境私钥",
            Kind = SecretKind.SshPrivateKey,
            Fingerprint = "SHA256:abc123test",
        };
        await fixture.Vault.SaveAsync(secret, new SecretPayload { PrivateKeyContent = "dummy-key-content" });

        var identity = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "服务器身份",
            Methods = [new VaultPrivateKeyMethod { Id = Guid.NewGuid(), SecretId = null }],
        };
        await fixture.Repository.SaveAsync(identity);

        var editVm = new IdentityEditViewModel(identity);
        editVm.SecretsLoader = () => fixture.Session.ListLibrarySecretsAsync();
        editVm.ReferenceCountLoader = async id => SecretReferences.Find(await fixture.Repository.GetAllAsync(), id).Count;
        editVm.SaveIdentityAsync = id => fixture.Repository.SaveAsync(id);
        await editVm.LoadSecretsAsync();

        var window = new IdentityEditWindow(editVm);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            var row = Assert.Single(editVm.Methods);
            Assert.True(row.IsVaultMethod);
            Assert.Single(row.AvailableSecrets);

            // 选择已有材料
            row.SelectedSecretId = secretId;
            Assert.NotNull(row.SelectedSecret);
            Assert.Equal("生产环境私钥", row.SelectedSecret.Name);
            Assert.Contains("生产环境私钥", row.SelectedSecretStatus);
            Assert.Contains("SHA256:abc123test", row.SelectedSecretStatus);

            // 执行保存
            await editVm.ConfirmCommand.ExecuteAsync(null);
            Assert.True(editVm.IsConfirmed);

            // 重新从仓储加载，验证 SecretId 正确持久化
            var saved = await fixture.Repository.GetByIdAsync(identity.Id);
            Assert.NotNull(saved);
            var savedMethod = Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(saved.Methods));
            Assert.Equal(secretId, savedMethod.SecretId);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Editor_CreateNewSecret_AddsToDropdownAndSelects() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        var identity = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "运维身份",
            Methods = [new VaultPasswordMethod { Id = Guid.NewGuid(), SecretId = null }],
        };
        await fixture.Repository.SaveAsync(identity);

        var editVm = new IdentityEditViewModel(identity);
        editVm.SecretsLoader = () => fixture.Session.ListLibrarySecretsAsync();
        editVm.SaveIdentityAsync = id => fixture.Repository.SaveAsync(id);

        // 模拟新建材料创建回调
        editVm.CreateSecretAsync = async isPrivate =>
        {
            var newSec = new VaultSecret
            {
                Id = Guid.NewGuid(),
                Name = "新口令材料",
                Kind = isPrivate ? SecretKind.SshPrivateKey : SecretKind.Password,
            };
            await fixture.Session.SaveLibrarySecretAsync(newSec, new SecretPayload { Password = "pass" });
            return newSec;
        };

        await editVm.LoadSecretsAsync();

        var window = new IdentityEditWindow(editVm);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            var row = Assert.Single(editVm.Methods);
            Assert.Empty(row.AvailableSecrets);

            // 触发新建
            await row.CreateNewSecretCommand.ExecuteAsync(null);
            Assert.Single(row.AvailableSecrets);
            Assert.NotNull(row.SelectedSecretId);
            Assert.Equal("新口令材料", row.SelectedSecret?.Name);

            // 保存后 Model 及仓储均绑定该条目
            await editVm.ConfirmCommand.ExecuteAsync(null);
            Assert.True(editVm.IsConfirmed);

            var saved = await fixture.Repository.GetByIdAsync(identity.Id);
            var savedMethod = Assert.IsType<VaultPasswordMethod>(Assert.Single(saved!.Methods));
            Assert.Equal(row.SelectedSecretId, savedMethod.SecretId);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Editor_Save_WhenSelectedSecretDangling_RejectsSave() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Guid missingId = Guid.NewGuid();
        var identity = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "故障排查身份",
            Methods = [new VaultPrivateKeyMethod { Id = Guid.NewGuid(), SecretId = missingId }],
        };

        var editVm = new IdentityEditViewModel(identity);
        editVm.SecretsLoader = () => fixture.Session.ListLibrarySecretsAsync();
        editVm.SaveIdentityAsync = id => fixture.Repository.SaveAsync(id);
        await editVm.LoadSecretsAsync();

        var window = new IdentityEditWindow(editVm);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            // 尝试保存，应因条目不存在而校验失败并设置 ErrorMessage
            await editVm.ConfirmCommand.ExecuteAsync(null);
            Assert.False(editVm.IsConfirmed);
            Assert.NotNull(editVm.ErrorMessage);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task LibraryWindow_ToolbarButtonsAndFilterSwitch_BoundCorrectly() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        var libraryVm = new VaultSecretLibraryViewModel(fixture.Session, fixture.Repository, fixture.Interaction, NullLogger<VaultSecretLibraryViewModel>.Instance);
        await libraryVm.LoadAsync();

        var window = new VaultSecretLibraryWindow(libraryVm);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            // 材料库窗口须提供新建/导入、重命名、删除与无引用筛选控件
            Assert.NotNull(window.FindControl<Button>("ImportPrivateKeyButton"));
            Assert.NotNull(window.FindControl<Button>("NewPasswordButton"));
            Assert.NotNull(window.FindControl<Button>("RenameButton"));

            var deleteBtn = window.FindControl<Button>("DeleteButton");
            Assert.NotNull(deleteBtn);

            var unreferencedBox = window.FindControl<CheckBox>("UnreferencedOnlyCheckBox");
            Assert.NotNull(unreferencedBox);

            var list = window.FindControl<ListBox>("SecretList");
            Assert.NotNull(list);

            // 切换无引用开关，断言 ViewModel 属性同步
            unreferencedBox.IsChecked = true;
            HeadlessAvalonia.Pump();
            Assert.True(libraryVm.UnreferencedOnly);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task LibraryWindow_RestoreSelection_AfterItemsReplaced() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Guid secretId = Guid.NewGuid();
        var secret = new VaultSecret
        {
            Id = secretId,
            Name = "无引用私钥",
            Kind = SecretKind.SshPrivateKey,
        };
        await fixture.Vault.SaveAsync(secret, new SecretPayload { PrivateKeyContent = "key-data" });

        var libraryVm = new VaultSecretLibraryViewModel(fixture.Session, fixture.Repository, fixture.Interaction, NullLogger<VaultSecretLibraryViewModel>.Instance);
        await libraryVm.LoadAsync();

        var window = new VaultSecretLibraryWindow(libraryVm);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            var list = window.FindControl<ListBox>("SecretList")!;
            Assert.Single(libraryVm.Items);
            list.SelectedItem = libraryVm.Items[0];
            HeadlessAvalonia.Pump();
            Assert.NotNull(list.SelectedItem);

            // 触发重新加载（导致 Items 集合整体替换）
            await libraryVm.LoadAsync();
            HeadlessAvalonia.Pump();

            // 验证按 Id 成功恢复了选中项
            Assert.NotNull(list.SelectedItem);
            var restored = Assert.IsType<VaultSecretListItemViewModel>(list.SelectedItem);
            Assert.Equal(secretId, restored.Id);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task LibraryWindow_Delete_ReferencedSecret_IsBlockedWithNotification() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Guid secretId = Guid.NewGuid();
        var secret = new VaultSecret
        {
            Id = secretId,
            Name = "生产环境密钥",
            Kind = SecretKind.SshPrivateKey,
        };
        await fixture.Vault.SaveAsync(secret, new SecretPayload { PrivateKeyContent = "key-data" });

        var identity = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "生产身份",
            Methods = [new VaultPrivateKeyMethod { Id = Guid.NewGuid(), SecretId = secretId }],
        };
        await fixture.Repository.SaveAsync(identity);

        var libraryVm = new VaultSecretLibraryViewModel(fixture.Session, fixture.Repository, fixture.Interaction, NullLogger<VaultSecretLibraryViewModel>.Instance);
        await libraryVm.LoadAsync();

        var window = new VaultSecretLibraryWindow(libraryVm);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            var list = window.FindControl<ListBox>("SecretList")!;
            Assert.Single(libraryVm.Items);
            list.SelectedItem = libraryVm.Items[0];
            HeadlessAvalonia.Pump();

            var deleteBtn = window.FindControl<Button>("DeleteButton")!;
            Assert.True(deleteBtn.IsEnabled);

            // 点击删除按钮触发删除命令
            deleteBtn.Command!.Execute(deleteBtn.CommandParameter);
            HeadlessAvalonia.Pump();

            // 条目仍存在，且触发了阻止删除通知
            Assert.Single(libraryVm.Items);
            Assert.Contains(fixture.Interaction.Notifications, n => n.Title == Kei.Term.App.Helpers.Strings.Get("VaultSecretLibrary.DeleteBlockedTitle"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Editor_Save_WhenSecretsLoaderThrows_RejectsSave() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        var identity = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "测试身份",
            Methods = [new VaultPrivateKeyMethod { Id = Guid.NewGuid(), SecretId = Guid.NewGuid() }],
        };

        var editVm = new IdentityEditViewModel(identity);
        editVm.SecretsLoader = () => throw new InvalidOperationException("网络或数据库断开");
        editVm.SaveIdentityAsync = id => fixture.Repository.SaveAsync(id);

        var window = new IdentityEditWindow(editVm);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            await editVm.ConfirmCommand.ExecuteAsync(null);
            Assert.False(editVm.IsConfirmed);
            Assert.NotNull(editVm.ErrorMessage);
            Assert.Equal(Kei.Term.App.Helpers.Strings.Get("Secret.Error.SecretsLoadFailed"), editVm.ErrorMessage);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Editor_Save_WhenSecretsLoaderNull_SkipsValidationAndAllowsSave() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Guid secretId = Guid.NewGuid();
        var identity = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "测试身份",
            Methods = [new VaultPrivateKeyMethod { Id = Guid.NewGuid(), SecretId = secretId }],
        };

        var editVm = new IdentityEditViewModel(identity);
        // 编辑器未接入材料库（无 SecretsLoader）：无可校验数据源，按原契约放行
        editVm.SecretsLoader = null;
        editVm.SaveIdentityAsync = id => fixture.Repository.SaveAsync(id);

        var window = new IdentityEditWindow(editVm);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            await editVm.ConfirmCommand.ExecuteAsync(null);
            Assert.True(editVm.IsConfirmed);
            Assert.Null(editVm.ErrorMessage);

            var saved = await fixture.Repository.GetByIdAsync(identity.Id);
            var savedMethod = Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(saved!.Methods));
            Assert.Equal(secretId, savedMethod.SecretId);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Editor_SwitchSelectedSecret_UpdatesReferenceCountText() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        Guid secretId1 = Guid.NewGuid();
        Guid secretId2 = Guid.NewGuid();

        var sec1 = new VaultSecret { Id = secretId1, Name = "材料1", Kind = SecretKind.Password };
        var sec2 = new VaultSecret { Id = secretId2, Name = "材料2", Kind = SecretKind.Password };

        await fixture.Vault.SaveAsync(sec1, new SecretPayload { Password = "p1" });
        await fixture.Vault.SaveAsync(sec2, new SecretPayload { Password = "p2" });

        var id1 = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "关联身份1",
            Methods = [new VaultPasswordMethod { Id = Guid.NewGuid(), SecretId = secretId1 }],
        };
        var id2 = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "关联身份2",
            Methods = [new VaultPasswordMethod { Id = Guid.NewGuid(), SecretId = secretId1 }],
        };
        await fixture.Repository.SaveAsync(id1);
        await fixture.Repository.SaveAsync(id2);

        var testIdentity = new Identity
        {
            Id = Guid.NewGuid(),
            Name = "编辑身份",
            Methods = [new VaultPasswordMethod { Id = Guid.NewGuid(), SecretId = null }],
        };
        var editVm = new IdentityEditViewModel(testIdentity);
        editVm.SecretsLoader = () => fixture.Session.ListLibrarySecretsAsync();
        editVm.ReferenceCountLoader = async id => SecretReferences.Find(await fixture.Repository.GetAllAsync(), id).Count;
        await editVm.LoadSecretsAsync();

        var window = new IdentityEditWindow(editVm);
        try
        {
            window.Show();
            HeadlessAvalonia.Pump();

            var row = Assert.Single(editVm.Methods);
            Assert.True(row.IsVaultMethod);

            // 切选材料1 (2次引用)
            row.SelectedSecretId = secretId1;
            await WaitForAsync(() => row.SelectedSecretReferenceCount == 2);
            Assert.Equal(2, row.SelectedSecretReferenceCount);
            Assert.Contains("引用 2", row.SelectedSecretStatus);

            // 切选材料2 (0次引用)
            row.SelectedSecretId = secretId2;
            await WaitForAsync(() => row.SelectedSecretReferenceCount == 0);
            Assert.Equal(0, row.SelectedSecretReferenceCount);
            Assert.Contains("引用 0", row.SelectedSecretStatus);
        }
        finally
        {
            window.Close();
        }
    });

    // 引用数在后台任务完成后经 Dispatcher 回填，轮询等待避免固定延迟带来的偶发失败
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
            HeadlessAvalonia.Pump();
        }
    }

    [Fact]
    public Task Manager_CreateLibrarySecret_FromDraft_PromptsThenPersists() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        var manager = new IdentityManagerViewModel(fixture.Repository, fixture.Vault)
        {
            Interaction = fixture.Interaction,
        };
        manager.ConfigureVaultSession(fixture.Session);
        fixture.Interaction.NewSecretDrafts.Enqueue(
            new VaultSecretDraft("编排密码材料", false, null, null, "orchestrated"));

        VaultSecret? created = await manager.CreateLibrarySecretAsync(isPrivateKey: false);

        Assert.NotNull(created);
        Assert.Equal("编排密码材料", created.Name);
        Assert.Equal(SecretKind.Password, created.Kind);
        // 交互服务按密码类型发起弹窗
        Assert.Equal([false], fixture.Interaction.NewSecretPrompts);
        var list = await fixture.Session.ListLibrarySecretsAsync();
        Assert.Contains(list, s => s.Id == created.Id && s.Name == "编排密码材料");
    });

    [Fact]
    public Task Manager_CreateLibrarySecret_CancelledDraft_ReturnsNullWithoutPersisting() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        var manager = new IdentityManagerViewModel(fixture.Repository, fixture.Vault)
        {
            Interaction = fixture.Interaction,
        };
        manager.ConfigureVaultSession(fixture.Session);
        fixture.Interaction.NewSecretDrafts.Enqueue(null);

        VaultSecret? created = await manager.CreateLibrarySecretAsync(isPrivateKey: true);

        Assert.Null(created);
        Assert.Equal([true], fixture.Interaction.NewSecretPrompts);
        Assert.Empty(await fixture.Session.ListLibrarySecretsAsync());
    });

    [Fact]
    public Task Manager_CreateLibrarySecret_WithoutVaultSession_ReturnsNullWithoutPrompting() => HeadlessAvalonia.RunAsync(async () =>
    {
        using Fixture fixture = await Fixture.CreateAsync();
        var manager = new IdentityManagerViewModel(fixture.Repository, fixture.Vault)
        {
            Interaction = fixture.Interaction,
        };

        VaultSecret? created = await manager.CreateLibrarySecretAsync(isPrivateKey: false);

        Assert.Null(created);
        Assert.Empty(fixture.Interaction.NewSecretPrompts);
    });
}