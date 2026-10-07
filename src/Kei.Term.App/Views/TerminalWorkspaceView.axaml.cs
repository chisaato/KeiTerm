using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Recycling;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Kei.Term.App.ViewModels;
using Kei.Term.App.Workspaces;
using TabPlacement = Kei.Term.Core.Models.Profiles.TabPlacement;

namespace Kei.Term.App.Views;

// 单窗口里的连接停靠区。各组标签由 Dock 显示，Shell 与 SFTP 仍是同一个连接视图。
public partial class TerminalWorkspaceView : UserControl
{
    private MainViewModel? _bound;

    public TerminalWorkspaceView()
    {
        InitializeComponent();
        // 模板绑定会盖过样式。布局完成后按当前类名直接写停靠边。
        ControlRecyclingDataTemplate.SetControlRecycling(DockHost, new ControlRecycling());
        DockHost.LayoutUpdated += (_, _) => ApplyStripDock();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public void ApplyPlacement(TabPlacement placement)
    {
        bool bottom = placement == TabPlacement.Bottom;
        DockHost.Classes.Set("tabs-bottom", bottom);
        DockHost.Classes.Set("tabs-top", !bottom);
        ApplyStripDock();
    }

    // DocumentTabLayout 没有 Bottom，模板里的绑定又压过样式，所以这里直接改停靠。
    private void ApplyStripDock()
    {
        Avalonia.Controls.Dock edge = DockHost.Classes.Contains("tabs-bottom")
            ? Avalonia.Controls.Dock.Bottom
            : Avalonia.Controls.Dock.Top;
        foreach (Visual visual in DockHost.GetVisualDescendants())
        {
            if (visual is DocumentTabStrip strip)
            {
                DockPanel.SetDock(strip, edge);
            }
            else if (visual is Panel panel && panel.Name == "PART_DocumentSeperatorHost")
            {
                DockPanel.SetDock(panel, edge);
            }
            else if (visual is TerminalConnectionView connection
                && connection.DataContext is TerminalTabViewModel terminal
                && _bound?.Workspace.FindDocument(terminal) is TerminalWorkspaceDocument document)
            {
                RememberView(document, connection);
            }
            else if (visual is DocumentTabStripItem item && item.ContextMenu == null)
            {
                // 业务菜单覆盖整片标签命中区域，包括标题两侧的 padding。
                Control? header = item.GetVisualDescendants().OfType<Control>()
                    .FirstOrDefault(control => control.ContextMenu != null);
                if (header != null)
                {
                    header.ContextMenu!.DataContext = header.DataContext;
                    item.ContextMenu = header.ContextMenu;
                    header.ContextMenu = null;
                }
            }
        }
    }

    public void RememberView(TerminalWorkspaceDocument document, Control view)
    {
        if (ControlRecyclingDataTemplate.GetControlRecycling(DockHost) is not ControlRecycling recycling)
        {
            return;
        }

        recycling.Add(document, view);
    }

    public void RememberView(TerminalTabViewModel tab, Control view)
    {
        if (_bound?.Workspace.FindDocument(tab) is TerminalWorkspaceDocument document)
            RememberView(document, view);
    }

    public void ActivateConnection(TerminalTabViewModel tab)
    {
        _bound?.Workspace.Activate(tab);
    }

    public void ActivateItem(ViewModelBase item) => _bound?.Workspace.Activate(item);

    // 关闭前按文档实例移出回收缓存。不清空其他连接。
    public bool ReleaseTab(TerminalTabViewModel tab)
    {
        if (_bound == null)
        {
            return false;
        }

        TerminalWorkspaceDocument? document = FindDocument(_bound.Workspace.Layout, tab);
        if (document == null)
        {
            return false;
        }

        if (ControlRecyclingDataTemplate.GetControlRecycling(DockHost) is not ControlRecycling recycling)
        {
            return false;
        }

        if (recycling.TryGetValue(document, out object? cached) && cached is Control control)
        {
            Detach(control);
        }

        return recycling.Remove(document);
    }

    public bool IsCached(TerminalWorkspaceDocument document)
    {
        if (ControlRecyclingDataTemplate.GetControlRecycling(DockHost) is not ControlRecycling recycling)
        {
            return false;
        }

        return recycling.TryGetValue(document, out _);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_bound != null)
        {
            _bound.TabReleasing -= OnTabReleasing;
            _bound.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _bound = DataContext as MainViewModel;
        if (_bound == null)
        {
            return;
        }

        _bound.TabReleasing += OnTabReleasing;
        _bound.PropertyChanged += OnViewModelPropertyChanged;
        ApplyPlacement(_bound.TabPlacement);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TabPlacement) && _bound != null)
        {
            ApplyPlacement(_bound.TabPlacement);
        }
    }

    private void OnTabReleasing(TerminalTabViewModel tab) => ReleaseTab(tab);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed || _bound == null)
        {
            return;
        }

        if (FindTabItem(e.Source as Visual) is not { DataContext: WorkspaceDocument document })
        {
            return;
        }

        e.Handled = true;
        _bound.Workspace.Factory.CloseDockable(document);
    }

    private static DocumentTabStripItem? FindTabItem(Visual? visual)
    {
        while (visual != null)
        {
            if (visual is DocumentTabStripItem item)
            {
                return item;
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }

    private static TerminalWorkspaceDocument? FindDocument(IDockable node, TerminalTabViewModel tab)
    {
        if (node is TerminalWorkspaceDocument document && ReferenceEquals(document.Tab, tab))
        {
            return document;
        }

        if (node is not IDock dock || dock.VisibleDockables == null)
        {
            return null;
        }

        foreach (IDockable child in dock.VisibleDockables)
        {
            TerminalWorkspaceDocument? found = FindDocument(child, tab);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static void Detach(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case ContentControl content when ReferenceEquals(content.Content, control):
                content.Content = null;
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, control):
                presenter.Content = null;
                break;
        }
    }
}
