using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels.Settings;

// 「安全」分类页：主密码立即生效，自动锁定进入设置草稿。
public partial class SecuritySettingsPage : ViewModelBase
{
    private readonly IVaultManager? _vault;

    [ObservableProperty]
    private int _lockTimeoutMinutes;

    [ObservableProperty]
    private VaultQuickUnlockSettingsViewModel? _quickUnlock;

    [ObservableProperty]
    private string _masterPasswordStatus = string.Empty;

    [ObservableProperty]
    private string _passwordButtonText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveMasterPasswordCommand))]
    private bool _canRemoveMasterPassword;

    [ObservableProperty]
    private bool _isVaultLocked;

    public bool HasVault => _vault != null;

    public Func<IInteractionService>? Interaction { get; set; }

    // true 表示已有主密码，对话框只收集新密码，不再提供明文选项。
    public Func<bool, Task<VaultSetupResult?>>? VaultSetupDialogAsync { get; set; }

    public SecuritySettingsPage(IVaultManager? vault = null)
    {
        _vault = vault;
        RefreshVaultState();
    }

    public void RefreshVaultState()
    {
        if (_vault == null)
        {
            MasterPasswordStatus = string.Empty;
            PasswordButtonText = string.Empty;
            CanRemoveMasterPassword = false;
            IsVaultLocked = false;
            return;
        }

        bool plain = _vault.IsPlainMode;
        IsVaultLocked = !plain && !_vault.IsUnlocked;
        MasterPasswordStatus = plain
            ? Strings.Get("Settings.Security.NoMasterPassword")
            : IsVaultLocked
                ? Strings.Get("Settings.Security.HasMasterPasswordLocked")
                : Strings.Get("Settings.Security.HasMasterPassword");
        PasswordButtonText = Strings.Get(plain
            ? "Settings.Security.SetMasterPassword"
            : "Settings.Security.ChangeMasterPassword");
        CanRemoveMasterPassword = !plain;
    }

    [RelayCommand]
    private async Task UnlockVaultAsync()
    {
        if (_vault == null || Interaction == null || _vault.IsPlainMode || _vault.IsUnlocked) return;
        string? error = null;
        while (!_vault.IsUnlocked)
        {
            string? password = await Interaction().PromptMasterPasswordAsync(error);
            if (string.IsNullOrEmpty(password)) break;
            try
            {
                await _vault.UnlockAsync(password, rememberOnThisDevice: false);
                RefreshVaultState();
                if (QuickUnlock != null) await QuickUnlock.RefreshAsync();
                return;
            }
            catch (Exception)
            {
                error = Strings.Get("Vault.WrongPassword");
            }
        }
    }

    [RelayCommand]
    private async Task SetMasterPasswordAsync()
    {
        if (_vault == null || VaultSetupDialogAsync == null)
        {
            return;
        }

        if (!_vault.IsPlainMode && !_vault.IsUnlocked)
        {
            if (Interaction != null)
            {
                await Interaction().NotifyAsync(
                    Strings.Get("Settings.Security.MasterPasswordSection"),
                    Strings.Get("Settings.Security.Locked"),
                    NotificationLevel.Warning);
            }

            return;
        }

        bool changing = !_vault.IsPlainMode;
        VaultSetupResult? setup = await VaultSetupDialogAsync(changing);
        if (setup == null
            || setup.Choice != VaultSetupChoice.SetMasterPassword
            || string.IsNullOrEmpty(setup.MasterPassword))
        {
            return;
        }

        try
        {
            await _vault.SetMasterPasswordAsync(setup.MasterPassword);
        }
        catch (Exception ex)
        {
            if (Interaction != null)
            {
                await Interaction().NotifyAsync(Strings.Get("Settings.Security.SetFailed"), ex.Message, NotificationLevel.Error);
            }
        }
        finally
        {
            RefreshVaultState();
            if (QuickUnlock != null)
            {
                await QuickUnlock.RefreshAsync();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveMasterPassword))]
    private async Task RemoveMasterPasswordAsync()
    {
        if (_vault == null || Interaction == null || _vault.IsPlainMode) return;
        if (!_vault.IsUnlocked)
        {
            await Interaction().NotifyAsync(
                Strings.Get("Settings.Security.MasterPasswordSection"),
                Strings.Get("Settings.Security.Locked"),
                NotificationLevel.Warning);
            return;
        }

        bool confirmed = await Interaction().ConfirmDeleteAsync(Strings.Get("Settings.Security.RemoveConfirm"));
        if (!confirmed) return;

        try
        {
            await _vault.RemoveMasterPasswordAsync();
        }
        catch (Exception ex)
        {
            await Interaction().NotifyAsync(Strings.Get("Settings.Security.RemoveFailed"), ex.Message, NotificationLevel.Error);
        }
        finally
        {
            RefreshVaultState();
            if (QuickUnlock != null) await QuickUnlock.RefreshAsync();
        }
    }
}
