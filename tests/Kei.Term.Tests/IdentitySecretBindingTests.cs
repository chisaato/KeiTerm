using System;
using System.Threading.Tasks;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Vault;
using Xunit;

namespace Kei.Term.Tests;

// 身份编辑器材料绑定：SecretId 在打开-保存往返与下拉刷新中不丢失
public class IdentitySecretBindingTests
{
    private static IdentityMethodRowViewModel Row(VaultPrivateKeyMethod method, params VaultSecret[] secrets)
        => new(method) { AvailableSecrets = secrets };

    [Fact]
    public async Task ExistingIdentity_BoundSecret_SurvivesSaveWithoutChanges()
    {
        Guid methodId = Guid.NewGuid();
        Guid secretId = Guid.NewGuid();
        var existing = new Identity
        {
            Name = "n",
            Methods = [new VaultPrivateKeyMethod { Id = methodId, SecretId = secretId }],
        };

        Identity? saved = null;
        var vm = new IdentityEditViewModel(existing);
        vm.SaveIdentityAsync = identity => { saved = identity; return Task.CompletedTask; };

        await vm.ApplyCommand.ExecuteAsync(null);

        VaultPrivateKeyMethod method = Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(saved!.Methods));
        Assert.Equal(secretId, method.SecretId);
    }

    [Fact]
    public async Task ExistingIdentity_RebindToAnotherSecret_PersistsOnSave()
    {
        Guid boundId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();
        var existing = new Identity
        {
            Name = "n",
            Methods = [new VaultPrivateKeyMethod { Id = Guid.NewGuid(), SecretId = boundId }],
        };

        Identity? saved = null;
        var vm = new IdentityEditViewModel(existing);
        vm.SaveIdentityAsync = identity => { saved = identity; return Task.CompletedTask; };

        Assert.Single(vm.Methods).SelectedSecretId = otherId;
        await vm.ApplyCommand.ExecuteAsync(null);

        VaultPrivateKeyMethod method = Assert.IsType<VaultPrivateKeyMethod>(Assert.Single(saved!.Methods));
        Assert.Equal(otherId, method.SecretId);
    }

    [Fact]
    public void ComboBoxNullWrite_DoesNotClearBoundSecretId()
    {
        Guid secretId = Guid.NewGuid();
        VaultPrivateKeyMethod method = new() { SecretId = secretId };
        IdentityMethodRowViewModel row = Row(method, new VaultSecret { Id = secretId, Name = "s", Kind = SecretKind.SshPrivateKey });

        // ComboBox 在 ItemsSource 变化/选取失败时可能回写 SelectedItem = null
        row.SelectedSecret = null;

        Assert.Equal(secretId, row.SelectedSecretId);
        Assert.Equal(secretId, method.SecretId);
    }

    [Fact]
    public void FilteredItemsSourceWithoutBoundId_KeepsBoundSecretId()
    {
        Guid boundId = Guid.NewGuid();
        VaultPrivateKeyMethod method = new() { SecretId = boundId };
        IdentityMethodRowViewModel row = Row(method, new VaultSecret { Id = boundId, Name = "s", Kind = SecretKind.SshPrivateKey });

        // 过滤后下拉里不再包含已绑定条目：显示为空，但绑定 Id 必须保留
        row.AvailableSecrets = [new VaultSecret { Id = Guid.NewGuid(), Name = "other", Kind = SecretKind.SshPrivateKey }];

        Assert.Null(row.SelectedSecret);
        Assert.Equal(boundId, row.SelectedSecretId);
        Assert.Equal(boundId, method.SecretId);
    }

    [Fact]
    public void ExplicitClear_UnbindsSecretId()
    {
        Guid secretId = Guid.NewGuid();
        VaultPrivateKeyMethod method = new() { SecretId = secretId };
        IdentityMethodRowViewModel row = Row(method, new VaultSecret { Id = secretId, Name = "s", Kind = SecretKind.SshPrivateKey });

        // 显式清空（未来的「解除绑定」操作）才允许置 null
        row.SelectedSecretId = null;

        Assert.Null(method.SecretId);
    }

    [Fact]
    public async Task LoadSecrets_WhenLoaderThrows_KeepsEditorUsableWithEmptyList()
    {
        var vm = new IdentityEditViewModel(new Identity
        {
            Methods = [new VaultPrivateKeyMethod { SecretId = Guid.NewGuid() }],
        });
        vm.SecretsLoader = () => throw new InvalidOperationException("boom");

        await vm.LoadSecretsAsync();

        Assert.Empty(vm.AvailableSecrets);
        Assert.Empty(Assert.Single(vm.Methods).AvailableSecrets);
    }
}
