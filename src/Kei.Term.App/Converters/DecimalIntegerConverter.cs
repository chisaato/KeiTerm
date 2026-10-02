using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Kei.Term.App.Converters;

// NumericUpDown.Value 是 decimal?。调色器通道是 int/byte，不转的话微调框写不回去。
public sealed class DecimalIntegerConverter : IValueConverter
{
    public static readonly DecimalIntegerConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            byte b => (decimal)b,
            int i => (decimal)i,
            long l => (decimal)l,
            decimal d => d,
            double n => (decimal)n,
            _ => 0m
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not decimal number)
        {
            return targetType == typeof(byte) ? (byte)0 : 0;
        }

        int rounded = (int)Math.Round(number, MidpointRounding.AwayFromZero);
        if (targetType == typeof(byte))
        {
            return (byte)Math.Clamp(rounded, 0, 255);
        }

        return rounded;
    }
}
