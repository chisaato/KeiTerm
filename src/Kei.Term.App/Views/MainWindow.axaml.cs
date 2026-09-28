using System;
using System.ComponentModel;
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
    // 侧栏默认宽度与记忆宽度（收起后恢复用）
    private const double SidebarDefaultWidth = 260;
    private double _lastSidebarWidth = SidebarDefaultWidth;

    // 关闭确认放行标记：弹窗确认后直接放行本次关闭
    private bool _closeConfirmed;

    // 拖拽进行中的节点 Id（PointerPressed 命中记录，移动超阈值后启动 DoDragDrop）
    private TreeNodeBase? _dragNode;
    private Guid? _activeDraggedId;
    private TreeViewItem? _lastHoveredItem;
    private ILogger? _logger;

    // Avalonia 12 的 DoDragDropAsync 需 PointerPressedEventArgs：暂存按下事件参数，待超阈值后再启动
    private PointerPressedEventArgs? _dragPressedArgs;
    private Point _dragStart;

    // 标签栏拖拽状态
    private TerminalTabViewModel? _dragTab;
    private TerminalTabViewModel? _activeDraggedTab;
    private PointerPressedEventArgs? _dragTabPressedArgs;
    private Point _dragTabStart;
    private bool _isDraggingTab;

    public MainWindow()
    {
        InitializeComponent();

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

        // 标签栏拖拽与点击处理：在 TabsItemsControl 容器上附加事件
        TabsItemsControl.AddHandler(PointerPressedEvent, Tab_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        TabsItemsControl.AddHandler(Button.ClickEvent, Tab_ButtonClicked, RoutingStrategies.Bubble, handledEventsToo: true);
        TabsItemsControl.AddHandler(PointerMovedEvent, Tab_PointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        TabsItemsControl.AddHandler(DragDrop.DragOverEvent, Tab_DragOver);
        TabsItemsControl.AddHandler(DragDrop.DropEvent, Tab_Drop);

        // DataContext 变化时挂接 VM 属性监听（侧栏收起/恢复需要联动列宽）
        PropertyChanged += OnWindowPropertyChanged;
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

    public void WireDialogs(MainViewModel vm, IdentityManagerViewModel identityMgrVm, SettingsViewModel settingsVm, ILogger? logger = null)
    {
        _logger = logger;
        // 弹窗 lambda 统一容错：异常记录后按取消语义返回，避免 AsyncRelayCommand 静默吞异常
        var log = logger ?? NullLogger.Instance;

        vm.OpenSessionDialogAsync = (existing, parentId, identities) =>
            Safe.RunAsync<SessionNode?>(log, "打开会话编辑窗口", async () =>
            {
                log.LogInformation("会话编辑窗口打开 模式={Mode}", existing == null ? "新建" : "编辑");
                var editVm = new SessionEditViewModel(existing, parentId, identities);
                var win = new SessionEditWindow(editVm);
                await win.ShowDialog(this);
                var result = editVm.IsConfirmed ? editVm.ApplyToModel(existing) : null;
                log.LogInformation("会话编辑窗口关闭 结果={Result}", result == null ? "取消" : "确认");
                return result;
            });

        vm.OpenFolderDialogAsync = (existing, parentId) =>
            Safe.RunAsync<FolderNode?>(log, "打开文件夹编辑窗口", async () =>
            {
                log.LogInformation("文件夹编辑窗口打开 模式={Mode}", existing == null ? "新建" : "编辑");
                var editVm = new FolderEditViewModel(existing, parentId);
                var win = new FolderEditWindow(editVm);
                await win.ShowDialog(this);
                var result = editVm.IsConfirmed ? editVm.ApplyToModel(existing) : null;
                log.LogInformation("文件夹编辑窗口关闭 结果={Result}", result == null ? "取消" : "确认");
                return result;
            });

        vm.OpenIdentityManagerDialogAsync = () => Safe.RunAsync(log, "打开身份管理器", async () =>
        {
            log.LogInformation("身份管理器打开");
            await identityMgrVm.LoadAsync();
            var win = new IdentityManagerWindow(identityMgrVm, vm.Logger);
            await win.ShowDialog(this);
            log.LogInformation("身份管理器关闭");
        });

        // 身份管理器编辑器由管理器窗口自身以模态方式打开（保证 owner 正确）
        identityMgrVm.ConfirmDeleteAsync = _ => Task.FromResult(true);
        // 手动锁定 Vault 时一并清空 SessionOnly 口令缓存
        identityMgrVm.LockVaultAction = vm.LockVault;
        // 编辑器回显 Vault 私钥信息 / 「应用」时写入 Vault 材料
        identityMgrVm.VaultKeyInfoLoader = vm.GetVaultKeyInfoAsync;
        identityMgrVm.PersistVaultKeysAsync = vm.PersistVaultKeyImportsAsync;

        vm.OpenSettingsDialogAsync = () => Safe.RunAsync(log, "打开设置", async () =>
        {
            log.LogInformation("设置窗口打开");
            var win = new SettingsWindow(settingsVm);
            await win.ShowDialog(this);
            log.LogInformation("设置窗口关闭");

            // 设置确认保存后立即应用控件库主题（Apply 幂等，KeiClassic 即卸载第三方主题）
            if (settingsVm.IsConfirmed)
            {
                Services.UiDesignSystemService.Apply(vm.CurrentSettings.ControlLibraryTheme);
                // 实时热更新树显示密度与尺寸
                Services.UiDesignSystemService.ApplyTreeDensity(
                    vm.CurrentSettings.TreeItemHeight,
                    vm.CurrentSettings.TreeFontSize,
                    vm.CurrentSettings.TreeIconSize,
                    vm.CurrentSettings.TreeIndent);
                // 排序模式可能变更，立即刷新树排序
                _ = vm.ReloadTreeAsync();

                // 联动更新标签栏位置
                if (Enum.TryParse<Kei.Term.Core.Models.Profiles.TabPlacement>(vm.CurrentSettings.TabPlacement, true, out var placement))
                {
                    vm.TabPlacement = placement;
                    UpdateTabPlacement(placement);
                }
            }
            else
            {
                // 若用户取消，恢复标签栏位置与主题
                if (Enum.TryParse<Kei.Term.Core.Models.Profiles.TabPlacement>(vm.CurrentSettings.TabPlacement, true, out var origPlacement))
                {
                    vm.TabPlacement = origPlacement;
                    UpdateTabPlacement(origPlacement);
                }
            }
        });

        vm.ConfirmDeleteAsync = async name =>
        {
            // 简单确认弹窗，默认返回 true，后续可挂载独立 MessageBox
            await Task.CompletedTask;
            return true;
        };

        // 统一认证窗（完整模式）；取消 = null
        vm.AuthPromptDialogAsync = (prefillUsername, vaultKeys, defaultMethod) =>
            Safe.RunAsync<AuthPromptResult?>(log, "打开认证窗口", async () =>
            {
                var win = new AuthPromptWindow(prefillUsername, vaultKeys, defaultMethod);
                return await win.ShowDialog<AuthPromptResult?>(this);
            });

        // KI 真交互提示窗；取消 = 空应答
        vm.InteractiveInputDialogAsync = prompt =>
            Safe.RunAsync<string?>(log, "打开 KI 交互窗口", async () =>
            {
                var win = new AuthPromptWindow(prompt);
                return await win.ShowDialog<string?>(this);
            });

        // 主密码懒解锁框：错误提示传入，返回 null 表示取消
        vm.MasterPasswordDialogAsync = error =>
            Safe.RunAsync<string?>(log, "打开主密码窗口", async () =>
            {
                var win = new MasterPasswordWindow(error);
                return await win.ShowDialog<string?>(this);
            });

        // 文件私钥口令三态框
        vm.PassphrasePromptDialogAsync = method =>
            Safe.RunAsync<PassphrasePromptResult?>(log, "打开口令窗口", async () =>
            {
                var win = new PassphrasePromptWindow(method);
                return await win.ShowDialog<PassphrasePromptResult?>(this);
            });

        // 快速连接对话框：窗口收集输入，确认后回调主 VM 完成保存与连接
        vm.QuickConnectDialogAsync = () => Safe.RunAsync(log, "打开快速连接窗口", async () =>
        {
            log.LogInformation("快速连接窗口打开");
            var quickVm = new QuickConnectViewModel(vm.CurrentSettings);
            var win = new QuickConnectWindow(quickVm);
            await win.ShowDialog(this);
            if (!quickVm.IsConfirmed)
            {
                log.LogInformation("快速连接窗口取消");
                return;
            }

            // 密码仅本次内存流转；勾选保存时仅落库元数据
            await vm.ConnectQuickAsync(quickVm.Host, quickVm.Port, quickVm.Username, quickVm.Password, quickVm.SaveAsSession);
        });

        // SecureCRT 导入：使用原生 StorageProvider 弹出文件夹选择器
        vm.PickFolderDialogAsync = async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "选择 SecureCRT Sessions 目录",
                AllowMultiple = false
            });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        };

        // Konsole 导入文件选择器与调色预览弹窗
        vm.PickKonsoleFileDialogAsync = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = Strings.Get("Menu.File.ImportKonsole"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Konsole Color Scheme (*.colorscheme)") { Patterns = new[] { "*.colorscheme" } },
                    new Avalonia.Platform.Storage.FilePickerFileType("All Files (*.*)") { Patterns = new[] { "*.*" } }
                }
            });
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        };

        vm.OpenTerminalProfileEditDialogAsync = async (sourceProfile) =>
        {
            var editVm = new TerminalProfileEditViewModel(sourceProfile);
            var dialog = new TerminalProfileEditWindow(editVm);
            var result = await dialog.ShowDialog<bool>(this);
            return result && editVm.IsConfirmed ? editVm.ResultProfile : null;
        };

        // 统一消息通知：更新主窗口底部/中央状态提示栏，并在日志留痕
        vm.ShowNotificationAsync = async (title, message) =>
        {
            log.LogInformation("[通知] {Title}: {Message}", title, message);
            vm.StatusMessage = $"{title}: {message.Replace("\n", " ")}";
            await Task.CompletedTask;
        };
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
            transfer.Add(DataTransferItem.CreateText($"tab:{tab.Title}"));
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
        var sourceTab = _activeDraggedTab ?? vm.SelectedTab;

        if (targetTab != null && sourceTab != null && targetTab != sourceTab)
        {
            var fromIdx = vm.Tabs.IndexOf(sourceTab);
            var toIdx = vm.Tabs.IndexOf(targetTab);
            if (fromIdx >= 0 && toIdx >= 0)
            {
                vm.MoveTab(fromIdx, toIdx);
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
                if (DataContext is MainViewModel vm && btn.DataContext is TerminalTabViewModel tab)
                {
                    _ = vm.CloseTabCommand.ExecuteAsync(tab);
                }
            }
        }
    }

    private static TerminalTabViewModel? FindTabViewModelFromVisual(Visual? visual)
    {
        while (visual != null)
        {
            if (visual.DataContext is TerminalTabViewModel tab)
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

    // 菜单"退出"：走桌面生命周期正常关闭（触发 OnClosing 确认逻辑）
    private void OnExitMenuClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Close();
        }
    }

    // 菜单"关于"：极简版本信息弹窗
    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
        var about = new Window
        {
            Title = Strings.Get("About.Title"),
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = GetThemeBrush("Kei.Bg.Panel"),
            BorderBrush = GetThemeBrush("Kei.Border")
        };
        about.Content = new StackPanel
        {
            Margin = new Thickness(28, 22),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = Strings.Get("About.AppName"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = GetThemeBrush("Kei.Text.Primary")
                },
                new TextBlock
                {
                    Text = string.Format(Strings.Get("About.VersionFormat"), version),
                    FontSize = 12,
                    Foreground = GetThemeBrush("Kei.Text.Secondary")
                },
                new TextBlock
                {
                    Text = Strings.Get("About.Description"),
                    FontSize = 12,
                    Foreground = GetThemeBrush("Kei.Text.Muted")
                }
            }
        };
        await about.ShowDialog(this);
    }

    // 关闭确认：设置允许且有打开标签时弹窗确认，取消则阻止关闭
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_closeConfirmed
            || DataContext is not MainViewModel vm
            || !vm.ConfirmBeforeClose
            || vm.Tabs.Count == 0)
        {
            return;
        }

        e.Cancel = true;
        if (await ShowCloseConfirmDialogAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    // 简洁的深色确认窗口（中文文案）
    private async Task<bool> ShowCloseConfirmDialogAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = Strings.Get("Dialog.CloseConfirm.Title"),
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = GetThemeBrush("Kei.Bg.Panel"),
            BorderBrush = GetThemeBrush("Kei.Border")
        };

        var message = new TextBlock
        {
            Text = Strings.Get("Dialog.CloseConfirm.Message"),
            Foreground = GetThemeBrush("Kei.Text.Primary"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            FontSize = 13,
            Margin = new Thickness(20, 18, 20, 4)
        };

        var cancelButton = new Button { Content = Strings.Get("Common.Cancel"), Padding = new Thickness(14, 5), MinWidth = 76 };
        var confirmButton = new Button
        {
            Content = Strings.Get("Dialog.CloseConfirm.CloseButton"),
            Padding = new Thickness(14, 5),
            MinWidth = 76,
            Margin = new Thickness(8, 0, 0, 0),
            Background = GetThemeBrush("Kei.Accent"),
            Foreground = Brushes.White
        };

        cancelButton.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        confirmButton.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(20, 8, 20, 16),
            Children = { cancelButton, confirmButton }
        };

        dialog.Content = new StackPanel { Children = { message, buttons } };
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    // 从应用级主题资源取画刷
    private static IBrush? GetThemeBrush(string key)
    {
        return Application.Current?.Resources is ResourceDictionary resources
            && resources.TryGetValue(key, out var value)
            ? value as IBrush
            : null;
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
            UpdateSidebarColumn(newVm.IsSessionManagerVisible);
            UpdateTabPlacement(newVm.TabPlacement);
            if (newVm.SelectedTab != null)
            {
                Dispatcher.UIThread.Post(() => newVm.SelectedTab?.Terminal.Focus());
            }
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;

        if (e.PropertyName == nameof(MainViewModel.IsSessionManagerVisible))
        {
            UpdateSidebarColumn(vm.IsSessionManagerVisible);
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

    // 收起时把侧栏列宽压为 0，恢复时回到记忆宽度（默认 260，可被 GridSplitter 拖拽覆盖）。
    // 侧栏现位于 Row2 内嵌的 MainSplitGrid（第 0 列），非根 Grid。
    private void UpdateSidebarColumn(bool visible)
    {
        var column = MainSplitGrid.ColumnDefinitions[0];
        if (visible)
        {
            column.Width = new GridLength(
                _lastSidebarWidth > 1 ? _lastSidebarWidth : SidebarDefaultWidth,
                GridUnitType.Pixel);
        }
        else
        {
            if (column.Width.IsAbsolute && column.Width.Value > 1)
            {
                _lastSidebarWidth = column.Width.Value;
            }
            column.Width = new GridLength(0);
        }
    }
}
