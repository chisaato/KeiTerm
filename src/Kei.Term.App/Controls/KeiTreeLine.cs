namespace Kei.Term.App.Controls;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

/// <summary>
/// 按父项箭头中心和直接子项的实际行坐标绘制树连接线。
/// 横线与竖线共用一个坐标系，末端停在最后一个直接子项的行中点，
/// 不受缩进、行高、内边距以及子项自身展开高度的影响。
/// </summary>
public class KeiTreeLine : Control
{
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<KeiTreeLine, IBrush?>(nameof(LineBrush));

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    static KeiTreeLine()
    {
        AffectsRender<KeiTreeLine>(LineBrushProperty);
    }

    private ItemsPresenter? _itemsPresenter;
    private Panel? _itemsPanel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookEvents();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnhookEvents();
        base.OnDetachedFromVisualTree(e);
    }

    private void HookEvents()
    {
        UnhookEvents();

        if (Parent is Panel parentPanel)
        {
            _itemsPresenter = parentPanel.Children.OfType<ItemsPresenter>().FirstOrDefault();
            if (_itemsPresenter != null)
            {
                _itemsPresenter.SizeChanged += OnPresenterSizeChanged;
                _itemsPresenter.EffectiveViewportChanged += OnPresenterEffectiveViewportChanged;
                _itemsPresenter.LayoutUpdated += OnTreeLayoutUpdated;
                HookPanel(_itemsPresenter.Panel);
            }
        }
    }

    private void HookPanel(Panel? panel)
    {
        if (_itemsPanel != null)
        {
            if (_itemsPanel.Children is INotifyCollectionChanged oldNcc)
            {
                oldNcc.CollectionChanged -= OnChildrenChanged;
            }
        }

        _itemsPanel = panel;

        if (_itemsPanel != null)
        {
            if (_itemsPanel.Children is INotifyCollectionChanged newNcc)
            {
                newNcc.CollectionChanged += OnChildrenChanged;
            }
        }
    }

    private void UnhookEvents()
    {
        if (_itemsPresenter != null)
        {
            _itemsPresenter.SizeChanged -= OnPresenterSizeChanged;
            _itemsPresenter.EffectiveViewportChanged -= OnPresenterEffectiveViewportChanged;
            _itemsPresenter.LayoutUpdated -= OnTreeLayoutUpdated;
            _itemsPresenter = null;
        }

        if (_itemsPanel != null)
        {
            if (_itemsPanel.Children is INotifyCollectionChanged ncc)
            {
                ncc.CollectionChanged -= OnChildrenChanged;
            }
            _itemsPanel = null;
        }
    }

    private void OnPresenterSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_itemsPresenter?.Panel != _itemsPanel)
        {
            HookPanel(_itemsPresenter?.Panel);
        }
        InvalidateVisual();
    }

    private void OnPresenterEffectiveViewportChanged(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private void OnTreeLayoutUpdated(object? sender, EventArgs e)
    {
        // 展开、改密度或行内容高度变化后，重新读取布局；竖横线不缓存旧坐标。
        if (_itemsPresenter?.Panel != _itemsPanel)
        {
            HookPanel(_itemsPresenter?.Panel);
        }
        InvalidateVisual();
    }

    private IReadOnlyList<Point> GetChildBranches()
    {
        if (_itemsPresenter == null && Parent is Panel)
        {
            HookEvents();
        }

        if (_itemsPresenter == null)
        {
            return Array.Empty<Point>();
        }

        Panel? panel = _itemsPresenter.Panel ?? _itemsPanel;
        if (panel == null || panel.Children.Count == 0)
        {
            return Array.Empty<Point>();
        }

        List<Point> branches = new(panel.Children.Count);
        foreach (Control child in panel.Children)
        {
            if (child is not TreeViewItem item || !item.IsVisible)
            {
                continue;
            }
            Control? header = FindPart(item, "PART_Header");
            if (header?.Bounds.Height > 0 && header.TranslatePoint(new Point(0, header.Bounds.Height / 2), this) is { } point)
            {
                branches.Add(point);
            }
        }
        return branches;
    }

    private static Control? FindPart(TreeViewItem item, string name) => item.GetVisualDescendants()
        .OfType<Control>().FirstOrDefault(control => control.Name == name && ReferenceEquals(control.TemplatedParent, item));

    public double CalculateLineLength()
    {
        IReadOnlyList<Point> branches = GetChildBranches();
        return branches.Count > 0 ? Math.Max(0, branches[^1].Y) : 0;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        IBrush? brush = LineBrush;
        if (brush == null)
        {
            return;
        }

        IReadOnlyList<Point> branches = GetChildBranches();
        TreeViewItem? parentItem = TemplatedParent as TreeViewItem ?? this.FindAncestorOfType<TreeViewItem>();
        Control? chevron = parentItem == null ? null : FindPart(parentItem, "PART_ExpandCollapseChevronContainer");
        if (branches.Count == 0 || chevron == null || chevron.TranslatePoint(new Point(chevron.Bounds.Width / 2, 0), this) is not { } axis)
        {
            return;
        }

        // 将 1px 线条边缘贴到设备像素，避免不同密度下出现半像素发虚。
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double x = Math.Floor(axis.X * scale) / scale;
        double endY = Math.Floor(branches[^1].Y * scale) / scale;
        context.FillRectangle(brush, new Rect(x, 0, 1, Math.Max(0, endY) + 1));
        foreach (Point branch in branches)
        {
            double y = Math.Floor(branch.Y * scale) / scale;
            double endX = Math.Floor(branch.X * scale) / scale;
            if (endX > x)
            {
                context.FillRectangle(brush, new Rect(x, y, endX - x, 1));
            }
        }
    }
}
