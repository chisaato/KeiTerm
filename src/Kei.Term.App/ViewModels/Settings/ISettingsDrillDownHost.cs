namespace Kei.Term.App.ViewModels.Settings;

public interface ISettingsDrillDownHost
{
    SettingsSubPageViewModel? CurrentSubPage { get; }
    bool IsInSubPage { get; }
    void NavigateToSubPage(string title, object content, System.Action? onClosed = null);
    void NavigateBack();
}
