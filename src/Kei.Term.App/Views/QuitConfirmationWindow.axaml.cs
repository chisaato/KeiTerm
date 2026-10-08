using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kei.Term.App.Helpers;
using Kei.Term.App.Services;

namespace Kei.Term.App.Views;

public partial class QuitConfirmationWindow : Window
{
    private readonly QuitTrigger _trigger;
    public bool DoNotAskAgain => DoNotAskAgainCheckBox.IsChecked == true;

    public QuitConfirmationWindow() : this(QuitTrigger.Application) { }

    public QuitConfirmationWindow(QuitTrigger trigger)
    {
        _trigger = trigger;
        InitializeComponent();
        ConfirmationMessage.Text = Strings.Format("Quit.Confirm.Message", AppShortcuts.Label(
            trigger == QuitTrigger.CloseWindow ? AppShortcuts.CloseTab : AppShortcuts.QuitApplication));
        ConfirmationDetail.Text = Strings.Get("Quit.Confirm.Detail");
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => CancelButton.Focus();
    }

    // 原生菜单可能把 ⌘W 发给主窗口；与弹窗内的按键共用同一行为。
    public void HandleCloseShortcut()
    {
        // ⌘W 只确认由它自己打开的提示；⌘Q 提示中忽略它，也不关闭背后的标签。
        if (_trigger == QuitTrigger.CloseWindow) Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs args) => Close(false);

    private void OnExitClick(object? sender, RoutedEventArgs args) => Close(true);

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            Close(false);
        }
        else if (AppShortcuts.CloseTab.Matches(args))
        {
            args.Handled = true;
            HandleCloseShortcut();
        }
        else if (AppShortcuts.QuitApplication.Matches(args))
        {
            args.Handled = true;
            if (_trigger == QuitTrigger.Application) Close(true);
        }
    }
}
