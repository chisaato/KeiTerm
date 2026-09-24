using Avalonia.Controls;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class FolderEditWindow : Window
{
    public FolderEditWindow()
    {
        InitializeComponent();
    }

    public FolderEditWindow(FolderEditViewModel vm) : this()
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
