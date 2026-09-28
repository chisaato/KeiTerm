using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Kei.Term.App.Converters;

public class BoolToGridLengthConverter : IValueConverter
{
    public static readonly BoolToGridLengthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
        {
            return new GridLength(320, GridUnitType.Pixel);
        }
        return new GridLength(0, GridUnitType.Pixel);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class FileManagerDockConverter : IValueConverter
{
    public static readonly FileManagerDockConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool onLeft = value is bool b && b;
        string role = parameter as string ?? string.Empty;

        return role switch
        {
            "FileManager" => onLeft ? 0 : 2,
            "Splitter" => 1,
            "Terminal" => onLeft ? 2 : 0,
            _ => 0
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// 根据侧栏是否可见及停靠方向，计算左右两侧 ColumnDefinition 的 Width
// parameter: "Left" | "Right"
public class FileManagerColumnWidthConverter : IMultiValueConverter
{
    public static readonly FileManagerColumnWidthConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isVisible = values.Count > 0 && values[0] is bool v && v;
        bool isOnLeft = values.Count > 1 && values[1] is bool l && l;
        string side = parameter as string ?? "Right";

        if (!isVisible)
        {
            if (side == "Left")
            {
                return isOnLeft ? new GridLength(0, GridUnitType.Pixel) : new GridLength(1, GridUnitType.Star);
            }
            else // "Right"
            {
                return isOnLeft ? new GridLength(1, GridUnitType.Star) : new GridLength(0, GridUnitType.Pixel);
            }
        }

        // isVisible == true
        if (side == "Left")
        {
            return isOnLeft ? new GridLength(460, GridUnitType.Pixel) : new GridLength(1, GridUnitType.Star);
        }
        else if (side == "Right")
        {
            return isOnLeft ? new GridLength(1, GridUnitType.Star) : new GridLength(460, GridUnitType.Pixel);
        }

        return new GridLength(1, GridUnitType.Star);
    }
}
