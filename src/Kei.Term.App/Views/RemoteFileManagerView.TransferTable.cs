namespace Kei.Term.App.Views;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kei.Term.App.ViewModels;

// 传输队列表头的列宽拖动：拖动期间只改共享列状态（表头与所有行同步），松手后才保存。
public partial class RemoteFileManagerView
{
    private TransferTableColumn? _resizingColumn;
    private double _resizeStartX;
    private double _resizeStartWidth;

    private void InitializeTransferTable()
    {
        Grid? header = this.FindControl<Grid>("TransferHeaderGrid");
        if (header == null)
        {
            return;
        }

        // 视口宽度变化时通知表格排布，用于空间不足时压缩列宽。
        Control? scroller = this.FindControl<ScrollViewer>("TransferTableScroller");
        if (scroller != null)
        {
            scroller.PropertyChanged += (_, args) =>
            {
                if (args.Property == BoundsProperty && DataContext is RemoteFileManagerViewModel model)
                {
                    model.TransferView.SetAvailableWidth(scroller.Bounds.Width);
                }
            };
        }
        header.AddHandler(PointerPressedEvent, OnColumnGripPressed, Avalonia.Interactivity.RoutingStrategies.Bubble);
        header.AddHandler(PointerMovedEvent, OnColumnGripMoved, Avalonia.Interactivity.RoutingStrategies.Bubble);
        header.AddHandler(PointerReleasedEvent, OnColumnGripReleased, Avalonia.Interactivity.RoutingStrategies.Bubble);
        header.AddHandler(PointerCaptureLostEvent, OnColumnGripCaptureLost, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    private static Border? FindGrip(object? source)
    {
        for (Visual? current = source as Visual; current != null; current = current.GetVisualParent())
        {
            if (current is Border border && border.Classes.Contains("columnGrip"))
            {
                return border;
            }
        }

        return null;
    }

    private void OnColumnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Grid header || DataContext is not RemoteFileManagerViewModel vm
            || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed
            || FindGrip(e.Source) is not { Tag: string key })
        {
            return;
        }

        TransferTableColumn? column = vm.TransferLayout.Find(key);
        TransferTableColumnView? columnView = vm.TransferView.Find(key);
        if (column == null || columnView == null)
        {
            return;
        }

        e.Handled = true;
        // 双击把手：恢复该列默认宽度并保存。
        if (e.ClickCount >= 2)
        {
            column.ResetWidth();
            vm.TransferLayout.Commit();
            return;
        }

        _resizingColumn = column;
        _resizeStartX = e.GetPosition(header).X;
        // 从当前显示宽度起算，避免压缩状态下拖动时列"跳"一下。
        _resizeStartWidth = columnView.EffectiveWidth;
        e.Pointer.Capture(header);
    }

    private void OnColumnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_resizingColumn == null || sender is not Grid header)
        {
            return;
        }

        // 名称列在把手左侧吸收空间：向左拖，当前列变宽。
        double delta = e.GetPosition(header).X - _resizeStartX;
        _resizingColumn.SetWidth(_resizeStartWidth - delta);
        e.Handled = true;
    }

    private void OnColumnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizingColumn == null)
        {
            return;
        }

        e.Pointer.Capture(null);
        EndColumnResize();
        e.Handled = true;
    }

    private void OnColumnGripCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndColumnResize();

    private void EndColumnResize()
    {
        if (_resizingColumn == null)
        {
            return;
        }

        _resizingColumn = null;
        if (DataContext is RemoteFileManagerViewModel vm)
        {
            vm.TransferLayout.Commit();
        }
    }
}
