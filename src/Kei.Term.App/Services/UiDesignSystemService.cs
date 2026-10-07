using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Kei.Term.App.Services;

// 设计系统服务：以 FluentTheme 为基座，挂载 KeiTerm 专属紧凑桌面设计令牌与控件样式。
public static class UiDesignSystemService
{
    public const string KeiClassicKey = "KeiClassic";
    public const string KeiDarkKey = "Dark";

    // KeiTerm 专属桌面设计令牌字典（深色工业桌面风格）
    private static readonly ResourceDictionary ClassicTokens = LoadDictionary("KeiTokens.axaml");

    // KeiTerm 专属全局矢量图标字典
    private static readonly ResourceDictionary ClassicIcons = LoadDictionary("KeiIcons.axaml");

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

    // 动态注册与更新会话树密度尺寸资源
    public static void ApplyTreeDensity(double itemHeight, double fontSize, double iconSize, double indent)
    {
        var app = Application.Current;
        if (app == null) return;

        var h = itemHeight > 0 ? itemHeight : 22.0;
        var fs = fontSize > 0 ? fontSize : 12.0;
        var icon = iconSize > 0 ? iconSize : 14.0;
        var ind = indent > 0 ? indent : 12.0;

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
