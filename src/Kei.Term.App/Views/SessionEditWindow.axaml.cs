using Avalonia.Controls;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class SessionEditWindow : Window
{
    public SessionEditWindow()
    {
        InitializeComponent();
    }

    public SessionEditWindow(SessionEditViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;

        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }
}
