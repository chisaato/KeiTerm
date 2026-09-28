# 终端设置拆分与外观左右分栏实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 拆分设置中的终端仿真行为与终端外观为独立大类，重构终端外观为左右分栏布局以常驻展示实时 Demo，扩充 Demo 为包含中日韩完整 CJK 的文本样本，并在左下角规整外部配色导入区，放大窗口初始尺寸。

**Architecture:**
- `SettingsViewModel` 拆分独立的 `TerminalAppearanceSettingsPage`，并维护清晰的分类项定义；
- `SettingsWindow.axaml` 提升默认尺寸至 980×660，新增左右两栏网格（编辑与导入居左，常驻实时 `TerminalShellPreviewView` 居右）；
- `TerminalShellPreviewView.axaml.cs` 更新静态 ANSI 字节流，补充包含中日韩三种文字和 ASCII 混合的等宽样本；
- `MainWindow.axaml` 调整默认尺寸至 1280×820。

**Tech Stack:** C# 13, .NET 10, Avalonia 12.x, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-terminal-settings-split-and-split-view-design.md`

## Global Constraints

- 分层约束：`Kei.Term.Core` 严禁引入任何 Avalonia 依赖；全部 UI 调整位于 `Kei.Term.App`。
- 事务约束：保持 `SettingsWindow` 已有的 `IsBusy` 绑定与关闭事务防护，不得破坏草稿回滚与原子保存机制。
- 命名与代码：多用行内注释，遵守 C# 13 规范；测试用例全部纳入 `tests/Kei.Term.Tests`。

## Review Focus

1. 左右分栏在默认 980×660 与最小 880×580 尺寸下的拉伸行为（左侧固定/可滚动，右侧常驻拉伸不被挤出可视区）。
2. `TerminalShellPreviewView` 的 CJK 样本在切换不同主字体/字号时的换行与绘制稳定性。
3. `SettingsViewModel.Categories` 拆分后各分类页面的数据上下文与命令绑定（新建、调色、从 Konsole 导入）连通性。
4. 现有 197 个自动化回归用例不受分类拆分影响。

---

### Task 1: 扩充 CJK 实时 Demo 预览样本与窗口尺寸调整

**Files:**
- Modify: `src/Kei.Term.App/Views/Controls/TerminalShellPreviewView.axaml.cs`
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml`
- Modify: `src/Kei.Term.App/Views/SettingsWindow.axaml`
- Test: `tests/Kei.Term.Tests/TerminalProfileEditViewModelTests.cs`

- [ ] **Step 1: 在测试中编写关于 CJK 样本存在性的断言测试**
- [ ] **Step 2: 运行测试验证失败**
- [ ] **Step 3: 更新 `DemoAnsiSample` 字节流包含 ZH/JA/KO/EN 四语言样本，并调整窗口尺寸**
- [ ] **Step 4: 运行测试验证通过**

---

### Task 2: 拆分设置分类大类模型 (Terminal vs Terminal Appearance)

**Files:**
- Modify: `src/Kei.Term.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/Kei.Term.App/Resources/Strings.resx`
- Test: `tests/Kei.Term.Tests/TerminalProfileApplicationTests.cs`

- [ ] **Step 1: 在测试中编写验证 `SettingsViewModel.Categories` 包含「终端外观」独立类别的测试**
- [ ] **Step 2: 运行测试验证失败**
- [ ] **Step 3: 新增 `TerminalAppearanceSettingsPage`，并在 `Categories` 中完成拆分注入，添加本地化字符串**
- [ ] **Step 4: 运行测试验证通过**

---

### Task 3: 重构「终端外观」页面为左右分栏布局与左下导入区

**Files:**
- Modify: `src/Kei.Term.App/Views/SettingsWindow.axaml`
- Test: `tests/Kei.Term.Tests/TerminalProfileEditViewModelTests.cs`

- [ ] **Step 1: 编写 XAML 结构与布局契约测试（验证左右两栏定义、右侧常驻 `TerminalShellPreviewView`、`!IsBusy` 继承）**
- [ ] **Step 2: 运行测试验证失败**
- [ ] **Step 3: 实现 `TerminalAppearanceSettingsPage` 的左右两栏 DataTemplate，将导入外部配色放置于左栏底部**
- [ ] **Step 4: 运行测试验证通过**

---

### Task 4: 全量回归与编译验证

**Files:**
- Test: 全量单元测试

- [ ] **Step 1: 运行完整构建 `dotnet build Kei.Term.slnx` 确认 0 警告 0 错误**
- [ ] **Step 2: 运行全量单元测试 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj` 确认全部通过**
- [ ] **Step 3: 运行 `git diff --check` 确认代码格式规范**
