using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel);
        ResultsList.DoubleTapped += OnResultDoubleTapped;
    }

    public CommandPaletteView(CommandPaletteViewModel viewModel) : this() => DataContext = viewModel;

    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.CaretIndex = QueryBox.Text?.Length ?? 0;
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not CommandPaletteViewModel vm) return;
        switch (args.Key)
        {
            case Key.Escape: vm.CancelCommand.Execute(null); break;
            case Key.Enter: vm.ExecuteSelectedCommand.Execute(null); break;
            case Key.Up: vm.MoveSelection(-1); ScrollToSelection(vm); break;
            case Key.Down: vm.MoveSelection(1); ScrollToSelection(vm); break;
            default: return;
        }
        args.Handled = true;
    }

    private void ScrollToSelection(CommandPaletteViewModel vm)
    {
        if (vm.SelectedResult != null) ResultsList.ScrollIntoView(vm.SelectedResult);
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (args.Source is Avalonia.Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null
            && DataContext is CommandPaletteViewModel vm)
        {
            vm.ExecuteSelectedCommand.Execute(null);
            args.Handled = true;
        }
    }
}
