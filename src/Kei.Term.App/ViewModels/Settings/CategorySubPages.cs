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
public sealed class TerminalAppearanceSettingsPage(AppearanceSettingsPage appearance) : ViewModelBase
{
    public AppearanceSettingsPage Appearance { get; } = appearance;
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
