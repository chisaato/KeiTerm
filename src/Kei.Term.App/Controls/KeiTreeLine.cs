namespace Kei.Term.App.Controls;

using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

/// <summary>
/// 树形视图连接垂直引导线：
/// 替代原先无节制铺满整个展开区域的 Rectangle，
/// 根据其同级 ItemsPresenter 中的最后一个直接子节点，精准将竖线长度截断在最后一个子节点的 Header 中心水平横线处，
/// 消除树形视图末尾子项下方凸出一条竖线的问题。
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

        AddHandler(TreeViewItem.ExpandedEvent, OnSubtreeExpandedCollapsed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(TreeViewItem.CollapsedEvent, OnSubtreeExpandedCollapsed, RoutingStrategies.Bubble, handledEventsToo: true);

        if (Parent is Panel parentPanel)
        {
            _itemsPresenter = parentPanel.Children.OfType<ItemsPresenter>().FirstOrDefault();
            if (_itemsPresenter != null)
            {
                _itemsPresenter.SizeChanged += OnPresenterSizeChanged;
                _itemsPresenter.EffectiveViewportChanged += OnPresenterEffectiveViewportChanged;
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
        RemoveHandler(TreeViewItem.ExpandedEvent, OnSubtreeExpandedCollapsed);
        RemoveHandler(TreeViewItem.CollapsedEvent, OnSubtreeExpandedCollapsed);

        if (_itemsPresenter != null)
        {
            _itemsPresenter.SizeChanged -= OnPresenterSizeChanged;
            _itemsPresenter.EffectiveViewportChanged -= OnPresenterEffectiveViewportChanged;
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

    private void OnSubtreeExpandedCollapsed(object? sender, RoutedEventArgs e)
    {
        InvalidateVisual();
    }

    public double CalculateLineLength()
    {
        if (_itemsPresenter == null)
        {
            if (Parent is Panel parentPanel)
            {
                _itemsPresenter = parentPanel.Children.OfType<ItemsPresenter>().FirstOrDefault();
                if (_itemsPresenter != null)
                {
                    HookPanel(_itemsPresenter.Panel);
                }
            }
        }

        if (_itemsPresenter == null)
        {
            return 0;
        }

        var panel = _itemsPresenter.Panel ?? _itemsPanel;
        if (panel == null || panel.Children.Count == 0)
        {
            return 0;
        }

        TreeViewItem? lastItem = null;
        for (int i = panel.Children.Count - 1; i >= 0; i--)
        {
            if (panel.Children[i] is TreeViewItem tvi && tvi.IsVisible)
            {
                lastItem = tvi;
                break;
            }
        }

        if (lastItem == null)
        {
            return 0;
        }

        var transform = lastItem.TransformToVisual(this);
        if (!transform.HasValue)
        {
            return 0;
        }

        Point lastOrigin = transform.Value.Transform(new Point(0, 0));

        double headerHeight = 0;
        var layoutRoot = lastItem.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "PART_ContentPill" || b.Classes.Contains("TreeViewItemContentPill") || b.Name == "PART_LayoutRoot" || b.Classes.Contains("TreeViewItemLayoutRoot"));
        if (layoutRoot != null && layoutRoot.Bounds.Height > 0)
        {
            headerHeight = layoutRoot.Bounds.Height;
        }

        if (headerHeight <= 0)
        {
            if (lastItem.TryFindResource("Kei.Tree.ItemHeight", out var res) && res is double d && d > 0)
            {
                headerHeight = d;
            }
            else if (lastItem.MinHeight > 0 && !double.IsNaN(lastItem.MinHeight))
            {
                headerHeight = lastItem.MinHeight;
            }
            else
            {
                headerHeight = 22.0;
            }
        }

        double endY = lastOrigin.Y + (headerHeight / 2.0);
        return Math.Max(0, endY);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var brush = LineBrush;
        if (brush == null)
        {
            return;
        }

        double length = CalculateLineLength();
        if (length <= 0)
        {
            return;
        }

        double width = Bounds.Width > 0 ? Bounds.Width : 1.0;
        context.FillRectangle(brush, new Rect(0, 0, width, length));
    }
}
