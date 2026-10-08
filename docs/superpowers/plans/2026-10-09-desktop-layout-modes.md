# Desktop Layout Modes (Classic vs Modern) Implementation Plan

> **For agentic workers:** 使用 `subagent-driven-development` 按下述职责执行；任务以复选框跟踪，不在每阶段重复全量回归或提交。

**Spec:** `docs/superpowers/specs/2026-10-09-desktop-layout-modes-design.md`

## Goal
实现 KeiTerm 的双桌面布局模式（Classic 默认与 Modern 可选）共存以及 Dock 标签页（Tab）现代精致化视觉升级。
Classic 模式保持既有操作结构与行为完整性；Modern 模式提供左侧紧凑 Activity Rail 与高度集成的顶部操作带（保持丰富可发现工具栏与常驻连接管理器入口）；Dock 标签页在两种模式下统一完成无边框小尺寸关闭按钮 `✕`、充裕点击热区与现代状态反馈。
布局模式变更遵循**“随现有设置保存流程持久化，下次启动完全生效”**策略；取消设置编辑不得写入配置，当前窗口不进行半切换。

---

## Architecture & Topology Boundaries
1. **拓扑不重构（No Topology Restructuring）**：
   - 本期不改变既有 `WorkspaceCoordinator` 的单窗口分屏机制。
   - 现有各窗格组（Pane Group）的 Tab 宿主维持在 Dock 内部，**不提升为全局连接管理标签，不任意混搭连接**。
2. **布局外壳分层与单宿主稳定挂载**：
   - 核心分屏工作区 `TerminalWorkspaceView`（包含底层 PTY / SSH Transport / 终端实例）作为独立单例视图组件，在窗口启动时根据生效的 `EffectiveLayoutMode` 挂载至对应的外壳布局中。
   - 运行期在“设置”中修改布局模式时，仅持久化至 `AppSettings.LayoutMode`，当前运行期主窗口维持 Effective Mode 稳定工作，不触发任何 Visual Tree 动态解挂或会话断连。
3. **设置隔离模型**：
   - 常规外观设置（GUI 配色 Profile、树密度等）维持原有的运行期即时预览/热刷新。
   - 布局模式选择（`LayoutMode`）明确为“下次生效”，界面显示提示文本，不进行运行期半切换。
4. **防御性平台窗口装饰策略**：
    - Linux 默认采用保守 Fallback：保留原生系统标题栏装饰（`ExtendClientAreaToDecorationsHint = false`），窗口拖动与双击行为仍需实机验证。
   - macOS / Windows 依平台 Insets 动态避让系统控制区，未验证平台 API 前不作固定像素硬编码保证。

---

## Tech Stack
- **平台框架**：.NET 10 (C# 13), Avalonia 12.x, CommunityToolkit.Mvvm
- **停靠体系**：`wieslawsoltes/Dock` (Dock.Avalonia 12.1.0.6)
- **图标与样式**：项目既有 Lucide 矢量 StreamGeometry 资源（`KeiIcons.axaml`）、Kei 设计系统 Tokens（`KeiTokens.axaml`、`KeiDockTheme.axaml`）
- **测试框架**：xUnit, Avalonia.Headless.XUnit

---

## Global Constraints
- **规范与代码约束**：
  - 遵循 `AGENTS.md`：多用行内注释，少用行后注释；禁止多条 `var` 声明堆叠；核心业务变更补充单元测试。
   - 本期不重构 Dock 拓扑、不引入多终端/OOBE/浮动窗口，不预先开展大规模组件复用重构。
  - **不修复不相关的基线失败测试**：已知全量测试中 `TerminalHost.ClearScreen_RemovesScrollback_SoNothingLeftToScroll` 与 `TerminalFind.FindCommands_NavigateMatchesAndCloseWithoutChangingOutput` 为已知待隔离 baseline，本期不借机扩张修改。
  - **测试真实性**：测试必须能因生产行为出错而实际失败，严禁无意义 Mock 测试（XAML 契约与资源存在性测试除外）。
   - 不逐阶段提交；是否提交实现成果遵循主会话与用户约定。

## Review Focus
- 缺失、未知字符串或未知数字布局值：只降级该字段到 Classic，不丢失其余设置。
- 保存 Modern 或取消草稿：当前窗口布局、工作区引用及连接均不变化；重建窗口才应用已保存模式。
- Modern 菜单与工具入口：与 Classic 的命令、禁用状态及既有快捷键保持一致。
- 窄窗口、长标题与高 DPI：关闭热区可点、标题省略可辨识、工具栏有可达的溢出入口。
- 侧栏收起/悬停/固定与 Tab 关闭、中键、重排：焦点与实际关闭目标正确，无重复宿主。

---

## Task Decomposition (3 Milestones)

### Milestone 1: 配置持久化与设置页映射 (Core Settings & Persistence)
> **Write Scope**: `src/Kei.Term.Core/Models/Profiles/LayoutMode.cs`, `src/Kei.Term.Core/Settings/AppSettings.cs`, `src/Kei.Term.App/ViewModels/Settings/AppearanceSettingsPage.cs`, `src/Kei.Term.App/Views/SettingsWindow.axaml`, `tests/Kei.Term.Tests/LayoutModeSettingsTests.cs`
> **Owner**: Fixer (模型/配置逻辑) / Designer (设置页 XAML 提示)

- [ ] **1.1 定义 LayoutMode 枚举与持久化属性**
  - 在 `Kei.Term.Core.Models.Profiles` 新增 `LayoutMode` 枚举：`Classic` (0, 默认), `Modern` (1)。
  - 在 `AppSettings` 中新增属性：`public LayoutMode LayoutMode { get; set; } = LayoutMode.Classic;`。
  - 确保旧版未知配置反序列化或缺省时安全回退至 `LayoutMode.Classic`。
- [x] **1.2 扩展 AppearanceSettingsPage 与设置提示**
  - 在 `AppearanceSettingsPage` 中增加 `LayoutModeOptions` 选项列表与 `SelectedLayoutMode` 属性。
  - 绑定到 `SettingsWindow.axaml` 的“外观 -> 界面模式”配置区。
  - 增加提示文案绑定（例如：“*切换布局将在下次启动应用时生效*”），明确展示已保存状态但不触发即时界面重组。
  - 确保 GUI 配色（`ActiveGuiProfileId`）和标签栏位置（`TabPlacement`）等现有即时刷新功能不受影响。
- [ ] **1.3 编写配置与降级单元测试**
  - 在 `tests/Kei.Term.Tests/LayoutModeSettingsTests.cs` 中实现行为测试：
     - `LayoutMode_PersistenceAndFallback`：经实际 `JsonSettingsService` 保存/加载 Modern，验证缺失、非法字符串、空字符串与未知数字值回退为 Classic，同时保留其他设置字段；不以回显默认常量代替行为验证。
    - `AppearanceSettingsPage_SelectionUpdatesDraftWithoutInstantRerender`：断言修改选项后仅更新草稿属性，不破坏其他外观设置。

---

### Milestone 2: Dock 标签页 (Tab) 现代精致化样式升级 (Tab Visual Refinement)
> **Write Scope**: `src/Kei.Term.App/DesignSystem/KeiDockTheme.axaml`, `tests/Kei.Term.Tests/DockTabThemeContractTests.cs`
> **Owner**: Designer

- [x] **2.1 彻底移除复古方框，重构 Tab 关闭按钮 ControlTheme**
  - 修改 `KeiDockTheme.axaml` 中的 `dc|DocumentTabStripItem` 模板样式：
    - 移除默认硬质关闭边框与实体背景，常态保持 `Background="Transparent"`，`BorderThickness="0"`。
    - 点击热区保持外层容器 `24×24px`，确保鼠标命中率。
    - 内部矢量 `PART_ClosePath` 调整为 `10×10px` 居中紧凑视口，描边 `1.5px`，严格绑定 Lucide 关闭图标几何。
    - 增加微圆角悬停反馈：`:pointerover` 时应用 `CornerRadius="3"` 与背景色 `{DynamicResource Kei.Bg.Hover}`。
    - 增加按压反馈：`:pressed` 时应用 `{DynamicResource Kei.Bg.Pressed}`。
- [x] **2.2 优化 Tab 项状态条、长标题省略与无障碍属性**
  - 确保状态条（`ConnectionStateBrush` 驱动）保持 `3×14px` 垂直居中胶囊。
  - 标签标题 `TextBlock` 显式限定最大宽度（如 `MaxWidth="200"`），并设置 `TextTrimming="CharacterEllipsis"`（尾部省略），且绑定完整 `ToolTip.Tip`。
  - 为关闭按钮添加 `AutomationProperties.Name`（绑定 `{loc:KeiString Main.Tab.Menu.Close}`）及 `:focus-visible` 焦点虚线高亮。
- [x] **2.3 编写 Tab 样式契约与资源测试**
  - 在 `tests/Kei.Term.Tests/DockTabThemeContractTests.cs` 中实现 XAML 契约测试：
    - `TabCloseButton_HasCorrectDimensionsAndHitBox`：断言关闭按钮外层判定尺寸为 24px、内层图标为 10px。
     - `TabHeader_TrimsWithCharacterEllipsis`：断言标题控件配置了 `CharacterEllipsis` 且具有 ToolTip 绑定。
     - 补充 Headless 实际控件测试：关闭按钮及中键关闭指定文档，不误关闭相邻文档；长标题在窄窗口仍有可命中的关闭区与完整提示。

---

### Milestone 3: 主窗口 Classic 与 Modern 外壳集成 (Shell Implementation)
> **Write Scope**: `src/Kei.Term.App/Views/MainWindow.axaml`, `src/Kei.Term.App/Views/MainWindow.axaml.cs`, `src/Kei.Term.App/Views/MainWindow.Sidebar.cs`, `tests/Kei.Term.Tests/MainWindowLayoutModeTests.cs`
> **Owner**: Designer

- [x] **3.1 主窗口启动期锁定 Effective 布局模式**
  - 在 `MainWindow.axaml.cs` 初始化时读取当前已提交配置的 `LayoutMode`，将其锁定为窗口生命周期内的 `EffectiveLayoutMode`。
  - 若运行于 Linux 平台，默认应用保守装饰策略（`ExtendClientAreaToDecorationsHint = false`），保持原生标题栏。
- [x] **3.2 构建 Modern 外壳组件（Activity Rail + 顶部操作带）**
  - 在 `MainWindow.axaml` 中实现 Modern 布局外壳：
    - **Activity Rail (44px)**：挂载主菜单汉堡按钮、会话树开关（`ToggleSessionManagerPinCommand`）、SFTP 开关（`ToggleFileManagerCommand`）、凭据管理（`OpenIdentityManagerCommand`）与设置（`OpenSettingsCommand`）。
    - **顶部集成操作带**：放置紧凑快捷操作组（新建标签 `NewTabCommand`、快速连接 `QuickConnectCommand`、连接/断开、命令面板 `OpenCommandPaletteCommand`）。
    - 保持连接管理器抽屉（`SessionManagerBorder`）悬停展开与固定逻辑（`MainWindow.Sidebar.cs`）完全可用。
- [x] **3.3 保持单一 Dock 工作区稳定挂载**
  - 确保单一 `TerminalWorkspaceView`（包含所有终端会话与连接）按 Effective 模式挂载至中央工作区，**绝不创建两个工作区实例，绝不发生双挂载**。
- [x] **3.4 编写主窗口外壳集成行为测试**
  - 在 `tests/Kei.Term.Tests/MainWindowLayoutModeTests.cs` 中实现 Headless 测试：
    - `MainWindow_InitializesWithEffectiveClassicLayout_WhenConfigured`：配置为 Classic 时，`NativeMenuBar` 与全宽工具栏可见。
     - `MainWindow_InitializesWithEffectiveModernLayout_WhenConfigured`：配置为 Modern 时，Activity Rail 可见，原有功能命令可正常路由且不报双挂载异常。
     - `LayoutSelection_SaveDoesNotChangeRunningShell`：保存另一模式后断言当前菜单/Rail、工作区及终端实例引用不变，重新创建窗口才应用保存值。
     - `LayoutSelection_CancelDoesNotPersist`：取消设置草稿后重建窗口仍使用原模式。
     - `ModernShell_ActionsAndSidebarRemainReachable`：验证菜单命令、禁用态、窄窗口溢出入口，以及侧栏悬停/固定/收起和焦点恢复。

---

## Final Build & Test Command
```bash
# 验证生产工程编译无警告与错误
dotnet build Kei.Term.slnx

# 运行新增的针对性布局与 Tab 契约测试
dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~LayoutMode|FullyQualifiedName~DockTabTheme"

# 最终统一回归；记录并区分已知基线失败与新回归
dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj
```

---

## Task Graph & Ownership

```
[Milestone 1: 配置逻辑] (Fixer) ║ [Milestone 2: Tab 现代美化] (Designer)
       └─────────────────────┬─────────────────────┘
       ▼
[设置页 XAML + Milestone 3: 主窗口外壳装配] (Designer，等待配置接口)
       │
       ▼
[静态可行性验证 & 主会话 Review]
```

### Write Ownership Matrix
- `src/Kei.Term.Core/Models/Profiles/LayoutMode.cs`: **Fixer**
- `src/Kei.Term.Infrastructure/Settings/JsonSettingsService.cs`: **Fixer**（字段级容错，沿用现有序列化机制）
- `src/Kei.Term.Core/Settings/AppSettings.cs`: **Fixer**
- `src/Kei.Term.App/ViewModels/Settings/AppearanceSettingsPage.cs`: **Fixer**
- `src/Kei.Term.App/Views/SettingsWindow.axaml`: **Designer**
- `src/Kei.Term.App/DesignSystem/KeiDockTheme.axaml`: **Designer**
- `src/Kei.Term.App/Views/MainWindow.axaml*`: **Designer**
- `tests/Kei.Term.Tests/*`: **Fixer / Designer 对应模块测试**

### Test Ownership & Review Responsibility
- **Plan 静态可行性与源码核实 Owner**：@designer；执行时核验当前版本 API 与资源键，禁止依据草图猜测接口。
- **代码实现审查与 Self-Review Owner**：主会话 (Orchestrator)。
- **跨平台多环境（macOS / Win / Linux）实机效果最终确认**：由具备对应环境的主会话/用户在可用物理机上验证。
