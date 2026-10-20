using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Kei.Term.App.Helpers;

namespace Kei.Term.App.Converters;

// 根据 bool 展开状态返回 "收起" 或 "展开"
public class ToastExpandTextConverter : IValueConverter
{
    public static readonly ToastExpandTextConverter Instance = new();

    public object? Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        bool isExpanded = value is true;
        return Strings.Get(isExpanded ? "Toast.Action.Collapse" : "Toast.Action.Expand");
    }

    public object? ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
