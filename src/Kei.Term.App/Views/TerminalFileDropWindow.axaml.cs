using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class TerminalFileDropWindow : Window
{
    public TerminalFileDropWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    public TerminalFileDropWindow(TerminalFileDropPromptViewModel model) : this() => DataContext = model;

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalFileDropPromptViewModel model) Close(model.BuildChoice());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { OnConfirm(sender, e); e.Handled = true; }
    }
}
