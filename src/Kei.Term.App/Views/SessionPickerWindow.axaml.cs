using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;

namespace Kei.Term.App.Views;

public partial class SessionPickerWindow : Window
{
    public SessionPickerWindow()
    {
        InitializeComponent();
    }

    public SessionPickerWindow(SessionPickerViewModel vm) : this()
    {
        PickerSessionOpacity.Current = vm;
        DataContext = vm;
        vm.RequestClose += () =>
        {
            PickerSessionOpacity.Current = null;
            Close(vm.Result);
        };
        Closed += (_, _) => PickerSessionOpacity.Current = null;
    }

    private void Tree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SessionPickerViewModel vm)
        {
            return;
        }

        if (e.Source is not Control source)
        {
            return;
        }

        TreeNodeBase? node = source.DataContext as TreeNodeBase
            ?? source.FindAncestorOfType<TreeViewItem>()?.DataContext as TreeNodeBase;
        vm.Activate(node);
        if (vm.IsConfirmed)
        {
            e.Handled = true;
            Close(vm.Result);
        }
    }
}

// 不可选会话留在树上，但明显淡出。对话框期间只有一个选择器。
public sealed class PickerSessionOpacity : IValueConverter
{
    public static PickerSessionOpacity Instance { get; } = new();
    public static SessionPickerViewModel? Current { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (Current != null && value is SessionNode session && !Current.IsSelectable(session))
        {
            return 0.4;
        }

        return 1.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
