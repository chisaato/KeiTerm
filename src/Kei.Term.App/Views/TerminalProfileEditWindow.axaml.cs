using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class TerminalProfileEditWindow : Window
{
    public TerminalProfileEditWindow()
    {
        InitializeComponent();
    }

    public TerminalProfileEditWindow(TerminalProfileEditViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalProfileEditViewModel vm)
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
