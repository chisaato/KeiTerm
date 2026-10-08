using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.App.Services;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.ViewModels;

// 身份管理器：身份列表 + 新建/编辑/删除 + 锁定 Vault
public partial class IdentityManagerViewModel : ViewModelBase
{
    private readonly IIdentityRepository _repo;
    private readonly IVaultManager _vault;
    private readonly IVaultSecretStore _secretStore;
    private readonly ILogger<IdentityManagerViewModel> _logger;

    // 供编辑器/窗口层复用同一 logger
    public ILogger Logger => _logger;

    public IInteractionService Interaction { get; set; } = NullInteractionService.Instance;

    [ObservableProperty]
    private ObservableCollection<Identity> _identities = [];

    [ObservableProperty]
    private Identity? _selectedIdentity;

    // 有主密码且当前已解锁时才允许手动锁定
    [ObservableProperty]
    private bool _canLockVault;

    // 明文模式：界面给出不加密警示
    public bool IsPlainMode => _vault.IsPlainMode;

    // 打开编辑窗口（窗口返回确认后的身份编辑结果；null = 取消）
    public Func<Identity?, Task<IdentityEditResult?>>? OpenEditDialogAsync { get; set; }

    // 删除确认（未注入时默认确认）
    public Func<string, Task<bool>>? ConfirmDeleteAsync { get; set; }

    // 锁定 Vault 的实际动作（由 MainViewModel 提供，一并清空 SessionOnly 口令缓存）
    public Action? LockVaultAction { get; set; }

    // 读取指定 Vault 私钥方法已存材料信息（字节数 + 指纹），编辑器回显用
    public Func<Guid, Guid, Task<VaultKeyInfo?>>? VaultKeyInfoLoader { get; set; }

    // 身份落库后统一写入 Vault 私钥材料（由 MainViewModel 提供）；false = 未解锁被取消
    public Func<Guid, IReadOnlyList<VaultKeyImport>, Task<bool>>? PersistVaultKeysAsync { get; set; }

    // 弹保管库初始化框（仅由明文横幅「设置主密码」触发）
    public Func<Task<VaultSetupResult>>? VaultSetupDialogAsync { get; set; }

    public IdentityManagerViewModel(
        IIdentityRepository repo,
        IVaultManager vault,
        IVaultSecretStore secretStore,
        ILogger<IdentityManagerViewModel>? logger = null)
    {
        _repo = repo;
        _vault = vault;
        _secretStore = secretStore;
        _logger = logger ?? NullLogger<IdentityManagerViewModel>.Instance;
    }

    public async Task LoadAsync()
    {
        Identities = new ObservableCollection<Identity>(await _repo.GetAllAsync());
        SelectedIdentity = Identities.FirstOrDefault();
        RefreshVaultState();
        OnPropertyChanged(nameof(IsPlainMode));
        _logger.LogInformation("身份列表加载完成 数量={Count}", Identities.Count);
    }

    [RelayCommand]
    private async Task AddIdentityAsync()
    {
        _logger.LogInformation("新建身份命令触发");
        if (OpenEditDialogAsync == null)
        {
            _logger.LogWarning("新建身份中止：编辑器未接线");
            return;
        }

        var result = await Safe.RunAsync(_logger, "新建身份", () => OpenEditDialogAsync(null));
        if (result == null)
        {
            _logger.LogInformation("新建身份取消");
            return;
        }

        // 身份与 Vault 材料已由编辑器「应用」时落库，这里仅刷新列表
        await LoadAsync();
        SelectedIdentity = Identities.FirstOrDefault(i => i.Id == result.Identity.Id);
    }

    [RelayCommand]
    private async Task EditIdentityAsync()
    {
        _logger.LogInformation("编辑身份命令触发 IdentityId={IdentityId}", SelectedIdentity?.Id);
        if (SelectedIdentity == null)
        {
            _logger.LogInformation("编辑身份点击但未选中任何身份，忽略");
            return;
        }

        if (OpenEditDialogAsync == null)
        {
            _logger.LogWarning("编辑身份中止：编辑器未接线");
            return;
        }

        var result = await Safe.RunAsync(_logger, "编辑身份", () => OpenEditDialogAsync(SelectedIdentity));
        if (result == null)
        {
            _logger.LogInformation("编辑身份取消或无变更 IdentityId={IdentityId}", SelectedIdentity.Id);
            return;
        }

        // 身份与 Vault 材料已由编辑器「应用」时落库，这里仅刷新列表
        await LoadAsync();
        SelectedIdentity = Identities.FirstOrDefault(i => i.Id == result.Identity.Id);
    }

    [RelayCommand]
    private Task DeleteIdentityAsync() => Safe.RunAsync(_logger, "删除身份", async () =>
    {
        if (SelectedIdentity == null)
        {
            return;
        }

        var id = SelectedIdentity.Id;
        var name = SelectedIdentity.Name;
        _logger.LogInformation("删除身份 IdentityId={IdentityId} 名称={Name}", id, name);

        if (ConfirmDeleteAsync != null && !await ConfirmDeleteAsync(name))
        {
            _logger.LogInformation("删除身份取消 IdentityId={IdentityId}", id);
            return;
        }

        // 仓储 + 材料存储双清（identity_secrets 亦有 ON DELETE CASCADE 兜底）
        await _secretStore.DeleteSecretsAsync(id);
        await _repo.DeleteAsync(id);
        await LoadAsync();
    });

    [RelayCommand]
    private void LockVault()
    {
        _logger.LogInformation("身份管理器触发手动锁定保管库");
        LockVaultAction?.Invoke();
        RefreshVaultState();
    }

    // 编辑器「应用」时调用：身份落库
    public Task SaveIdentityAsync(Identity identity)
    {
        _logger.LogInformation("身份落库 IdentityId={IdentityId} 名称={Name}", identity.Id, identity.Name);
        return _repo.SaveAsync(identity);
    }

    // 明文横幅「设置主密码」：弹初始化框，成功后横幅消失、可手动锁定
    [RelayCommand]
    private Task SetMasterPasswordAsync() => Safe.RunAsync(_logger, "设置主密码", async () =>
    {
        if (VaultSetupDialogAsync == null)
        {
            return;
        }

        var setup = await VaultSetupDialogAsync();
        if (setup.Choice != VaultSetupChoice.SetMasterPassword || string.IsNullOrEmpty(setup.MasterPassword))
        {
            _logger.LogInformation("设置主密码取消 选择={Choice}", setup.Choice);
            return;
        }

        try
        {
            await _vault.SetMasterPasswordAsync(setup.MasterPassword);
            _logger.LogInformation("设置主密码成功，保管库切换为加密模式");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置主密码未完整完成");
            await Interaction.NotifyAsync(Strings.Get("IdentityManager.SetMasterPassword"), ex.Message);
        }
        finally
        {
            // 密钥事务可能已提交、历史日志清理仍失败；按实际 Vault 状态刷新，不能继续显示明文模式。
            RefreshVaultState();
            OnPropertyChanged(nameof(IsPlainMode));
        }
    });

    private void RefreshVaultState()
    {
        CanLockVault = !_vault.IsPlainMode && _vault.IsUnlocked;
    }
}
