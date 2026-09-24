# KeiTerm UI 改进设计：菜单栏恢复 / 连接管理器修复 / 本地化 / 应用内主题切换

日期：2026-09-24
状态：待用户审阅

## 背景与现状

- **无菜单栏**：`MainWindow.axaml` 仅工具栏 + ContextMenu ×2，无顶部 MenuBar。
- **连接管理器缺陷**：
  - 全仓无任何拖拽代码；`ITreeRepository.MoveNodeAsync` 已定义但 UI 未接。
  - 空白处右键新建按"当前选中项"落点：选中文件夹 → 建在其内。用户右键空白时幽灵选中仍停在原节点，导致新目录落进错误分组。
  - 数据模型已支持任意多平级顶级节点（`ParentId == null` 即根），无单根强制。
- **本地化**：全部中文文案硬编码于 `.axaml` 与 C#，无 resx 设施。
- **主题**：FluentTheme + 自定义 `Themes/KeiTheme.axaml`（`Kei.*` 画刷），无第三方控件库。
- Avalonia 12.1.2 / .NET 10 / CommunityToolkit.Mvvm。

## 用户决策记录

| 决策点 | 结论 |
|---|---|
| 连接管理器顶层模型 | 虚拟根 "Sessions"（仅展示层；数据层保持平级多根，不动 SQLite 模型） |
| 菜单栏范围 | 精简真实菜单（文件/编辑/查看/工具/帮助），不做占位空菜单 |
| 本地化 | resx 中性语言 = 现有中文；英文翻译后续单独立项 |
| 执行顺序 | 主应用修复 → 本地化 → 主题切换 |
| AtomUI | 砍掉（LGPL v3 + 强依赖 ReactiveUI + breaking changes 频繁） |
| 主题对比方式 | 不建独立壳工程，改为应用内设置切换 Material/Semi |

## Phase 1 — 主应用修复

### 1.1 菜单栏恢复

`MainWindow.axaml` 根 Grid 行定义由 `Auto,Auto,*,Auto` 改为 `Auto,Auto,Auto,*,Auto`，新增首行 MenuBar。全部菜单项绑定 `MainViewModel` 现有命令，不新增假命令：

- **文件**：新建会话（`Ctrl+N`）/ 新建文件夹 / 快速连接 / ─ / 连接 / 断开 / ─ / 退出
- **编辑**：剪切 / 复制 / 粘贴 / 删除（绑现有树节点剪贴板命令）
- **查看**：连接管理器 / 撰写栏（ToggleMenuItem，`IsChecked` 绑 `IsSessionManagerVisible` / `IsComposeBarVisible`）
- **工具**：身份管理 / 设置
- **帮助**：关于（新增极简 About 窗：应用名 + 版本号，真实可用）

### 1.2 连接管理器修复

**Core 层（可测逻辑）**：

- `Models/TreeNodes.cs` 新增 `VirtualRootNode : TreeNodeBase`：`Name = "Sessions"`（固定英文，遵循 SecureCRT 惯例，不参与本地化），`Id = Guid.Empty`，`ParentId = null`，默认展开。
- `MainViewModel.BuildTree`：构建出的 roots 全部挂到单个 `VirtualRootNode` 下；`TreeNodes` 集合仅含该虚拟根。
- `TreeFilter`：适配虚拟根——过滤后虚拟根始终显示，子节点为命中项 + 祖先链。
- **落点规则**统一为：选中 `FolderNode` → 其 `Id`；选中 `SessionNode` → 其 `ParentId`；选中 `VirtualRootNode` 或无选中 → `null`（顶级）。应用于 `CreateSessionAsync` / `CreateFolderAsync` / 粘贴落点。
- 新增 `TreeDropResolver`（纯函数）：输入（被拖节点、目标节点或 null、落点区），输出（新 `ParentId` + 有效性）。防环：目标非自身且非自身子孙（泛化现有 `IsValidCloneTarget`）。

**App 层**：

- 右键空白修复：TreeView `ContextMenuOpening` 命中测试，空白处 → `SelectedTreeNode = null`。
- 拖拽：TreeView 挂 `DragDrop`（DragOver/Drop），DataObject 携带节点 Id；Drop → `TreeDropResolver` → `MoveNodeAsync(nodeId, newParentId)` → 仓储 `MoveNodeAsync` + `SortOrder` 追加到目标末尾 + 重建树。
- **虚拟根防持久化**：VM 所有 Save/Move/Delete 入口拒绝 `VirtualRootNode`（`Id == Guid.Empty` 守卫）。

**单元测试**（`tests/Kei.Term.Tests`）：BuildTree 含虚拟根、TreeFilter 适配、TreeDropResolver（含防环/自拖）、落点规则四组。

## Phase 2 — 本地化（@designer 执行）

- 新增 `Kei.Term.App/Resources/Strings.resx`，中性语言 = 现有全部中文文案。
- axaml：`Text` / `Title` / `Content` / `Header` 等 → `{x:Static s:Strings.*}`；C#（`StatusMessage`、对话框标题、消息框文案）→ `Strings.*`。
- 范围：全部 Window + ViewModel 消息 + ContextMenu + 设置子页模板。
- **约束**：只换字符串，不动布局/交互/视觉结构；完成后 `dotnet build` + `dotnet test` 必须全绿。

## Phase 3 — 应用内主题切换（Material / Semi）

- 包引用：`Material.Avalonia 3.20.0` + `Semi.Avalonia 12.1.0.1`（均 MIT，均兼容 Avalonia 12.1.2）。
- 机制：`App.axaml` 保留 FluentTheme 为基座（两库均为覆盖式主题）；**互斥激活**——切换时从 `Application.Styles` 移除当前主题实例、挂载目标主题实例，禁止两者同时挂载。
- 设置：`AppearanceSettingsPage` 新增"控件库主题"选项：`Kei 经典`（Fluent + KeiTheme，现状）/ `Semi` / `Material`；持久化到设置 JSON，启动时应用。
- **前置 Spike（半天）**：双主题运行时互切实测；若个别控件换皮残留，降级为"切换后提示重启生效"。
- `Kei.*` 画刷与各 Window 内联自定义样式保留在上层，不随主题切换丢弃。
- 已知边界：DataGrid 等卫星包本次不引入；`Semi.Avalonia` 的 Dock/Tabalonia/AvaloniaEdit 为闭源包，规避。

## 测试与验收

- Core 新逻辑全部有单测；`dotnet build Kei.Term.slnx` 与 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj` 全绿。
- 交互体验不代运行，由用户本人评估（项目既定约束）。

## 明确不做（YAGNI）

- 英文翻译（后续单独立项）
- Kei.* 画刷 → 各库 token 的深度映射（主题对比期后再定）
- samples/ 独立壳工程（已废弃）
- Transfer/Script 等占位菜单
