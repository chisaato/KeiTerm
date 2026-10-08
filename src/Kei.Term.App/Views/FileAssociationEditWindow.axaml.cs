using Avalonia.Controls;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class FileAssociationEditWindow : Window
{
    public FileAssociationEditWindow() => InitializeComponent();

    public FileAssociationEditWindow(FileAssociationEditViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;
        Closed += (_, _) => vm.RequestClose -= Close;
    }
}
