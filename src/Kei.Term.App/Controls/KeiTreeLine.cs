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
/// SecureCRT 风格：竖线正穿过子项展开箭头槽中心，并在箭头字形/图标周围断开留出微小缝隙（ArrowGap）。
/// 横线接到展开箭头右侧以及接到叶子行图标前，也保留同样的断开留白。
/// </summary>
public class KeiTreeLine : Control
{
    /// <summary>
    /// SecureCRT 风格间隙（逻辑像素）：
    /// 竖线在展开箭头字形上下各留出的断线缝隙；
    /// 以及横线在箭头右侧或叶子图标左侧留出的断线缝隙。
    /// </summary>
    public const double ArrowGap = 1.5;

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

        // 无论叶子还是文件夹，横线都从竖线处向右引申到 HeaderPresenter（图标/文字左缘），
        // 从而穿过箭头槽（若有展开箭头，展开箭头正盖在横竖线交点上并有实色底遮盖；若是叶子行，横线直通图标）。
        double startX = 0;
        if (FindPart(item, "PART_HeaderPresenter") is { } presenter
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

        // SecureCRT 风格：竖线正穿过子项展开箭头槽中心（压线，子项展开箭头自带背景遮盖竖线或落在竖线上）。
        // 优先对齐到直接子项的箭头槽中心；若子项尚未布局则取父箭头轴 + 缩进或父图标中心。
        double? childSlotCenter = GetLeftmostArrowSlotCenter(children);
        Control? chevron = FindPart(parentItem, "PART_ExpandCollapseChevronContainer");
        Point? iconCenter = GetIconCenter(parentItem);
        double? axis = childSlotCenter
            ?? iconCenter?.X
            ?? (chevron?.TranslatePoint(new Point(chevron.Bounds.Width / 2, 0), this) is { } chevronAxis
                ? chevronAxis.X
                : null);
        if (axis is not { } axisX)
        {
            return;
        }

        double railX = Math.Max(0, axisX);
        double endY = GetSubtreeEndY(children[^1]);

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double x = Math.Floor(railX * scale) / scale;
        double end = Math.Floor(Math.Max(0, endY) * scale) / scale;

        // 收集展开子项中可见展开箭头的垂直断线缝隙区间 [gapTop, gapBottom]
        List<(double Top, double Bottom)> verticalGaps = [];
        foreach (TreeViewItem child in children)
        {
            if (FindPart(child, "PART_ExpandCollapseChevron") is { IsVisible: true } arrowBtn
                && arrowBtn.Bounds.Height > 0
                && arrowBtn.TranslatePoint(default, this) is { } arrowOrigin)
            {
                // 字形按 arrowBtn 的实际边界计算，上下挖掉 ArrowGap
                double gapTop = Math.Floor((arrowOrigin.Y - ArrowGap) * scale) / scale;
                double gapBottom = Math.Ceiling((arrowOrigin.Y + arrowBtn.Bounds.Height + ArrowGap) * scale) / scale;
                if (gapBottom > gapTop)
                {
                    verticalGaps.Add((gapTop, gapBottom));
                }
            }
        }
        // 按 Top 排序并合并重叠缝隙
        verticalGaps.Sort((a, b) => a.Top.CompareTo(b.Top));
        List<(double Top, double Bottom)> mergedGaps = [];
        foreach (var gap in verticalGaps)
        {
            if (mergedGaps.Count > 0 && gap.Top <= mergedGaps[^1].Bottom)
            {
                mergedGaps[^1] = (mergedGaps[^1].Top, Math.Max(mergedGaps[^1].Bottom, gap.Bottom));
            }
            else
            {
                mergedGaps.Add(gap);
            }
        }

        // 分段绘制竖线（避开箭头断线缝隙）
        double currentY = 0;
        foreach (var (gapTop, gapBottom) in mergedGaps)
        {
            double segmentEnd = Math.Min(end + 1, gapTop);
            if (segmentEnd > currentY)
            {
                context.FillRectangle(brush, new Rect(x, currentY, 1, segmentEnd - currentY));
            }
            currentY = Math.Max(currentY, gapBottom);
            if (currentY >= end + 1)
            {
                break;
            }
        }
        if (currentY < end + 1)
        {
            context.FillRectangle(brush, new Rect(x, currentY, 1, (end + 1) - currentY));
        }

        // 绘制每个子项的横线
        for (int i = 0; i < children.Count && i < rows.Count; i++)
        {
            TreeViewItem child = children[i];
            Point row = rows[i];
            double y = Math.Floor(row.Y * scale) / scale;
            double contentX = Math.Floor(row.X * scale) / scale;

            if (FindPart(child, "PART_ExpandCollapseChevron") is { IsVisible: true } arrowBtn
                && arrowBtn.Bounds.Width > 0
                && arrowBtn.TranslatePoint(default, this) is { } arrowOrigin)
            {
                // 文件夹行：展开箭头中心正落在竖线上（线与箭头在上下断开）。
                // 横线在箭头右边缘与 contentX（图标）之间绘制，在箭头右侧保留 ArrowGap 缝隙，在图标前保留 ArrowGap 缝隙
                double arrowRight = Math.Ceiling((arrowOrigin.X + arrowBtn.Bounds.Width + ArrowGap) * scale) / scale;
                double branchEnd = Math.Floor((contentX - ArrowGap) * scale) / scale;
                if (branchEnd > arrowRight)
                {
                    context.FillRectangle(brush, new Rect(arrowRight, y, branchEnd - arrowRight, 1));
                }
            }
            else
            {
                // 叶子行：横线穿过隐藏箭头占位直达图标，竖线处引出，至图标前保留 ArrowGap 缝隙
                double branchStart = x;
                double branchEnd = Math.Floor((contentX - ArrowGap) * scale) / scale;
                if (branchEnd > branchStart)
                {
                    context.FillRectangle(brush, new Rect(branchStart, y, branchEnd - branchStart, 1));
                }
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

    // 最左子项的箭头槽（PART_ExpandCollapseChevronContainer）中心：叶子行的箭头被隐藏但槽仍占位。
    private double? GetLeftmostArrowSlotCenter(List<TreeViewItem> children)
    {
        double? leftmostCenter = null;
        foreach (TreeViewItem child in children)
        {
            Control? slot = FindPart(child, "PART_ExpandCollapseChevronContainer");
            if (slot is not { Bounds.Width: > 0 } || slot.TranslatePoint(new Point(slot.Bounds.Width / 2, 0), this) is not { } center)
            {
                continue;
            }

            if (leftmostCenter == null || center.X < leftmostCenter)
            {
                leftmostCenter = center.X;
            }
        }
        return leftmostCenter;
    }

    private static Control? FindPart(TreeViewItem item, string name) => item.GetVisualDescendants()
        .OfType<Control>().FirstOrDefault(control => control.Name == name && ReferenceEquals(control.TemplatedParent, item));
}
