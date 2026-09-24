# Profile 系统、终端调色盘、字体扫描与标签栏增强实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 GUI Profile 与 Terminal Profile 解耦系统，提供预制经典调色盘、纯白前景色与核心颜色独立调控、系统字体动态扫描选择器、SecureCRT 风格连接状态竖条与标签拖拽、Konsole 风格标签栏置顶/置底、JSON 导入导出及图标栅格统一。

**Architecture:** 
1. `Kei.Term.Core` 建立数据模型（`GuiProfile`, `TerminalProfile`, `TabBarSettings`）与内置经典调色盘工厂（`BuiltInPresets`），提供纯 JSON 序列化与校验，扩展 `SessionNode.TerminalProfileId` 与 SQLite 仓储读写。
2. `Kei.Term.App` 建立 `ProfileManagerService` 管理全局及会话级 Profile 加载与持久化；提供 `SystemFontScanner` 动态枚举系统字体；重写画刷动态解析注入器。
3. `MainWindow` 升级标签栏布局（支持 Top/Bottom `Grid.Row` 动态重排）、左侧状态指示竖条（基于 `ConnectionState` 渲染动态画刷）及水平拖拽重排；系统设置页新增调色盘面板、字体微调预览面板及 JSON 导入导出操作。

**Tech Stack:** .NET 10 (C# 13), Avalonia 12.1.2, CommunityToolkit.Mvvm 8.4.2, Microsoft.Data.Sqlite 10.0.12, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-24-profile-system-and-appearance-design.md`

## Global Constraints

- `Kei.Term.Core` 严禁引入任何 UI（Avalonia）或平台特定依赖。
- 不执行任何 `git commit`（仓库处于零提交状态，改动保留在工作树）。
- 所有新增核心领域模型与算法在 `tests/Kei.Term.Tests` 中编写单测并全量通过。
- 状态枚举领域概念统一采用 `ConnectionState`，彻底避免 `VMState` 缩写。
- 新增所有用户可见文本均进入 `src/Kei.Term.App/Resources/Strings.resx`，严禁硬编码。

## Review Focus

- 损坏或缺失字段的 Profile JSON 导入时能平滑回退安全默认值（Fallback Defaults），不引发反序列化异常。
- 切换不存在或被删除的自定义 Profile ID 时，自动优雅回退内置默认（Built-in Default）。
- 标签栏上下位置切换时，保持终端会话实例及输入焦点完好，不发生控件重建。
- 标签拖拽在边界索引（首项/末项/自拖拽）时不错位且保持激活状态。
- 系统字体扫描器在无任何等宽字体的极端环境下仍能安全列出默认回退字体。

---

### Task 1: Core Profile 数据模型、内置预置工厂与测试

**Files:**
- Create: `src/Kei.Term.Core/Models/Profiles/GuiProfile.cs`
- Create: `src/Kei.Term.Core/Models/Profiles/TerminalProfile.cs`
- Create: `src/Kei.Term.Core/Models/Profiles/TabBarSettings.cs`
- Create: `src/Kei.Term.Core/Models/Profiles/BuiltInPresets.cs`
- Create: `tests/Kei.Term.Tests/ProfileModelTests.cs`

**Interfaces:**
- Produces:
  - `GuiProfile`, `TerminalProfile`, `TabBarSettings`, `TabPlacement`
  - `BuiltInPresets.DefaultGuiProfiles`: VS Code Dark, One Dark, Nord, Solarized Dark
  - `BuiltInPresets.DefaultTerminalProfiles`: Monokai, Dracula, Gruvbox, Solarized Dark/Light, Tomorrow Night
  - `BuiltInPresets.GetDefaultGuiProfile()`, `BuiltInPresets.GetDefaultTerminalProfile()`

- [ ] **Step 1: 编写单测验证模型默认兜底值与内置预置合法性**
- [ ] **Step 2: 运行测试验证失败**
- [ ] **Step 3: 编写 Core 层 Profile 模型与 BuiltInPresets 静态工厂**
- [ ] **Step 4: 运行测试确保全部通过**

---

### Task 2: SessionNode 扩展与 SQLite 仓储持久化

**Files:**
- Modify: `src/Kei.Term.Core/Models/TreeNodes.cs`
- Modify: `src/Kei.Term.Infrastructure/Storage/SqliteTreeRepository.cs`
- Test: `tests/Kei.Term.Tests/SqliteProfilePersistenceTests.cs`

**Interfaces:**
- Consumes: Task 1 中的 Profile 模型
- Produces:
  - `SessionNode.TerminalProfileId` 属性持久化到 `session_details.terminal_profile_id`

- [ ] **Step 1: 编写 SessionNode 包含 TerminalProfileId 的 SQLite 持久化测试**
- [ ] **Step 2: 运行测试验证失败**
- [ ] **Step 3: 更新 SqliteTreeRepository 的表建构与读写逻辑**
- [ ] **Step 4: 运行测试确保通过**

---

### Task 3: ProfileManagerService 与 JSON 导入导出

**Files:**
- Create: `src/Kei.Term.Core/Services/ProfileBundleSerializer.cs`
- Create: `src/Kei.Term.App/Services/ProfileManagerService.cs`
- Test: `tests/Kei.Term.Tests/ProfileBundleSerializerTests.cs`

**Interfaces:**
- Consumes: Task 1 模型
- Produces:
  - `ProfileBundleSerializer.Serialize(...)` & `Deserialize(...)`
  - `ProfileManagerService.ActiveGuiProfile`, `ProfileManagerService.DefaultTerminalProfile`
  - `ProfileManagerService.ApplyGuiProfile(GuiProfile)`

- [ ] **Step 1: 编写 JSON Bundle 序列化与缺失字段兜底测试**
- [ ] **Step 2: 运行测试验证失败**
- [ ] **Step 3: 实现 Core 序列化器与 App ProfileManagerService**
- [ ] **Step 4: 运行测试确保通过**

---

### Task 4: 系统字体动态扫描器 (SystemFontScanner) 与实时预览

**Files:**
- Create: `src/Kei.Term.App/Services/SystemFontScanner.cs`
- Modify: `src/Kei.Term.App/ViewModels/Settings/AppearanceSettingsPage.cs`
- Modify: `src/Kei.Term.App/Views/SettingsWindow.axaml`

**Interfaces:**
- Consumes: Avalonia `FontManager.Current.SystemFontFamilies`
- Produces:
  - `SystemFontScanner.GetInstalledFonts()`
  - 下拉框绑定字体族、字重选择、斜体切换与实时预览文本块

- [ ] **Step 1: 编写 SystemFontScanner 获取系统字体并置顶等宽字体的逻辑**
- [ ] **Step 2: 将扫描器接入 AppearanceSettingsPage，淘汰手输 TextBox**
- [ ] **Step 3: 在 SettingsWindow.axaml 呈现字体选择器与预览组件**
- [ ] **Step 4: 验证项目构建与界面绑定有效性**

---

### Task 5: 标签栏重构：SecureCRT 状态指示竖条与 Konsole 置顶/置底

**Files:**
- Modify: `src/Kei.Term.App/ViewModels/TerminalTabViewModel.cs` (升级 `ConnectionState`)
- Modify: `src/Kei.Term.App/Converters/TabStatusBrushConverter.cs` (改名或适配 `ConnectionStateBrushConverter`)
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml`
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml.cs`

**Interfaces:**
- Consumes: `TabBarSettings.Placement`, `ConnectionState`
- Produces:
  - 标签左侧 3px 宽高亮指示竖条（替代 8px 小圆点）
  - 动态切换标签栏 Grid.Row (Top / Bottom)

- [ ] **Step 1: 重构状态枚举为 ConnectionState 并更新各处引用**
- [ ] **Step 2: 重构 TabItem 模板，以左侧 Border 竖条替代圆点**
- [ ] **Step 3: 在 MainWindow 接入 TabPlacement 响应逻辑**
- [ ] **Step 4: 运行全量测试确保 0 破坏**

---

### Task 6: 标签栏水平拖拽重排 (Tab Reordering)

**Files:**
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml`
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml.cs`
- Modify: `src/Kei.Term.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: Task 5 的 TabItem 容器
- Produces:
  - 标签拖拽事件监听（PointerPressed -> 6px 阈值 -> DoDragDropAsync）
  - 目标项 DragOver/Drop 触发 `Tabs.Move(oldIdx, newIdx)`

- [ ] **Step 1: 在 MainWindow.axaml.cs 添加标签拖拽逻辑与阈值检测**
- [ ] **Step 2: 在 MainViewModel 添加 `MoveTab(int fromIndex, int toIndex)`**
- [ ] **Step 3: 验证拖拽重排与保持激活标签状态**

---

### Task 7: 工具栏图标统一栅格化规范与 JSON 导入导出界面集成

**Files:**
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml` (图标 StreamGeometry 资源规范化)
- Modify: `src/Kei.Term.App/Views/SettingsWindow.axaml` (导入导出按钮与对话框)
- Modify: `src/Kei.Term.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/Kei.Term.App/Resources/Strings.resx`

**Interfaces:**
- Consumes: Task 3 的导入导出接口
- Produces:
  - 规整一致的 16x16 / 20x20 工具栏图标
  - 设置界面一键导出/导入 Profile JSON

- [ ] **Step 1: 重构统一 MainWindow.axaml 的 StreamGeometry 图标栅格**
- [ ] **Step 2: 在设置界面添加“导出配置”和“导入配置”命令与本地化文案**
- [ ] **Step 3: 全量构建并运行单元测试**

---

### Task 8: 全分支回归审查与多平台视觉校验

- [ ] 运行 `dotnet build Kei.Term.slnx`（确保 0 警告 0 错误）
- [ ] 运行全量单元测试（确保全部通过，且新增测试无回归）
- [ ] 检查所有 UI 资源动态解析链条，确保无硬编码色值残留
