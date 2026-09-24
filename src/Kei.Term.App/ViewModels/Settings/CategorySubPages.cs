namespace Kei.Term.App.ViewModels.Settings;

// 树设置分类项容器（直接代理 AppearanceSettingsPage 的树密度设置）
public sealed class TreeSettingsProxyPage : ViewModelBase
{
    public AppearanceSettingsPage Appearance { get; }

    public TreeSettingsProxyPage(AppearanceSettingsPage appearance)
    {
        Appearance = appearance;
    }
}

// 终端设置分类项容器（整合 TerminalSettingsPage 与终端字体/配色）
public sealed class TerminalSettingsCombinedPage : ViewModelBase
{
    public TerminalSettingsPage Terminal { get; }
    public AppearanceSettingsPage Appearance { get; }

    public TerminalSettingsCombinedPage(TerminalSettingsPage terminal, AppearanceSettingsPage appearance)
    {
        Terminal = terminal;
        Appearance = appearance;
    }
}

// 标签栏设置分类项容器（代理 AppearanceSettingsPage 中的标签栏与配置包导入导出）
public sealed class TabBarSettingsProxyPage : ViewModelBase
{
    public AppearanceSettingsPage Appearance { get; }

    public TabBarSettingsProxyPage(AppearanceSettingsPage appearance)
    {
        Appearance = appearance;
    }
}
