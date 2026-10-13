using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;

namespace Kei.Term.App.ViewModels.Settings;

// 本机凭据操作立即生效，不进入全局设置的应用 / 取消草稿。
public partial class VaultQuickUnlockSettingsViewModel : ViewModelBase
{
    private readonly VaultQuickUnlockService _service;
    private readonly Func<IInteractionService> _interaction;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand))]
    private bool _isBusy;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    private bool _hasLoaded;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowEnable))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    private bool _isSupported;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    private bool _isAvailable;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowEnable))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand))]
    private bool _isEnabled;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    private bool _hasMasterPassword;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(EnableButtonText))]
    private string _displayName = Strings.Get("VaultQuickUnlock.DisplayName");

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError), nameof(StatusText))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool ShowEnable => IsSupported && !IsEnabled;
    public string EnableButtonText => Strings.Format("VaultQuickUnlock.Enable", DisplayName);

    public string StatusText
    {
        get
        {
            if (IsBusy) return Strings.Get("VaultQuickUnlock.Checking");
            if (!HasLoaded) return Strings.Get(HasError ? "VaultQuickUnlock.Unavailable" : "VaultQuickUnlock.Checking");
            if (!IsSupported) return Strings.Get("VaultQuickUnlock.Unsupported");
            if (!HasMasterPassword) return Strings.Get("VaultQuickUnlock.NeedsMasterPassword");
            if (!IsAvailable) return Strings.Format("VaultQuickUnlock.StatusUnavailable", DisplayName);
            return Strings.Format(IsEnabled ? "VaultQuickUnlock.Enabled" : "VaultQuickUnlock.Disabled", DisplayName);
        }
    }

    public VaultQuickUnlockSettingsViewModel(VaultQuickUnlockService service, Func<IInteractionService> interaction)
    {
        _service = service;
        _interaction = interaction;
    }

    private bool CanEnable() => HasLoaded && IsSupported && IsAvailable && HasMasterPassword && !IsEnabled && !IsBusy;
    // 清除本机授权不要求验证硬件可用，方便迁移设备或移除指纹后撤销凭据。
    private bool CanDisable() => IsEnabled && !IsBusy;

    public Task RefreshAsync(CancellationToken ct = default)
        => RunAsync(() => ReloadStatusAsync(ct), "VaultQuickUnlock.CheckFailed");

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await _service.EnableAsync(_interaction(), ct);
        await ReloadStatusAsync(ct);
    }, "VaultQuickUnlock.EnableFailed");

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private Task DisableAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await _service.DisableAsync(ct);
        await ReloadStatusAsync(ct);
    }, "VaultQuickUnlock.DisableFailed");

    private async Task ReloadStatusAsync(CancellationToken ct)
    {
        QuickUnlockStatus status = await _service.GetStatusAsync(ct);
        await OnUiAsync(() =>
        {
            IsSupported = status.IsSupported;
            IsAvailable = status.IsAvailable;
            IsEnabled = status.IsEnabled;
            HasMasterPassword = status.HasMasterPassword;
            DisplayName = status.DisplayName;
            HasLoaded = true;
        });
    }

    private async Task RunAsync(Func<Task> action, string failureKey)
    {
        bool started = false;
        await OnUiAsync(() =>
        {
            if (IsBusy) return;
            started = true;
            IsBusy = true;
            ErrorMessage = string.Empty;
        });
        if (!started) return;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // 用户取消系统验证不产生授权，也不覆盖原有设置状态。
        }
        catch (Exception)
        {
            // 不将系统凭据后端的异常详情带到 UI；主密码路径仍然可用。
            await OnUiAsync(() => ErrorMessage = Strings.Get(failureKey));
        }
        finally
        {
            await OnUiAsync(() => IsBusy = false);
        }
    }

    private static async Task OnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else await Dispatcher.UIThread.InvokeAsync(action);
    }
}
