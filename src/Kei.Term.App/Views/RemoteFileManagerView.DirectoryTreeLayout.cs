using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kei.Term.App.ViewModels;

namespace Kei.Term.App.Views;

public partial class RemoteFileManagerView
{
    private RemoteFileManagerViewModel? _directoryTreeLayoutOwner;

    private void InitializeDirectoryTreeLayout()
    {
        DirectoryContentGrid.SizeChanged += (_, _) => UpdateDirectoryTreeLayout();
        DirectoryTreeSplitter.AddHandler(Thumb.DragCompletedEvent, OnDirectoryTreeDragCompleted,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void BindDirectoryTreeLayout()
    {
        ReleaseDirectoryTreeLayout();
        _directoryTreeLayoutOwner = DataContext as RemoteFileManagerViewModel;
        if (_directoryTreeLayoutOwner != null)
            _directoryTreeLayoutOwner.PropertyChanged += OnDirectoryTreeLayoutChanged;
        UpdateDirectoryTreeLayout();
    }

    private void ReleaseDirectoryTreeLayout()
    {
        if (_directoryTreeLayoutOwner != null)
            _directoryTreeLayoutOwner.PropertyChanged -= OnDirectoryTreeLayoutChanged;
        _directoryTreeLayoutOwner = null;
    }

    private void OnDirectoryTreeLayoutChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(RemoteFileManagerViewModel.IsDirectoryTreeVisible)
            or nameof(RemoteFileManagerViewModel.DirectoryTreeWidth))
            UpdateDirectoryTreeLayout();
    }

    private void UpdateDirectoryTreeLayout()
    {
        ColumnDefinition treeColumn = DirectoryContentGrid.ColumnDefinitions[0];
        ColumnDefinition splitterColumn = DirectoryContentGrid.ColumnDefinitions[1];
        bool visible = _directoryTreeLayoutOwner?.IsDirectoryTreeVisible == true;
        // 收起时同时释放树和分隔条列，文件列表才能使用整个内容区域。
        splitterColumn.Width = visible ? GridLength.Auto : new GridLength(0);
        treeColumn.MinWidth = visible ? 80 : 0;
        treeColumn.MaxWidth = visible ? Math.Max(80, DirectoryContentGrid.Bounds.Width - 99) : 0;
        double width = _directoryTreeLayoutOwner?.DirectoryTreeWidth ?? 160;
        treeColumn.Width = visible
            ? new GridLength(Math.Clamp(width, treeColumn.MinWidth, treeColumn.MaxWidth))
            : new GridLength(0);
    }

    private void OnDirectoryTreeDragCompleted(object? sender, VectorEventArgs args)
    {
        if (_directoryTreeLayoutOwner?.IsDirectoryTreeVisible != true) return;
        // 只记录主动拖动，窗口变窄时的自动压缩不会覆盖用户的展开宽度。
        _directoryTreeLayoutOwner.DirectoryTreeWidth = DirectoryContentGrid.ColumnDefinitions[0].ActualWidth;
    }
}
