using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Kei.Term.App.Services;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Settings;

namespace Kei.Term.App.Views;

// 窗口启动期固定 Effective 模式，保持单一 Dock 工作区实例与稳定生命周期。
public partial class MainWindow
{
    // 当前窗口生命周期内锁定的有效布局模式（不随运行期设置草稿或下次启动保存而重组）
    public LayoutMode EffectiveLayoutMode { get; private set; } = LayoutMode.Classic;

    private readonly bool _deferStartupLayout;
    private bool _effectiveLayoutLocked;
    private NativeMenuBridge? _menuBridge;
    private bool _menuBridgeCloseHooked;

    public void FinalizeStartupLayout()
    {
        if (_effectiveLayoutLocked) return;
        if (DataContext is MainViewModel vm)
        {
            ApplyLayoutLock(vm);
        }
    }

    private void ApplyLayoutLock(MainViewModel model)
    {
        if (_effectiveLayoutLocked) return;
        _effectiveLayoutLocked = true;
        EffectiveLayoutMode = model.CurrentSettings?.LayoutMode ?? LayoutMode.Classic;
        ApplyLayoutModeShell(EffectiveLayoutMode);
    }

    private void InitializeLayoutMode(MainViewModel? model)
    {
        // 空 DataContext 不能锁死 Classic，否则随后到达的真实 VM 无法完成首次初始化。
        if (_effectiveLayoutLocked || model == null) return;
        if (_deferStartupLayout) return;
        ApplyLayoutLock(model);
    }

    private void ApplyLayoutModeShell(LayoutMode mode)
    {
        bool isModern = mode == LayoutMode.Modern;

        // Classic 专用外壳元素：顶部菜单栏和全宽工具栏
        if (ClassicMenuBar != null) ClassicMenuBar.IsVisible = !isModern;
        if (ClassicToolbarBorder != null) ClassicToolbarBorder.IsVisible = !isModern;

        // Modern 专用外壳元素：左侧 44px Activity Rail 和顶部紧凑工具条
        if (ModernActivityRail != null) ModernActivityRail.IsVisible = isModern;
        if (ModernToolbarBar != null) ModernToolbarBar.IsVisible = isModern;

        // 侧栏树工具栏按布局模式切换：Classic 采用传统通栏工具栏，Modern 采用融入 Header 的紧凑操作组
        Border? classicSessionToolbar = this.FindControl<Border>("ClassicSessionToolbar");
        if (classicSessionToolbar != null) classicSessionToolbar.IsVisible = !isModern;

        Border? modernSessionTreeHeader = this.FindControl<Border>("ModernSessionTreeHeader");
        if (modernSessionTreeHeader != null) modernSessionTreeHeader.IsVisible = isModern;

        // 统一侧栏停靠列宽刷新
        if (DataContext is MainViewModel vm)
        {
            UpdateSessionManagerLayout(vm);
        }
    }

    // 弹出现有 NativeMenu 导出的系统命令菜单（供 Modern 模式 Rail 顶部汉堡按钮点击）
    private void OnModernMenuButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        NativeMenu? nativeMenu = NativeMenu.GetMenu(this);
        if (nativeMenu == null) return;

        EnsureMenuBridgeReleasedOnWindowClose();
        _menuBridge?.Dispose();
        NativeMenuBridge bridge = new();
        ContextMenu contextMenu = BuildContextMenuFromNativeMenu(nativeMenu, bridge);
        contextMenu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        contextMenu.Closed += (_, _) => ReleaseMenuBridge(bridge, button, contextMenu);
        button.ContextMenu = contextMenu;
        _menuBridge = bridge;
        contextMenu.Open(button);
    }

    private void EnsureMenuBridgeReleasedOnWindowClose()
    {
        if (_menuBridgeCloseHooked) return;
        _menuBridgeCloseHooked = true;
        Closed += (_, _) => _menuBridge?.Dispose();
    }

    private void ReleaseMenuBridge(NativeMenuBridge bridge, Button button, ContextMenu contextMenu)
    {
        bridge.Dispose();
        if (ReferenceEquals(_menuBridge, bridge)) _menuBridge = null;
        if (ReferenceEquals(button.ContextMenu, contextMenu)) button.ContextMenu = null;
    }

    private ContextMenu BuildContextMenuFromNativeMenu(NativeMenu menu, NativeMenuBridge bridge)
    {
        ContextMenu contextMenu = new();
        foreach (NativeMenuItemBase entry in menu.Items)
        {
            Control? item = ConvertNativeMenuItemToMenuItem(entry, bridge);
            if (item != null)
            {
                contextMenu.Items.Add(item);
            }
        }
        return contextMenu;
    }

    private Control? ConvertNativeMenuItemToMenuItem(NativeMenuItemBase itemBase, NativeMenuBridge bridge)
    {
        if (itemBase is NativeMenuItemSeparator)
        {
            return new Separator();
        }

        if (itemBase is not NativeMenuItem item)
        {
            return null;
        }

        MenuItem menuItem = new()
        {
            Header = item.Header,
            Command = item.Command,
            CommandParameter = item.CommandParameter,
            IsEnabled = item.IsEnabled,
            InputGesture = item.Gesture,
            ToggleType = item.ToggleType,
            IsChecked = item.IsChecked
        };
        bridge.Track(item, menuItem, this);

        if (item.Menu is { } subMenu && subMenu.Items.Count > 0)
        {
            foreach (NativeMenuItemBase child in subMenu.Items)
            {
                Control? childControl = ConvertNativeMenuItemToMenuItem(child, bridge);
                if (childControl != null)
                {
                    menuItem.Items.Add(childControl);
                }
            }
        }

        return menuItem;
    }

    // NativeMenuItem.Click 没有公开的触发方法。适配层只转发启动时登记的现有处理函数。
    private sealed class NativeMenuBridge : IDisposable
    {
        private readonly List<Subscription> _subscriptions = new();
        private bool _disposed;

        public void Track(NativeMenuItem source, MenuItem target, MainWindow window)
        {
            void OnSourceChanged(object? _, AvaloniaPropertyChangedEventArgs e)
            {
                if (_disposed) return;
                if (e.Property == NativeMenuItem.HeaderProperty) target.Header = source.Header;
                else if (e.Property == NativeMenuItem.IsEnabledProperty) target.IsEnabled = source.IsEnabled;
                else if (e.Property == NativeMenuItem.CommandParameterProperty) target.CommandParameter = source.CommandParameter;
                else if (e.Property == NativeMenuItem.CommandProperty) target.Command = source.Command;
                else if (e.Property == NativeMenuItem.GestureProperty) target.InputGesture = source.Gesture;
                else if (e.Property == NativeMenuItem.ToggleTypeProperty) target.ToggleType = source.ToggleType;
                else if (e.Property == NativeMenuItem.IsCheckedProperty) CopyCheck(source, target);
            }

            void OnTargetChanged(object? _, AvaloniaPropertyChangedEventArgs e)
            {
                if (_disposed || e.Property != MenuItem.IsCheckedProperty) return;
                CopyCheck(target, source);
            }

            source.PropertyChanged += OnSourceChanged;
            target.PropertyChanged += OnTargetChanged;
            EventHandler<RoutedEventArgs>? click = null;
            if (window.HasAdaptedClickRoute(source))
            {
                click = (_, _) => window.RouteAdaptedNativeClick(source);
                target.Click += click;
            }

            _subscriptions.Add(new Subscription(source, target, OnSourceChanged, OnTargetChanged, click));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Subscription subscription in _subscriptions) subscription.Release();
            _subscriptions.Clear();
        }

        private static void CopyCheck(NativeMenuItem source, MenuItem target)
        {
            if (target.IsChecked == source.IsChecked) return;
            target.IsChecked = source.IsChecked;
        }

        private static void CopyCheck(MenuItem source, NativeMenuItem target)
        {
            if (target.IsChecked == source.IsChecked) return;
            target.IsChecked = source.IsChecked;
        }

        private sealed class Subscription(
            NativeMenuItem source,
            MenuItem target,
            EventHandler<AvaloniaPropertyChangedEventArgs> onSource,
            EventHandler<AvaloniaPropertyChangedEventArgs> onTarget,
            EventHandler<RoutedEventArgs>? click)
        {
            public void Release()
            {
                source.PropertyChanged -= onSource;
                target.PropertyChanged -= onTarget;
                if (click != null) target.Click -= click;
            }
        }
    }
}
