# Dock 单窗口连接整体移动实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在单窗口内分屏多个连接，连接的 Shell 与所属 SFTP 始终一起移动，先交付可实际体验的实验布局。

**Architecture:** 用 Dock 文档树作为唯一分屏拓扑，每个文档包装已有 `TerminalTabViewModel`，不改变 SSH/SFTP 的业务归属。小型工作区协调器负责活动连接、迁移和关闭路由；连接内容视图保留真实终端与 SFTP 状态，主窗口只承载工作区及共享工具栏/撰写栏。仅定向提取 `MainViewModel` 的标签职责，不全面重构。

**Tech Stack:** .NET 10、Avalonia 12.1.2、CommunityToolkit.Mvvm 8.4.2、RoyalApps.RoyalTerminal.Avalonia 0.6.0-preview.2；候选 Dock 包统一锁定 12.1.0.6。

**Spec:** `docs/superpowers/specs/2026-10-06-dock-workspace-design.md`

## Global Constraints

- 一个连接的 Shell 与所属 SFTP 是不可拆分的整体；不单独停靠 SFTP。
- 单窗口；不编辑器、不浮动窗口、不跨窗口拖动、不首次布局持久化或活会话恢复。
- 每组一层连接标签；复用现有 `TabPlacement`，全组顶部/底部一致。
- 移动、隐藏、重排不得断开 SSH/SFTP、重新认证或重新实例化终端；明确关闭才释放。
- UI 状态只在 UI 线程修改；复用设计令牌、编译绑定与统一交互服务。
- 不修改 Core 的 UI 依赖边界、数据库 schema 或认证流程。
- 不触碰 `docs/mobile-platform-research.md` 或用户其他变更；不 push，不分阶段自动 commit。
- 仅门槛探针及各任务必要的定向测试；全量构建和回归在最终状态统一运行，不重复逐阶段回归。

## Review Focus

1. 点击非活动组的 SFTP 后进入共享撰写栏：命令必须发到刚才交互的连接，而不是旧活动组。
2. 移走组内最后一个连接：仅收拢空组，不能因 Dock 的关闭通知顺带释放移动中的会话。
3. 两组同时可见时切换活动组：不能因原全局 `IsSelected` 循环把另一个组隐藏。
4. 连续开关连接：缓存不能留住已关闭连接的视图/订阅；同一连接关闭只释放一次。
5. 底部标签启用后再生成新组、拖出窗口或按 ESC：新组仍居底，取消动作保持原拓扑。

## 文件边界与工作顺序

- 门槛探针：扩展 `tests/Kei.Term.Tests/TerminalHostTests.cs`，复用 `HeadlessAvalonia.cs`；临时可视诊断材料只放 `/tmp/opencode`。
- 模型与协调：新增 `src/Kei.Term.App/Workspaces/TerminalWorkspaceDocument.cs`、`TerminalWorkspaceFactory.cs`、`WorkspaceCoordinator.cs`、`WorkspaceMessages.cs`。
- 界面：新增 `src/Kei.Term.App/Views/TerminalConnectionView.axaml` 及 `.axaml.cs`、`TerminalWorkspaceView.axaml` 及 `.axaml.cs`，按需新增工作区主题文件；修改 `MainWindow.axaml/.cs`、`App.axaml`。
- 接线：修改 `MainViewModel.cs` 的标签相关部分；只在职责需要时修改 `TerminalTabViewModel.Header.cs`，不重写连接逻辑。
- 测试：新增 `WorkspaceCoordinatorTests.cs`、`WorkspaceHostTests.cs`，替换 `TabReorderingTests.cs` 中不调用生产逻辑的模拟断言。
- 顺序：探针 → 工作区模型 → UI 与接线 → 最终验证。fixer 与 designer 不同时修改 `MainViewModel`；UI 接线由 designer 单独负责。

---

### Task 1：依赖与真实终端重挂载门槛

**Owner:** fixer；主会话核对证据，用户负责真实桌面观感验收。

**Files:** `TerminalHostTests.cs`、`src/Kei.Term.App/Kei.Term.App.csproj`（仅批准后的包引用；主题入口由 Task 3 的 designer 处理）。

**Interfaces:** 不改变业务接口；后续使用现有 `TerminalTabViewModel.Terminal`、`TrySyncTerminalSize()`、`HeadlessAvalonia.RunAsync` / `Pump()`。

- [ ] 核验并锁定 `Dock.Avalonia`、`Dock.Model.Mvvm`、`Dock.Avalonia.Themes.Fluent` 为 `12.1.0.6`；不升级/降级 Avalonia。官方 `Dock.Avalonia` nuspec 的最低 Avalonia 是 `12.1.1`，`12.1.0.7` 不作为本轮候选。还原失败或 API 不符时停止并报告，不擅自换框架。
- [ ] 新增 `Reparent_PreservesTerminalInstance_AndContinuesOutput`：真实终端在两个 ScrollViewer 宿主间先解除旧父级再挂到新父级；断言终端引用相同、Renderer 可用，搬移后仍能接收并显示输出。不能只断言“不抛异常”。
- [ ] 新增 `Reparent_ResizesGrid_AndPreservesHistory`：改变宿主宽高并 Pump，断言行列随实际字体度量更新、历史内容保留且滚动绑定可继续使用。使用真实缓冲/滚动可观测状态，不把已有 ClearScreen 失败当成本测试通过。
- [ ] 运行 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~Reparent_'`；记录断言、结果与限制。探针失败则先诊断，不能直接全面替换主窗口。
- [ ] 明确证据边界：Headless + Skia 能证明真实控件挂载/尺寸/缓冲行为，不能证明原生窗口渲染无黑屏或闪烁。真实桌面是否可以由 agent 运行需遵循用户许可；否则提供最小复现步骤由用户运行，保持原生渲染结论为“待验收”，不声称全部平台已验证。

### Task 2：唯一 Dock 拓扑与工作区协调器

**Owner:** fixer。允许修改 Workspaces 与该任务测试；不写 UI，不修改 `MainViewModel`。

**Files:** 新增上述四个 Workspaces 文件和 `WorkspaceCoordinatorTests.cs`；更新 `TabReorderingTests.cs`。

**Interfaces:**

- `TerminalWorkspaceDocument : Dock.Model.Mvvm.Controls.Document`，构造函数 `TerminalWorkspaceDocument(TerminalTabViewModel tab)`；只读 `TerminalTabViewModel Tab`；Id 为本次打开实例唯一标识，不能以会话配置 Id 代替。
- `TerminalWorkspaceFactory : Dock.Model.Mvvm.Factory`，覆盖 `CreateLayout()` 与 `CreateDocumentDock()`；新增组统一应用能力策略。禁止通过把 `TerminalTabViewModel` 改成 Dock 基类来耦合业务。
- `WorkspaceCoordinator` 持有 `TerminalWorkspaceFactory Factory`、`IRootDock Layout`、`ObservableCollection<TerminalTabViewModel> AllTabs`、`TerminalTabViewModel? ActiveTab`。
- `void AddTab(TerminalTabViewModel tab)`：加入当前活动组并激活。
- `void Activate(TerminalTabViewModel tab)`：定位所属组，更新组内选中与全局活动项；离开内容进入共享工具栏不清空活动项。
- `void MoveTab(TerminalTabViewModel tab, IDocumentDock destination, int index)`：只改变拓扑，不释放资源；索引超界无操作。
- `void SplitTab(TerminalTabViewModel tab, IDocumentDock target, DockOperation direction)`：仅接受 Left/Right/Top/Bottom；单组仅有一个连接向自身分屏不生成空兄弟组。
- `void RemoveTab(TerminalTabViewModel tab)`：明确关闭时移除拓扑，幂等；该方法不直接 Dispose，释放仍由现有关闭链路负责。
- `void ReorderTab(TerminalTabViewModel tab, int index)`、`void Dispose()`：协调器退出时解除自身订阅；不把解除订阅混同于迁移释放连接。
- 跨模块消息：`WorkspaceActiveTabChangedMessage(WorkspaceCoordinator Source, TerminalTabViewModel? Tab)`、`WorkspaceCloseRequestedMessage(WorkspaceCoordinator Source, TerminalTabViewModel Tab)`，通过项目现有 MVVM Messenger 模式通信；接收者按 Source 过滤并在退出注销。

**锁定版本接缝：** 使用 `Factory.MoveDockable(IDock sourceDock, IDock targetDock, IDockable sourceDockable, IDockable? targetDockable)`、`SplitToDock(IDock dock, IDockable dockable, DockOperation operation)`、`CloseDockable(IDockable? dockable)`；监听 `ActiveDockableChanged` 与关闭/迁移事件。`DockOperation` 位于 `Dock.Model.Core`。索引到 Dock 目标项的映射由协调器完成，调用前完成有效性检查。

- [ ] 写生产行为测试，再最小实现：`Move_PreservesTabIdentity_AndSftpState`、`Move_LastTabCollapsesSource_WithoutDisconnecting`、`Reorder_InvalidIndexIsNoOp`、`Split_LeavesBothGroupsSelected`、`Split_OnlyTabAgainstOwnGroupIsNoOp`、`Activate_PreservesOtherGroupSelection`。
- [ ] 用 Dock 原生树与工厂操作作为唯一布局来源，不维护另一份独立分割树。组信息通过 Dock document docks 派生；保留 AllTabs 仅用于全连接外观更新与兼容现有命令，不作为第二份拓扑。
- [ ] 对 Dock 拖放产生的布局变化同步 ActiveTab/AllTabs，不仅测试手工调用自定义方法。直接关闭请求进入现有应用关闭命令，使用幂等/重入保护；迁移清理空组不得发送连接关闭消息。
- [ ] 保留 `TerminalTabViewModel.IsSelected` 为全局活动/未读提示语义；内容显示改由每个 Dock 组的 ActiveDockable 控制，不新增全局可见性循环。两组同时有组内选中连接，但只有一个全局活动连接。
- [ ] 增加 `CloseRequest_IsRoutedOnce`、`Remove_RepeatedCallIsNoOp`、`RemovingActiveTab_SelectsSameGroupNeighbour_OrRemainingGroup`，验证真实生产消息与拓扑，不回显常量、不手工模拟 ObservableCollection 算法。
- [ ] 定向运行 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~WorkspaceCoordinatorTests|FullyQualifiedName~TabReorderingTests'`；返回接口/修改文件/结果，再交给 designer。

### Task 3：复合连接视图、Dock 外观与主窗口接线

**Owner:** designer；允许上述 Views/主题/App/MainViewModel/必要 Header 与 `WorkspaceHostTests.cs`，不改认证、SFTP业务或 schema。

**Interfaces:** 使用 Task 2 的 Layout、Factory、Activate/Add/Remove 与消息；沿用 MainViewModel 现有 `Tabs`、`SelectedTab`、`MoveTab(int fromIndex, int toIndex)` 和连接/关闭命令外部接口。

- [ ] 从 `MainWindow.axaml:418-495` 提取整个连接内容为 `TerminalConnectionView`，包含终端 ScrollViewer、查找相关内容、SFTP 与内部分割条。保留现有 token、命令和左右停靠，不增加 Shell/SFTP 二级标签。
- [ ] 保证一个打开实例只拥有一个复合 View：使用已核验的 Dock recycling 接口或显式按 Document Id 缓存；迁移前脱离旧父级，不能同时挂两个父级。保留 SFTP 展开宽度与目录/传输状态；明确关闭时逐实例清理缓存，不清空其他活动连接缓存。
- [ ] `TerminalWorkspaceView` 使用 DockControl 展示 Layout/Factory；Dock 新创建组继承全局禁浮动/禁 pin 策略，不额外展示应用不支持的右键功能。保留连接状态色条、关闭按钮、中键关闭与拖拽插入反馈；不保留与 Dock 冲突的旧拖拽处理链。
- [ ] 顶部标签使用内建布局；底部标签按锁定包真实模板覆盖，不能假定 `DocumentTabLayout.Bottom` 存在，也不能只给祖先加 class 就假定子控件自动获得同 class。先核验 PART 名称和祖先选择器；全局配置变化及之后新生成的组都要命中。
  - 对 DockControl 的 `.tabs-bottom` 使用后代选择器命中各 `DocumentControl` 的模板 `DocumentTabStrip#PART_TabStrip` 与 `Panel#PART_DocumentSeperatorHost`，设置 `DockPanel.Dock=Bottom`；真实模板穿透与绑定优先级以 host 测试为准，若不命中由 designer 调整最小局部主题，不能删除底部选项。
  - 回收器具体类型 `Avalonia.Controls.Recycling.ControlRecycling` 提供 `bool Remove(object data)`，关闭时传文档实例逐项移除；接口类型不暴露该方法。移除缓存不替代显式资源释放/退订，禁止关闭一个连接时调用全缓存 Clear。
  - 能力类型 `Dock.Model.Core.DockCapabilityPolicy`；Root 的禁浮动策略会被个体覆盖或容器策略覆盖，Factory 创建的每个节点不得声明更高优先级的允许浮动覆盖，文档本身同样设 `CanFloat=false`。
- [ ] 定向提取 MainViewModel 标签职责：创建连接调用 Workspace.AddTab；关闭先 Workspace.RemoveTab，再复用当前退订与异步 Dispose；SelectedTab 与 ActiveTab 双向同步须有同值/重入保护，所有现有工具栏 CanExecute 通知仍更新。保留全连接字号、主题、重连等遍历行为。
- [ ] 在复合视图的 handledEventsToo 输入/焦点路径中激活所属连接，涵盖终端与 SFTP 内控件；进入共享撰写栏保持最后目标，不因 toolbar 得到焦点清空 ActiveTab。
- [ ] 替换主窗口右侧旧 TabsItemsControl 与单选可见区；移除仅限旧标签条的拖拽处理，不动会话树拖放与连接管理器。
- [ ] Headless 增加 `TwoGroups_ShowSelectedContentSimultaneously`、`SftpInteraction_SelectsComposeTarget`、`ComposeFocus_PreservesLastTarget`、`BottomTabs_ApplyToNewSplitGroups`、`CancelledDrag_PreservesMembership`、`Close_RemovesOnlyClosedDocumentViewCache`。调用生产协调器/视图；测试命令发送使用记录远端输入的测试会话，断言实际收到命令的目标，不只断言 VM 属性。
- [ ] 新增 `Close_DisposesSessionExactlyOnce_WhileMoveDoesNotDispose` 与 `Split_PropagatesChangedTerminalSizeToEndpoint`：接入真实 TerminalTabViewModel 的测试端点，观察资源释放与尺寸下发；避免仅对自制代理计数却未调用生产连接生命周期。
- [ ] 对同一连接复合 View/Terminal 的身份和 SFTP 状态做前后对照；必要 XAML 资源契约复用项目规范，不以源码文本断言代替行为。
- [ ] 定向运行 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~WorkspaceHostTests|FullyQualifiedName~WorkspaceCoordinatorTests|FullyQualifiedName~TabReorderingTests'`，主会话审核中文文案，视觉判断归 designer。

### Task 4：统一最终验证与体验交付

**Owner:** 主会话综合验证；designer 对拖拽/标签/焦点观感负责，fixer 对拓扑与生命周期行为证据负责。

- [ ] 核对 `git diff --check`、变更文件边界、无浮动/第二层标签/布局存储扩项；确认临时诊断文件没有进入产品目录。
- [ ] 运行一次 `dotnet build Kei.Term.slnx`，要求 0 错误；对任何新增警告说明并处理。
- [ ] 运行一次 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --no-build`，分别报告新增工作区测试与基线失败，不掩盖失败、不为过测试改写无关认证行为。
- [ ] 已知基线：422 通过、3 失败、6 跳过；失败为 `NoMaterials_AndFallbackCancelled_OpensNoTab`、`Reconnect_AuthCancelled_LeavesExistingTabUntouched`、`ClearScreen_RemovesScrollback_SoNothingLeftToScroll`。状态若改变按实际结果报告，不把历史数字当最新结论。
- [ ] 有 `/usr/sbin/sshd` 且隔离测试可用时，仅为真实会话生命周期需要运行 `KEITERM_SSHD_TESTS=1 dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --no-build`；缺环境则说明未覆盖，不启动用户真实连接做破坏性验证。
- [ ] 提供 `dotnet run --project src/Kei.Term.App/Kei.Term.App.csproj` 和六步验收：开两个连接 → 展开 A 的 SFTP → 拖 B 分屏 → 把 A 整体迁到另一组 → 从 SFTP 激活后用撰写栏发送 → 切到底部标签、取消窗口外拖放并关闭一组。明确自动证据与用户桌面待验收项。
- [ ] 只有探针/工作区行为与最终构建通过才交付“可试用”；原生渲染、滚动手感与操作舒适度未经实跑不能声称已验收。若用户要求提交，在最终状态完成后统一 commit；不自行 push。

## 依赖证据与限制

- `https://www.nuget.org/packages/Dock.Avalonia/12.1.0.6`
- `https://api.nuget.org/v3-flatcontainer/dock.avalonia/12.1.0.6/dock.avalonia.nuspec`：仓库提交 `cc08602d02fde1b85067cec064da29f34785e505`，提供 net10.0，Avalonia 最低 12.1.1。
- `https://www.nuget.org/packages/Dock.Model.Mvvm/12.1.0.6`
- 具体第三方 Factory/回收缓存/模板 API 按锁定版本源码核验后使用，不复制未编译示例；底部标签不是原生枚举值。Task 1 的还原/编译及 Task 3 的实际 host 测试是包可用性的证据，不用文档推断替代。
- 锁定提交源码接缝：`src/Dock.Model/FactoryBase.Dockable.cs`、`FactoryBase.cs`、`FactoryBase.Events.cs`、`DockCapabilityResolver.cs`；`src/Dock.Controls.Recycling/ControlRecycling.cs`；`src/Dock.Avalonia.Themes.Fluent/Controls/DocumentControl.axaml`。此前仅给 DocumentControl 本身 class 的示例不采用。

## 审批与执行方式

本计划使用专责子代理：fixer 负责探针与纯逻辑，designer 负责全部 UI 和接线，主会话负责协调、文案与最终验证；不把任务拆成大量微提交。计划需经用户审阅确认后实施。第三方 API 的事实修正若不改变上述交互与接口边界可直接记录；若门槛失败要求换实现路线或改变范围，先向用户说明再修订设计/计划。
