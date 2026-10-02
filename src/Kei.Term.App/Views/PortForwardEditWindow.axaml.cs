using Avalonia.Controls;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class PortForwardEditWindow : Window
{
    public PortForwardEditWindow()
    {
        InitializeComponent();
    }

    public PortForwardEditWindow(PortForwardEditViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;
    }
}
