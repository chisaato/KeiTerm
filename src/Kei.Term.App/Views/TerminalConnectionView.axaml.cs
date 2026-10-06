using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Workspaces;

namespace Kei.Term.App.Views;

// 一次打开的终端和文件管理器。数据上下文是文档，点击或焦点进入时激活所属连接。
public partial class TerminalConnectionView : UserControl
{
    public TerminalConnectionView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RegisterCache();
        ClaimTerminal();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseTerminal();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RegisterCache();
        ClaimTerminal();
    }

    // 终端控件只能有一个父级。切换时新视图会先出现，必须先从旧滚动区摘下来。
    private void ClaimTerminal()
    {
        TerminalTabViewModel? tab = ResolveTab();
        if (tab == null)
        {
            return;
        }

        Control terminal = tab.Terminal;
        if (ReferenceEquals(TerminalScroll.Content, terminal))
        {
            return;
        }

        ReleaseFromOwner(terminal);
        TerminalScroll.Content = terminal;
    }

    private TerminalTabViewModel? ResolveTab()
    {
        return DataContext switch
        {
            TerminalWorkspaceDocument document => document.Tab,
            TerminalTabViewModel tab => tab,
            _ => null
        };
    }

    private void ReleaseTerminal()
    {
        if (TerminalScroll.Content != null)
        {
            TerminalScroll.Content = null;
        }
    }

    private static void ReleaseFromOwner(Control terminal)
    {
        if (terminal.Parent is ScrollContentPresenter presenter && presenter.TemplatedParent is ScrollViewer scroll)
        {
            scroll.Content = null;
            return;
        }

        switch (terminal.Parent)
        {
            case ScrollViewer direct:
                direct.Content = null;
                break;
            case ContentPresenter contentPresenter:
                contentPresenter.Content = null;
                break;
            case ContentControl content when ReferenceEquals(content.Content, terminal):
                content.Content = null;
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, terminal):
                decorator.Child = null;
                break;
            case Panel panel:
                panel.Children.Remove(terminal);
                break;
        }
    }

    // Dock 的内容模板不一定把视图放进回收器。这里按文档实例补登记，关闭时才能只删这一份。
    private void RegisterCache()
    {
        if (DataContext is not TerminalWorkspaceDocument document)
        {
            return;
        }

        this.FindAncestorOfType<TerminalWorkspaceView>()?.RememberView(document, this);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 中键关闭由工作区处理，避免在终端内容里误关连接。
        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        ActivateFromContext();
    }

    private void OnGotFocus(object? sender, FocusChangedEventArgs e) => ActivateFromContext();

    private void ActivateFromContext()
    {
        TerminalTabViewModel? tab = ResolveTab();
        if (tab == null)
        {
            return;
        }

        this.FindAncestorOfType<TerminalWorkspaceView>()?.ActivateConnection(tab);
        if (this.FindAncestorOfType<MainWindow>()?.DataContext is MainViewModel mainVm)
        {
            mainVm.SelectTabCommand.Execute(tab);
        }
    }
}
