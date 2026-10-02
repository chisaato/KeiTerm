using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Kei.Term.App.Converters;

// 调色器色块的数据是十六进制字符串。绑定不会走 XAML 类型转换，不转成画刷就是空块。
public sealed class HexStringToBrushConverter : IValueConverter
{
    public static readonly HexStringToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || !Color.TryParse(hex, out Color color))
        {
            return new SolidColorBrush(Colors.Transparent);
        }

        return new SolidColorBrush(color);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
