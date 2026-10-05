using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class FontDialogWindow : Window
{
    public FontDialogWindow()
    {
        InitializeComponent();
    }

    public FontDialogWindow(FontDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FontDialogViewModel vm)
        {
            vm.Confirm();
            if (!vm.IsConfirmed)
            {
                return;
            }
        }

        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FontDialogViewModel vm)
        {
            vm.Cancel();
        }

        Close(false);
    }
}
