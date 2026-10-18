using System;
using System.Globalization;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;

namespace Kei.Term.App.Views.Controls;

// 根据当前页面是否自行接管滚动，动态切换外层 ScrollViewer 的垂直滚动策略
public sealed class SettingsScrollVisibilityConverter : IValueConverter
{
    public static readonly SettingsScrollVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            return ScrollBarVisibility.Disabled;
        }
        return ScrollBarVisibility.Auto;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
