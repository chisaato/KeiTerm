using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kei.Term.App.ViewModels.Notifications;

namespace Kei.Term.App.Views;

public partial class ToastItemView : UserControl
{
    // 约 30fps 刷新倒计时条，数值直接读自 ViewModel 的真实计时
    private readonly DispatcherTimer _barTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    public ToastItemView()
    {
        InitializeComponent();
        _barTimer.Tick += (_, _) => UpdateCountdownBar();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateCountdownBar();
        _barTimer.Start();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _barTimer.Stop();
        base.OnUnloaded(e);
    }

    private void UpdateCountdownBar()
    {
        if (DataContext is not ToastItemViewModel vm || !vm.HasAutoDismiss)
        {
            return;
        }

        double width = CountdownBar.Parent is Control parent ? parent.Bounds.Width : 0;
        CountdownBar.Width = Math.Max(0, width * vm.RemainingFraction);
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
