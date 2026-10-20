using System;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Kei.Term.App.Models;

namespace Kei.Term.App.Converters;

// 将通知级别 NotificationLevel 转换为图标几何与画刷
public static class NotificationLevelConverters
{
    public static readonly IValueConverter LevelToBrush =
        new FuncValueConverter<NotificationLevel, IBrush>(level =>
        {
            string key = level switch
            {
                NotificationLevel.Success => "Kei.Status.Connected",
                NotificationLevel.Warning => "Kei.Status.Connecting",
                NotificationLevel.Error => "Kei.Status.Error",
                _ => "Kei.Status.Info",
            };
            if (Avalonia.Application.Current?.TryFindResource(key, out object? resource) == true && resource is IBrush brush)
            {
                return brush;
            }
            return Brushes.DodgerBlue;
        });

    public static readonly IValueConverter LevelToGeometry =
        new FuncValueConverter<NotificationLevel, StreamGeometry?>(level =>
        {
            string key = level switch
            {
                NotificationLevel.Success => "Kei.Icon.Check",
                NotificationLevel.Warning => "Kei.Icon.Warning",
                NotificationLevel.Error => "Kei.Icon.Close",
                _ => "Kei.Icon.Terminal",
            };
            if (Avalonia.Application.Current?.TryFindResource(key, out object? resource) == true && resource is StreamGeometry geo)
            {
                return geo;
            }
            return null;
        });
}
