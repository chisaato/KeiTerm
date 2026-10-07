using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Workspaces;

namespace Kei.Term.App.Views;

// 一次打开的终端和文件管理器。数据上下文是文档，点击或焦点进入时激活所属连接。
public partial class TerminalConnectionView : UserControl
{
    private SidebarTransition? _fileManagerTransition;
    private TerminalTabViewModel? _fileManagerTab;
    private Window? _fileManagerWindow;
    private bool _fileManagerPresentationInitialized;
    private bool _fileManagerTargetVisible;
    private bool _fileManagerPresentedOnLeft;
    private double _lastFileManagerWidth = 460;

    public TerminalConnectionView()
    {
        InitializeComponent();
        _fileManagerTransition = new SidebarTransition(SftpSurface);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindFileManager();
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            _fileManagerWindow = window;
            window.Closed += OnFileManagerWindowClosed;
        }
        RegisterCache();
        ClaimTerminal();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnbindFileManager();
        ReleaseTerminal();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is TerminalWorkspaceDocument document)
        {
            DataContext = document.Tab;
            return;
        }
        BindFileManager();
        RegisterCache();
        ClaimTerminal();
    }

    private void BindFileManager()
    {
        TerminalTabViewModel? tab = ResolveTab();
        if (!ReferenceEquals(_fileManagerTab, tab))
        {
            if (_fileManagerTab != null) _fileManagerTab.PropertyChanged -= OnFileManagerPropertyChanged;
            _fileManagerTransition?.Cancel();
            _fileManagerTab = tab;
            _fileManagerPresentationInitialized = false;
            if (tab != null) tab.PropertyChanged += OnFileManagerPropertyChanged;
        }
        if (tab != null) UpdateFileManagerLayout(tab);
        else if (_fileManagerTransition != null)
        {
            _fileManagerTransition.SetImmediate(false);
            SftpHost.IsVisible = false;
            SftpHost.IsHitTestVisible = false;
            FileManagerSplitter.IsVisible = false;
            FileManagerSplitter.IsHitTestVisible = false;
            ApplyFileManagerColumns(false);
        }
    }

    private void UnbindFileManager()
    {
        _fileManagerTransition?.Cancel();
        if (_fileManagerTab != null) _fileManagerTab.PropertyChanged -= OnFileManagerPropertyChanged;
        _fileManagerTab = null;
        _fileManagerPresentationInitialized = false;
        if (_fileManagerWindow != null) _fileManagerWindow.Closed -= OnFileManagerWindowClosed;
        _fileManagerWindow = null;
    }

    private void OnFileManagerWindowClosed(object? sender, EventArgs args) => UnbindFileManager();

    private void OnFileManagerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is TerminalTabViewModel tab
            && args.PropertyName is nameof(TerminalTabViewModel.IsFileManagerVisible) or nameof(TerminalTabViewModel.IsFileManagerOnLeft))
            UpdateFileManagerLayout(tab);
    }

    private void UpdateFileManagerLayout(TerminalTabViewModel tab)
    {
        if (_fileManagerTransition == null) return;
        ColumnDefinition currentColumn = ConnectionLayout.ColumnDefinitions[_fileManagerPresentedOnLeft ? 0 : 2];
        if (SftpHost.IsVisible && currentColumn.Width.IsAbsolute && currentColumn.Width.Value > 1)
            _lastFileManagerWidth = currentColumn.Width.Value;

        bool visible = tab.IsFileManagerVisible;
        bool fromHidden = !SftpHost.IsVisible;
        bool moved = _fileManagerPresentationInitialized && _fileManagerPresentedOnLeft != tab.IsFileManagerOnLeft;
        _fileManagerPresentedOnLeft = tab.IsFileManagerOnLeft;
        ApplyFileManagerColumns(visible || SftpHost.IsVisible);
        if (visible) SftpHost.IsVisible = true;
        SftpHost.IsHitTestVisible = visible;
        FileManagerSplitter.IsVisible = visible || SftpHost.IsVisible;
        FileManagerSplitter.IsHitTestVisible = visible;

        if (!visible && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focus
            && (ReferenceEquals(focus, SftpHost) || focus.GetVisualAncestors().Contains(SftpHost)))
            tab.Terminal.Focus();

        if (!_fileManagerPresentationInitialized || moved)
        {
            _fileManagerTransition.SetImmediate(visible);
            SftpHost.IsVisible = visible;
            if (!visible) CompleteFileManagerHide(tab);
        }
        else if (visible != _fileManagerTargetVisible)
        {
            _fileManagerTransition.Start(visible, tab.IsFileManagerOnLeft ? -12 : 12, fromHidden, () =>
            {
                if (!visible) CompleteFileManagerHide(tab);
            });
        }
        _fileManagerTargetVisible = visible;
        _fileManagerPresentationInitialized = true;
    }

    private void ApplyFileManagerColumns(bool presented)
    {
        GridLength fileWidth = new(presented ? _lastFileManagerWidth : 0);
        GridLength terminalWidth = new(1, GridUnitType.Star);
        ConnectionLayout.ColumnDefinitions[0].Width = _fileManagerPresentedOnLeft ? fileWidth : terminalWidth;
        ConnectionLayout.ColumnDefinitions[2].Width = _fileManagerPresentedOnLeft ? terminalWidth : fileWidth;
    }

    private void CompleteFileManagerHide(TerminalTabViewModel tab)
    {
        SftpHost.IsVisible = false;
        FileManagerSplitter.IsVisible = false;
        ApplyFileManagerColumns(false);
        // 实际释放列宽晚于 VM 状态，完成布局后再次同步 SSH 终端尺寸。
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_fileManagerTab, tab) && !tab.IsFileManagerVisible) tab.TrySyncTerminalSize();
        }, DispatcherPriority.Render);
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
        if (ResolveTab() is not { } tab)
        {
            return;
        }

        this.FindAncestorOfType<TerminalWorkspaceView>()?.RememberView(tab, this);
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
