namespace Kei.Term.App.ViewModels.Settings;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

// 树设置分类项容器（直接代理 AppearanceSettingsPage 的树密度设置）
public sealed class TreeSettingsProxyPage : ViewModelBase
{
    public AppearanceSettingsPage Appearance { get; }

    public GeneralSettingsPage General { get; }

    public TreeSettingsProxyPage(AppearanceSettingsPage appearance, GeneralSettingsPage? general = null)
    {
        Appearance = appearance;
        General = general ?? new GeneralSettingsPage(string.Empty);
    }
}

// 「终端」分类项容器：仅承载底层终端行为（回滚缓冲、默认仿真类型）
public sealed class TerminalSettingsCombinedPage : ViewModelBase
{
    public TerminalSettingsPage Terminal { get; }

    public TerminalSettingsCombinedPage(TerminalSettingsPage terminal)
    {
        Terminal = terminal;
    }
}

// 「终端外观」分类项容器：终端配色方案、主字体与回退字体配置
public sealed class TerminalAppearanceSettingsPage : ViewModelBase, ISelfScrollSettingsPage
{
    public AppearanceSettingsPage Appearance { get; }

    public bool ManagesOwnScroll => Appearance.ManagesOwnScroll;

    public TerminalAppearanceSettingsPage(AppearanceSettingsPage appearance)
    {
        Appearance = appearance;
        Appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Appearance.ManagesOwnScroll))
            {
                OnPropertyChanged(nameof(ManagesOwnScroll));
            }
        };
    }
}

// 标签栏设置分类项容器：位置与全局标题行为共用原页面草稿。
public sealed partial class TabBarSettingsProxyPage : ViewModelBase, ISettingsDrillDownHost
{
    public AppearanceSettingsPage Appearance { get; }

    public TerminalSettingsPage Terminal { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInSubPage))]
    [NotifyPropertyChangedFor(nameof(SubPageTitle))]
    [NotifyPropertyChangedFor(nameof(SubPageContent))]
    private SettingsSubPageViewModel? _currentSubPage;

    public bool IsInSubPage => CurrentSubPage != null;

    // 视图绑定用的非空路径，避免未下钻时 CurrentSubPage 为 null 产生绑定警告
    public string SubPageTitle => CurrentSubPage?.Title ?? string.Empty;

    public object? SubPageContent => CurrentSubPage?.Content;

    public void NavigateToSubPage(string title, object content, Action? onClosed = null)
    {
        CurrentSubPage = new SettingsSubPageViewModel(title, content, onClosed);
    }

    [RelayCommand]
    public void NavigateBack()
    {
        if (CurrentSubPage != null)
        {
            Action? callback = CurrentSubPage.OnClosed;
            CurrentSubPage = null;
            callback?.Invoke();
        }
    }

    [RelayCommand]
    private void DrillDownVariables()
    {
        NavigateToSubPage(Helpers.Strings.Get("Settings.TabBar.VariableReference"), new TabBarVariablesSubPage(this));
    }

    public TabBarSettingsProxyPage(AppearanceSettingsPage appearance, TerminalSettingsPage? terminal = null)
    {
        Appearance = appearance;
        Terminal = terminal ?? new TerminalSettingsPage();
    }
}

public class TabBarVariablesSubPage : ViewModelBase
{
    public TabBarSettingsProxyPage Parent { get; }
    public TabBarVariablesSubPage(TabBarSettingsProxyPage parent) => Parent = parent;
}
