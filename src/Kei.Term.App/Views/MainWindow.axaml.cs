using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.Views;

public partial class MainWindow : Window
{
    // 拖拽进行中的节点 Id（PointerPressed 命中记录，移动超阈值后启动 DoDragDrop）
    private TreeNodeBase? _dragNode;
    private Guid? _activeDraggedId;
    private TreeViewItem? _lastHoveredItem;
    private ILogger? _logger;

    // Avalonia 12 的 DoDragDropAsync 需 PointerPressedEventArgs：暂存按下事件参数，待超阈值后再启动
    private PointerPressedEventArgs? _dragPressedArgs;
    private Point _dragStart;

    // 标签栏拖拽状态
    private ViewModelBase? _dragTab;
    private ViewModelBase? _activeDraggedTab;
    private PointerPressedEventArgs? _dragTabPressedArgs;
    private Point _dragTabStart;
    private bool _isDraggingTab;

    public MainWindow()
    {
        InitializeComponent();

        SetUpPlatformKeyBindings();
        SetUpSessionManager();
        WireMenuClickHandlers();

        // 在 InitializeComponent 后拿到 SessionTree（先给 axaml 的 TreeView 加 x:Name="SessionTree"）
        SessionTree.PointerReleased += Tree_PointerReleased;

        // 树拖拽：由于 TreeViewItem 内部处理了 PointerPressed 并将 e.Handled 置为 true，
        // 必须使用 AddHandler(..., handledEventsToo: true) 才能捕获按下事件，否则拖拽完全无法启动
        SessionTree.AddHandler(PointerPressedEvent, Tree_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SessionTree.AddHandler(PointerMovedEvent, Tree_PointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SessionTree.AddHandler(DragDrop.DragOverEvent, Tree_DragOver);
        SessionTree.AddHandler(DragDrop.DragLeaveEvent, Tree_DragLeave);
        SessionTree.AddHandler(DragDrop.DropEvent, Tree_Drop);
        SessionTree.AddHandler(TreeViewItem.ExpandedEvent, Tree_ItemExpanded);
        SessionTree.AddHandler(TreeViewItem.CollapsedEvent, Tree_ItemCollapsed);
        SessionTree.AddHandler(KeyDownEvent, Tree_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        SessionTree.AddHandler(LostFocusEvent, Tree_RenameLostFocus, RoutingStrategies.Bubble);

        // 标签栏拖拽与点击处理：在 TabsItemsControl 容器上附加事件
        TabsItemsControl.AddHandler(PointerPressedEvent, Tab_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        TabsItemsControl.AddHandler(Button.ClickEvent, Tab_ButtonClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        TabsItemsControl.AddHandler(PointerMovedEvent, Tab_PointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        TabsItemsControl.AddHandler(DragDrop.DragOverEvent, Tab_DragOver);
        TabsItemsControl.AddHandler(DragDrop.DropEvent, Tab_Drop);

        // DataContext 变化时挂接 VM 属性监听（侧栏收起/恢复需要联动列宽）
        PropertyChanged += OnWindowPropertyChanged;
    }

    // NativeMenuItem 不是 Control，XAML 编译器不支持在其上写 Click="方法名"（只能在代码里订阅）。
    // 这里按 Header 资源键挂接：Header 与 XAML 的 {loc:KeiString} 取自同一资源，
    // 因此本地化切换后依然精确匹配。"退出/关于"都属视图职责（关窗口、开关于弹窗），不下沉到 ViewModel。
    private void WireMenuClickHandlers()
    {
        NativeMenu? menu = NativeMenu.GetMenu(this);
        if (menu == null)
        {
            return;
        }

        string exitHeader = Strings.Get("Menu.File.Exit");
        string aboutHeader = Strings.Get("Menu.Help.About");

        foreach (NativeMenuItem item in EnumerateMenuItems(menu.Items))
        {
            if (item.Header == exitHeader)
            {
                item.Click += OnExitMenuClick;
            }
            else if (item.Header == aboutHeader)
            {
                item.Click += OnAboutClick;
            }
        }
    }

    // 深度优先遍历菜单树（递归进入子菜单），分隔符等非 NativeMenuItem 项自动跳过
    private static IEnumerable<NativeMenuItem> EnumerateMenuItems(IEnumerable entries)
    {
        foreach (object? entry in entries)
        {
            if (entry is not NativeMenuItem item)
            {
                continue;
            }

            yield return item;

            if (item.Menu is { } submenu)
            {
                foreach (NativeMenuItem child in EnumerateMenuItems(submenu.Items))
                {
                    yield return child;
                }
            }
        }
    }

    private void Tree_ItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem item && item.DataContext is TreeNodeBase node)
        {
            _logger?.LogInformation("树节点展开 事件源={Item} 节点={Name}({Id}) 类型={Type}", item, node.Name, node.Id, node.NodeType);
        }
    }

    private void Tree_ItemCollapsed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem item && item.DataContext is TreeNodeBase node)
        {
            _logger?.LogInformation("树节点收起 事件源={Item} 节点={Name}({Id}) 类型={Type}", item, node.Name, node.Id, node.NodeType);
        }
    }

    // 注入交互服务并完成身份管理器的回调装配（弹窗实现见 MainWindowInteractionService）
    public void WireDialogs(
        MainViewModel vm,
        IdentityManagerViewModel identityMgrVm,
        SettingsViewModel settingsVm,
        ILogger? logger = null,
        KnownHostsManagerViewModel? knownHostsVm = null)
    {
        _logger = logger;
        vm.Interaction = new MainWindowInteractionService(
            this,
            vm,
            identityMgrVm,
            settingsVm,
            knownHostsVm,
            logger ?? NullLogger.Instance);

        // 身份管理器编辑器由管理器窗口自身以模态方式打开（保证 owner 正确）
        identityMgrVm.ConfirmDeleteAsync = _ => Task.FromResult(true);
        // 手动锁定 Vault 时一并清空 SessionOnly 口令缓存
        identityMgrVm.LockVaultAction = vm.VaultSession.Lock;
        // 编辑器回显 Vault 私钥信息 / 「应用」时写入 Vault 材料
        identityMgrVm.VaultKeyInfoLoader = vm.VaultSession.GetVaultKeyInfoAsync;
        identityMgrVm.PersistVaultKeysAsync = vm.VaultSession.PersistVaultKeyImportsAsync;
        vm.TreeRenameStarted += FocusTreeRenameBox;
    }

    // 只有会话树持有焦点时才吃 F2。文本框、下拉、终端聚焦则放过。
    private void Tree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (e.Source is TextBox { Classes: var classes } box && classes.Contains("treeRenameBox"))
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = vm.CommitTreeRenameAsync(vm.TreeRenameText);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                vm.CancelTreeRename();
            }

            return;
        }

        if (e.Key != Key.F2 || !vm.RenameSelectedNodeCommand.CanExecute(null))
        {
            return;
        }

        if (!TreeHasFocus() || FocusIsTextInput())
        {
            return;
        }

        e.Handled = true;
        vm.RenameSelectedNodeCommand.Execute(null);
    }

    private void Tree_RenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.RenamingNode == null)
        {
            return;
        }

        if (e.Source is TextBox box && box.Classes.Contains("treeRenameBox"))
        {
            _ = vm.CommitTreeRenameAsync(vm.TreeRenameText);
        }
    }

    private bool TreeHasFocus()
    {
        return FocusManager?.GetFocusedElement() is Visual focused && IsUnder(SessionTree, focused);
    }

    private bool FocusIsTextInput()
    {
        if (FocusManager?.GetFocusedElement() is not Visual focused)
        {
            return false;
        }

        for (Visual? node = focused; node != null; node = node.GetVisualParent())
        {
            if (node is TextBox or ComboBox or NumericUpDown)
            {
                return true;
            }

            if (node.GetType().Name.Contains("Terminal", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(Visual ancestor, Visual node)
    {
        for (Visual? cursor = node; cursor != null; cursor = cursor.GetVisualParent())
        {
            if (cursor == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private void FocusTreeRenameBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not MainViewModel vm || vm.RenamingNode == null)
            {
                return;
            }

            if (SessionTree.ContainerFromItem(vm.RenamingNode) is not Control container)
            {
                return;
            }

            TextBox? box = null;
            foreach (var visual in container.GetVisualDescendants())
            {
                if (visual is TextBox candidate && candidate.Classes.Contains("treeRenameBox"))
                {
                    box = candidate;
                    break;
                }
            }

            if (box == null)
            {
                return;
            }

            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    // 右键松开在空白处（命中点不在任何 TreeViewItem 上）→ 清除选中，使新建/粘贴落到顶级
    private void Tree_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not TreeView tree
            || DataContext is not MainViewModel vm
            || e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        if (GetNodeAt(tree, e.GetPosition(tree)) == null)
        {
            vm.SelectedTreeNode = null;
        }
    }

    // 命中测试：坐标处向上找 TreeViewItem，取其 DataContext 与自身容器
    private static (TreeNodeBase? Node, TreeViewItem? Item) GetNodeAndItemAt(TreeView tree, Point point)
    {
        var hit = tree.GetVisualAt(point);
        while (hit != null && hit is not TreeViewItem)
        {
            hit = hit.GetVisualParent();
        }
        var item = hit as TreeViewItem;
        return (item?.DataContext as TreeNodeBase, item);
    }

    private static TreeNodeBase? GetNodeAt(TreeView tree, Point point) => GetNodeAndItemAt(tree, point).Node;

    private void ClearDropHighlight()
    {
        if (_lastHoveredItem != null)
        {
            _lastHoveredItem.Classes.Remove("drop-target-valid");
            _lastHoveredItem.Classes.Remove("drop-target-invalid");
            _lastHoveredItem = null;
        }
    }

    private void UpdateDropHighlight(TreeViewItem? currentItem, bool isValid)
    {
        if (_lastHoveredItem != currentItem)
        {
            ClearDropHighlight();
            if (currentItem != null)
            {
                currentItem.Classes.Add(isValid ? "drop-target-valid" : "drop-target-invalid");
                _lastHoveredItem = currentItem;
            }
        }
        else if (_lastHoveredItem != null)
        {
            _lastHoveredItem.Classes.Set("drop-target-valid", isValid);
            _lastHoveredItem.Classes.Set("drop-target-invalid", !isValid);
        }
    }

    // 记录按下位置与命中的可拖节点（虚拟根不可拖）
    private void Tree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragNode = null;
        _dragPressedArgs = null;
        if (sender is TreeView tree
            && e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
        {
            _dragStart = e.GetPosition(tree);
            var (node, item) = GetNodeAndItemAt(tree, _dragStart);
            _dragNode = node is VirtualRootNode ? null : node;
            _dragPressedArgs = _dragNode == null ? null : e;
            _logger?.LogInformation("PointerPressed 捕获 坐标={Pos} 命中文档={Node} 容器={Item}", _dragStart, node?.Name, item);
        }
    }

    // 按住左键移动超 4px → 启动拖拽（携带节点 Id 字符串）
    private async void Tree_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode == null
            || _dragPressedArgs == null
            || sender is not TreeView tree
            || !e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Avalonia 无 WPF 的 ManhattanLength，用 |dx|+|dy| 等价判定阈值
        var position = e.GetPosition(tree);
        if (Math.Abs(position.X - _dragStart.X) + Math.Abs(position.Y - _dragStart.Y) < 4)
        {
            return;
        }

        var id = _dragNode.Id;
        _activeDraggedId = id;
        var pressedArgs = _dragPressedArgs;
        var nodeName = _dragNode.Name;
        // 启动后清空，避免重复触发
        _dragNode = null;
        _dragPressedArgs = null;

        _logger?.LogInformation("启动树节点拖拽 NodeId={Id} Name={Name}", id, nodeName);

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(id.ToString()));
            var dropEffect = await DragDrop.DoDragDropAsync(pressedArgs, transfer, DragDropEffects.Move);
            _logger?.LogInformation("树节点拖拽交互完成 DropEffect={Effect}", dropEffect);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "DoDragDropAsync 异常");
        }
        finally
        {
            _activeDraggedId = null;
            ClearDropHighlight();
        }
    }

    // 悬停判定：合法则显示移动光标，并高亮目标项
    private void Tree_DragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm
            || sender is not TreeView tree)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropHighlight();
            return;
        }

        // 优先从 DataTransfer 获取 ID，若跨进程/平台数据通道未就绪则回退到内存 activeId
        Guid draggedId;
        if (Guid.TryParse(e.DataTransfer?.TryGetText(), out var idFromTransfer))
        {
            draggedId = idFromTransfer;
        }
        else if (_activeDraggedId.HasValue)
        {
            draggedId = _activeDraggedId.Value;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropHighlight();
            return;
        }

        var (targetNode, targetItem) = GetNodeAndItemAt(tree, e.GetPosition(tree));
        var result = TreeDropResolver.Resolve(vm.DebugAllNodesCache, draggedId, targetNode);
        var isValid = result.IsValid && !result.IsNoOp;

        e.DragEffects = isValid ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;

        UpdateDropHighlight(targetItem, isValid);
    }

    // 拖拽离开控件区域：清理高亮
    private void Tree_DragLeave(object? sender, DragEventArgs e)
    {
        ClearDropHighlight();
    }

    // 落下：解析并执行移动
    private void Tree_Drop(object? sender, DragEventArgs e)
    {
        ClearDropHighlight();

        if (DataContext is not MainViewModel vm
            || sender is not TreeView tree)
        {
            return;
        }

        Guid draggedId;
        if (Guid.TryParse(e.DataTransfer?.TryGetText(), out var idFromTransfer))
        {
            draggedId = idFromTransfer;
        }
        else if (_activeDraggedId.HasValue)
        {
            draggedId = _activeDraggedId.Value;
        }
        else
        {
            return;
        }

        var target = GetNodeAt(tree, e.GetPosition(tree));
        var newParentId = target switch
        {
            null => null,
            VirtualRootNode => null,
            FolderNode folder => folder.Id,
            SessionNode session => session.ParentId,
            _ => null
        };
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        _ = vm.MoveNodeToAsync(draggedId, newParentId);
    }

    // ==========================================
    // 标签栏拖拽重排 (Tab Reordering)
    // ==========================================

    private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragTab = null;
        _dragTabPressedArgs = null;

        // 中键点击关闭标签（浏览器 / SecureCRT 习惯）
        if (e.GetCurrentPoint(TabsItemsControl).Properties.IsMiddleButtonPressed)
        {
            if (DataContext is MainViewModel mainVm && FindTabViewModelFromVisual(e.Source as Visual) is { } middleTab)
            {
                e.Handled = true;
                _ = mainVm.CloseWorkspaceTabCommand.ExecuteAsync(middleTab);
            }

            return;
        }

        // 仅处理鼠标左键按下
        if (!e.GetCurrentPoint(TabsItemsControl).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 排除关闭按钮点击触发的拖拽
        if (e.Source is Visual sourceVisual)
        {
            var btn = sourceVisual.FindAncestorOfType<Button>(includeSelf: true);
            if (btn != null && btn.Classes.Contains("closeBtn"))
            {
                return;
            }
        }

        // 寻找命中的 TerminalTabViewModel
        var tabVm = FindTabViewModelFromVisual(e.Source as Visual);
        if (tabVm != null)
        {
            _dragTab = tabVm;
            _dragTabPressedArgs = e;
            _dragTabStart = e.GetPosition(TabsItemsControl);
        }
    }

    private async void Tab_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDraggingTab
            || _dragTab == null
            || _dragTabPressedArgs == null
            || !e.GetCurrentPoint(TabsItemsControl).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var currentPos = e.GetPosition(TabsItemsControl);
        // 水平位移阈值 >= 6px 启动拖拽，防止普通点击切换标签被拦截
        if (Math.Abs(currentPos.X - _dragTabStart.X) < 6)
        {
            return;
        }

        var pressedArgs = _dragTabPressedArgs;
        var tab = _dragTab;
        _activeDraggedTab = tab;
        _dragTab = null;
        _dragTabPressedArgs = null;
        _isDraggingTab = true;

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText("workspace-tab"));
            await DragDrop.DoDragDropAsync(pressedArgs, transfer, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Tab DoDragDropAsync 异常");
        }
        finally
        {
            _isDraggingTab = false;
            _activeDraggedTab = null;
        }
    }

    private void Tab_DragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !_isDraggingTab)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var targetTab = FindTabViewModelFromVisual(e.Source as Visual);
        if (targetTab != null && targetTab != _activeDraggedTab)
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void Tab_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var targetTab = FindTabViewModelFromVisual(e.Source as Visual);
        var sourceTab = _activeDraggedTab ?? vm.ActiveWorkspaceTab;

        if (targetTab != null && sourceTab != null && targetTab != sourceTab)
        {
            var fromIdx = vm.WorkspaceTabs.IndexOf(sourceTab);
            var toIdx = vm.WorkspaceTabs.IndexOf(targetTab);
            if (fromIdx >= 0 && toIdx >= 0)
            {
                vm.MoveWorkspaceTab(fromIdx, toIdx);
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
            }
        }
    }

    private void Tab_ButtonClicked(object? sender, RoutedEventArgs e)
    {
        // 显式拦截 ✕ 关闭按钮点击，阻止事件冒泡到外层 tabItem 切换标签，并直接触发关闭
        if (e.Source is Visual visual)
        {
            var btn = visual.FindAncestorOfType<Button>(includeSelf: true);
            if (btn != null && btn.Classes.Contains("closeBtn"))
            {
                e.Handled = true;
                if (DataContext is MainViewModel vm && btn.DataContext is ViewModelBase tab)
                {
                    _ = vm.CloseWorkspaceTabCommand.ExecuteAsync(tab);
                }
            }
        }
    }

    private static ViewModelBase? FindTabViewModelFromVisual(Visual? visual)
    {
        while (visual != null)
        {
            if (visual.DataContext is ViewModelBase tab && tab is TerminalTabViewModel or NewTabViewModel)
            {
                return tab;
            }
            visual = visual.GetVisualParent();
        }
        return null;
    }

    // 双击会话节点直接连接（与右键菜单"连接"行为一致）
    private void Tree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (this.DataContext is MainViewModel vm
            && vm.SelectedTreeNode is SessionNode
            && vm.ConnectSelectedSessionCommand.CanExecute(null))
        {
            vm.ConnectSelectedSessionCommand.Execute(null);
        }

        e.Handled = true;
    }

    // 文件菜单与 macOS 原生退出快捷键共享同一确认流程。
    // NativeMenuItem.Click 是 EventHandler（EventArgs），签名必须与之一致才能直接订阅
    private void OnExitMenuClick(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsMacOS() && Application.Current is App app)
        {
            _ = Safe.RunAsync(_logger ?? NullLogger.Instance, "请求退出应用", app.RequestQuitAsync);
        }
        else if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Close();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != DataContextProperty)
        {
            return;
        }

        // 切换 DataContext 时同步 VM 属性监听
        if (e.OldValue is MainViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnMainViewModelPropertyChanged;
        }
        if (e.NewValue is MainViewModel newVm)
        {
            newVm.PropertyChanged += OnMainViewModelPropertyChanged;
            UpdateSessionManagerLayout(newVm);
            UpdateTabPlacement(newVm.TabPlacement);
            // 菜单手势依赖 VM 的 Command 实例，必须在 DataContext 就绪后装配
            ApplyMenuShortcuts(newVm);
            if (newVm.SelectedTab != null)
            {
                Dispatcher.UIThread.Post(() => newVm.SelectedTab?.Terminal.Focus());
            }
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;

        if (e.PropertyName is nameof(MainViewModel.IsSessionManagerVisible) or nameof(MainViewModel.IsSessionManagerPinned))
        {
            UpdateSessionManagerLayout(vm);
        }
        else if (e.PropertyName == nameof(MainViewModel.TabPlacement))
        {
            UpdateTabPlacement(vm.TabPlacement);
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedTab))
        {
            if (vm.SelectedTab != null)
            {
                Dispatcher.UIThread.Post(() => vm.SelectedTab?.Terminal.Focus());
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.CommandPaletteShortcutLabel))
        {
            ApplyMenuShortcuts(vm);
        }
        else if (e.PropertyName == nameof(MainViewModel.ActiveWorkspaceTab) && vm.ActiveWorkspaceTab is NewTabViewModel starter)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (vm.ActiveWorkspaceTab == starter)
                    foreach (NewTabView view in this.GetVisualDescendants().OfType<NewTabView>())
                        if (ReferenceEquals(view.DataContext, starter)) view.FocusDefaultAction();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Konsole 风格标签条上下切换：调整 Grid.Row 及 RowDefinition 高度，绝不重新实例化终端控件！
    /// </summary>
    public void UpdateTabPlacement(Kei.Term.Core.Models.Profiles.TabPlacement placement)
    {
        if (TabsBarBorder == null || TerminalContainer == null || RightContentGrid == null) return;

        if (placement == Kei.Term.Core.Models.Profiles.TabPlacement.Bottom)
        {
            // 终端占 Row 0 (*)，标签栏占 Row 1 (Auto)
            RightContentGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            RightContentGrid.RowDefinitions[1].Height = GridLength.Auto;

            Grid.SetRow(TerminalContainer, 0);
            Grid.SetRow(TabsBarBorder, 1);
            TabsBarBorder.BorderThickness = new Thickness(0, 1, 0, 0);
        }
        else
        {
            // 标签栏占 Row 0 (Auto)，终端占 Row 1 (*)
            RightContentGrid.RowDefinitions[0].Height = GridLength.Auto;
            RightContentGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);

            Grid.SetRow(TabsBarBorder, 0);
            Grid.SetRow(TerminalContainer, 1);
            TabsBarBorder.BorderThickness = new Thickness(0, 0, 0, 1);
        }
    }

}
