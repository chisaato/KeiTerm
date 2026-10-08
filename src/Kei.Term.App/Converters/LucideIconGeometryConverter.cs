using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Kei.Term.App.DesignSystem;

namespace Kei.Term.App.Converters;

// 设置页使用字符串图标数据，与全局图标应用相同的轮廓归一化。
public sealed class LucideIconGeometryConverter : IValueConverter
{
    public static LucideIconGeometryConverter Instance { get; } = new();
    private readonly Dictionary<string, Geometry> _cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path) return null;
        if (!_cache.TryGetValue(path, out Geometry? geometry))
        {
            geometry = LucideIconGeometry.Normalize(Geometry.Parse(path));
            _cache.Add(path, geometry);
        }
        return geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
