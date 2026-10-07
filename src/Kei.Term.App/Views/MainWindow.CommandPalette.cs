using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

// 快捷面板留在主窗口内；选中、Esc、点空白或关窗口都完成同一个交互任务。
public partial class MainWindow
{
    private TaskCompletionSource<CommandPaletteItem?>? _paletteCompletion;
    private CommandPaletteViewModel? _paletteModel;
    private Control? _palettePreviousFocus;

    public Task<CommandPaletteItem?> ShowCommandPaletteAsync(CommandPaletteViewModel viewModel)
    {
        CompleteCommandPalette();
        _paletteCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _paletteModel = viewModel;
        _palettePreviousFocus = FocusManager?.GetFocusedElement() as Control;
        CommandPaletteView view = new(viewModel);
        viewModel.RequestClose += CompleteCommandPalette;
        CommandPaletteHost.Content = view;
        CommandPaletteOverlay.IsVisible = true;
        Closed += OnPaletteOwnerClosed;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_paletteModel, viewModel)) view.FocusQuery();
        }, DispatcherPriority.Loaded);
        return _paletteCompletion.Task;
    }

    private void CommandPaletteOverlay_PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Avalonia.Visual source && !IsUnder(CommandPaletteHost, source)) CompleteCommandPalette();
    }

    private void OnPaletteOwnerClosed(object? sender, EventArgs args) => CompleteCommandPalette();

    private void CompleteCommandPalette()
    {
        if (_paletteCompletion == null) return;
        TaskCompletionSource<CommandPaletteItem?> completion = _paletteCompletion;
        CommandPaletteViewModel? model = _paletteModel;
        _paletteCompletion = null;
        _paletteModel = null;
        if (model != null) model.RequestClose -= CompleteCommandPalette;
        CommandPaletteOverlay.IsVisible = false;
        CommandPaletteHost.Content = null;
        Closed -= OnPaletteOwnerClosed;
        if (_palettePreviousFocus is { IsEffectivelyVisible: true } previous && TopLevel.GetTopLevel(previous) == this) previous.Focus();
        _palettePreviousFocus = null;
        completion.TrySetResult(model?.IsConfirmed == true ? model.Result : null);
    }
}
