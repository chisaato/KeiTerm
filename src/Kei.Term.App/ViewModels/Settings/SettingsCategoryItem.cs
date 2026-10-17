namespace Kei.Term.App.ViewModels.Settings;

// 左侧分类条目：支持分组标题与常规单行条目
public sealed class SettingsCategoryItem
{
    public SettingsCategoryItem(string title, string? description, object page, string icon, string group = "")
    {
        Title = title;
        Description = description;
        Page = page;
        Icon = icon;
        Group = group;
        IsHeader = false;
    }

    private SettingsCategoryItem(string groupTitle)
    {
        Title = groupTitle;
        Description = null;
        Page = string.Empty;
        Icon = string.Empty;
        Group = groupTitle;
        IsHeader = true;
    }

    public static SettingsCategoryItem CreateHeader(string title) => new(title);

    // 是否为不可选的分组标题
    public bool IsHeader { get; }

    // 所属分组名称（用于面包屑第一级）
    public string Group { get; }

    // 分类名，列表项与右侧页头共用
    public string Title { get; }

    // 页头副标题 / 描述
    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    // 右侧内容区通过 DataTemplate 按实际类型分发的设置页 VM
    public object Page { get; }

    // 24×24 Lucide 中心线数据
    public string Icon { get; }
}

