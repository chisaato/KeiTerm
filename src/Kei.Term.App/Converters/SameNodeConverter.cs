using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Kei.Term.Core.Models;

namespace Kei.Term.App.Converters;

// 当前树节点是否就是正在原地改名的那一项
public sealed class SameNodeConverter : IMultiValueConverter
{
    public static readonly SameNodeConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not TreeNodeBase node || values[1] is not TreeNodeBase renaming)
        {
            return false;
        }

        return ReferenceEquals(node, renaming);
    }
}
