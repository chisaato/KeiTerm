using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;

namespace Kei.Term.App.Views;

public partial class ProxyEditWindow : Window
{
    public ProxyEditWindow()
    {
        InitializeComponent();
    }

    public ProxyEditWindow(ProxyEditViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;
        Closed += (_, _) => vm.RequestClose -= Close;
    }
}

public sealed class ProxyTypeLabelConverter : IValueConverter
{
    public static ProxyTypeLabelConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProxyConfigKind kind)
        {
            return value?.ToString();
        }

        return kind switch
        {
            ProxyConfigKind.Http => "HTTP",
            ProxyConfigKind.Session => "已有会话",
            _ => "SOCKS5"
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
