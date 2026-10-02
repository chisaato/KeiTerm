using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class ColorChooserWindow : Window
{
    public ColorChooserWindow()
    {
        InitializeComponent();

        ColorPlane.HueSatChanged += (h, s) =>
        {
            if (DataContext is ColorChooserViewModel vm)
            {
                vm.UpdateHsv(h, s, vm.Val);
            }
        };

        ValueBar.ValChanged += (v) =>
        {
            if (DataContext is ColorChooserViewModel vm)
            {
                vm.UpdateHsv(vm.Hue, vm.Sat, v);
            }
        };
    }

    public ColorChooserWindow(ColorChooserViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ColorChooserViewModel vm)
        {
            vm.Confirm();
            Close(true);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
