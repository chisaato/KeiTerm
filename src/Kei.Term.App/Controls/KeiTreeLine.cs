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
using Kei.Term.App.Views.Controls;

/// <summary>
/// 按实际布局坐标绘制会话树的父子连接线。
/// 竖线挂在父项图标轴（SessionTreeItemView 里图标的水平中心）上——线看起来从文件夹图标正下方引出；
/// 取不到图标时退化到父箭头轴。竖线从父行底部向下贯通该父项的整棵已展开子树：
/// 末项仍是叶子时收在它的行中点（└ 形），末项自己也已展开时继续下潜到子树最末一行，
/// 因此同一层的竖线在整个展开范围内连续不断。横向短线由每个直接子项的行中点决定
/// （叶子穿过隐藏箭头的占位连到图标/文字），不缓存缩进、行高、内边距与子项自身展开高度。
/// </summary>
public class KeiTreeLine : Control
{
    // 竖线到子项箭头槽左缘的留白；箭头字形在槽内再内缩 2px，所以到手性箭头的实际间距 ≥ 4px。
    private const double ArrowClearance = 2;

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

    // 行中点取自己布局完成的头部：未测量（高度为 0）与不可见的行都不画线。
    private static Point? GetRowCenter(TreeViewItem item, Visual target)
    {
        if (!item.IsVisible)
        {
            return null;
        }

        Control? header = FindPart(item, "PART_Header");
        if (header is not { Bounds.Height: > 0 })
        {
            return null;
        }

        // 叶子行的箭头被隐藏但仍占位：横线要穿过占位，一直连到图标/文字，而不是停在 Header 左缘。
        double startX = 0;
        if (FindPart(item, "PART_ExpandCollapseChevron") is { IsVisible: false }
            && FindPart(item, "PART_HeaderPresenter") is { } presenter
            && presenter.TranslatePoint(new Point(0, 0), header) is { } presenterOrigin)
        {
            startX = presenterOrigin.X;
        }

        return header.TranslatePoint(new Point(startX, header.Bounds.Height / 2), target);
    }

    // 自身展开区域内的直接子项（只含已完成布局的行），横线与竖线共用这一组坐标。
    private List<TreeViewItem> GetLayoutedChildren()
    {
        if (_itemsPresenter == null && Parent is Panel)
        {
            HookEvents();
        }

        Panel? panel = _itemsPresenter?.Panel ?? _itemsPanel;
        if (panel == null)
        {
            return [];
        }

        List<TreeViewItem> children = new(panel.Children.Count);
        foreach (Control child in panel.Children)
        {
            if (child is TreeViewItem item && GetRowCenter(item, this) != null)
            {
                children.Add(item);
            }
        }
        return children;
    }

    // 已展开子项的最后一个可见行，用于把竖线继续下潜到它的子树里。
    private TreeViewItem? GetLastLayoutedChild(TreeViewItem item)
    {
        if (!item.IsExpanded)
        {
            return null;
        }

        Panel? panel = (FindPart(item, "PART_ItemsPresenter") as ItemsPresenter)?.Panel;
        if (panel == null)
        {
            return null;
        }

        for (int i = panel.Children.Count - 1; i >= 0; i--)
        {
            if (panel.Children[i] is TreeViewItem child && GetRowCenter(child, this) != null)
            {
                return child;
            }
        }
        return null;
    }

    // 子树最末一行的行中点：末项展开时竖线停在它下面最后一行，而不是停在末项自己的行上。
    private double GetSubtreeEndY(TreeViewItem item)
    {
        double endY = GetRowCenter(item, this)?.Y ?? 0;
        TreeViewItem deepest = item;
        while (GetLastLayoutedChild(deepest) is { } child)
        {
            endY = GetRowCenter(child, this)?.Y ?? endY;
            deepest = child;
        }
        return endY;
    }

    /// <summary>竖线终点相对自身顶部的长度，等于子树最末一行的行中点。</summary>
    public double CalculateLineLength()
    {
        List<TreeViewItem> children = GetLayoutedChildren();
        return children.Count > 0 ? Math.Max(0, GetSubtreeEndY(children[^1])) : 0;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        IBrush? brush = LineBrush;
        if (brush == null)
        {
            return;
        }

        List<TreeViewItem> children = GetLayoutedChildren();
        TreeViewItem? parentItem = TemplatedParent as TreeViewItem ?? this.FindAncestorOfType<TreeViewItem>();
        if (children.Count == 0 || parentItem == null)
        {
            return;
        }

        List<Point> rows = [];
        foreach (TreeViewItem child in children)
        {
            if (GetRowCenter(child, this) is { } row)
            {
                rows.Add(row);
            }
        }
        if (rows.Count == 0)
        {
            return;
        }

        // 竖线列优先取父行图标轴（从文件夹图标正下方引出），退化到父箭头轴。
        Control? chevron = FindPart(parentItem, "PART_ExpandCollapseChevronContainer");
        Point? iconCenter = GetIconCenter(parentItem);
        double? axis = iconCenter?.X
            ?? (chevron?.TranslatePoint(new Point(chevron.Bounds.Width / 2, 0), this) is { } chevronAxis
                ? chevronAxis.X
                : null);
        if (axis is not { } axisX)
        {
            return;
        }

        double railX = axisX;
        if (iconCenter != null && GetLeftmostArrowSlotLeft(children) is { } slotLeft)
        {
            // 图标轴可能落在子项缩进槽的右侧（缩进偏小时），退回子项箭头槽左侧，任何缩进下都不会压住子项箭头。
            railX = Math.Min(railX, slotLeft - ArrowClearance);
        }
        railX = Math.Max(0, railX);
        double endY = GetSubtreeEndY(children[^1]);

        // 将 1px 线条边缘贴到设备像素，避免不同密度下出现半像素发虚。
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double x = Math.Floor(railX * scale) / scale;
        double end = Math.Floor(Math.Max(0, endY) * scale) / scale;
        context.FillRectangle(brush, new Rect(x, 0, 1, end + 1));
        foreach (Point row in rows)
        {
            double y = Math.Floor(row.Y * scale) / scale;
            double contentX = Math.Floor(row.X * scale) / scale;
            if (contentX > x)
            {
                context.FillRectangle(brush, new Rect(x, y, contentX - x, 1));
            }
        }
    }

    // 父行图标（SessionTreeItemView 里的 Viewbox）在本控件坐标系中的水平中心；模板里没有该控件时返回 null。
    private Point? GetIconCenter(TreeViewItem parentItem)
    {
        Control? presenter = FindPart(parentItem, "PART_HeaderPresenter");
        SessionTreeItemView? view = presenter?.GetVisualDescendants().OfType<SessionTreeItemView>().FirstOrDefault();
        Viewbox? icon = view?.GetVisualDescendants().OfType<Viewbox>().FirstOrDefault(box => box.Bounds.Width > 0);
        return icon?.TranslatePoint(new Point(icon.Bounds.Width / 2, 0), this);
    }

    // 最左子项的箭头槽（PART_ExpandCollapseChevronContainer）左缘：叶子行的箭头被隐藏但槽仍占位。
    private double? GetLeftmostArrowSlotLeft(List<TreeViewItem> children)
    {
        double? leftmost = null;
        foreach (TreeViewItem child in children)
        {
            Control? slot = FindPart(child, "PART_ExpandCollapseChevronContainer");
            if (slot is not { Bounds.Width: > 0 } || slot.TranslatePoint(default, this) is not { } origin)
            {
                continue;
            }

            if (leftmost == null || origin.X < leftmost)
            {
                leftmost = origin.X;
            }
        }
        return leftmost;
    }

    private static Control? FindPart(TreeViewItem item, string name) => item.GetVisualDescendants()
        .OfType<Control>().FirstOrDefault(control => control.Name == name && ReferenceEquals(control.TemplatedParent, item));
}
