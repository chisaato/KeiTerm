using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.ViewModels;

// 固定面板与临时悬浮面板共用同一棵树；临时展开状态不写入启动偏好。
public partial class MainViewModel
{
    private bool _applyingSessionManagerSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionManagerDocked))]
    private bool _isSessionManagerVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionManagerDocked))]
    [NotifyPropertyChangedFor(nameof(SessionManagerToggleToolTip))]
    private bool _isSessionManagerPinned = true;

    public bool IsSessionManagerDocked => IsSessionManagerVisible && IsSessionManagerPinned;
    public string SessionManagerToggleToolTip => Strings.Get(IsSessionManagerPinned
        ? "Main.SessionManager.UnpinTip" : "Main.SessionManager.PinTip");

    partial void OnIsSessionManagerVisibleChanged(bool value)
    {
        if (_applyingSessionManagerSettings || !IsSessionManagerPinned
            || CurrentSettings.SessionManagerVisibilityMode != PanelVisibilityMode.RememberLastState) return;
        CurrentSettings.LastSessionManagerVisible = value;
        _ = SaveSessionManagerSettingsAsync();
    }

    partial void OnIsSessionManagerPinnedChanged(bool value)
    {
        if (_applyingSessionManagerSettings) return;
        CurrentSettings.SessionManagerPinned = value;
        // 固定一个收起的面板时，同时显示它。
        if (value) IsSessionManagerVisible = true;
        _ = SaveSessionManagerSettingsAsync();
    }

    [RelayCommand]
    private void ToggleSessionManager() => ToggleSessionManagerPin();

    [RelayCommand]
    private void ToggleSessionManagerPin() => IsSessionManagerPinned = !IsSessionManagerPinned;

    public void ShowFloatingSessionManager()
    {
        if (!IsSessionManagerPinned) IsSessionManagerVisible = true;
    }

    public void DismissFloatingSessionManager()
    {
        if (!IsSessionManagerPinned) IsSessionManagerVisible = false;
    }

    public void ApplySessionManagerSettings()
    {
        _applyingSessionManagerSettings = true;
        try
        {
            IsSessionManagerPinned = CurrentSettings.SessionManagerPinned;
            // 固定即显示；旧的独立显示偏好不再产生“已固定但隐藏”的冲突状态。
            IsSessionManagerVisible = IsSessionManagerPinned;
        }
        finally { _applyingSessionManagerSettings = false; }
    }

    private Task SaveSessionManagerSettingsAsync() => Safe.RunAsync(_logger, "保存会话管理器显示方式",
        () => _settingsService.SaveSettingsAsync(CurrentSettings));
}
