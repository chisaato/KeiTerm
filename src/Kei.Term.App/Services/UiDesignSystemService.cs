using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Kei.Term.App.DesignSystem;

namespace Kei.Term.App.Services;

// 设计系统服务：以 FluentTheme 为基座，挂载 KeiTerm 专属紧凑桌面设计令牌与控件样式。
public static class UiDesignSystemService
{
    public const string KeiClassicKey = "KeiClassic";
    public const string KeiDarkKey = "Dark";

    // KeiTerm 专属桌面设计令牌字典（深色工业桌面风格）
    private static readonly ResourceDictionary ClassicTokens = LoadDictionary("KeiTokens.axaml");

    // KeiTerm 专属全局矢量图标字典
    private static readonly ResourceDictionary ClassicIcons = new LucideIconResources();

    // Kei 紧凑控件外观样式（树形结构引导线、工具栏按钮、标签页、输入框等）
    private static readonly Styles ClassicControlStyles = LoadStyles("KeiControls.axaml");

    private static ResourceDictionary? _activeTokens;
    private static Styles? _dockTheme;

    public static void Apply(string? key = null)
    {
        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        var styles = app.Styles;

        // 挂载 Kei 专属设计令牌字典与全局矢量图标字典
        var merged = app.Resources.MergedDictionaries;
        if (_activeTokens != null)
        {
            merged.Remove(_activeTokens);
        }

        _activeTokens = ClassicTokens;
        merged.Add(_activeTokens);

        if (!merged.Contains(ClassicIcons))
        {
            merged.Add(ClassicIcons);
        }

        // 挂载紧凑控件外观样式
        if (!styles.Contains(ClassicControlStyles))
        {
            styles.Add(ClassicControlStyles);
        }

        // 停靠主题在令牌之后加载，底部标签选择器才能解析到 Kei 颜色。
        _dockTheme ??= LoadStyles("KeiDockTheme.axaml");
        if (!styles.Contains(_dockTheme))
        {
            styles.Add(_dockTheme);
        }
    }

    // 树一行的横向构成（与 KeiControls.axaml 里 KeiTreeViewItem 模板一致，改模板要同步改这里）：
    //   内容左缘 → 箭头槽（容器 16 + 右边距 6）→ 图标（Kei.Tree.IconSize）→ 图标与名称间距。
    private const double TreeChevronSlotWidth = 16;
    private const double TreeChevronGap = 6;
    // 竖线到子项箭头槽左缘的留白：箭头字形在槽内再内缩 2px，实际到手性箭头的间距 ≥ 4px。
    private const double TreeRailClearance = 2;

    // 引导线画在父项图标轴上，子项内容必须整体落在竖线右侧；缩进小于图标轴到箭头槽的距离时线会压住子项箭头。
    public static double MinimumTreeIndent(double iconSize) =>
        TreeChevronSlotWidth + TreeChevronGap + (iconSize > 0 ? iconSize : 14.0) / 2 + TreeRailClearance;

    // 生效缩进：用户缩进偏小时抬到最小值；用户缩进更大时照旧生效（横线随之变长）。
    public static double EffectiveTreeIndent(double indent, double iconSize) =>
        Math.Max(indent > 0 ? indent : 12.0, MinimumTreeIndent(iconSize));

    // 动态注册与更新会话树密度尺寸资源
    public static void ApplyTreeDensity(double itemHeight, double fontSize, double iconSize, double indent)
    {
        var app = Application.Current;
        if (app == null) return;

        var h = itemHeight > 0 ? itemHeight : 22.0;
        var fs = fontSize > 0 ? fontSize : 12.0;
        var icon = iconSize > 0 ? iconSize : 14.0;
        var ind = EffectiveTreeIndent(indent, icon);

        app.Resources["Kei.Tree.ItemHeight"] = h;
        app.Resources["Kei.Tree.FontSize"] = fs;
        app.Resources["Kei.Tree.IconSize"] = icon;
        app.Resources["Kei.Tree.Indent"] = ind;
    }

    private static ResourceDictionary LoadDictionary(string fileName)
        => (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://Kei.Term.App/DesignSystem/{fileName}"), null)!;

    private static Styles LoadStyles(string fileName)
        => (Styles)AvaloniaXamlLoader.Load(new Uri($"avares://Kei.Term.App/DesignSystem/{fileName}"), null)!;
}
