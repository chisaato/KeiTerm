using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.Converters;

public sealed class FileSizeConverter : IMultiValueConverter
{
    public static readonly FileSizeConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count >= 2 && values[0] is long bytes && values[1] is FileSizeDisplayMode mode
            ? FileSizeFormatter.Format(bytes, mode, culture)
            : BindingOperations.DoNothing;
}
