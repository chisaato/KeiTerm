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
using Kei.Term.App.Helpers;
using Kei.Term.App.Logging;
using Kei.Term.App.Models;
using Kei.Term.App.Services;
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

public partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    // 认证失败后单次弹窗重试的最大次数（总弹窗上限）
    private const int MaxAuthRetries = 3;

    // 同一次连接中主机密钥人工确认的上限（防止服务器每次呈现不同密钥导致无限弹窗）
    private const int MaxHostKeyConfirmations = 2;

    private readonly ITreeRepository _treeRepo;
    private readonly IIdentityRepository _identityRepo;
    private readonly IExternalEditorRepository? _editorRepo;
    private readonly IVaultManager _vault;
    private readonly IVaultSecretStore _vaultSecretStore;
    private readonly ISettingsService _settingsService;
    private readonly Services.ProfileManagerService? _profileManager;
    private readonly ISshSessionFactory _sshFactory;
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

    // Vault 主密码连续失败计数（成功即清零）
    private int _vaultUnlockFailures;

    // 最近一次从仓储取回的扁平节点缓存，过滤/建树/解析继承链时复用，避免重复查库
    private IReadOnlyList<TreeNodeBase> _allNodesCache = [];

    // SessionOnly 口令缓存：键 = 方法 Id；Vault 锁定/退出时清空
    private readonly Dictionary<Guid, string> _sessionPassphrases = new();

    // 最近一次 Vault 访问时刻，供自动锁定计时判断
    private DateTime _lastVaultAccessUtc = DateTime.UtcNow;

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

    // 左侧连接管理器面板显隐
    [ObservableProperty]
    private bool _isSessionManagerVisible = true;

    [ObservableProperty]
    private string _composeText = string.Empty;

    // 过滤关键字，变更即刷新树展示
    [ObservableProperty]
    private string _filterText = string.Empty;

    // 树是否为空（控制空树引导态显隐）
    [ObservableProperty]
    private bool _hasNodes;

    // 是否存在终端标签（控制标签条显隐）
    [ObservableProperty]
    private bool _hasTabs;

    // 标签栏停靠位置（Top 置顶 / Bottom 置底）
    [ObservableProperty]
    private TabPlacement _tabPlacement = TabPlacement.Top;

    // 暂态状态提示（如粘贴目标无效）
    [ObservableProperty]
    private string? _statusMessage;

    // 关闭窗口前是否需要确认（来自设置）
    public bool ConfirmBeforeClose => _settingsService.Current.ConfirmBeforeClose;

    // UI 对话框委托
    public Func<SessionNode?, Guid?, IReadOnlyList<Identity>, Task<SessionNode?>>? OpenSessionDialogAsync { get; set; }
    public Func<FolderNode?, Guid?, Task<FolderNode?>>? OpenFolderDialogAsync { get; set; }
    public Func<Task>? OpenIdentityManagerDialogAsync { get; set; }
    public Func<Task>? OpenKnownHostsDialogAsync { get; set; }
    public Func<Task>? OpenSettingsDialogAsync { get; set; }
    public Func<string, Task<bool>>? ConfirmDeleteAsync { get; set; }

    // 统一认证窗完整模式：参数为（预填用户名, 保管库密钥选项, 指定默认方法?）；返回 null 表示取消
    public Func<string, IReadOnlyList<VaultKeyOption>, AuthPromptMethod?, Task<AuthPromptResult?>>? AuthPromptDialogAsync { get; set; }

    // KI 真交互提示模式：参数为服务器提示文本，返回应答（null/取消 → 空应答）
    public Func<string, Task<string?>>? InteractiveInputDialogAsync { get; set; }

    // 主密码输入框：参数为错误提示；返回 null 表示取消
    public Func<string?, Task<string?>>? MasterPasswordDialogAsync { get; set; }

    // 文件私钥口令三态框
    public Func<FilePrivateKeyMethod, Task<PassphrasePromptResult?>>? PassphrasePromptDialogAsync { get; set; }

    // 快速连接对话框：窗口内自行收集输入，确认后回调 ConnectQuickAsync
    public Func<Task>? QuickConnectDialogAsync { get; set; }

    // 目录选择对话框：用于导入 SecureCRT 会话目录，返回所选文件夹路径（取消 = null）
    public Func<Task<string?>>? PickFolderDialogAsync { get; set; }

    // 打开 Konsole 配色方案文件对话框
    public Func<Task<string?>>? PickKonsoleFileDialogAsync { get; set; }

    // 打开 Terminal Profile 调色预览窗口委托（参数：源方案, 当前字体快照）
    public Func<TerminalProfile?, TerminalFontSnapshot, Task<TerminalProfile?>>? OpenTerminalProfileEditDialogAsync { get; set; }

    // 提示通知/消息弹窗委托：参数为（标题, 内容）
    public Func<string, string, Task>? ShowNotificationAsync { get; set; }

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
        HostKeyTrustService? hostKeyTrust = null)
    {
        _hostKeyTrust = hostKeyTrust;
        _treeRepo = treeRepo;
        _identityRepo = identityRepo;
        _editorRepo = editorRepo;
        _profileManager = profileManager;
        _vault = vault;
        _vaultSecretStore = vaultSecretStore;
        _settingsService = settingsService;
        _sshFactory = sshFactory;
        _logger = logger ?? NullLogger<MainViewModel>.Instance;
        _loggerFactory = loggerFactory;
        _uiDispatch = uiDispatch ?? DefaultUiDispatch;

        // 标签集合变化时同步 HasTabs，控制标签条显隐
        Tabs.CollectionChanged += (_, _) => HasTabs = Tabs.Count > 0;

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

    partial void OnFilterTextChanged(string value) => RefreshTreeFromCache();

    partial void OnSelectedTreeNodeChanged(TreeNodeBase? value)
    {
        // 选中节点变化后刷新依赖选中态的命令可用性
        ConnectSelectedSessionCommand.NotifyCanExecuteChanged();
        CutNodeCommand.NotifyCanExecuteChanged();
        CopyNodeCommand.NotifyCanExecuteChanged();
        EditSelectedNodeCommand.NotifyCanExecuteChanged();
        DeleteSelectedNodeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        // 同步每个标签的选中态，供标签条样式使用
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab == value;
        }
        DisconnectCurrentTabCommand.NotifyCanExecuteChanged();
    }

    public async Task InitializeAsync()
    {
        var settings = _settingsService.Current;

        // 初始化连接管理器显隐状态
        IsSessionManagerVisible = settings.SessionManagerVisibilityMode switch
        {
            PanelVisibilityMode.AlwaysVisible => true,
            PanelVisibilityMode.AlwaysHidden => false,
            PanelVisibilityMode.RememberLastState => settings.LastSessionManagerVisible,
            _ => true
        };

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
        _lastVaultAccessUtc = DateTime.UtcNow;
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _lockTimer.Tick += OnAutoLockTick;
        _lockTimer.Start();
    }

    private void OnAutoLockTick(object? sender, EventArgs e)
    {
        var minutes = _settingsService.Current.LockTimeoutMinutes;
        if (minutes <= 0 || _vault.IsPlainMode || !_vault.IsUnlocked)
        {
            return;
        }

        if (DateTime.UtcNow - _lastVaultAccessUtc >= TimeSpan.FromMinutes(minutes))
        {
            _logger.LogInformation("Vault 自动锁定计时触发 空闲阈值={Minutes} 分钟", minutes);
            LockVault();
        }
    }

    // 锁定 Vault 并清空 SessionOnly 口令缓存（手动锁定与超时锁定共用）
    public void LockVault()
    {
        _vault.Lock();
        _sessionPassphrases.Clear();
        _lastVaultAccessUtc = DateTime.UtcNow;
        _logger.LogInformation("Vault 已锁定（内存 MEK 与 SessionOnly 口令缓存已清空）");
    }

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
    private Task CreateSessionAsync() => Safe.RunAsync(_logger, "新建会话", async () =>
    {
        if (OpenSessionDialogAsync == null) return;
        var parentId = TreePlacement.ResolveCreationParent(SelectedTreeNode);
        var identities = await _identityRepo.GetAllAsync();
        var result = await OpenSessionDialogAsync(null, parentId, identities);
        if (result != null)
        {
            await _treeRepo.SaveNodeAsync(result);
            await ReloadTreeAsync();
        }
    });

    [RelayCommand]
    private Task CreateFolderAsync() => Safe.RunAsync(_logger, "新建文件夹", async () =>
    {
        if (OpenFolderDialogAsync == null) return;
        var parentId = TreePlacement.ResolveCreationParent(SelectedTreeNode);
        var result = await OpenFolderDialogAsync(null, parentId);
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
            if (OpenSessionDialogAsync == null) return;
            var identities = await _identityRepo.GetAllAsync();
            var result = await OpenSessionDialogAsync(session, session.ParentId, identities);
            if (result != null)
            {
                await _treeRepo.SaveNodeAsync(result);
                await ReloadTreeAsync();
            }
        }
        else if (SelectedTreeNode is FolderNode folder)
        {
            if (OpenFolderDialogAsync == null) return;
            var result = await OpenFolderDialogAsync(folder, folder.ParentId);
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

        if (ConfirmDeleteAsync != null)
        {
            var ok = await ConfirmDeleteAsync(SelectedTreeNode.Name);
            if (!ok) return;
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
        if (OpenIdentityManagerDialogAsync != null)
        {
            await OpenIdentityManagerDialogAsync();
        }
    });

    [RelayCommand]
    private Task OpenKnownHostsAsync() => Safe.RunAsync(_logger, "打开已知主机", async () =>
    {
        if (OpenKnownHostsDialogAsync != null)
        {
            await OpenKnownHostsDialogAsync();
        }
    });

    [RelayCommand]
    private Task OpenSettingsAsync() => Safe.RunAsync(_logger, "打开设置", async () =>
    {
        if (OpenSettingsDialogAsync != null)
        {
            await OpenSettingsDialogAsync();
        }
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
        if (QuickConnectDialogAsync != null)
        {
            await QuickConnectDialogAsync();
        }
    });

    // 导入 ~/.ssh/config：具体 Host 别名 → 会话，IdentityFile → 身份，ProxyJump → 跳板链
    [RelayCommand]
    private Task ImportOpenSshConfigAsync() => Safe.RunAsync(_logger, "导入 OpenSSH 配置", async () =>
    {
        string path = OpenSshPaths.UserConfig;
        if (!File.Exists(path))
        {
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(Strings.Get("Menu.File.ImportOpenSshConfig"), $"未找到 {path}");
            }
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

        if (ShowNotificationAsync != null)
        {
            string message = $"导入 {summary.SessionsImported} 个会话（跳过已存在 {summary.SessionsSkipped} 个），新建 {summary.IdentitiesCreated} 个身份。";
            if (summary.Warnings.Count > 0)
            {
                message += "\n" + string.Join("\n", summary.Warnings);
            }
            await ShowNotificationAsync(Strings.Get("Menu.File.ImportOpenSshConfig"), message);
        }
    });

    // 导入 ~/.ssh/known_hosts：与系统 ssh 共享已建立的主机信任（含哈希条目与 @revoked）
    [RelayCommand]
    private Task ImportKnownHostsAsync() => Safe.RunAsync(_logger, "导入 known_hosts", async () =>
    {
        string path = OpenSshPaths.UserKnownHosts;
        if (_hostKeyTrust == null || !File.Exists(path))
        {
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(Strings.Get("Menu.File.ImportKnownHosts"), $"未找到 {path}");
            }
            return;
        }

        KnownHostsParseResult parsed = OpenSshKnownHostsParser.Parse(await File.ReadAllTextAsync(path));
        int count = await _hostKeyTrust.ImportAsync(parsed.Entries);

        if (ShowNotificationAsync != null)
        {
            await ShowNotificationAsync(
                Strings.Get("Menu.File.ImportKnownHosts"),
                $"导入 {count} 条主机密钥，跳过无法识别的行 {parsed.SkippedLines} 行。");
        }
    });

    // 导入 SecureCRT 会话：弹出目录选择框，提取层级目录与会话，并为用到的凭据建立占位 Profile
    [RelayCommand]
    private Task ImportSecureCrtAsync() => Safe.RunAsync(_logger, "导入 SecureCRT 会话", async () =>
    {
        if (PickFolderDialogAsync == null) return;
        var folderPath = await PickFolderDialogAsync();
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

        if (ShowNotificationAsync != null)
        {
            var msg = $"成功扫描 {summary.TotalFilesScanned} 个文件，新建 {summary.FoldersCreated} 个目录，导入 {summary.SessionsImported} 个会话。";
            if (summary.IdentitiesCreated > 0)
            {
                msg += $"\n为未注册凭据创建了 {summary.IdentitiesCreated} 个空身份档案 ({string.Join(", ", summary.ImportedIdentityNames)})，请在身份管理器中补全私钥或口令。";
            }
            await ShowNotificationAsync("SecureCRT 导入完成", msg);
        }
    });

    [RelayCommand]
    private Task ImportKonsoleAsync() => Safe.RunAsync(_logger, "导入 Konsole 配色方案", async () =>
    {
        if (PickKonsoleFileDialogAsync == null || OpenTerminalProfileEditDialogAsync == null) return;
        var filePath = await PickKonsoleFileDialogAsync();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        var content = await File.ReadAllTextAsync(filePath);
        var defaultName = Path.GetFileNameWithoutExtension(filePath);
        var parsed = Kei.Term.Core.Services.KonsoleColorSchemeParser.Parse(content, defaultName);

        // 独立导入使用已应用的全局字体设置；配色不读取也不写回字体
        var confirmed = await OpenTerminalProfileEditDialogAsync(parsed, BuildAppliedFontSnapshot(_settingsService.Current));
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
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(
                    Strings.Get("TerminalProfileEdit.Title"),
                    Strings.Get("Settings.Appearance.BundleImportSuccess"));
            }
        }
        catch (Exception ex)
        {
            // 落盘失败：撤销内存草稿并给出可见提示，不假装成功
            profileManager.RemoveCustomTerminalProfile(confirmed.Id);
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(Strings.Get("TerminalProfileEdit.Title"), ex.Message);
            }
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

        await OpenResolvedAsync(resolved, useIdentity: false, preloaded);
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

    // 显示/隐藏左侧连接管理器面板
    [RelayCommand]
    private void ToggleSessionManager()
    {
        IsSessionManagerVisible = !IsSessionManagerVisible;
        if (_settingsService.Current.SessionManagerVisibilityMode == PanelVisibilityMode.RememberLastState)
        {
            _settingsService.Current.LastSessionManagerVisible = IsSessionManagerVisible;
            _ = _settingsService.SaveSettingsAsync(_settingsService.Current);
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
        await OpenResolvedAsync(resolved, useIdentity: true, preloaded: null);
    });

    // 弹统一认证窗（完整模式）；identity 提供保管库密钥下拉项
    private async Task<AuthPromptResult?> PromptAuthAsync(
        string prefillUsername,
        Identity? identity,
        AuthPromptMethod? defaultMethod = null)
    {
        if (AuthPromptDialogAsync == null)
        {
            return null;
        }

        var result = await AuthPromptDialogAsync(prefillUsername, BuildVaultKeyOptions(identity), defaultMethod);
        _logger.LogInformation(
            "认证窗结果 方法={Method} 用户名={Username}",
            result?.Method.ToString() ?? "取消",
            result?.Username ?? "(空)");
        return result;
    }

    // 当前身份中已配置的 Vault 私钥方法 → 认证窗「保管库密钥」下拉项
    private static IReadOnlyList<VaultKeyOption> BuildVaultKeyOptions(Identity? identity)
    {
        if (identity == null)
        {
            return [];
        }

        var options = new List<VaultKeyOption>();
        var index = 0;
        foreach (var method in identity.Methods.OfType<VaultPrivateKeyMethod>())
        {
            index++;
            options.Add(new VaultKeyOption(method.Id, string.Format(Strings.Get("Status.Vault.KeyOptionFormat"), identity.Name, index)));
        }

        return options;
    }

    // 认证窗结果 → 认证材料；Interactive 返回零材料（交由 KI 桥应答）
    private async Task<(MaterializedAuthMethod? Material, string? Username)> ResolvePromptResultAsync(
        AuthPromptResult result,
        Identity? identity)
    {
        switch (result.Method)
        {
            case AuthPromptMethod.Password:
                return (
                    new MaterializedAuthMethod(AuthMaterialKind.Password, new SecretPayload { Password = result.Password ?? string.Empty }),
                    result.Username);

            case AuthPromptMethod.PublicKeyFile:
            {
                if (string.IsNullOrWhiteSpace(result.KeyFilePath))
                {
                    return (null, result.Username);
                }

                var file = await PrivateKeyImport.ReadAsync(result.KeyFilePath);
                if (file == null)
                {
                    return (null, result.Username);
                }

                return (
                    new MaterializedAuthMethod(
                        AuthMaterialKind.PrivateKey,
                        new SecretPayload { PrivateKeyContent = file.Content, Passphrase = result.Passphrase }),
                    result.Username);
            }

            case AuthPromptMethod.PublicKeyVault:
            {
                if (identity == null || result.VaultMethodId == null)
                {
                    return (null, result.Username);
                }

                var secrets = await LoadIdentitySecretsAsync(identity.Id);
                if (!secrets.TryGetValue(result.VaultMethodId.Value.ToString(), out var payload)
                    || string.IsNullOrEmpty(payload.PrivateKeyContent))
                {
                    return (null, result.Username);
                }

                return (new MaterializedAuthMethod(AuthMaterialKind.PrivateKey, payload), result.Username);
            }

            case AuthPromptMethod.Interactive:
            default:
                // 交互式：零材料，连接时由 KI 桥逐条问答
                return (null, result.Username);
        }
    }

    // 解析认证主体：会话/全局解析出的 IdentityId → 身份库；找不到返回 null
    private async Task<Identity?> ResolveIdentityAsync(ResolvedSessionConfig resolved)
    {
        var identityId = resolved.IdentityId ?? _settingsService.Current.DefaultIdentityId;
        if (identityId == null)
        {
            _logger.LogInformation("认证主体解析=无身份（未绑定且无全局默认）");
            return null;
        }

        var fromSession = resolved.IdentityId != null;
        var identity = await _identityRepo.GetByIdAsync(identityId.Value);
        _logger.LogInformation(
            "认证主体解析 来源={Source} 身份Id={IdentityId} 命中={Hit}",
            fromSession ? "会话绑定" : "全局默认",
            identityId,
            identity != null);
        return identity;
    }

    // 统一认证管线：解析主体 → 构建计划 → 物化（UI 弹窗）→ 建标签 → 后台连接
    private async Task OpenResolvedAsync(
        ResolvedSessionConfig resolved,
        bool useIdentity,
        MaterializedAuthMethod? preloaded)
    {
        var settings = _settingsService.Current;

        // 1. 认证主体：会话绑定 → 全局默认 → 无
        var identity = useIdentity ? await ResolveIdentityAsync(resolved) : null;

        // 2. 认证尝试序列（显式方法 → Agent 兜底 → 单次弹窗兜底）
        var steps = AuthPlanBuilder.Plan(identity?.Methods, settings.PreferSystemAgent, identity?.Username);
        _logger.LogInformation(
            "认证计划 host={Host}:{Port} 会话={Session} 身份={Identity} 步骤={Steps}",
            resolved.Host,
            resolved.Port,
            resolved.SessionName,
            identity?.Name ?? "(无)",
            string.Join(" -> ", steps.Select(DescribeAuthStep)));

        // 3. 方法物化：取 Vault 材料/读文件/问口令/交互弹窗（均在本 UI 线程上下文完成）
        var materialized = new List<MaterializedAuthMethod>();
        if (preloaded != null)
        {
            materialized.Add(preloaded);
        }
        materialized.AddRange(await MaterializePlanAsync(steps, identity, resolved));
        _logger.LogInformation(
            "认证材料物化完成 材料数={Count} 类型={Kinds}",
            materialized.Count,
            string.Join(",", materialized.Select(m => m.Kind)));

        // 4. 无任何可用材料 → 主动弹统一认证窗兜底；取消则不建标签直接中止。
        //    选择「交互式」时材料为空，仍继续连接（由 KI 桥逐条问答）。
        if (materialized.Count == 0)
        {
            var fallback = await PromptAuthAsync(identity?.Username ?? resolved.Username, identity);
            if (fallback == null)
            {
                return;
            }

            var (fallbackMaterial, fallbackUsername) = await ResolvePromptResultAsync(fallback, identity);
            if (!string.IsNullOrWhiteSpace(fallbackUsername))
            {
                resolved = resolved with { Username = fallbackUsername.Trim() };
            }
            if (fallbackMaterial != null)
            {
                materialized.Add(fallbackMaterial);
            }
        }

        // 5. 跳板链：逐跳解析配置并物化认证（每一跳都是普通会话，复用其身份）
        IReadOnlyList<SshHop> jumpHops;
        try
        {
            jumpHops = await MaterializeJumpHopsAsync(resolved);
        }
        catch (JumpChainException ex)
        {
            _logger.LogWarning("跳板链解析失败 会话={Session} 原因={Reason}", resolved.SessionName, ex.Message);
            if (ShowNotificationAsync != null)
            {
                await ShowNotificationAsync(resolved.SessionName, ex.Message);
            }
            return;
        }

        // 6. 先建标签（Connecting），后台线程完成创建与连接
        //    有效配色：会话显式 ID → 全局默认 → 内置默认，始终走同一适配路径
        var effectiveProfile = ResolveEffectiveTerminalProfile(resolved.TerminalProfileId);
        var tab = new TerminalTabViewModel(
            resolved.SessionName,
            BuildAppliedFontSnapshot(settings),
            effectiveProfile,
            resolved.TerminalProfileId,
            _logger);
        // 构造只记录状态，此处显式注入配色（新标签立即生效）
        tab.ApplyTerminalProfile(effectiveProfile);
        Tabs.Add(tab);
        tab.CloseRequested += OnTabCloseRequested;
        SelectedTab = tab;

        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.ConnectTimeoutSeconds));
        await ConnectWithRetryAsync(tab, resolved, materialized, identity, timeout, jumpHops);
    }

    // 跳板逐跳物化：取不到任何材料时弹认证窗（预填该跳用户名）；用户取消则中止整条连接
    private async Task<IReadOnlyList<SshHop>> MaterializeJumpHopsAsync(ResolvedSessionConfig resolved)
    {
        if (resolved.JumpHostSessionId == null)
        {
            return [];
        }

        var target = new SessionNode
        {
            Id = resolved.SessionId,
            Name = resolved.SessionName,
            JumpHostSessionId = resolved.JumpHostSessionId
        };
        Dictionary<Guid, SessionNode> sessions = _allNodesCache.OfType<SessionNode>().ToDictionary(n => n.Id);
        IReadOnlyList<SessionNode> chain = JumpChainResolver.Resolve(
            target,
            id => sessions.TryGetValue(id, out SessionNode? node) ? node : null);

        var hops = new List<SshHop>();
        foreach (SessionNode jumpNode in chain)
        {
            ResolvedSessionConfig hopConfig = SessionConfigBuilder.Build(jumpNode, _settingsService.Current);
            Identity? hopIdentity = await ResolveIdentityAsync(hopConfig);
            IReadOnlyList<AuthStep> steps = AuthPlanBuilder.Plan(
                hopIdentity?.Methods,
                _settingsService.Current.PreferSystemAgent,
                hopIdentity?.Username);
            List<MaterializedAuthMethod> hopMaterials = await MaterializePlanAsync(steps, hopIdentity, hopConfig);

            if (hopMaterials.Count == 0)
            {
                AuthPromptResult? prompt = await PromptAuthAsync(hopIdentity?.Username ?? hopConfig.Username, hopIdentity);
                if (prompt == null)
                {
                    throw new JumpChainException($"已取消跳板机认证: {jumpNode.Name}");
                }

                (MaterializedAuthMethod? material, string? username) = await ResolvePromptResultAsync(prompt, hopIdentity);
                if (!string.IsNullOrWhiteSpace(username))
                {
                    hopConfig = hopConfig with { Username = username.Trim() };
                }
                if (material != null)
                {
                    hopMaterials.Add(material);
                }
            }

            _logger.LogInformation(
                "跳板物化完成 跳板={Jump} host={Host}:{Port} 材料数={Count}",
                jumpNode.Name,
                hopConfig.Host,
                hopConfig.Port,
                hopMaterials.Count);
            hops.Add(new SshHop(hopConfig, hopMaterials));
        }

        return hops;
    }

    private SshConnectOptions BuildConnectOptions(TimeSpan timeout, IReadOnlyList<SshHop> jumpHops)
    {
        AppSettings settings = _settingsService.Current;
        return new SshConnectOptions
        {
            ConnectTimeout = timeout,
            KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(0, settings.KeepAliveIntervalSeconds)),
            InteractivePrompt = BuildInteractivePrompt(),
            HostKeyVerifier = _hostKeyTrust,
            AgentSocketPath = settings.CustomAgentSocketPath,
            JumpHosts = jumpHops
        };
    }

    // 认证计划步骤的可读描述（仅类型，不含任何材料值）
    private static string DescribeAuthStep(AuthStep step) => step switch
    {
        MethodStep methodStep => $"Method:{methodStep.Method.GetType().Name}",
        AgentFallbackStep => "AgentFallback",
        SingleUsePromptStep => "SingleUsePrompt",
        _ => step.GetType().Name,
    };

    // 逐步物化认证计划；SingleUsePromptStep 不在此预先打扰用户，留作失败回弹
    private async Task<List<MaterializedAuthMethod>> MaterializePlanAsync(
        IReadOnlyList<AuthStep> steps,
        Identity? identity,
        ResolvedSessionConfig resolved)
    {
        var results = new List<MaterializedAuthMethod>();

        // identity_secrets 整包材料缓存（按 methodId 字符串键控）
        Dictionary<string, SecretPayload>? secrets = null;

        async Task EnsureSecretsAsync()
        {
            if (identity == null)
            {
                return;
            }

            secrets ??= await LoadIdentitySecretsAsync(identity.Id);
        }

        var context = new AuthMaterializerContext
        {
            ReadPrivateKeyFileAsync = ReadPrivateKeyFileAsync,
            Logger = _logger,
            GetSessionPassphrase = methodId =>
                _sessionPassphrases.TryGetValue(methodId, out var cached) ? cached : null,
            GetVaultSecret = methodId =>
                secrets != null && secrets.TryGetValue(methodId.ToString(), out var payload) ? payload : null,
            PromptPassphraseAsync = PromptPassphraseAsync,
            SaveVaultSecretAsync = async (methodId, payload) =>
            {
                if (identity == null)
                {
                    return;
                }

                await PersistVaultSecretAsync(identity.Id, methodId, payload);
                // 同步内存副本，后续同一连接内读取命中
                secrets ??= new Dictionary<string, SecretPayload>();
                secrets[methodId.ToString()] = payload;
            },
            PromptInteractiveAsync = async (method, username, ct) =>
            {
                // Interactive 方法：以交互式为默认项弹完整认证窗，取消则跳过该方法
                var prompt = await PromptAuthAsync(username, identity, AuthPromptMethod.Interactive);
                return prompt == null
                    ? null
                    : new SecretPayload { Password = prompt.Password ?? string.Empty };
            },
        };

        foreach (var step in steps)
        {
            switch (step)
            {
                case MethodStep methodStep:
                    await EnsureSecretsAsync();
                    var material = await AuthMaterializer.MaterializeAsync(methodStep.Method, resolved.Username, context);
                    if (material != null)
                    {
                        results.Add(material);
                    }
                    break;

                case AgentFallbackStep:
                    // 无身份且全局允许时，注入 Agent 全量身份尝试
                    results.Add(new MaterializedAuthMethod(AuthMaterialKind.Agent, null));
                    break;

                case SingleUsePromptStep:
                    // 失败回弹兜底，连接阶段处理
                    break;
            }
        }

        return results;
    }

    // 读取身份整包材料；Vault 加密且锁定时先懒解锁，取消则返回空（方法顺延跳过）
    private async Task<Dictionary<string, SecretPayload>> LoadIdentitySecretsAsync(Guid identityId)
    {
        if (!_vault.IsPlainMode && !_vault.IsUnlocked && !await EnsureVaultUnlockedAsync())
        {
            return new Dictionary<string, SecretPayload>();
        }

        try
        {
            var secrets = await _vaultSecretStore.GetSecretsAsync(identityId);
            _lastVaultAccessUtc = DateTime.UtcNow;
            return secrets;
        }
        catch
        {
            return new Dictionary<string, SecretPayload>();
        }
    }

    // 主密码懒解锁：循环重试直到成功/取消
    private async Task<bool> EnsureVaultUnlockedAsync()
    {
        if (_vault.IsPlainMode || _vault.IsUnlocked)
        {
            return true;
        }

        if (MasterPasswordDialogAsync == null)
        {
            return false;
        }

        var error = Strings.Get("Status.Vault.UnlockPrompt");
        _logger.LogInformation("保管库已锁定，弹出主密码框等待解锁");
        while (true)
        {
            var password = await MasterPasswordDialogAsync(error);
            if (password == null)
            {
                _logger.LogInformation("主密码框取消，保管库保持锁定");
                return false;
            }

            try
            {
                await _vault.UnlockAsync(password, false);
                _lastVaultAccessUtc = DateTime.UtcNow;
                _vaultUnlockFailures = 0;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                _vaultUnlockFailures++;
                _logger.LogWarning("Vault 解锁失败：主密码错误 连续失败={Failures} 次", _vaultUnlockFailures);
                error = Strings.Get("Status.Vault.PasswordIncorrect");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vault 解锁异常");
                error = ex.Message;
            }
        }
    }

    // 写入 Vault 前确保可用：明文模式（未初始化或已选明文）直通，加密模式需先解锁
    private async Task<bool> EnsureVaultReadyForWriteAsync()
    {
        if (_vault.IsPlainMode)
        {
            // 零摩擦：不弹初始化框，直接明文写入（明文警示由身份管理器横幅承担）
            _logger.LogInformation("保管库为明文模式，凭据材料将明文写入本地数据库");
            return true;
        }

        return await EnsureVaultUnlockedAsync();
    }

    // 读取私钥文件并探测是否需要口令（不引入 SSH 依赖，按文件头特征判断）
    private static Task<FileKeyReadResult?> ReadPrivateKeyFileAsync(string path, CancellationToken ct)
    {
        return Task.Run<FileKeyReadResult?>(async () =>
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var content = await File.ReadAllTextAsync(path, ct);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return null;
                }

                return new FileKeyReadResult(content, DetectEncryptedPrivateKey(content));
            }
            catch
            {
                // 不存在/不可读 → 跳过该方法
                return null;
            }
        }, ct);
    }

    private static bool DetectEncryptedPrivateKey(string content)
    {
        // PKCS#8 加密私钥
        if (content.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
        {
            return true;
        }

        // 传统 OpenSSH/PEM 加密头
        if (content.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal)
            || content.Contains("DEK-Info:", StringComparison.Ordinal))
        {
            return true;
        }

        // OpenSSH 新格式（openssh-key-v1）加密私钥的 KDF 名固定为 bcrypt
        return content.Contains("bcrypt", StringComparison.OrdinalIgnoreCase);
    }

    // 口令弹窗（三态）；SessionOnly 勾选记住时写入本次运行内存缓存
    private async Task<PassphrasePromptResult?> PromptPassphraseAsync(FilePrivateKeyMethod method, CancellationToken ct)
    {
        if (PassphrasePromptDialogAsync == null)
        {
            return null;
        }

        _logger.LogInformation("口令框打开 方法Id={MethodId} 模式={Mode}", method.Id, method.PassphraseMode);
        var result = await PassphrasePromptDialogAsync(method);
        if (result == null)
        {
            _logger.LogInformation("口令框取消 方法Id={MethodId}", method.Id);
            return null;
        }

        _logger.LogInformation("口令框确认 方法Id={MethodId} 本次运行记住={Remember}", method.Id, result.Remember);
        if (method.PassphraseMode == PassphrasePersistence.SessionOnly && result.Remember)
        {
            _sessionPassphrases[method.Id] = result.Passphrase;
        }

        return result;
    }

    // 把单个方法的材料并入身份整包写回 Vault
    private async Task PersistVaultSecretAsync(Guid identityId, Guid methodId, SecretPayload payload)
    {
        if (!await EnsureVaultReadyForWriteAsync())
        {
            // 用户取消初始化/解锁：不持久化，材料仍在本次内存
            _logger.LogInformation("Vault 材料未持久化（未解锁/取消） 方法Id={MethodId}", methodId);
            return;
        }

        var secrets = await _vaultSecretStore.GetSecretsAsync(identityId);
        secrets[methodId.ToString()] = payload;
        await _vaultSecretStore.SaveSecretsAsync(identityId, secrets);
        _lastVaultAccessUtc = DateTime.UtcNow;
        // 仅记录有无与字节数，绝不记录材料内容
        _logger.LogInformation(
            "Vault 材料持久化 身份Id={IdentityId} 方法Id={MethodId} 含密码={HasPassword} 私钥字节={KeyBytes} 含口令={HasPassphrase}",
            identityId,
            methodId,
            !string.IsNullOrEmpty(payload.Password),
            payload.PrivateKeyContent == null ? 0 : PrivateKeyImport.ByteCount(payload.PrivateKeyContent),
            !string.IsNullOrEmpty(payload.Passphrase));
    }

    // 读取指定 Vault 私钥方法的已存材料信息（字节数 + 指纹）；无材料返回 null。供身份编辑器回显。
    public async Task<VaultKeyInfo?> GetVaultKeyInfoAsync(Guid identityId, Guid methodId)
    {
        var secrets = await LoadIdentitySecretsAsync(identityId);
        if (!secrets.TryGetValue(methodId.ToString(), out var payload)
            || string.IsNullOrEmpty(payload.PrivateKeyContent))
        {
            return null;
        }

        return new VaultKeyInfo(
            PrivateKeyImport.ByteCount(payload.PrivateKeyContent),
            SshKeyFingerprint.Compute(payload.PrivateKeyContent, payload.Passphrase));
    }

    // 身份落库后统一写入/删除其 Vault 私钥材料（编辑器「应用」时暂存的导入结果）；
    // 返回 false 表示保管库未解锁（用户取消），调用方据此保持错误提示
    public async Task<bool> PersistVaultKeyImportsAsync(Guid identityId, IReadOnlyList<VaultKeyImport> imports)
    {
        if (imports.Count == 0)
        {
            return true;
        }

        if (!await EnsureVaultReadyForWriteAsync())
        {
            // 加密保管库未解锁：不持久化
            return false;
        }

        var secrets = await _vaultSecretStore.GetSecretsAsync(identityId);
        foreach (var import in imports)
        {
            var key = import.MethodId.ToString();
            if (import.Remove)
            {
                secrets.Remove(key);
                _logger.LogInformation("Vault 私钥材料移除 身份Id={IdentityId} 方法Id={MethodId}", identityId, import.MethodId);
                continue;
            }

            if (!string.IsNullOrEmpty(import.PrivateKeyContent))
            {
                secrets[key] = new SecretPayload
                {
                    PrivateKeyContent = import.PrivateKeyContent,
                    Passphrase = import.Passphrase,
                };
                // 记录字节数与指纹有无，不记录私钥内容
                var hasFingerprint = SshKeyFingerprint.Compute(import.PrivateKeyContent, import.Passphrase) != null;
                _logger.LogInformation(
                    "Vault 私钥材料写入 身份Id={IdentityId} 方法Id={MethodId} 字节={Bytes} 指纹={HasFingerprint}",
                    identityId,
                    import.MethodId,
                    PrivateKeyImport.ByteCount(import.PrivateKeyContent),
                    hasFingerprint);
            }
        }

        await _vaultSecretStore.SaveSecretsAsync(identityId, secrets);
        _lastVaultAccessUtc = DateTime.UtcNow;
        return true;
    }

    // SSH 连接：后台线程执行，认证失败经 Dispatcher 回弹统一认证窗重试（上限 3 次）
    private async Task ConnectWithRetryAsync(
        TerminalTabViewModel tab,
        ResolvedSessionConfig resolved,
        List<MaterializedAuthMethod> materialized,
        Identity? identity,
        TimeSpan timeout,
        IReadOnlyList<SshHop> jumpHops)
    {
        var promptCount = 0;
        var hostKeyConfirmations = 0;
        var current = new List<MaterializedAuthMethod>(materialized);

        while (true)
        {
            if (tab.IsDisposed)
            {
                return;
            }

            ISshSession? session = null;
            Exception? failure = null;

            _logger.LogInformation(
                "创建 SSH 会话 host={Host}:{Port} 会话={Session} 材料数={Count}",
                resolved.Host,
                resolved.Port,
                resolved.SessionName,
                current.Count);

            try
            {
                // 连接流程整体运行于后台线程，避免阻塞 UI
                SshConnectOptions options = BuildConnectOptions(timeout, jumpHops);
                session = await Task.Run(() =>
                    _sshFactory.CreateSessionAsync(resolved, current, options, CancellationToken.None));
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (session != null)
            {
                if (tab.IsDisposed)
                {
                    await session.DisposeAsync();
                    return;
                }

                // 端点挂载必须在 UI 线程完成
                tab.AttachSession(session);

                await Task.Run(async () =>
                {
                    try
                    {
                        await session.ConnectAsync();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });
            }

            if (failure == null)
            {
                _logger.LogInformation("SSH 会话连接成功 host={Host}:{Port}", resolved.Host, resolved.Port);
                tab.MarkConnected();

                // 异步预准备并挂载文件管理器：必须用最终认证成功的材料（重试后的 current），
                // 而非首轮材料，否则重试输入的新密码不会用于文件通道
                IReadOnlyList<MaterializedAuthMethod> successfulMaterials = current.ToList();
                ResolvedSessionConfig successfulConfig = resolved;
                SshConnectOptions fileOptions = BuildConnectOptions(timeout, jumpHops);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var fs = await _sshFactory.CreateFileSystemAsync(successfulConfig, successfulMaterials, session, fileOptions);
                        var trackerLogger = _loggerFactory?.CreateLogger<LocalFileTracker>() ?? (_logger as ILogger);
                        var tracker = new LocalFileTracker(
                            cacheBaseDirectory: _settingsService.Current.FileTransfer.CacheDirectory,
                            mode: _settingsService.Current.FileTransfer.WatcherMode,
                            pollingIntervalSeconds: _settingsService.Current.FileTransfer.PollingIntervalSeconds,
                            writeDebounceMilliseconds: _settingsService.Current.FileTransfer.WriteDebounceMilliseconds,
                            logger: trackerLogger);
                        var rfmLogger = _loggerFactory?.CreateLogger<RemoteFileManagerViewModel>() ?? (_logger as ILogger);
                        var launcherLogger = _loggerFactory?.CreateLogger<FileEditorLauncher>() ?? (_logger as ILogger);
                        await tab.InitializeFileManagerAsync(fs, tracker, _settingsService, _editorRepo, launcherLogger ?? rfmLogger);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "初始化远程文件系统侧栏失败");
                    }
                });

                return;
            }

            _logger.LogError(
                failure,
                "SSH 会话创建或连接失败 host={Host}:{Port} 会话={Session}",
                resolved.Host,
                resolved.Port,
                resolved.SessionName);

            await tab.DetachSessionAsync();

            // 主机密钥需人工确认：在握手之外弹窗（不受连接超时约束），放行后直接重连
            if (failure is HostKeyRejectedException { Outcome.RequiresConfirmation: true } hostKeyFailure
                && _hostKeyTrust is { CanConfirm: true }
                && hostKeyConfirmations < MaxHostKeyConfirmations)
            {
                hostKeyConfirmations++;
                if (await _hostKeyTrust.ConfirmAsync(hostKeyFailure.Outcome.Evaluation))
                {
                    _logger.LogInformation("主机密钥已人工确认，重新连接 host={Host}:{Port}", resolved.Host, resolved.Port);
                    continue;
                }
            }

            // 弹窗次数达上限或非认证类失败：终端显示失败原因
            if (promptCount >= MaxAuthRetries || !IsAuthenticationFailure(failure))
            {
                _logger.LogError(
                    "认证失败终止 host={Host}:{Port} 重试次数={Retries} 原因={Reason}",
                    resolved.Host,
                    resolved.Port,
                    promptCount,
                    DescribeFailure(failure));
                tab.ReportError(DescribeFailure(failure));
                return;
            }

            promptCount++;
            _logger.LogInformation("认证失败，第 {Attempt} 次弹出认证重试 host={Host}:{Port}", promptCount, resolved.Host, resolved.Port);
            var prompt = await PromptAuthAsync(resolved.Username, identity);
            if (prompt == null)
            {
                _logger.LogInformation("用户取消认证重试，连接中止 host={Host}:{Port}", resolved.Host, resolved.Port);
                tab.ReportError(DescribeFailure(failure));
                return;
            }

            var (newMaterial, newUsername) = await ResolvePromptResultAsync(prompt, identity);
            if (!string.IsNullOrWhiteSpace(newUsername))
            {
                resolved = resolved with { Username = newUsername.Trim() };
            }

            // 按用户新选的方法重建材料：交互式 = 零材料重连（靠 KI 桥应答）
            switch (prompt.Method)
            {
                case AuthPromptMethod.Password:
                    current.RemoveAll(m => m.Kind == AuthMaterialKind.Password);
                    break;
                case AuthPromptMethod.PublicKeyFile:
                case AuthPromptMethod.PublicKeyVault:
                    current.RemoveAll(m => m.Kind == AuthMaterialKind.PrivateKey);
                    break;
                case AuthPromptMethod.Interactive:
                default:
                    current.Clear();
                    break;
            }

            if (newMaterial != null)
            {
                current.Add(newMaterial);
            }
        }
    }

    // keyboard-interactive 真交互回调：从 SSH 后台线程经 Dispatcher 弹提示窗输入（2FA 可用）
    private Func<string, Task<string?>> BuildInteractivePrompt()
    {
        return prompt =>
        {
            // 提示文本来自服务器，可记录；应答内容可能含密码/OTP，绝不记录
            _logger.LogInformation("KI 认证提示弹出 提示={Prompt}", prompt);
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var response = InteractiveInputDialogAsync == null
                        ? null
                        : await InteractiveInputDialogAsync(prompt);
                    _logger.LogInformation("KI 认证提示应答 已应答={Answered}", !string.IsNullOrEmpty(response));
                    tcs.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "KI 认证提示处理异常");
                    tcs.TrySetResult(null);
                }
            });
            return tcs.Task;
        };
    }

    private static bool IsAuthenticationFailure(Exception ex)
        => ex.GetType().Name.Contains("Authentication", StringComparison.Ordinal);

    private static string DescribeFailure(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? Strings.Get("Status.Auth.Failed") : ex.Message;

    /// <summary>
    /// 重排标签顺序，保持当前选中标签不变
    /// </summary>
    public void MoveTab(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Tabs.Count || toIndex < 0 || toIndex >= Tabs.Count)
        {
            return;
        }

        if (fromIndex == toIndex)
        {
            return;
        }

        var selected = SelectedTab;
        Tabs.Move(fromIndex, toIndex);
        if (selected != null)
        {
            SelectedTab = selected;
        }
    }

    private void OnTabCloseRequested(TerminalTabViewModel tab) => _ = CloseTabCommand.ExecuteAsync(tab);

    [RelayCommand]
    private Task CloseTabAsync(TerminalTabViewModel? tab) => Safe.RunAsync(_logger, "关闭标签", () =>
    {
        if (tab == null)
        {
            return Task.CompletedTask;
        }

        tab.CloseRequested -= OnTabCloseRequested;
        var isCurrentSelected = SelectedTab == tab;
        var tabIndex = Tabs.IndexOf(tab);

        // 先从集合中移除并立即更新选中态，确保 UI 响应无阻塞
        Tabs.Remove(tab);

        if (isCurrentSelected)
        {
            // 优先切到同位置或前一个标签，若均无则切至末尾或 null
            if (Tabs.Count > 0)
            {
                var nextIndex = Math.Clamp(tabIndex - 1, 0, Tabs.Count - 1);
                SelectedTab = Tabs[nextIndex];
            }
            else
            {
                SelectedTab = null;
            }
        }

        // 后台异步清理底层会话与网络资源，避免任何网络读取阻塞导致 UI 卡顿
        _ = Task.Run(async () =>
        {
            try
            {
                await tab.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "后台释放终端标签异常 标题={Title}", tab.Title);
            }
        });

        return Task.CompletedTask;
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
