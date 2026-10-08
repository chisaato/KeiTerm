using Avalonia;
using Avalonia.Controls;

namespace Kei.Term.App.Views.Controls;

public partial class TerminalFindBar : UserControl
{
    public TerminalFindBar()
    {
        InitializeComponent();
    }

    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            FocusQuery();
        }
    }
}
