using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class FileManagerWorkspaceView : UserControl
{
    public FileManagerWorkspaceView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, Activate, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, Activate, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Activate(object? sender, RoutedEventArgs args)
    {
        if (DataContext is FileManagerTabViewModel tab)
            this.FindAncestorOfType<TerminalWorkspaceView>()?.ActivateItem(tab);
    }

    public void FocusDefaultAction()
    {
        if (IsEffectivelyVisible && TopLevel.GetTopLevel(this) is Window window
            && (window is not MainWindow main || !main.IsCommandPaletteOpen)
            && !window.OwnedWindows.Any(child => child.IsVisible))
            FilesView.FindControl<TextBox>("FileManagerPathInput")?.Focus();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        if (DataContext is FileManagerTabViewModel tab)
            this.FindAncestorOfType<TerminalWorkspaceView>()?.RememberView(tab, this);
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is FileManagerTabViewModel { IsSelected: true }) FocusDefaultAction();
        }, DispatcherPriority.Loaded);
    }
}
