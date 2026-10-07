using Avalonia.Controls;

namespace Kei.Term.App.Views;

public partial class NewTabView : UserControl
{
    public NewTabView()
    {
        InitializeComponent();
    }

    public void FocusDefaultAction() => ConnectSavedSessionButton.Focus();
}
