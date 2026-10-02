using System.Threading.Tasks;
using Avalonia.Controls;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;

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
        vm.EditPortForwardAsync = existing => EditPortForwardAsync(vm, existing);

        KeyDown += (sender, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private async Task<PortForward?> EditPortForwardAsync(SessionEditViewModel owner, PortForward? existing)
    {
        var editVm = new PortForwardEditViewModel(existing, owner.NodeId);
        await new PortForwardEditWindow(editVm).ShowDialog(this);
        return editVm.IsConfirmed ? editVm.Build() : null;
    }
}
