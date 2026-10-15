using System;
using System.Linq;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Kei.Term.App.Views;

public partial class TerminalConnectionView
{
    private void InitializeTerminalFileDrop()
    {
        DragDrop.SetAllowDrop(TerminalScroll, true);
        // Avalonia 拖放事件使用冒泡路由；仍接收子控件已处理的事件。
        TerminalScroll.AddHandler(DragDrop.DragOverEvent, OnTerminalDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        TerminalScroll.AddHandler(DragDrop.DropEvent, OnTerminalDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnTerminalDragOver(object? sender, DragEventArgs e)
    {
        bool accepts = ResolveTab() is { IsDisposed: false, State: ViewModels.ConnectionState.Connected, IsHandlingTerminalDrop: false }
            && e.DataTransfer.TryGetFiles()?.Any(file => file.TryGetLocalPath() != null) == true;
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTerminalDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ResolveTab() is not { } tab) return;
        string[] paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath())
            .Where(path => path != null).Cast<string>().ToArray() ?? [];
        await tab.HandleTerminalFileDropAsync(paths);
    }
}
