using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

// 在同一视觉树内切换固定/覆盖布局，保留节点选择、右键菜单与拖放事件。
public partial class MainWindow
{
    private const double SidebarDefaultWidth = 260;
    private const int SidebarHoverHideDelayMilliseconds = 220;
    private readonly DispatcherTimer _sessionManagerHoverTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(SidebarHoverHideDelayMilliseconds)
    };
    private double _lastSidebarWidth = SidebarDefaultWidth;
    private bool _floatingSessionManagerOpen;
    private Control? _previousSessionManagerFocus;
    private IPointer? _sessionManagerPointer;
    private bool _sessionManagerContextRequestActive;
    private bool _sessionManagerClosed;
    private bool _sessionManagerAnimationVisible;
    private bool _sessionManagerAnimationPinned;

    private void SetUpSessionManager()
    {
        AddHandler(PointerPressedEvent, SessionManager_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, SessionManager_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, SessionManager_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(ContextRequestedEvent, SessionManager_ContextRequested,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, SessionManager_KeyDown, RoutingStrategies.Tunnel);
        SessionManagerToggleButton.PointerEntered += SessionManager_PointerEntered;
        SessionManagerToggleButton.PointerExited += SessionManager_PointerExited;
        SessionManagerBorder.PointerEntered += SessionManager_PointerEntered;
        SessionManagerBorder.PointerExited += SessionManager_PointerExited;
        SessionManagerRevealZone.PointerEntered += SessionManager_PointerEntered;
        SessionManagerRevealZone.PointerExited += SessionManager_PointerExited;
        PointerExited += SessionManager_PointerExited;
        _sessionManagerHoverTimer.Tick += SessionManager_HoverTimerTick;
        Closed += SessionManager_Closed;
    }

    private void UpdateSessionManagerLayout(MainViewModel model)
    {
        ColumnDefinition sidebarColumn = MainSplitGrid.ColumnDefinitions[0];
        if (sidebarColumn.Width.IsAbsolute && sidebarColumn.Width.Value > 1)
            _lastSidebarWidth = sidebarColumn.Width.Value;

        bool docked = model.IsSessionManagerDocked;
        sidebarColumn.Width = new GridLength(docked ? _lastSidebarWidth : 0);
        // 面板收起和悬浮时都彻底释放分割条占用的宽度。
        MainSplitGrid.ColumnDefinitions[1].Width = new GridLength(docked ? 4 : 0);
        Grid.SetColumnSpan(SessionManagerBorder, docked ? 1 : 3);
        SessionManagerBorder.HorizontalAlignment = docked ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        SessionManagerBorder.Width = docked ? double.NaN : _lastSidebarWidth;
        SessionManagerBorder.ZIndex = docked ? 0 : 10;

        bool floatingOpen = model.IsSessionManagerVisible && !model.IsSessionManagerPinned;
        bool canReveal = !model.IsSessionManagerPinned && !model.IsSessionManagerVisible;
        SessionManagerRevealZone.IsVisible = canReveal;
        SessionManagerRevealZone.IsHitTestVisible = canReveal;
        if (floatingOpen && !_floatingSessionManagerOpen)
        {
            Control? focus = FocusManager?.GetFocusedElement() as Control;
            _previousSessionManagerFocus = focus != null && !IsUnder(SessionManagerBorder, focus) ? focus : null;
            // 悬停展开只改变布局；输入焦点继续留在终端或原控件。
        }
        else if (!model.IsSessionManagerVisible && _floatingSessionManagerOpen)
        {
            Control? focus = FocusManager?.GetFocusedElement() as Control;
            if (focus != null && IsUnder(SessionManagerBorder, focus)
                && _previousSessionManagerFocus is { IsEffectivelyVisible: true } previous)
                previous.Focus();
            _previousSessionManagerFocus = null;
        }
        _floatingSessionManagerOpen = floatingOpen;
        UpdateSessionManagerEntranceAnimation(model);
        if (!floatingOpen || IsPointerInSessionManager()) _sessionManagerHoverTimer.Stop();
        else ScheduleSessionManagerHoverHide();
    }

    private void UpdateSessionManagerEntranceAnimation(MainViewModel model)
    {
        bool visible = model.IsSessionManagerVisible;
        bool pinned = model.IsSessionManagerPinned;
        if (visible == _sessionManagerAnimationVisible && pinned == _sessionManagerAnimationPinned) return;

        // 移除样式立即取消原生动画，恢复基础 Opacity/Transform，不留下异步任务。
        SessionManagerSurface.Classes.Remove("revealing");
        if (visible && !_sessionManagerAnimationVisible && !_sessionManagerClosed)
            SessionManagerSurface.Classes.Add("revealing");
        _sessionManagerAnimationVisible = visible;
        _sessionManagerAnimationPinned = pinned;
    }

    private bool IsPointerInSessionManager() => SessionManagerToggleButton.IsPointerOver
        || SessionManagerBorder.IsPointerOver
        || SessionManagerRevealZone.IsPointerOver;

    private bool IsSessionManagerInteractionActive(MainViewModel model)
    {
        if (model.RenamingNode != null || _activeDraggedId.HasValue || _isDraggingTab
            || _sessionManagerContextRequestActive || OwnedWindows.Any(window => window.IsVisible)) return true;
        if (_sessionManagerPointer?.Captured is Visual captured && IsUnder(SessionManagerBorder, captured)) return true;
        return SessionManagerBorder.GetVisualDescendants().OfType<Control>()
            .Any(control => control.ContextMenu is { IsOpen: true });
    }

    private void SessionManager_PointerEntered(object? sender, PointerEventArgs args)
    {
        _sessionManagerPointer = args.Pointer;
        _sessionManagerHoverTimer.Stop();
        if (_sessionManagerClosed || DataContext is not MainViewModel { IsSessionManagerPinned: false } model
            || OwnedWindows.Any(window => window.IsVisible)) return;
        if (ReferenceEquals(sender, SessionManagerToggleButton) || ReferenceEquals(sender, SessionManagerRevealZone))
            model.ShowFloatingSessionManager();
    }

    private void SessionManager_PointerExited(object? sender, PointerEventArgs args)
    {
        _sessionManagerPointer = args.Pointer;
        ScheduleSessionManagerHoverHide();
    }

    private void SessionManager_PointerMoved(object? sender, PointerEventArgs args)
    {
        _sessionManagerPointer = args.Pointer;
        if (IsPointerInSessionManager()) _sessionManagerHoverTimer.Stop();
        else ScheduleSessionManagerHoverHide();
    }

    private void SessionManager_PointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        _sessionManagerPointer = args.Pointer;
        ScheduleSessionManagerHoverHide();
    }

    private void SessionManager_ContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (args.Source is not Visual source || !IsUnder(SessionManagerBorder, source)) return;
        // 原生菜单在 ContextRequested 的隧道/冒泡之间同步跟踪，IsOpen 不会变为 true。
        // 保留请求期间的保护；应用菜单展开后则由实际 IsOpen 状态接续。
        _sessionManagerContextRequestActive = args.Route == RoutingStrategies.Tunnel;
        if (_sessionManagerContextRequestActive) _sessionManagerHoverTimer.Stop();
        else ScheduleSessionManagerHoverHide();
    }

    private void ScheduleSessionManagerHoverHide()
    {
        if (_sessionManagerClosed || IsPointerInSessionManager()
            || DataContext is not MainViewModel { IsSessionManagerVisible: true, IsSessionManagerPinned: false }) return;
        // 从按钮跨过工具栏间隙进入侧栏时保留短暂宽限，不被连续 PointerMoved 重置。
        if (!_sessionManagerHoverTimer.IsEnabled) _sessionManagerHoverTimer.Start();
    }

    private void SessionManager_HoverTimerTick(object? sender, EventArgs args)
    {
        _sessionManagerHoverTimer.Stop();
        if (_sessionManagerClosed || IsPointerInSessionManager()
            || DataContext is not MainViewModel { IsSessionManagerVisible: true, IsSessionManagerPinned: false } model) return;
        if (IsSessionManagerInteractionActive(model))
        {
            // 右键菜单、重命名、拖放或子窗口结束后重新判断，不中断当前操作。
            ScheduleSessionManagerHoverHide();
            return;
        }
        model.DismissFloatingSessionManager();
    }

    private void SessionManager_PointerPressed(object? sender, PointerPressedEventArgs args)
    {
        _sessionManagerPointer = args.Pointer;
        if (DataContext is not MainViewModel { IsSessionManagerVisible: true, IsSessionManagerPinned: false } model
            || args.Source is not Visual source || IsUnder(SessionManagerBorder, source)
            || IsUnder(SessionManagerToggleButton, source) || IsUnder(SessionManagerRevealZone, source)
            || IsSessionManagerInteractionActive(model)) return;
        // 不吞掉点击，终端和工具栏继续响应同一次操作。
        model.DismissFloatingSessionManager();
    }

    private void SessionManager_KeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || _paletteCompletion != null
            || DataContext is not MainViewModel { IsSessionManagerVisible: true, IsSessionManagerPinned: false } model
            || IsSessionManagerInteractionActive(model))
            return;
        args.Handled = true;
        model.DismissFloatingSessionManager();
    }

    private void SessionManager_Closed(object? sender, EventArgs args)
    {
        _sessionManagerClosed = true;
        SessionManagerSurface.Classes.Remove("revealing");
        _sessionManagerHoverTimer.Stop();
        _sessionManagerHoverTimer.Tick -= SessionManager_HoverTimerTick;
        SessionManagerToggleButton.PointerEntered -= SessionManager_PointerEntered;
        SessionManagerToggleButton.PointerExited -= SessionManager_PointerExited;
        SessionManagerBorder.PointerEntered -= SessionManager_PointerEntered;
        SessionManagerBorder.PointerExited -= SessionManager_PointerExited;
        SessionManagerRevealZone.PointerEntered -= SessionManager_PointerEntered;
        SessionManagerRevealZone.PointerExited -= SessionManager_PointerExited;
        PointerExited -= SessionManager_PointerExited;
        RemoveHandler(PointerPressedEvent, SessionManager_PointerPressed);
        RemoveHandler(PointerMovedEvent, SessionManager_PointerMoved);
        RemoveHandler(PointerReleasedEvent, SessionManager_PointerReleased);
        RemoveHandler(ContextRequestedEvent, SessionManager_ContextRequested);
        RemoveHandler(KeyDownEvent, SessionManager_KeyDown);
        Closed -= SessionManager_Closed;
        _sessionManagerPointer = null;
        _previousSessionManagerFocus = null;
    }
}
