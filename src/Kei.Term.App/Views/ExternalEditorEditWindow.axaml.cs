using Avalonia.Controls;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class ExternalEditorEditWindow : Window
{
    public ExternalEditorEditWindow() => InitializeComponent();

    public ExternalEditorEditWindow(ExternalEditorEditViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose += Close;
        Closed += (_, _) => vm.RequestClose -= Close;
    }
}
