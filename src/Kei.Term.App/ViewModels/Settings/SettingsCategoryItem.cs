namespace Kei.Term.App.ViewModels.Settings;

// 左侧分类树条目：标题、描述、图标与对应设置页
public sealed class SettingsCategoryItem
{
    public SettingsCategoryItem(string title, string? description, object page, string icon)
    {
        Title = title;
        Description = description;
        Page = page;
        Icon = icon;
    }

    // 分类名，列表项与右侧页头共用
    public string Title { get; }

    // 页头副标题（可空，为空时不显示副标题）
    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    // 右侧内容区通过 DataTemplate 按实际类型分发的设置页 VM
    public object Page { get; }

    // 16×16 线性图标几何数据
    public string Icon { get; }
}
