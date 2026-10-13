using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Kei.Term.App.Views;

public partial class RemoteFileManagerView
{
    private double? _activityUserHeight;
    private double? _activityAutomaticHeight;
    private bool _activityResizing;

    private void InitializeActivityLayout()
    {
        SizeChanged += (_, _) => UpdateActivityPanelLayout();
        FileActivitySplitter.AddHandler(Thumb.DragStartedEvent, OnActivityPanelDragStarted,
            RoutingStrategies.Bubble, handledEventsToo: true);
        FileActivitySplitter.AddHandler(Thumb.DragCompletedEvent, OnActivityPanelDragCompleted,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void UpdateActivityPanelLayout()
    {
        RowDefinition row = FileManagerLayout.RowDefinitions[4];
        FileManagerLayout.RowDefinitions[2].MinHeight = 96;
        // 自动布局最多占一半；手动拖动可扩大，仍给文件列表留出可操作空间。
        double maximumHeight = ActivityMaximumHeight(_activityResizing || _activityUserHeight != null);
        row.MaxHeight = maximumHeight;
        // 同时约束内容的测量高度，避免 Auto 行只裁剪列表而不生成滚动范围。
        FileActivityPanel.MaxHeight = maximumHeight;
        bool open = _activityOwner?.IsActivityPanelOpen == true;
        row.MinHeight = open ? 64 : 0;
        row.Height = open && (_activityUserHeight ?? _activityAutomaticHeight) is { } height
            ? new GridLength(Math.Clamp(height, row.MinHeight, row.MaxHeight)) : GridLength.Auto;
    }

    private void KeepActivityPanelHeightOnTabChange()
    {
        if (_activityOwner?.IsActivityPanelOpen != true || _activityUserHeight != null) return;
        // 切换内容前保留已经布局的公共高度，空队列不会使整个栏突然收缩。
        if (_activityAutomaticHeight == null && FileActivityPanel.Bounds.Height >= 64)
            _activityAutomaticHeight = FileActivityPanel.Bounds.Height;
        UpdateActivityPanelLayout();
    }

    private void OnActivityPanelDragCompleted(object? sender, VectorEventArgs args)
    {
        _activityResizing = false;
        if (_activityOwner?.IsActivityPanelOpen != true) return;
        RowDefinition row = FileManagerLayout.RowDefinitions[4];
        _activityUserHeight = row.Height.IsAbsolute ? row.Height.Value : row.ActualHeight;
        UpdateActivityPanelLayout();
    }

    private void OnActivityPanelDragStarted(object? sender, VectorEventArgs args)
    {
        _activityResizing = true;
        double maximumHeight = ActivityMaximumHeight(manual: true);
        FileManagerLayout.RowDefinitions[4].MaxHeight = maximumHeight;
        FileActivityPanel.MaxHeight = maximumHeight;
    }

    private double ActivityMaximumHeight(bool manual)
    {
        if (!manual) return Math.Max(64, Bounds.Height * 0.5);
        double surroundingHeight = FileManagerLayout.RowDefinitions[0].ActualHeight
            + FileManagerLayout.RowDefinitions[1].ActualHeight + FileManagerLayout.RowDefinitions[3].ActualHeight
            + FileManagerLayout.RowDefinitions[5].ActualHeight;
        return Math.Max(64, Bounds.Height - surroundingHeight - 96);
    }
}
