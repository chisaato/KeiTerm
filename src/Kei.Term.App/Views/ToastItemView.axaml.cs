using Avalonia.Controls;
using Avalonia.Input;
using Kei.Term.App.ViewModels.Notifications;

namespace Kei.Term.App.Views;

public partial class ToastItemView : UserControl
{
    public ToastItemView()
    {
        InitializeComponent();
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is ToastItemViewModel vm)
        {
            vm.PauseDismiss();
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is ToastItemViewModel vm)
        {
            vm.ResumeDismiss();
        }
    }
}
