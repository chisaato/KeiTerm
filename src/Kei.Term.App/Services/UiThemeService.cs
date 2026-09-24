using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Kei.Term.App.Services;

// 界面主题服务：以 FluentTheme 为基座，挂载 KeiTerm 专属紧凑桌面设计令牌与控件样式。
public static class UiThemeService
{
    public const string KeiClassicKey = "KeiClassic";
    public const string KeiDarkKey = "Dark";

    // KeiTerm 专属桌面令牌字典（深色工业桌面风格）
    private static readonly ResourceDictionary ClassicBrushes = LoadDictionary("KeiBrush-KeiClassic.axaml");

    // Kei 紧凑控件外观样式（工具栏按钮、标签页、输入框、下拉框高密度微调等）
    private static readonly Styles ClassicCompatStyles = LoadStyles("KeiClassicCompat.axaml");

    private static ResourceDictionary? _activeBrushes;

    public static void Apply(string? key = null)
    {
        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        var styles = app.Styles;

        // 挂载 Kei 专属画刷字典
        var merged = app.Resources.MergedDictionaries;
        if (_activeBrushes != null)
        {
            merged.Remove(_activeBrushes);
        }

        _activeBrushes = ClassicBrushes;
        merged.Add(_activeBrushes);

        // 挂载紧凑控件外观样式
        if (!styles.Contains(ClassicCompatStyles))
        {
            styles.Add(ClassicCompatStyles);
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
        => (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://Kei.Term.App/Themes/{fileName}"), null)!;

    private static Styles LoadStyles(string fileName)
        => (Styles)AvaloniaXamlLoader.Load(new Uri($"avares://Kei.Term.App/Themes/{fileName}"), null)!;
}
