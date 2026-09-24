namespace Kei.Term.App.ViewModels.Settings;

// 主题下拉选项
public sealed class ThemeOption
{
    public ThemeOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    // 持久化键值，仅 "Dark" | "System"
    public string Key { get; }

    // 界面显示文本
    public string Label { get; }
}
