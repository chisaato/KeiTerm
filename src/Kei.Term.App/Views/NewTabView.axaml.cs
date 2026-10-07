using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Threading;
using System.Linq;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class NewTabView : UserControl
{
    public NewTabView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, Activate, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, Activate, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Activate(object? sender, RoutedEventArgs args)
    {
        if (DataContext is NewTabViewModel tab)
            this.FindAncestorOfType<TerminalWorkspaceView>()?.ActivateItem(tab);
    }

    public void FocusDefaultAction()
    {
        // 模板可能晚于模态弹窗挂载，此时不能把键盘焦点抢回主窗口。
        if (IsEffectivelyVisible && TopLevel.GetTopLevel(this) is Window window
            && !window.OwnedWindows.Any(child => child.IsVisible)) ConnectSavedSessionButton.Focus();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        // Dock 的文档模板在选择消息之后挂载，焦点必须等实际视图出现。
        Dispatcher.UIThread.Post(() =>
        {
            if (IsEffectivelyVisible && DataContext is NewTabViewModel { IsSelected: true }) FocusDefaultAction();
        }, DispatcherPriority.Loaded);
    }
}
