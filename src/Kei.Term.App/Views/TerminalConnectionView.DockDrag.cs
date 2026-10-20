using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class TerminalConnectionView
{
    private Point? _headerDragStartPoint;
    private bool _isDraggingDock;
    private bool _dragTargetOnLeft;

    private void InitializeDockDragDrop()
    {
        AddHandler(PointerPressedEvent, OnDockHeaderPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnDockHeaderPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnDockHeaderPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnDockHeaderCaptureLost, RoutingStrategies.Tunnel);
    }

    private void OnDockHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _headerDragStartPoint = null;
        _isDraggingDock = false;

        // 仅响应鼠标左键点击标题栏空白区域
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (ResolveTab() is not { IsFileManagerVisible: true, IsShellVisible: true })
        {
            return;
        }

        if (e.Source is not Visual source)
        {
            return;
        }

        // 必须避免拦截按钮、文本框、分隔条等可交互控件
        if (source.FindAncestorOfType<Button>() != null
            || source is Button
            || source.FindAncestorOfType<TextBox>() != null
            || source is TextBox
            || source.FindAncestorOfType<GridSplitter>() != null
            || source is GridSplitter)
        {
            return;
        }

        // 检查是否在终端标题栏或文件管理器标题栏
        bool isInTerminalHeader = IsVisualDescendantOf(source, TerminalHeaderBorder);
        Border? fileManagerHeader = SftpHost.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "FileManagerToolbar" || (b.Parent is Grid g && g.Name == "FileManagerLayout" && Grid.GetRow(b) == 0));

        bool isInFileManagerHeader = fileManagerHeader != null && IsVisualDescendantOf(source, fileManagerHeader);

        if (isInTerminalHeader || isInFileManagerHeader)
        {
            _headerDragStartPoint = point.Position;
        }
    }

    private static bool IsVisualDescendantOf(Visual source, Visual target)
    {
        return ReferenceEquals(source, target) || source.GetVisualAncestors().Contains(target);
    }

    private void OnDockHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_headerDragStartPoint == null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            CancelDockDrag();
            return;
        }

        Point currentPos = point.Position;
        double deltaX = currentPos.X - _headerDragStartPoint.Value.X;
        double deltaY = currentPos.Y - _headerDragStartPoint.Value.Y;

        if (!_isDraggingDock)
        {
            if (DockDragDropHelper.HasExceededDragThreshold(deltaX, deltaY))
            {
                _isDraggingDock = true;
                e.Pointer.Capture(this);
            }
        }

        if (_isDraggingDock)
        {
            double totalWidth = Bounds.Width;
            _dragTargetOnLeft = DockDragDropHelper.CalculateTargetSide(currentPos.X, totalWidth);
            UpdateDockDropOverlay(true, _dragTargetOnLeft);
            e.Handled = true;
        }
    }

    private void OnDockHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingDock)
        {
            TerminalTabViewModel? tab = ResolveTab();
            if (tab != null)
            {
                tab.SetDockPosition(_dragTargetOnLeft);
            }

            e.Pointer.Capture(null);
            CancelDockDrag();
            e.Handled = true;
            return;
        }

        CancelDockDrag();
    }

    private void OnDockHeaderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CancelDockDrag();
    }

    private void CancelDockDrag()
    {
        _headerDragStartPoint = null;
        _isDraggingDock = false;
        UpdateDockDropOverlay(false, false);
    }

    private void UpdateDockDropOverlay(bool isVisible, bool targetOnLeft)
    {
        DockDropOverlay.IsVisible = isVisible;
        if (!isVisible)
        {
            DropTargetLeft.IsVisible = false;
            DropTargetRight.IsVisible = false;
            return;
        }

        DropTargetLeft.IsVisible = targetOnLeft;
        DropTargetRight.IsVisible = !targetOnLeft;
    }
}
