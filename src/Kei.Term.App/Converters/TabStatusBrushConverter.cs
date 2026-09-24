namespace Kei.Term.App.Converters;

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Kei.Term.App.ViewModels;

// 将标签连接状态映射为主题画刷 Kei.Status.*（指示条颜色）
public class ConnectionStateBrushConverter : IValueConverter
{
    // ConnectionState → 主题资源键
    private static readonly Dictionary<ConnectionState, string> ResourceKeys = new()
    {
        [ConnectionState.Connecting] = "Kei.Status.Connecting",
        [ConnectionState.Connected] = "Kei.Status.Connected",
        [ConnectionState.Disconnected] = "Kei.Status.Disconnected",
        [ConnectionState.Error] = "Kei.Status.Error"
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        ConnectionState? state = value switch
        {
            ConnectionState s => s,
#pragma warning disable CS0618
            TabStatus ts => (ConnectionState)ts,
#pragma warning restore CS0618
            _ => null
        };

        if (state.HasValue
            && ResourceKeys.TryGetValue(state.Value, out var key)
            && Application.Current?.Resources.TryGetResource(key, null, out var resource) == true
            && resource is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

// 保持平滑兼容保留派生类以防破坏旧 XAML 引用
public class TabStatusBrushConverter : ConnectionStateBrushConverter
{
}
