namespace Kei.Term.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Kei.Term.App.Helpers;
using Kei.Term.App.Workspaces;
using Kei.Term.App.Logging;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
using Kei.Term.App.Services.Connection;
using Kei.Term.App.Terminals;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Models.Profiles;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Vault;
using Kei.Term.Ssh.Abstractions;
using Kei.Term.Ssh.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public enum ComposeMode
{
    SingleLine,
    MultiLine
}

public partial class MainViewModel : ViewModelBase, IAsyncDisposable, IConnectionHost
{
    private readonly ITreeRepository _treeRepo;
    private readonly IIdentityRepository _identityRepo;
    private readonly IExternalEditorRepository? _editorRepo;
    private readonly ISettingsService _settingsService;
    private readonly Services.ProfileManagerService? _profileManager;
    private readonly HostKeyTrustService? _hostKeyTrust;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ILoggerFactory? _loggerFactory;

    // UI 线程分派：默认走 Avalonia Dispatcher；测试可注入同步执行器
    private readonly Action<Action> _uiDispatch;

    private int _disposed;

    // 供测试观察配色刷新是否被触发（生产无副作用）
    public int TerminalProfileRefreshCount { get; private set; }

    // 供窗口层复用同一 logger（弹窗 lambda 容错记录）
    public ILogger Logger => _logger;

    // 最近一次从仓储取回的扁平节点缓存，过滤/建树/解析继承链时复用，避免重复查库
    private IReadOnlyList<TreeNodeBase> _allNodesCache = [];

    // 自动锁定计时器（每分钟检查一次，默认 0 分钟不启用）
    private DispatcherTimer? _lockTimer;

    // 剪贴板状态：剪切 = 已从树中删除的子树快照（等待粘贴恢复）；复制 = 待克隆的源节点（仍保留在树中）
    private TreeNodeBase? _cutSnapshot;
    private TreeNodeBase? _copySourceNode;

    [ObservableProperty]
    private ObservableCollection<TreeNodeBase> _treeNodes = [];

    [ObservableProperty]
    private TreeNodeBase? _selectedTreeNode;

    [ObservableProperty]
    private ObservableCollection<TerminalTabViewModel> _tabs = [];

    [ObservableProperty]
    private TerminalTabViewModel? _selectedTab;

    [ObservableProperty]
    private bool _isComposeBarVisible = true;

    // 预输入模式：单行快捷栏 / 多行脚本台
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingleLineCompose))]
    [NotifyPropertyChangedFor(nameof(IsMultiLineCompose))]
    [NotifyPropertyChangedFor(nameof(ComposePlaceholder))]
    private ComposeMode _composeMode = ComposeMode.SingleLine;

    public bool IsSingleLineCompose
    {
        get => ComposeMode == ComposeMode.SingleLine;
        set
        {
            if (value) ComposeMode = ComposeMode.SingleLine;
        }
    }

    public bool IsMultiLineCompose
    {
        get => ComposeMode == ComposeMode.MultiLine;
        set
        {
            if (value) ComposeMode = ComposeMode.MultiLine;
        }
    }

    public string ComposePlaceholder => ComposeMode == ComposeMode.MultiLine
        ? Strings.Get("Main.Compose.PlaceholderMultiLine")
        : Strings.Get("Main.Compose.Placeholder");

    [ObservableProperty]
    private string _composeText = string.Empty;

    // 过滤关键字，变更即刷新树展示
    [ObservableProperty]
    private string _filterText = string.Empty;

    // 树是否为空（控制空树引导态显隐）
    [ObservableProperty]
    private bool _hasNodes;

    // 是否存在终端标签（控制空态与撰写栏）
    [ObservableProperty]
    private bool _hasTabs;

    // 停靠树是连接拓扑。Tabs 与协调器全集是同一个集合。
    public WorkspaceCoordinator Workspace { get; }

    // 视图在移除文档前释放对应的复合视图缓存。
    public event Action<TerminalTabViewModel>? TabReleasing;

    // 标签栏停靠位置（Top 置顶 / Bottom 置底）
    [ObservableProperty]
    private TabPlacement _tabPlacement = TabPlacement.Top;

    // 暂态状态提示（如粘贴目标无效）
    [ObservableProperty]
    private string? _statusMessage;

    // 关闭窗口前是否需要确认（来自设置）
    public bool ConfirmBeforeClose => _settingsService.Current.ConfirmBeforeClose;

    // 用户交互（弹窗 / 选择器 / 子窗口 / 通知）统一入口；窗口层注入 Avalonia 实现，默认按"取消"处理
    public IInteractionService Interaction { get; set; } = NullInteractionService.Instance;

    // Vault 会话（懒解锁、材料读写、口令缓存、空闲锁定判定）
    public VaultSessionService VaultSession { get; }

    // 连接编排（认证收集 → 跳板 → 建连重试 → 主机密钥确认 → 文件侧栏）
    private readonly ConnectionOrchestrator _connections;
    private readonly IProxyRepository? _proxyRepo;
    private readonly IPortForwardRepository? _portForwards;

    // 当前设置快照（供快速连接窗口取默认端口/用户名等）
    public AppSettings CurrentSettings => _settingsService.Current;

    public MainViewModel(
        ITreeRepository treeRepo,
        IIdentityRepository identityRepo,
        IVaultManager vault,
        IVaultSecretStore vaultSecretStore,
        ISettingsService settingsService,
        ISshSessionFactory sshFactory,
        IExternalEditorRepository? editorRepo = null,
        Services.ProfileManagerService? profileManager = null,
        ILogger<MainViewModel>? logger = null,
        ILoggerFactory? loggerFactory = null,
        Action<Action>? uiDispatch = null,
        HostKeyTrustService? hostKeyTrust = null,
        IProxyRepository? proxyRepo = null,
        IPortForwardRepository? portForwards = null,
        IProxySecretStore? proxySecrets = null)
    {
        _hostKeyTrust = hostKeyTrust;
        _treeRepo = treeRepo;
        _identityRepo = identityRepo;
        _editorRepo = editorRepo;
        _profileManager = profileManager;
        _settingsService = settingsService;
        _logger = logger ?? NullLogger<MainViewModel>.Instance;
        _loggerFactory = loggerFactory;
        _uiDispatch = uiDispatch ?? DefaultUiDispatch;
        VaultSession = new VaultSessionService(
            vault,
            vaultSecretStore,
            settingsService,
            () => Interaction,
            loggerFactory?.CreateLogger<VaultSessionService>());
        _proxyRepo = proxyRepo;
        _portForwards = portForwards;
        ProxySecrets = proxySecrets;
        _connections = new ConnectionOrchestrator(
            sshFactory,
            new AuthMaterialCollector(
                identityRepo,
                settingsService,
                VaultSession,
                () => Interaction,
                loggerFactory?.CreateLogger<AuthMaterialCollector>()),
            hostKeyTrust,
            settingsService,
            () => Interaction,
            _uiDispatch,
            loggerFactory?.CreateLogger<ConnectionOrchestrator>(),
            _proxyRepo,
            _portForwards,
            proxySecrets: proxySecrets,
            ensureUnlocked: proxySecrets == null ? null : () => VaultSession.EnsureUnlockedAsync());

        Workspace = new WorkspaceCoordinator();
        Tabs = Workspace.AllTabs;
        WeakReferenceMessenger.Default.Register<WorkspaceActiveItemChangedMessage>(this, OnWorkspaceActiveTabChanged);
        WeakReferenceMessenger.Default.Register<WorkspaceItemCloseRequestedMessage>(this, OnWorkspaceCloseRequested);
        Workspace.AllItems.CollectionChanged += OnWorkspaceItemsChanged;

        // 配色变更单点订阅：不为每个标签单独挂钩子
        if (_profileManager != null)
        {
            _profileManager.TerminalProfileChanged += OnTerminalProfileChanged;
        }
    }

    // 默认分派：UI 线程直接执行，其他线程 Post 回 UI 线程
    private static void DefaultUiDispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    // 配色变更事件入口：跨线程安全分派
    private void OnTerminalProfileChanged(TerminalProfileChange change)
    {
        if (_disposed != 0)
        {
            return;
        }

        _uiDispatch(() =>
        {
            // 排队事件可能在 Dispose 之后才执行：执行前再次检查，确保 shutdown race 安全
            if (_disposed != 0)
            {
                return;
            }

            ApplyTerminalProfileChange(change);
        });
    }

    // 单点刷新规则：
    // DefaultSelectionChanged → 只刷新继承全局（无显式 ID）的标签
    // ProfileContentEdited  → 刷新 EffectiveProfileId 命中该 ID 的标签（继承与显式都刷新）
    public void ApplyTerminalProfileChange(TerminalProfileChange change)
    {
        if (_disposed != 0 || _profileManager == null)
        {
            return;
        }

        TerminalProfileRefreshCount++;

        if (change.Reason == TerminalProfileChangeReason.DefaultSelectionChanged)
        {
            var effective = _profileManager.ResolveEffectiveTerminalProfile(null);
            foreach (var tab in Tabs.ToList())
            {
                // 已关闭/释放的标签不更新；显式覆盖的会话不跟随全局切换
                if (tab.IsDisposed || !string.IsNullOrWhiteSpace(tab.ExplicitProfileId))
                {
                    continue;
                }

                tab.ApplyTerminalProfile(effective);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(change.ProfileId))
        {
            return;
        }

        var profile = _profileManager.ResolveEffectiveTerminalProfile(change.ProfileId);
        foreach (var tab in Tabs.ToList())
        {
            if (tab.IsDisposed)
            {
                continue;
            }

            if (!string.Equals(tab.EffectiveProfileId, change.ProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tab.ApplyTerminalProfile(profile);
        }
    }

    // 释放：解除配色事件订阅并停止计时器（可重入安全）
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        if (_profileManager != null)
        {
            _profileManager.TerminalProfileChanged -= OnTerminalProfileChanged;
        }

        if (_lockTimer != null)
        {
            _lockTimer.Tick -= OnAutoLockTick;
            _lockTimer.Stop();
            _lockTimer = null;
        }

        WeakReferenceMessenger.Default.UnregisterAll(this);
        Workspace.AllItems.CollectionChanged -= OnWorkspaceItemsChanged;
        Workspace.Dispose();
        return ValueTask.CompletedTask;
    }

    // 有效配色解析：会话显式 ID → 全局默认 → 内置默认（管理器缺省时直接内置）
    private TerminalProfile ResolveEffectiveTerminalProfile(string? explicitProfileId)
        => _profileManager?.ResolveEffectiveTerminalProfile(explicitProfileId)
           ?? BuiltInPresets.GetDefaultTerminalProfile();

    // 由当前设置构造“已应用”字体快照
    private static TerminalFontSnapshot BuildAppliedFontSnapshot(AppSettings settings)
    {
        var fallbacks = string.IsNullOrWhiteSpace(settings.TerminalFallbackFontFamily)
            ? Array.Empty<string>()
            : settings.TerminalFallbackFontFamily
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new TerminalFontSnapshot(
            settings.FontFamily,
            fallbacks,
            settings.FontSize,
            settings.CursorBlink);
    }

    // 全局字号缩放（Ctrl+滚轮）：写回设置 → 刷新所有标签字体快照 → 立即保存；已到边界则不写不改不存
    public void ApplyGlobalFontZoom(int steps)
    {
        if (_disposed != 0 || steps == 0)
        {
            return;
        }

        var settings = _settingsService.Current;
        double target = TerminalFontZoom.Step(settings.FontSize, steps);
        if (target == settings.FontSize)
        {
            return;
        }

        settings.FontSize = target;
        var snapshot = BuildAppliedFontSnapshot(settings);
        foreach (var tab in Tabs.ToList())
        {
            if (tab.IsDisposed)
            {
                continue;
            }

            // 未创建 TerminalControl 的标签也更新快照，控件创建时自然沿用新字号
            tab.ApplyFontSnapshot(snapshot);
        }

        // 只提交字号：保存锁内补丁当前对象，不用这次拿到的完整快照覆盖其他字段
        _ = _settingsService.CommitFontSizeAsync(target);
    }

    // 标签请求缩放（Ctrl+滚轮）→ 统一走全局缩放
    private void OnTabFontZoomRequested(int steps) => ApplyGlobalFontZoom(steps);

    partial void OnFilterTextChanged(string value) => RefreshTreeFromCache();

    partial void OnSelectedTreeNodeChanged(TreeNodeBase? value)
    {
        // 选中节点变化后刷新依赖选中态的命令可用性
        ConnectSelectedSessionCommand.NotifyCanExecuteChanged();
        CutNodeCommand.NotifyCanExecuteChanged();
        CopyNodeCommand.NotifyCanExecuteChanged();
        EditSelectedNodeCommand.NotifyCanExecuteChanged();
        DeleteSelectedNodeCommand.NotifyCanExecuteChanged();
        RenameSelectedNodeCommand.NotifyCanExecuteChanged();
        DuplicateSelectedSessionCommand.NotifyCanExecuteChanged();
        if (RenamingNode != null && RenamingNode != value)
        {
            CancelTreeRename();
        }
    }

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        // IsSelected 只表示全局活动连接，不决定其他分屏组是否可见。
        foreach (var tab in Tabs)
        {
            tab.IsSelected = ReferenceEquals(tab, value);
        }
        if (value != null) ActiveWorkspaceTab = value;
        else if (ActiveWorkspaceTab is TerminalTabViewModel) ActiveWorkspaceTab = null;
        DisconnectCurrentTabCommand.NotifyCanExecuteChanged();
        OpenTerminalFindCommand.NotifyCanExecuteChanged();
        if (_syncingFromWorkspace || _disposed != 0 || value == null)
        {
            return;
        }

        Workspace.Activate(value);
    }

    [RelayCommand(CanExecute = nameof(CanOpenTerminalFind))]
    private void OpenTerminalFind()
    {
        SelectedTab?.OpenFind();
    }

    private bool CanOpenTerminalFind() => SelectedTab != null;

    public async Task InitializeAsync()
    {
        var settings = _settingsService.Current;

        ApplySessionManagerSettings();

        // 初始化撰写栏显隐状态
        IsComposeBarVisible = settings.ComposeBarVisibilityMode switch
        {
            PanelVisibilityMode.AlwaysVisible => true,
            PanelVisibilityMode.AlwaysHidden => false,
            PanelVisibilityMode.RememberLastState => settings.LastComposeBarVisible,
            _ => false
        };

        // 同步配置中的标签栏停靠位置（Top/Bottom）
        if (Enum.TryParse<TabPlacement>(settings.TabPlacement, true, out var placement))
        {
            TabPlacement = placement;
        }

        await ReloadTreeAsync();

        // 自动锁定计时：每分钟 tick，按设置的空闲阈值判断是否 Lock
        VaultSession.MarkAccessed();
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _lockTimer.Tick += OnAutoLockTick;
        _lockTimer.Start();
    }

    private void OnAutoLockTick(object? sender, EventArgs e) => VaultSession.AutoLockIfIdle(DateTime.UtcNow);

    // 锁定 Vault 并清空 SessionOnly 口令缓存（手动锁定入口）
    public void LockVault() => VaultSession.Lock();

    public async Task ReloadTreeAsync()
    {
        // 捕获当前树中所有已处于展开态的文件夹 ID，避免重查库/重构后展开态被重置
        var currentlyExpanded = _allNodesCache
            .OfType<FolderNode>()
            .Where(f => f.IsExpanded)
            .Select(f => f.Id)
            .ToHashSet();

        // 仅在此处查库并更新缓存，过滤/重建展示均走缓存
        var fromDb = await _treeRepo.GetAllNodesAsync();

        // 合并展开态：若之前在内存中已展开过，保留其展开态
        foreach (var node in fromDb)
        {
            if (node is FolderNode fn && currentlyExpanded.Contains(fn.Id))
            {
                fn.IsExpanded = true;
            }
        }

        _allNodesCache = fromDb;
        RefreshTreeFromCache();
    }

    // 用缓存的扁平节点按过滤关键字重建树展示（命中节点 + 祖先链，不重复查库）
    private void RefreshTreeFromCache()
    {
        var filtered = TreeFilter.Select(_allNodesCache, FilterText);
        BuildTree(filtered);
        // 虚拟根恒存在，故按真实节点数判定空树（过滤结果可能为空）
        HasNodes = filtered.Count > 0;
    }

    // 文件夹展开/折叠状态监听绑定与即时持久化
    private void HookFolderNodeEvents(IEnumerable<TreeNodeBase> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is FolderNode folder)
            {
                folder.PropertyChanged -= OnFolderPropertyChanged;
                folder.PropertyChanged += OnFolderPropertyChanged;
            }
        }
    }

    private void OnFolderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderNode.IsExpanded) && sender is FolderNode folder)
        {
            _ = _treeRepo.UpdateFolderExpandedAsync(folder.Id, folder.IsExpanded);
        }
    }

    private void BuildTree(IReadOnlyList<TreeNodeBase> allNodes)
    {
        HookFolderNodeEvents(allNodes);

        var lookup = allNodes.ToDictionary(n => n.Id);
        var roots = new List<TreeNodeBase>();

        // 清理旧 Children 引用以防重复添加
        foreach (var node in allNodes)
        {
            if (node is FolderNode fn)
            {
                fn.Children.Clear();
            }
        }

        foreach (var node in allNodes)
        {
            if (!node.ParentId.HasValue || !lookup.ContainsKey(node.ParentId.Value))
            {
                roots.Add(node);
            }
            else
            {
                var parent = lookup[node.ParentId.Value];
                if (parent is FolderNode folder)
                {
                    folder.Children.Add(node);
                }
            }
        }

        // 同级排序规约：
        // 1. 目录必须在前（0），会话在后（1）；
        // 2. 按 SortOrder 升序；
        // 3. 名称排序：根据当前配置，若为 AsciiFirst（默认），则首字符为 ASCII/英文/数字 排在非 ASCII/中文 前面；
        //    若同为 ASCII 或同为非 ASCII，则按大小写不敏感自然顺序比较。
        var isAsciiFirst = !string.Equals(CurrentSettings?.TreeSortMode, "Pinyin", StringComparison.OrdinalIgnoreCase);

        int CompareNodes(TreeNodeBase a, TreeNodeBase b)
        {
            var typeCmp = a.NodeType.CompareTo(b.NodeType);
            if (typeCmp != 0) return typeCmp;
            var orderCmp = a.SortOrder.CompareTo(b.SortOrder);
            if (orderCmp != 0) return orderCmp;

            var nameA = a.Name ?? string.Empty;
            var nameB = b.Name ?? string.Empty;

            if (isAsciiFirst && nameA.Length > 0 && nameB.Length > 0)
            {
                var aIsAscii = nameA[0] < 128;
                var bIsAscii = nameB[0] < 128;
                if (aIsAscii && !bIsAscii) return -1;
                if (!aIsAscii && bIsAscii) return 1;
            }

            return string.Compare(nameA, nameB, StringComparison.CurrentCultureIgnoreCase);
        }

        roots.Sort(CompareNodes);

        foreach (var node in allNodes)
        {
            if (node is FolderNode folder && folder.Children.Count > 1)
            {
                folder.Children.Sort(CompareNodes);
            }
        }

        // 包装为 SecureCRT 式单一虚拟根 "Sessions"（展示层，永不持久化）
        var virtualRoot = new VirtualRootNode();
        virtualRoot.Children.AddRange(roots);
        TreeNodes = new ObservableCollection<TreeNodeBase> { virtualRoot };
    }

    [RelayCommand]
    private Task CreateFolderAsync() => Safe.RunAsync(_logger, "新建文件夹", async () =>
    {
        var parentId = TreePlacement.ResolveCreationParent(SelectedTreeNode);
        var result = await Interaction.EditFolderAsync(null, parentId);
        if (result != null)
        {
            await _treeRepo.SaveNodeAsync(result);
            await ReloadTreeAsync();
        }
    });

    [RelayCommand(CanExecute = nameof(CanUseSelectedNode))]
    private Task EditSelectedNodeAsync() => Safe.RunAsync(_logger, "编辑选中项", async () =>
    {
        // 虚拟根不可编辑
        if (SelectedTreeNode == null || SelectedTreeNode is VirtualRootNode) return;

        if (SelectedTreeNode is SessionNode session)
        {
            var identities = await _identityRepo.GetAllAsync();
            var result = await Interaction.EditSessionAsync(session, session.ParentId, identities);
            if (result != null)
            {
                await _treeRepo.SaveNodeAsync(result);
                await PersistPendingForwardsAsync(result.Id);
                await ReloadTreeAsync();
            }
        }
        else if (SelectedTreeNode is FolderNode folder)
        {
            var result = await Interaction.EditFolderAsync(folder, folder.ParentId);
            if (result != null)
            {
                await _treeRepo.SaveNodeAsync(result);
                await ReloadTreeAsync();
            }
        }
    });

    [RelayCommand(CanExecute = nameof(CanUseSelectedNode))]
    private Task DeleteSelectedNodeAsync() => Safe.RunAsync(_logger, "删除选中项", async () =>
    {
        // 虚拟根不可删除
        if (SelectedTreeNode == null || SelectedTreeNode is VirtualRootNode) return;

        if (!await Interaction.ConfirmDeleteAsync(SelectedTreeNode.Name))
        {
            return;
        }

        await _treeRepo.DeleteNodeAsync(SelectedTreeNode.Id);
        SelectedTreeNode = null;
        await ReloadTreeAsync();
    });

    // === 剪贴板：剪切 / 复制 / 粘贴 ===

    // 复制：记录剪贴板源节点，并立即在源位置克隆一份（克隆根名称追加 “ 副本”）
    [RelayCommand(CanExecute = nameof(CanUseSelectedNode))]
    private Task CopyNodeAsync() => Safe.RunAsync(_logger, "复制节点", async () =>
    {
        if (SelectedTreeNode == null) return;

        _copySourceNode = SelectedTreeNode;
        _cutSnapshot = null;
        PasteNodeCommand.NotifyCanExecuteChanged();

        await CloneSubtreeIntoAsync(SelectedTreeNode, SelectedTreeNode.ParentId);
    });

    // 剪切语义：记录节点 Id 并立即 DeleteNodeAsync 从树中移除（子树随外键级联删除，存储不留行）；
    // 之后粘贴时以同 Id 重建整棵子树挂到目标位置，等效“移动到目标”；若始终未粘贴，数据不会恢复。
    [RelayCommand(CanExecute = nameof(CanUseSelectedNode))]
    private Task CutNodeAsync() => Safe.RunAsync(_logger, "剪切节点", async () =>
    {
        if (SelectedTreeNode == null) return;

        // 剪切前快照完整子树（含被过滤隐藏的子节点），保证粘贴可完整恢复
        var snapshot = await SnapshotSubtreeAsync(SelectedTreeNode.Id);
        if (snapshot == null) return;

        _cutSnapshot = snapshot;
        _copySourceNode = null;
        PasteNodeCommand.NotifyCanExecuteChanged();

        await _treeRepo.DeleteNodeAsync(snapshot.Id);
        SelectedTreeNode = null;
        await ReloadTreeAsync();
    });

    // 粘贴：剪切态 = 以同 Id 重建快照子树挂到目标（等效移动）；复制态 = 克隆源子树挂到目标（追加 “ 副本”）。
    // 目标 = 当前选中的文件夹，否则为根；粘贴后清空剪贴板状态。
    [RelayCommand(CanExecute = nameof(CanPasteNode))]
    private Task PasteNodeAsync() => Safe.RunAsync(_logger, "粘贴节点", async () =>
    {
        var targetId = SelectedTreeNode is FolderNode targetFolder ? targetFolder.Id : (Guid?)null;

        if (_cutSnapshot != null)
        {
            // 剪切态：源行已被级联删除，故以同 Id 重插整棵子树并改父到目标（MoveNodeAsync 无行可移，故不适用）
            _cutSnapshot.ParentId = targetId;
            await SaveSubtreeAsync(_cutSnapshot);
            ClearClipboard();
            await ReloadTreeAsync();
            return;
        }

        if (_copySourceNode != null)
        {
            // 复制态：目标无效（把文件夹粘到自身后代）时忽略并给出状态提示
            if (!IsValidCloneTarget(_copySourceNode, targetId))
            {
                StatusMessage = Strings.Get("Status.Paste.InvalidTarget");
                return;
            }

            await CloneSubtreeIntoAsync(_copySourceNode, targetId);
            ClearClipboard();
            await ReloadTreeAsync();
        }
    });

    private bool CanUseSelectedNode() => SelectedTreeNode is not null and not VirtualRootNode;

    [ObservableProperty]
    private TreeNodeBase? _renamingNode;

    [ObservableProperty]
    private string _treeRenameText = string.Empty;

    public event Action? TreeRenameStarted;

    private bool _renameCommitInFlight;

    // F2 与右键共用：只进入编辑，不另开对话框
    [RelayCommand(CanExecute = nameof(CanUseSelectedNode))]
    private void RenameSelectedNode()
    {
        if (SelectedTreeNode is not (SessionNode or FolderNode))
        {
            return;
        }

        TreeRenameText = SelectedTreeNode.Name;
        RenamingNode = SelectedTreeNode;
        TreeRenameStarted?.Invoke();
    }

    public void CancelTreeRename()
    {
        RenamingNode = null;
    }

    // 空白恢复原名且不写库。失败则名称回到保存前，并走现有错误提示。
    public async Task CommitTreeRenameAsync(string? proposed)
    {
        if (_renameCommitInFlight)
        {
            return;
        }

        TreeNodeBase? node = RenamingNode ?? SelectedTreeNode;
        if (node is not (SessionNode or FolderNode))
        {
            return;
        }

        _renameCommitInFlight = true;
        string original = node.Name;
        try
        {
            RenamingNode = null;
            string trimmed = proposed?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                node.Name = original;
                return;
            }

            node.Name = trimmed;
            try
            {
                await _treeRepo.SaveNodeAsync(node);
            }
            catch (Exception ex)
            {
                node.Name = original;
                await Interaction.NotifyAsync(Strings.Get("Tree.Menu.Rename"), ex.Message);
            }
        }
        finally
        {
            _renameCommitInFlight = false;
        }
    }

    private bool CanPasteNode() => _cutSnapshot != null || _copySourceNode != null;

    private void ClearClipboard()
    {
        _cutSnapshot = null;
        _copySourceNode = null;
        PasteNodeCommand.NotifyCanExecuteChanged();
    }

    // 从存储重建指定节点的完整子树（不依赖当前过滤状态），返回携带 Children 的子树根
    private async Task<TreeNodeBase?> SnapshotSubtreeAsync(Guid rootId)
    {
        var all = await _treeRepo.GetAllNodesAsync();
        var byId = all.ToDictionary(n => n.Id);
        if (!byId.TryGetValue(rootId, out var root))
        {
            return null;
        }

        // 临时列表为新实例，重建父子关系不影响当前展示缓存
        foreach (var node in all)
        {
            if (node is FolderNode folder)
            {
                folder.Children.Clear();
            }
        }
        foreach (var node in all)
        {
            if (node.ParentId.HasValue && byId.TryGetValue(node.ParentId.Value, out var parent) && parent is FolderNode folder)
            {
                folder.Children.Add(node);
            }
        }

        return root;
    }

    // 克隆 source 子树并挂到 targetParentId 下：先父后子逐节点落库，克隆根名称追加 “ 副本”，随后重载树
    private async Task CloneSubtreeIntoAsync(TreeNodeBase source, Guid? targetParentId)
    {
        var cloneRoot = TreeNodeCloner.DeepClone(source);
        cloneRoot.ParentId = targetParentId;
        cloneRoot.Name = string.Format(Strings.Get("Common.CopySuffix"), source.Name);
        await SaveSubtreeAsync(cloneRoot);
        await ReloadTreeAsync();
    }

    // 先父后子（前序）逐节点落库，满足外键约束
    private async Task SaveSubtreeAsync(TreeNodeBase node)
    {
        await _treeRepo.SaveNodeAsync(node);
        if (node is FolderNode folder)
        {
            foreach (var child in folder.Children)
            {
                await SaveSubtreeAsync(child);
            }
        }
    }

    // 克隆目标校验：目标不能是源节点自身或其后代（否则会形成环）；目标为根时始终合法
    private bool IsValidCloneTarget(TreeNodeBase source, Guid? targetId)
    {
        if (targetId == null)
        {
            return true;
        }

        var byId = _allNodesCache.ToDictionary(n => n.Id);
        var cursorId = targetId.Value;
        while (byId.TryGetValue(cursorId, out var node))
        {
            if (node.Id == source.Id)
            {
                return false;
            }
            if (node.ParentId == null)
            {
                break;
            }
            cursorId = node.ParentId.Value;
        }

        return true;
    }

    // 全部折叠：收起所有文件夹后刷新树展示，并同步持久化
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in _allNodesCache)
        {
            if (node is FolderNode folder)
            {
                folder.IsExpanded = false;
                _ = _treeRepo.UpdateFolderExpandedAsync(folder.Id, false);
            }
        }
        RefreshTreeFromCache();
    }

    // 拖拽命中合法性判定用：只读透传当前扁平节点缓存（命名避免与既有字段混淆）
    public IReadOnlyList<TreeNodeBase> DebugAllNodesCache => _allNodesCache;

    // 会话编辑器跳板候选：当前缓存里的会话快照，不含文件夹
    internal IReadOnlyList<SessionNode> SnapshotSessionNodes()
        => _allNodesCache.OfType<SessionNode>().ToArray();

    // 选择会话对话框用打开时的树快照，不在对话框期间重新加载
    internal IReadOnlyList<TreeNodeBase> SnapshotSessionTree()
        => TreeNodes.ToArray();

    internal Task<IReadOnlyList<ProxyProfile>> LoadProxiesAsync()
        => _proxyRepo == null
            ? Task.FromResult<IReadOnlyList<ProxyProfile>>([])
            : _proxyRepo.GetAllAsync();

    internal IProxySecretStore? ProxySecrets { get; }

    internal Task SaveProxyAsync(ProxyProfile proxy)
        => _proxyRepo == null ? Task.CompletedTask : _proxyRepo.SaveAsync(proxy);

    internal Task<IReadOnlyList<PortForward>> LoadPortForwardsAsync(Guid sessionId)
        => _portForwards == null
            ? Task.FromResult<IReadOnlyList<PortForward>>([])
            : _portForwards.GetBySessionAsync(sessionId);

    private async Task PersistPendingForwardsAsync(Guid sessionId)
    {
        if (_portForwards == null)
        {
            return;
        }

        IReadOnlyList<PortForward> pending = Interaction.TakePendingForwards();
        IReadOnlyList<PortForward> existing = await _portForwards.GetBySessionAsync(sessionId);
        var keep = pending.Select(f => f.Id).ToHashSet();
        foreach (PortForward old in existing)
        {
            if (!keep.Contains(old.Id))
            {
                await _portForwards.DeleteAsync(old.Id);
            }
        }

        foreach (PortForward forward in pending)
        {
            forward.SessionId = sessionId;
            await _portForwards.SaveAsync(forward);
        }
    }

    // 拖拽移动：经 TreeDropResolver 校验（防环）后调用仓储 MoveNodeAsync，SortOrder 追加到目标同级末尾
    public Task MoveNodeToAsync(Guid nodeId, Guid? newParentId) => Safe.RunAsync(_logger, "移动节点", async () =>
    {
        var result = TreeDropResolver.Resolve(
            _allNodesCache,
            nodeId,
            newParentId is { } pid ? _allNodesCache.FirstOrDefault(n => n.Id == pid) : null);
        if (!result.IsValid || result.IsNoOp)
        {
            return;
        }

        var siblings = _allNodesCache.Where(n => n.ParentId == newParentId && n.Id != nodeId);
        var sortOrder = siblings.Any() ? siblings.Max(n => n.SortOrder) + 1 : 0;
        await _treeRepo.MoveNodeAsync(nodeId, newParentId, sortOrder);

        // 如果放入的是文件夹，确保目标文件夹处于展开状态，便于用户立即看到移入结果
        if (newParentId.HasValue)
        {
            var targetFolder = _allNodesCache.OfType<FolderNode>().FirstOrDefault(f => f.Id == newParentId.Value);
            if (targetFolder != null && !targetFolder.IsExpanded)
            {
                targetFolder.IsExpanded = true;
                await _treeRepo.UpdateFolderExpandedAsync(targetFolder.Id, true);
            }
        }

        await ReloadTreeAsync();
    });

    [RelayCommand]
    private Task OpenIdentityManagerAsync() => Safe.RunAsync(_logger, "打开身份管理器", async () =>
    {
        await Interaction.OpenIdentityManagerAsync();
    });

    [RelayCommand]
    private Task OpenKnownHostsAsync() => Safe.RunAsync(_logger, "打开已知主机", async () =>
    {
        await Interaction.OpenKnownHostsAsync();
    });

    [RelayCommand]
    private Task OpenSettingsAsync() => Safe.RunAsync(_logger, "打开设置", async () =>
    {
        await Interaction.OpenSettingsAsync();
    });

    // 连接侧栏选中的会话（选中节点为会话时可用）
    [RelayCommand(CanExecute = nameof(CanConnectSelectedSession))]
    private Task ConnectSelectedSessionAsync()
        => Safe.RunAsync(_logger, "连接选中会话", () => OpenSessionAsync(SelectedTreeNode as SessionNode));

    private bool CanConnectSelectedSession() => SelectedTreeNode is SessionNode;

    // 快速连接：经委托打开快速连接窗口，确认后的保存/连接逻辑集中在 ConnectQuickAsync
    [RelayCommand]
    private Task QuickConnectAsync() => Safe.RunAsync(_logger, "快速连接", async () =>
    {
        await RunConnectionFromWorkspaceAsync(() => Interaction.OpenQuickConnectAsync());
    });

    // 导入 ~/.ssh/config：具体 Host 别名 → 会话，IdentityFile → 身份，ProxyJump → 跳板链
    [RelayCommand]
    private Task ImportOpenSshConfigAsync() => Safe.RunAsync(_logger, "导入 OpenSSH 配置", async () =>
    {
        string path = OpenSshPaths.UserConfig;
        if (!File.Exists(path))
        {
            await Interaction.NotifyAsync(Strings.Get("Menu.File.ImportOpenSshConfig"), $"未找到 {path}");
            return;
        }

        string content = await File.ReadAllTextAsync(path);
        var importer = new OpenSshConfigImporter(_treeRepo, _identityRepo);
        SshConfigImportSummary summary = await importer.ImportAsync(content, OpenSshPaths.Expand, OpenSshPaths.ResolveInclude);
        _logger.LogInformation(
            "OpenSSH 配置导入完成 会话={Sessions} 跳过={Skipped} 身份={Identities} 警告={Warnings}",
            summary.SessionsImported,
            summary.SessionsSkipped,
            summary.IdentitiesCreated,
            summary.Warnings.Count);

        await ReloadTreeAsync();

        string message = $"导入 {summary.SessionsImported} 个会话（跳过已存在 {summary.SessionsSkipped} 个），新建 {summary.IdentitiesCreated} 个身份。";
        if (summary.Warnings.Count > 0)
        {
            message += "\n" + string.Join("\n", summary.Warnings);
        }
        await Interaction.NotifyAsync(Strings.Get("Menu.File.ImportOpenSshConfig"), message);
    });

    // 导入 ~/.ssh/known_hosts：与系统 ssh 共享已建立的主机信任（含哈希条目与 @revoked）
    [RelayCommand]
    private Task ImportKnownHostsAsync() => Safe.RunAsync(_logger, "导入 known_hosts", async () =>
    {
        string path = OpenSshPaths.UserKnownHosts;
        if (_hostKeyTrust == null || !File.Exists(path))
        {
            await Interaction.NotifyAsync(Strings.Get("Menu.File.ImportKnownHosts"), $"未找到 {path}");
            return;
        }

        KnownHostsParseResult parsed = OpenSshKnownHostsParser.Parse(await File.ReadAllTextAsync(path));
        int count = await _hostKeyTrust.ImportAsync(parsed.Entries);

        await Interaction.NotifyAsync(
            Strings.Get("Menu.File.ImportKnownHosts"),
            $"导入 {count} 条主机密钥，跳过无法识别的行 {parsed.SkippedLines} 行。");
    });

    // 导入 SecureCRT 会话：弹出目录选择框，提取层级目录与会话，并为用到的凭据建立占位 Profile
    [RelayCommand]
    private Task ImportSecureCrtAsync() => Safe.RunAsync(_logger, "导入 SecureCRT 会话", async () =>
    {
        var folderPath = await Interaction.PickSecureCrtFolderAsync();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        _logger.LogInformation("开始从目录导入 SecureCRT 会话: {Path}", folderPath);
        var importer = new SecureCrtImporter(_treeRepo, _identityRepo);
        var summary = await importer.ImportFromDirectoryAsync(folderPath);
        _logger.LogInformation("SecureCRT 会话导入完成: 扫描={Scanned} 目录={Folders} 会话={Sessions} 凭据={Identities}",
            summary.TotalFilesScanned, summary.FoldersCreated, summary.SessionsImported, summary.IdentitiesCreated);

        await ReloadTreeAsync();

        var msg = $"成功扫描 {summary.TotalFilesScanned} 个文件，新建 {summary.FoldersCreated} 个目录，导入 {summary.SessionsImported} 个会话。";
        if (summary.IdentitiesCreated > 0)
        {
            msg += $"\n为未注册凭据创建了 {summary.IdentitiesCreated} 个空身份档案 ({string.Join(", ", summary.ImportedIdentityNames)})，请在身份管理器中补全私钥或口令。";
        }
        await Interaction.NotifyAsync("SecureCRT 导入完成", msg);
    });

    [RelayCommand]
    private Task ImportKonsoleAsync() => Safe.RunAsync(_logger, "导入 Konsole 配色方案", async () =>
    {
        var filePath = await Interaction.PickKonsoleSchemeFileAsync();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        var content = await File.ReadAllTextAsync(filePath);
        var defaultName = Path.GetFileNameWithoutExtension(filePath);
        var parsed = Kei.Term.Core.Services.KonsoleColorSchemeParser.Parse(content, defaultName);

        // 独立导入使用已应用的全局字体设置；配色不读取也不写回字体
        var confirmed = await Interaction.EditTerminalProfileAsync(parsed, BuildAppliedFontSnapshot(_settingsService.Current));
        if (confirmed == null)
        {
            return; // 取消无副作用
        }

        var profileManager = _profileManager ?? new Services.ProfileManagerService(_settingsService);
        profileManager.AddOrUpdateCustomTerminalProfile(confirmed.DeepCopy());
        try
        {
            // 独立导入：合并后原子落盘并推进已提交权威
            await profileManager.CommitAndSaveProfilesAsync();
            await Interaction.NotifyAsync(
                Strings.Get("TerminalProfileEdit.Title"),
                Strings.Get("Settings.Appearance.BundleImportSuccess"));
        }
        catch (Exception ex)
        {
            // 落盘失败：撤销内存草稿并给出可见提示，不假装成功
            profileManager.RemoveCustomTerminalProfile(confirmed.Id);
            await Interaction.NotifyAsync(Strings.Get("TerminalProfileEdit.Title"), ex.Message);
        }
    });

    // 快速连接善后：按用户输入解析配置 → 可选保存会话 → 接入统一认证管线
    public Task ConnectQuickAsync(string host, int port, string? username, string? password, bool saveAsSession)
        => Safe.RunAsync(_logger, "快速连接善后", () => ConnectQuickCoreAsync(host, port, username, password, saveAsSession));

    private async Task ConnectQuickCoreAsync(string host, int port, string? username, string? password, bool saveAsSession)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        var settings = _settingsService.Current;
        var trimmedHost = host.Trim();
        var hasUsername = !string.IsNullOrWhiteSpace(username);
        _logger.LogInformation(
            "快速连接 host={Host}:{Port} 用户名={Username} 保存会话={Save}",
            trimmedHost,
            port,
            hasUsername ? username!.Trim() : "(默认)",
            saveAsSession);

        // 临时节点走统一解析（端口/用户名/终端类型兜底），显示名按快速连接约定改写
        var node = new SessionNode
        {
            Id = Guid.NewGuid(),
            Name = trimmedHost,
            Host = trimmedHost,
            Port = port,
            Username = hasUsername ? username!.Trim() : null,
            // 快速连接不绑定身份
            IdentityId = null
        };
        var resolved = SessionConfigBuilder.Build(node, settings);
        resolved = resolved with
        {
            SessionName = hasUsername ? $"{username!.Trim()}@{trimmedHost}" : trimmedHost
        };

        // 勾选保存：以用户原始输入落库（未填项保持 null，继续走全局默认/内建兜底；身份为空）
        if (saveAsSession)
        {
            node.Name = resolved.SessionName;
            node.ParentId = SelectedTreeNode is FolderNode folder ? folder.Id : null;
            await _treeRepo.SaveNodeAsync(node);
            await ReloadTreeAsync();
        }

        // 用户输入的密码作为预置材料；无密码时由管线的单次弹窗兜底
        MaterializedAuthMethod? preloaded = password == null
            ? null
            : new MaterializedAuthMethod(AuthMaterialKind.Password, new SecretPayload { Password = password });

        await _connections.ConnectAsync(new ConnectionRequest(resolved, UseIdentity: false, preloaded), this);
    }

    // 断开当前标签（无选中标签时禁用）
    [RelayCommand(CanExecute = nameof(CanDisconnectCurrentTab))]
    private Task DisconnectCurrentTabAsync() => Safe.RunAsync(_logger, "断开当前标签", async () =>
    {
        if (SelectedTab == null) return;
        await SelectedTab.DisconnectAsync();
    });

    private bool CanDisconnectCurrentTab() => SelectedTab != null;

    // 点击标签切换选中
    [RelayCommand]
    private void SelectTab(TerminalTabViewModel? tab)
    {
        if (tab != null)
        {
            SelectedTab = tab;
        }
    }

    [RelayCommand]
    private Task OpenSessionAsync(SessionNode? sessionNode) => Safe.RunAsync(_logger, "打开会话连接", async () =>
    {
        if (sessionNode == null)
        {
            return;
        }

        _logger.LogInformation("发起会话连接 会话={Session} 绑定身份={IdentityId}", sessionNode.Name, sessionNode.IdentityId);
        // 扁平解析：会话自身 → 全局设置 → 内建兜底（IdentityId 已在解析中回退到全局默认身份）
        var resolved = SessionConfigBuilder.Build(sessionNode, _settingsService.Current);
        await RunConnectionFromWorkspaceAsync(() => _connections.ConnectAsync(new ConnectionRequest(resolved, UseIdentity: true), this));
    });

    [RelayCommand]
    private Task SendComposeAsync() => Safe.RunAsync(_logger, "发送快捷命令", async () =>
    {
        if (string.IsNullOrEmpty(ComposeText) || SelectedTab == null)
        {
            return;
        }

        await SelectedTab.SendCommandAsync(ComposeText);
        ComposeText = string.Empty;
    });

    [RelayCommand]
    private void ToggleComposeBar()
    {
        IsComposeBarVisible = !IsComposeBarVisible;
        if (_settingsService.Current.ComposeBarVisibilityMode == PanelVisibilityMode.RememberLastState)
        {
            _settingsService.Current.LastComposeBarVisible = IsComposeBarVisible;
            _ = _settingsService.SaveSettingsAsync(_settingsService.Current);
        }
    }

    [RelayCommand]
    private void ToggleFileManager()
    {
        if (SelectedTab != null)
        {
            SelectedTab.ToggleFileManager();
        }
    }

    [RelayCommand]
    private void SetComposeMode(ComposeMode mode)
    {
        ComposeMode = mode;
    }
}
