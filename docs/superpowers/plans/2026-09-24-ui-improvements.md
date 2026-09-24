# KeiTerm UI 改进实现计划（菜单栏 / 连接管理器 / 本地化 / 主题切换）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 恢复菜单栏、修复连接管理器（虚拟根 Sessions + 落点修复 + 拖拽）、全量本地化到 resx、应用内切换 Semi/Material 主题。

**Architecture:** Core 层新增纯逻辑（VirtualRootNode 模型、TreePlacement 落点、TreeDropResolver 拖拽解析）并单测；App 层接入 VM 与 MainWindow（菜单栏、DragDrop、About）；本地化经 resx + MarkupExtension；主题切换用"互斥激活 Application.Styles 主题实例"。

**Tech Stack:** .NET 10 / Avalonia 12.1.2 / CommunityToolkit.Mvvm 8.4.2 / xUnit；Phase 3 引入 Semi.Avalonia 12.1.0.1、Material.Avalonia 3.20.0。

**Spec:** `docs/superpowers/specs/2026-09-24-ui-improvements-design.md`

## Global Constraints

- 构建：`dotnet build Kei.Term.slnx`；测试：`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
- `Kei.Term.Core` 禁止引入 Avalonia/平台依赖（Core 新文件只用 BCL）
- SQLite 模型不动；`VirtualRootNode` 永不持久化（`Id == Guid.Empty` 守卫所有仓储写入/删除/移动入口）
- 虚拟根显示名固定 `Sessions`（英文，不参与本地化）
- 本地化中性语言 = 现有中文；仅换字符串，不动布局/交互
- 用户未要求 commit：**全计划无 commit 步骤**；交互体验由用户本人运行评估，不代运行
- 注释风格：行内注释优先，少用行后注释

---

### Task 1: Core — VirtualRootNode + TreePlacement + TreeDropResolver（含单测）

**Files:**
- Modify: `src/Kei.Term.Core/Models/TreeNodes.cs`
- Create: `src/Kei.Term.Core/Services/TreePlacement.cs`
- Create: `src/Kei.Term.Core/Services/TreeDropResolver.cs`
- Test: `tests/Kei.Term.Tests/TreePlacementTests.cs`（新建）
- Test: `tests/Kei.Term.Tests/TreeDropResolverTests.cs`（新建）

**Interfaces:**
- Consumes: 现有 `TreeNodeBase / FolderNode / SessionNode`（`src/Kei.Term.Core/Models/TreeNodes.cs`）
- Produces:
  - `NodeType.VirtualRoot = 2`（枚举新值，不落库）
  - `class VirtualRootNode : TreeNodeBase`（`Id = Guid.Empty`，`Name = "Sessions"`，含 `IsExpanded`、`Children`）
  - `static Guid? TreePlacement.ResolveCreationParent(TreeNodeBase? selected)`
  - `sealed record TreeDropResult(bool IsValid, Guid? NewParentId, bool IsNoOp)`
  - `static TreeDropResult TreeDropResolver.Resolve(IReadOnlyList<TreeNodeBase> allNodes, Guid draggedId, TreeNodeBase? target)`
  - Task 2/4 依赖以上签名，签名不得改动

- [ ] **Step 1: 写失败测试 `TreePlacementTests.cs`**

```csharp
using System;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class TreePlacementTests
{
    // 新建落点：文件夹 → 建在其内；会话 → 建在其父级；虚拟根/无选中 → 顶级(null)
    [Fact]
    public void ResolveCreationParent_FolderSelected_ReturnsFolderId()
    {
        var folder = new FolderNode { Id = Guid.NewGuid(), Name = "gzz" };
        Assert.Equal(folder.Id, TreePlacement.ResolveCreationParent(folder));
    }

    [Fact]
    public void ResolveCreationParent_SessionSelected_ReturnsItsParentId()
    {
        var parentId = Guid.NewGuid();
        var session = new SessionNode { Id = Guid.NewGuid(), ParentId = parentId, Name = "web" };
        Assert.Equal(parentId, TreePlacement.ResolveCreationParent(session));
    }

    [Fact]
    public void ResolveCreationParent_VirtualRootOrNone_ReturnsNull()
    {
        Assert.Null(TreePlacement.ResolveCreationParent(new VirtualRootNode()));
        Assert.Null(TreePlacement.ResolveCreationParent(null));
    }
}
```

- [ ] **Step 2: 写失败测试 `TreeDropResolverTests.cs`**

```csharp
using System;
using System.Collections.Generic;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Xunit;

namespace Kei.Term.Tests;

public class TreeDropResolverTests
{
    // root(Folder)
    //   ├─ prod(Folder)
    //   │    └─ web(Session)
    //   └─ local(Session)
    private static (FolderNode root, FolderNode prod, SessionNode web, SessionNode local) Build()
    {
        var root = new FolderNode { Id = Guid.NewGuid(), Name = "Root" };
        var prod = new FolderNode { Id = Guid.NewGuid(), ParentId = root.Id, Name = "Production" };
        var web = new SessionNode { Id = Guid.NewGuid(), ParentId = prod.Id, Name = "Web-1", Host = "10.0.0.1" };
        var local = new SessionNode { Id = Guid.NewGuid(), ParentId = root.Id, Name = "Local", Host = "127.0.0.1" };
        root.Children = [prod, local];
        prod.Children = [web];
        return (root, prod, web, local);
    }

    private static List<TreeNodeBase> Flatten(TreeNodeBase[] nodes) => [.. nodes];

    [Fact]
    public void Resolve_TargetFolder_ReturnsFolderId()
    {
        var (root, prod, web, local) = Build();
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), local.Id, prod);
        Assert.True(result.IsValid);
        Assert.Equal(prod.Id, result.NewParentId);
    }

    [Fact]
    public void Resolve_TargetSession_ReturnsSessionParentId()
    {
        var (root, prod, web, local) = Build();
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), local.Id, web);
        Assert.True(result.IsValid);
        Assert.Equal(prod.Id, result.NewParentId);
    }

    [Fact]
    public void Resolve_TargetVirtualRootOrBlank_ReturnsTopLevel()
    {
        var (root, prod, web, local) = Build();
        var all = Flatten([root, prod, web, local]);
        Assert.Null(TreeDropResolver.Resolve(all, local.Id, new VirtualRootNode()).NewParentId);
        Assert.Null(TreeDropResolver.Resolve(all, local.Id, null).NewParentId);
    }

    [Fact]
    public void Resolve_DropOntoSelf_IsInvalid()
    {
        var (root, prod, web, local) = Build();
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), prod.Id, prod);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Resolve_DropOntoOwnDescendant_IsInvalid()
    {
        var (root, prod, web, local) = Build();
        // prod 拖到自己的子孙 web 上 → 成环，非法
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), prod.Id, web);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Resolve_SameParentAsCurrent_IsNoOp()
    {
        var (root, prod, web, local) = Build();
        // local 本来就在 root 下，再拖到 root → 有效但为空操作
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), local.Id, root);
        Assert.True(result.IsValid);
        Assert.True(result.IsNoOp);
    }

    [Fact]
    public void Resolve_DraggedNodeMissing_IsInvalid()
    {
        var (root, prod, web, local) = Build();
        var result = TreeDropResolver.Resolve(Flatten([root, prod, web, local]), Guid.NewGuid(), prod);
        Assert.False(result.IsValid);
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~TreePlacement|FullyQualifiedName~TreeDropResolver"`
Expected: 编译失败（`VirtualRootNode` / `TreePlacement` / `TreeDropResolver` 不存在）

- [ ] **Step 4: 实现 `TreeNodes.cs` 追加**

在 `NodeType` 枚举加 `VirtualRoot = 2`（注明"仅展示层，不落库"），文件末尾（`SessionNode` 之后、`ResolvedSessionConfig` 之前）追加：

```csharp
// 展示层专用虚拟根（SecureCRT 式 "Sessions" 顶层）：不持久化，Id 固定 Empty 便于各入口守卫
public class VirtualRootNode : TreeNodeBase
{
    public VirtualRootNode()
    {
        Id = Guid.Empty;
        ParentId = null;
        Name = "Sessions";
    }

    public override NodeType NodeType => NodeType.VirtualRoot;
    public bool IsExpanded { get; set; } = true;
    public List<TreeNodeBase> Children { get; set; } = [];
}
```

- [ ] **Step 5: 实现 `TreePlacement.cs`**

```csharp
namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;

// 新建/粘贴落点判定：纯静态、可单测
public static class TreePlacement
{
    // 文件夹 → 建在其内；会话 → 建在其父级；虚拟根或无选中 → 顶级(null)
    public static Guid? ResolveCreationParent(TreeNodeBase? selected)
    {
        return selected switch
        {
            FolderNode folder => folder.Id,
            SessionNode session => session.ParentId,
            _ => null // VirtualRootNode / null / 未知类型
        };
    }
}
```

- [ ] **Step 6: 实现 `TreeDropResolver.cs`**

```csharp
namespace Kei.Term.Core.Services;

using Kei.Term.Core.Models;

// 拖拽落点解析结果
public sealed record TreeDropResult(bool IsValid, Guid? NewParentId, bool IsNoOp);

// 拖拽落点判定：纯静态、可单测（防环 + 落点映射）
public static class TreeDropResolver
{
    // target 为 null（空白处）或虚拟根 → 移到顶级；
    // 目标为会话 → 移到该会话同级；目标为文件夹 → 移入其下；
    // 目标为自身或自身子孙 → 非法（成环）；新父与现父相同 → 有效但 IsNoOp
    public static TreeDropResult Resolve(IReadOnlyList<TreeNodeBase> allNodes, Guid draggedId, TreeNodeBase? target)
    {
        var byId = new Dictionary<Guid, TreeNodeBase>(allNodes.Count);
        foreach (var node in allNodes)
        {
            byId[node.Id] = node;
        }

        if (!byId.TryGetValue(draggedId, out var dragged) || dragged is VirtualRootNode)
        {
            return new TreeDropResult(false, null, false);
        }

        // 落点映射
        Guid? newParentId = target switch
        {
            null => null,
            VirtualRootNode => null,
            FolderNode folder => folder.Id,
            SessionNode session => session.ParentId,
            _ => null
        };

        // 目标即自身 → 非法
        if (target != null && target.Id == draggedId)
        {
            return new TreeDropResult(false, null, false);
        }

        // 目标位于自身子孙内 → 非法（沿目标祖先链上溯，遇到 dragged 即成环）
        var cursor = target;
        while (cursor != null)
        {
            if (cursor.Id == draggedId)
            {
                return new TreeDropResult(false, null, false);
            }
            cursor = cursor.ParentId is { } pid ? byId.GetValueOrDefault(pid) : null;
        }

        var isNoOp = dragged.ParentId == newParentId;
        return new TreeDropResult(true, newParentId, isNoOp);
    }
}
```

注意：`target` 在树中但父链 `byId` 缺失时（悬空 ParentId）上溯自然终止，等效顶级，符合 BuildTree 现有"父不存在即根"的语义。

- [ ] **Step 7: 跑测试确认通过**

Run: `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~TreePlacement|FullyQualifiedName~TreeDropResolver"`
Expected: 全部 PASS；`dotnet build Kei.Term.slnx` 无错误

---

### Task 2: 主应用 — 虚拟根接入 + 命令守卫 + 右键空白落点修复

**Files:**
- Modify: `src/Kei.Term.App/ViewModels/MainViewModel.cs`（BuildTree 218-249、CreateSessionAsync 251-263、CreateFolderAsync 265-276、EditSelectedNodeAsync 278-304、DeleteSelectedNodeAsync 306-320、CanUseSelectedNode 389、RefreshTreeFromCache 211-216）
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml.cs`（新增 TreeView ContextMenuOpening 命中处理）
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml:180-183`（TreeView 挂 ContextMenuOpening 事件）

**Interfaces:**
- Consumes: Task 1 的 `VirtualRootNode`、`TreePlacement.ResolveCreationParent`
- Produces: `TreeNodes` 集合恒为单个虚拟根；`HasNodes` 语义 = 真实节点数 > 0；`SelectedTreeNode` 在右键空白时被置 null

- [ ] **Step 1: BuildTree 包虚拟根**

`MainViewModel.BuildTree` 末行 `TreeNodes = new ObservableCollection<TreeNodeBase>(roots);` 替换为：

```csharp
// 包装为 SecureCRT 式单一虚拟根 "Sessions"（展示层，永不持久化）
var virtualRoot = new VirtualRootNode();
virtualRoot.Children.AddRange(roots);
TreeNodes = new ObservableCollection<TreeNodeBase> { virtualRoot };
```

同文件 `RefreshTreeFromCache` 中 `HasNodes = TreeNodes.Count > 0;` 改为 `HasNodes = filtered.Count > 0;`（虚拟根恒存在，原判据失效）。

- [ ] **Step 2: 命令守卫与落点替换**

`CreateSessionAsync` 与 `CreateFolderAsync` 中：

```csharp
var parentId = SelectedTreeNode is FolderNode folder ? folder.Id : SelectedTreeNode?.ParentId;
```

统一替换为：

```csharp
var parentId = TreePlacement.ResolveCreationParent(SelectedTreeNode);
```

以下三处加虚拟根守卫（`VirtualRootNode` 不可编辑/删除/剪贴）：
- `EditSelectedNodeAsync` 与 `DeleteSelectedNodeAsync` 开头：`if (SelectedTreeNode == null || SelectedTreeNode is VirtualRootNode) return;`
- `CanUseSelectedNode()` 改为 `SelectedTreeNode is not null and not VirtualRootNode;`

粘贴落点 `PasteNodeAsync` 的 `SelectedTreeNode is FolderNode targetFolder ? targetFolder.Id : (Guid?)null` 保持不变（虚拟根自然落入 null=顶级）。

- [ ] **Step 3: 右键空白清除幽灵选中**

`MainWindow.axaml.cs` 新增方法（挂在 `WireDialogs` 或构造器中：`PropertyChanged` 之后注册均可，此处随构造器注册更稳）：

```csharp
// 右键空白处：清除幽灵选中，使"新建"落到顶级（修复目录误入选中文件夹的问题）
private void Tree_ContextOpening(object? sender, CancelEventArgs e)
{
    if (sender is not TreeView tree || DataContext is not MainViewModel vm)
    {
        return;
    }

    if (GetNodeAt(tree, e.GetPosition(tree) /* 见下：ContextMenuOpening 无事件参数坐标，改用鼠标绝对坐标 */))
    {
        return;
    }

    vm.SelectedTreeNode = null;
}
```

实现注记：Avalonia 的 `TreeView.ContextMenuOpening`（`ContextMenuOpeningEventArgs`）不直接给相对坐标，用 `ContextMenu` 打开前的 `PointerReleased`（右键）更直接。**采用此实现**：

```csharp
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

// 命中测试：坐标处向上找 TreeViewItem，取其 DataContext
private static TreeNodeBase? GetNodeAt(TreeView tree, Point point)
{
    var hit = tree.GetVisualAt(point);
    while (hit != null && hit is not TreeViewItem)
    {
        hit = hit.GetVisualParent();
    }
    return (hit as TreeViewItem)?.DataContext as TreeNodeBase;
}
```

需要的 using：`Avalonia.Input`（已有）、`System.ComponentModel`（已有）。构造器 `InitializeComponent();` 后加：

```csharp
// 在 InitializeComponent 后拿到 SessionTree（Task 2 先在 axaml 给 TreeView 加 x:Name="SessionTree"）
SessionTree.PointerReleased += Tree_PointerReleased;
```

- [ ] **Step 4: axaml 给 TreeView 命名**

`MainWindow.axaml:180` `<TreeView ItemsSource=...` 加 `x:Name="SessionTree"`。

- [ ] **Step 5: 构建验证**

Run: `dotnet build Kei.Term.slnx && dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
Expected: 构建与全部测试通过（含 Task 1 用例）

---

### Task 3: 主应用 — 菜单栏恢复 + 关于窗口

**Files:**
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml`（右内容区 Grid 行 251-403、KeyBindings 100-103）
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml.cs`（退出/关于处理 + About 弹窗）

**Interfaces:**
- Consumes: MainViewModel 现有命令（`CreateSessionCommand / CreateFolderCommand / QuickConnectCommand / ConnectSelectedSessionCommand / DisconnectCurrentTabCommand / CutNodeCommand / CopyNodeCommand / PasteNodeCommand / DeleteSelectedNodeCommand / OpenIdentityManagerCommand / OpenSettingsCommand / IsSessionManagerVisible / IsComposeBarVisible`）
- Produces: 右内容区行定义 `Auto,Auto,Auto,*,Auto`（菜单/工具栏/标签条/终端/撰写栏）；代码后置 `OnExitMenuClick` / `OnAboutClick`

- [ ] **Step 1: 菜单栏 axaml**

右内容区 `<Grid Grid.Column="2" RowDefinitions="Auto,Auto,*,Auto">` 改为 `RowDefinitions="Auto,Auto,Auto,*,Auto"`，并在其内最前插入（后续各行 Grid.Row 依次 +1：工具栏 0→1、标签条 1→2、终端 Border 与 ContentControl 2→3、撰写栏 3→4）：

```xml
<!-- Row0 菜单栏：精简真实菜单，全部绑定现有命令 -->
<MenuBar Grid.Row="0"
         Background="{StaticResource Kei.Bg.PanelAlt}"
         Foreground="{StaticResource Kei.Text.Primary}"
         MinHeight="30">
    <MenuItem Header="文件">
        <MenuItem Header="新建会话" Command="{Binding CreateSessionCommand}" InputGesture="Ctrl+N"/>
        <MenuItem Header="新建文件夹" Command="{Binding CreateFolderCommand}"/>
        <MenuItem Header="快速连接" Command="{Binding QuickConnectCommand}" InputGesture="Ctrl+Q"/>
        <Separator/>
        <MenuItem Header="连接" Command="{Binding ConnectSelectedSessionCommand}"/>
        <MenuItem Header="断开当前标签" Command="{Binding DisconnectCurrentTabCommand}"/>
        <Separator/>
        <MenuItem Header="退出" Click="OnExitMenuClick"/>
    </MenuItem>
    <MenuItem Header="编辑">
        <MenuItem Header="剪切" Command="{Binding CutNodeCommand}"/>
        <MenuItem Header="复制" Command="{Binding CopyNodeCommand}"/>
        <MenuItem Header="粘贴" Command="{Binding PasteNodeCommand}"/>
        <Separator/>
        <MenuItem Header="删除选中项" Command="{Binding DeleteSelectedNodeCommand}"/>
    </MenuItem>
    <MenuItem Header="查看">
        <MenuItem Header="连接管理器" IsCheckable="True" IsChecked="{Binding IsSessionManagerVisible, Mode=TwoWay}"/>
        <MenuItem Header="撰写栏" IsCheckable="True" IsChecked="{Binding IsComposeBarVisible, Mode=TwoWay}"/>
    </MenuItem>
    <MenuItem Header="工具">
        <MenuItem Header="身份与密钥管理" Command="{Binding OpenIdentityManagerCommand}"/>
        <MenuItem Header="偏好设置" Command="{Binding OpenSettingsCommand}"/>
    </MenuItem>
    <MenuItem Header="帮助">
        <MenuItem Header="关于 Kei.Term" Click="OnAboutClick"/>
    </MenuItem>
</MenuBar>
```

同时 `Window.KeyBindings`（100-103 行）追加 `<KeyBinding Gesture="Ctrl+N" Command="{Binding CreateSessionCommand}"/>`。

- [ ] **Step 2: 退出与关于（代码后置）**

`MainWindow.axaml.cs` 追加：

```csharp
// 菜单"退出"：走桌面生命周期正常关闭（触发 OnClosing 确认逻辑）
private void OnExitMenuClick(object? sender, EventArgs e)
{
    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.MainWindow?.Close();
    }
}

// 菜单"关于"：极简版本信息弹窗
private async void OnAboutClick(object? sender, EventArgs e)
{
    var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
    var about = new Window
    {
        Title = "关于 Kei.Term",
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
            new TextBlock { Text = "Kei.Term", FontSize = 18, FontWeight = FontWeight.SemiBold,
                Foreground = GetThemeBrush("Kei.Text.Primary") },
            new TextBlock { Text = $"版本 {version}", FontSize = 12,
                Foreground = GetThemeBrush("Kei.Text.Secondary") },
            new TextBlock { Text = "SSH 终端模拟器", FontSize = 12,
                Foreground = GetThemeBrush("Kei.Text.Muted") }
        }
    };
    await about.ShowDialog(this);
}
```

需要的 using：`Avalonia.Controls.ApplicationLifetimes`、`Avalonia.Layout`、`Avalonia.Media`（已有）。

- [ ] **Step 3: 构建验证**

Run: `dotnet build Kei.Term.slnx`
Expected: 无错误（axaml 编译期即校验命令名与事件签名）

---

### Task 4: 主应用 — 树拖拽（DragDrop → MoveNodeAsync）

**Files:**
- Modify: `src/Kei.Term.App/ViewModels/MainViewModel.cs`（新增 `MoveNodeToAsync`）
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml.cs`（PointerPressed/PointerMoved/DragOver/Drop 接线）
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml:180`（TreeView `AllowDrop="True"`）

**Interfaces:**
- Consumes: Task 1 的 `TreeDropResolver.Resolve`；`ITreeRepository.MoveNodeAsync(Guid nodeId, Guid? newParentId, int sortOrder, CancellationToken ct = default)`（`src/Kei.Term.Core/Storage/IRepositories.cs:12`）
- Produces: `public Task MoveNodeToAsync(Guid nodeId, Guid? newParentId)`（MainViewModel，供 Drop 调用）

- [ ] **Step 1: VM 增加 MoveNodeToAsync**

在 `CollapseAll`（478-490 行）附近追加：

```csharp
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
    await ReloadTreeAsync();
});
```

- [ ] **Step 2: 代码后置拖拽接线**

`MainWindow.axaml.cs`：字段 + 构造器注册 + 四个方法：

```csharp
// 拖拽进行中的节点 Id（PointerPressed 命中记录，移动超阈值后启动 DoDragDrop）
private TreeNodeBase? _dragNode;
private Point _dragStart;

// 构造器中（Task 2 的 SessionTree 注册之后）：
SessionTree.PointerPressed += Tree_PointerPressed;
SessionTree.PointerMoved += Tree_PointerMoved;
SessionTree.AddHandler(DragDrop.DragOverEvent, Tree_DragOver);
SessionTree.AddHandler(DragDrop.DropEvent, Tree_Drop);
```

```csharp
// 记录按下位置与命中的可拖节点（虚拟根不可拖）
private void Tree_PointerPressed(object? sender, PointerPressedEventArgs e)
{
    _dragNode = null;
    if (sender is TreeView tree
        && e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
    {
        _dragStart = e.GetPosition(tree);
        _dragNode = GetNodeAt(tree, _dragStart) is VirtualRootNode ? null : GetNodeAt(tree, _dragStart);
    }
}

// 按住左键移动超 4px → 启动拖拽（携带节点 Id 字符串）
private async void Tree_PointerMoved(object? sender, PointerEventArgs e)
{
    if (_dragNode == null
        || sender is not TreeView tree
        || !e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed
        || (e.GetPosition(tree) - _dragStart).ManhattanLength() < 4)
    {
        return;
    }

    var id = _dragNode.Id;
    _dragNode = null; // 启动后清空，避免重复触发
    var data = new DataObject();
    data.Set(DataFormats.Text, id.ToString());
    await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
}

// 悬停判定：合法则显示移动光标
private void Tree_DragOver(object? sender, DragEventArgs e)
{
    if (DataContext is not MainViewModel vm
        || !Guid.TryParse(e.Data?.GetText(), out var draggedId)
        || sender is not TreeView tree)
    {
        e.DragEffects = DragDropEffects.None;
        return;
    }

    var result = Core.Services.TreeDropResolver.Resolve(
        vm.DebugAllNodesCache, draggedId, GetNodeAt(tree, e.GetPosition(tree)));
    e.DragEffects = result.IsValid && !result.IsNoOp ? DragDropEffects.Move : DragDropEffects.None;
}

// 落下：解析并执行移动
private void Tree_Drop(object? sender, DragEventArgs e)
{
    if (DataContext is not MainViewModel vm
        || !Guid.TryParse(e.Data?.GetText(), out var draggedId)
        || sender is not TreeView tree)
    {
        return;
    }

    var target = GetNodeAt(tree, e.GetPosition(tree));
    var newParentId = target switch
    {
        null => null,
        Core.Models.VirtualRootNode => null,
        Core.Models.FolderNode folder => folder.Id,
        Core.Models.SessionNode session => session.ParentId,
        _ => null
    };
    _ = vm.MoveNodeToAsync(draggedId, newParentId);
}
```

实现注记：
1. `DragOver` 需要读取 VM 的节点缓存做合法性判定。给 `MainViewModel` 加只读透传：`public IReadOnlyList<TreeNodeBase> DebugAllNodesCache => _allNodesCache;`（命名即为避免与既有字段混淆，注释注明供拖拽命中判定使用）。
2. `DataObject` 用 `DataFormats.Text` 承载节点 Id（ Avalonia 原生 DataObject 跨进程拖拽才需要专用格式，进程内自拖自放用 Text 最简）。**Drop 不校验数据来源**，`Guid.TryParse` 兜底。
3. 需要新增 using：`Avalonia.Interactivity`（DragDrop/DragEventArgs 所在，若编译器提示则按提示补 `Avalonia.Input` 已覆盖）。

- [ ] **Step 3: axaml AllowDrop**

`MainWindow.axaml:180` TreeView 属性追加 `AllowDrop="True"`。

- [ ] **Step 4: 构建验证**

Run: `dotnet build Kei.Term.slnx && dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
Expected: 全绿

---

### Task 5: 本地化 — resx 抽取（@designer 执行）

**Files:**
- Create: `src/Kei.Term.App/Resources/Strings.resx`
- Create: `src/Kei.Term.App/Helpers/Strings.cs`
- Create: `src/Kei.Term.App/Helpers/KeiStringExtension.cs`
- Modify: `src/Kei.Term.App/Views/*.axaml`、`src/Kei.Term.App/Views/MainWindow.axaml.cs`、`src/Kei.Term.App/ViewModels/*.cs`（全量字符串替换）

**Interfaces:**
- Produces:
  - `static string Strings.Get(string key)`（C# 侧取词）
  - `{loc:KeiString Key}` 标记扩展（axaml 侧取词）
  - resx 清单名固定 `Kei.Term.App.Resources.Strings`（路径即清单名，勿改目录）

**键名约定**：`Pascal` 点分层 —— 窗口_用途，如 `Main.Title`、`Main.Filter.Placeholder`、`Menu.File`、`Menu.File.NewSession`、`Settings.Save`、`Tree.EmptyGuide`、`Status.Paste.InvalidTarget`、`About.Title`。

- [ ] **Step 1: 建 resx 与取词设施**

`Resources/Strings.resx` 标准 resx 2.0 头（`<root>` + `reshdr` + `xsd/xml` schema 声明），条目形如：

```xml
<data name="Main.Title" xml:space="preserve">
  <value>Kei.Term - SSH 终端</value>
</data>
<data name="Menu.File">
  <value>文件</value>
</data>
```

`Helpers/Strings.cs`：

```csharp
using System.Globalization;
using System.Resources;

namespace Kei.Term.App.Helpers;

// resx 取词入口：中性语言即中文；缺失键回退键名本身，避免界面出现空串
public static class Strings
{
    private static readonly ResourceManager Rm = new("Kei.Term.App.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key)
        => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
```

`Helpers/KeiStringExtension.cs`：

```csharp
using System;
using Avalonia.Markup.Xaml;

namespace Kei.Term.App.Helpers;

// axaml 静态取词：Text="{loc:KeiString Main.Title}"（XAML 加载时求值，v1 不做热切换）
public class KeiStringExtension : MarkupExtension
{
    public KeiStringExtension() { }
    public KeiStringExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
```

csproj 无需改动：SDK 对 `Resources\Strings.resx` 默认按 `Kei.Term.App.Resources.Strings` 清单名嵌入（与 `Strings.cs` 中的清单名一致，构建后若取不到词先核对二者）。

- [ ] **Step 2: 全量替换（机械，逐文件过）**

文件清单（覆盖全部用户可见字符串）：
`MainWindow.axaml`（Title/ToolTip/菜单头/ContextMenu/占位符/按钮文案/空态引导/撰写栏）、`MainWindow.axaml.cs`（关闭确认弹窗、About 弹窗文案）、`SettingsWindow.axaml`、`IdentityManagerWindow.axaml`、`IdentityEditWindow.axaml`、`QuickConnectWindow.axaml`、`SessionEditWindow.axaml`、`FolderEditWindow.axaml`、`AuthPromptWindow.axaml(+.cs)`、`MasterPasswordWindow.axaml`、`PassphrasePromptWindow.axaml`、`VaultSetupWindow.axaml`、`MainViewModel.cs`（`StatusMessage`、认证提示如"请输入主密码以解锁保管库"）、其余 `ViewModels/*.cs` 的用户可见文案、`ViewModels/Settings/*.cs` 页面内文案。

替换规则：
- axaml 静态文本：`Text="设置"` → `Text="{loc:KeiString Settings.Title}"`；根元素加 `xmlns:loc="using:Kei.Term.App.Helpers"`
- axaml 属性中的文本（ToolTip.Tip、Content、Header、PlaceholderText、Watermark）同规则
- C#：`StatusMessage = "无法粘贴：..."` → `StatusMessage = Strings.Get("Status.Paste.InvalidTarget")`，文件头 `using Kei.Term.App.Helpers;`
- **不动**：`VirtualRootNode.Name = "Sessions"`、日志文案（`_logger.LogXxx` 的中文仅入日志文件，非 UI；一并不抽，保持日志可读）
- 不改任何布局属性/绑定结构/样式

- [ ] **Step 3: 构建验证**

Run: `dotnet build Kei.Term.slnx && dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
Expected: 全绿；grep 抽查：`rg 'Text="[^\{]"' src/Kei.Term.App/Views/*.axaml` 中中文命中数应为 0（ToolTip 亦抽查）

---

### Task 6: 主题切换 — Semi / Material 应用内互斥激活

**Files:**
- Modify: `src/Kei.Term.App/Kei.Term.App.csproj`（加两个包引用）
- Create: `src/Kei.Term.App/Services/UiThemeService.cs`
- Modify: `src/Kei.Term.Core/Settings/AppSettings.cs`（加 `ControlLibraryTheme`）
- Modify: `src/Kei.Term.App/ViewModels/Settings/AppearanceSettingsPage.cs`
- Modify: `src/Kei.Term.App/ViewModels/SettingsViewModel.cs`（Reload 58 行附近 / SaveAsync 92 行附近）
- Modify: `src/Kei.Term.App/Views/SettingsWindow.axaml`（外观页 129-164 模板内加下拉）
- Modify: `src/Kei.Term.App/App.axaml.cs`（启动应用主题，101-107 行后）
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml.cs`（设置窗关闭后热切换，`WireDialogs` 内 OpenSettingsDialogAsync lambda）

**Interfaces:**
- Consumes: `Material.Avalonia` 的 `MaterialTheme`（`Material.Avalonia.Themes` 命名空间，`BaseTheme = BaseThemeMode.Dark`）；`Semi.Avalonia` 的 `SemiTheme`（`Semi.Avalonia` 命名空间）。**精确命名空间以还原 nuget 包内实际类型为准，编译报错时用 `dotnet` 反编译/对象浏览器核对**。
- Produces:
  - `AppSettings.ControlLibraryTheme`（`"KeiClassic" | "Semi" | "Material"`，默认 KeiClassic）
  - `static void UiThemeService.Apply(string key)`（幂等：先移除旧主题实例再挂新实例）

**Spike 前置说明**（spec 约定半天）：本任务 Step 1-3 完成即可编译验证机制；热切换观感（控件是否全部换皮）由用户运行判定——若个别控件残留旧样式，回退方案 = 在 `UiThemeService` 加 `RequireRestart` 常量，保存后仅提示"重启后生效"，启动时应用。该决策点在实现说明中显式留给用户。

- [ ] **Step 1: 包引用**

`Kei.Term.App.csproj` PackageReference 组追加：

```xml
<PackageReference Include="Material.Avalonia" Version="3.20.0" />
<PackageReference Include="Semi.Avalonia" Version="12.1.0.1" />
```

Run: `dotnet restore Kei.Term.slnx && dotnet build Kei.Term.slnx`
Expected: 还原成功、构建通过（两库均声明兼容 Avalonia ≥12.1.x）

- [ ] **Step 2: UiThemeService**

```csharp
using Avalonia;
using Avalonia.Styling;
using Material.Avalonia.Themes;
using Semi.Avalonia;

namespace Kei.Term.App.Services;

// 控件库主题：FluentTheme 常驻基座，Material/Semi 覆盖式主题互斥挂载（同时最多一个）
public static class UiThemeService
{
    public const string KeiClassicKey = "KeiClassic";
    public const string SemiKey = "Semi";
    public const string MaterialKey = "Material";

    // 幂等应用：移除既有 SemiTheme/MaterialTheme 后按 key 挂载；KeiClassic = 仅 Fluent + Kei.* 画刷
    public static void Apply(string? key)
    {
        var styles = Application.Current?.Styles;
        if (styles == null)
        {
            return;
        }

        for (var i = styles.Count - 1; i >= 0; i--)
        {
            if (styles[i] is SemiTheme or MaterialTheme)
            {
                styles.RemoveAt(i);
            }
        }

        switch (key)
        {
            case SemiKey:
                styles.Add(new SemiTheme());
                break;
            case MaterialKey:
                styles.Add(new MaterialTheme { BaseTheme = BaseThemeMode.Dark });
                break;
        }
    }
}
```

- [ ] **Step 3: 设置模型与设置页**

`AppSettings.cs`（`UiTheme` 字段后）追加：

```csharp
// 控件库主题："KeiClassic" | "Semi" | "Material"（Semi/Material 为覆盖式第三方控件主题）
public string ControlLibraryTheme { get; set; } = "KeiClassic";
```

`AppearanceSettingsPage.cs`：

```csharp
// 控件库主题候选项（Key 与 UiThemeService 常量对应）
public IReadOnlyList<ThemeOption> ControlLibraryOptions { get; } = new ThemeOption[]
{
    new("KeiClassic", "Kei 经典"),
    new("Semi", "Semi Design"),
    new("Material", "Material"),
};

[ObservableProperty]
private ThemeOption _selectedControlLibrary;

// 构造器内初始化（与 _selectedTheme 并列）：
_selectedControlLibrary = ControlLibraryOptions[0];

// 归一化：仅接受三个合法键，其余回退 KeiClassic
public string NormalizedControlLibraryKey => SelectedControlLibrary?.Key switch
{
    "Semi" => "Semi",
    "Material" => "Material",
    _ => "KeiClassic"
};

// 按持久化键恢复选中项
public void SetControlLibrary(string? key)
    => SelectedControlLibrary = ControlLibraryOptions.FirstOrDefault(o => o.Key == key) ?? ControlLibraryOptions[0];
```

`SettingsViewModel.Reload()` 加 `_appearance.SetControlLibrary(current.ControlLibraryTheme);`；`SaveAsync` 的 settings 初始化器"外观"段加 `ControlLibraryTheme = _appearance.NormalizedControlLibraryKey,`。

`SettingsWindow.axaml` 外观页模板（132-146 行"界面主题"段之后）插入：

```xml
<TextBlock Text="控件库主题" FontSize="13" FontWeight="Bold" Margin="0,10,0,0"
           Foreground="{DynamicResource Kei.Session.Foreground}"/>
<Grid ColumnDefinitions="150, *" ColumnSpacing="12">
    <TextBlock Grid.Column="0" Text="控件库:" VerticalAlignment="Center"
               Foreground="{DynamicResource Kei.Text.Secondary}"/>
    <ComboBox Grid.Column="1" MinWidth="220" HorizontalAlignment="Left"
              ItemsSource="{Binding ControlLibraryOptions}" SelectedItem="{Binding SelectedControlLibrary}"
              ToolTip.Tip="Semi/Material 为第三方控件主题，与 Kei 经典互斥生效">
        <ComboBox.ItemTemplate>
            <DataTemplate x:DataType="settings:ThemeOption">
                <TextBlock Text="{Binding Label}"/>
            </DataTemplate>
        </ComboBox.ItemTemplate>
    </ComboBox>
</Grid>
```

- [ ] **Step 4: 启动应用 + 保存后热切换**

`App.axaml.cs`（101-107 行 `RequestedThemeVariant` 赋值之后）加：

```csharp
// 按设置挂载第三方控件主题（KeiClassic = 不挂载，仅 Fluent + Kei.*）
UiThemeService.Apply(settingsService.Current.ControlLibraryTheme);
```

（文件头加 `using Kei.Term.App.Services;`）

`MainWindow.axaml.cs` 的 `WireDialogs` 内 `OpenSettingsDialogAsync` lambda，在 `await win.ShowDialog(this);` 之后加：

```csharp
// 设置确认保存后立即应用控件库主题（Apply 幂等，KeiClassic 即卸载第三方主题）
if (settingsVm.IsConfirmed)
{
    Services.UiThemeService.Apply(vm.CurrentSettings.ControlLibraryTheme);
}
```

- [ ] **Step 5: 全量验证**

Run: `dotnet build Kei.Term.slnx && dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`
Expected: 全绿。交互观感（三档切换、拖拽、菜单、本地化文案）由用户运行评估——按项目约定不代运行。

---

## Self-Review 记录

1. **Spec 覆盖**：菜单栏（Task 3）✓ 虚拟根+落点（Task 1/2）✓ 拖拽+防环（Task 1/4）✓ 右键空白修复（Task 2）✓ 本地化中性=中文（Task 5）✓ Semi/Material 互斥+设置项+启动应用+热切换（Task 6）✓ About 窗（Task 3）✓ Core 单测（Task 1）✓ Atom/samples 不做（无任务）✓
2. **占位符扫描**：Task 5 的全量替换以"清单+键名约定+替换规则"完整给出（字符串本体即各文件现存文案，属机械搬运，无 TBD）。
3. **类型一致性**：`TreeDropResult(IsValid, NewParentId, IsNoOp)` 在 Task 1 定义、Task 2/4 使用一致；`MoveNodeToAsync(Guid, Guid?)` 定义与调用一致；`ControlLibraryTheme` 键值三处（AppSettings/UiThemeService 常量/AppearanceSettingsPage）一致。
