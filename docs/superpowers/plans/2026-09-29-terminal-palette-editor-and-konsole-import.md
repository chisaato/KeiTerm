# 终端调色预览器、Konsole 配色导入与 BuiltIn 预置模块化实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 `BuiltInPresets.cs` 模块化拆分至独立的主题文件，实现 KDE Konsole `.colorscheme` 解析器，并构建集 SecureCRT 风格调色板与 Tabby 风格终端 Demo 实时预览于一体的独立编辑窗口，在设置外观和主菜单中完成导入与编辑工作流串联。

**Architecture:** 
- **Core 分层**：在 `Kei.Term.Core.Models.Profiles.BuiltIns` 命名空间下分别拆分 GUI 与 Terminal 各主题，`BuiltInPresets` 保持顶层聚合与常量 ID 向后兼容；在 `Kei.Term.Core.Services` 实现纯逻辑的 `KonsoleColorSchemeParser`。
- **App 呈现**：在 `Kei.Term.App` 构建 `TerminalProfileEditWindow` 与 `TerminalProfileEditViewModel`，通过数据绑定实现 ANSI 16 色与基础色的双向同步，并在右侧模拟真实的 Terminal 输出效果；通过 `ProfileManagerService` 串联外观设置页与主菜单。

**Tech Stack:** .NET 10 (C# 13), Avalonia 12.x, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-29-terminal-palette-editor-and-konsole-import-design.md`

## Global Constraints

- 严禁在 `Kei.Term.Core` 中引入 Avalonia 相关 UI 依赖。
- 不得破坏或变动 `src/Kei.Term.App/Themes/KeiBrush-KeiClassic.axaml` 中的 `Kei.Tree.Line` 与 `Kei.Tree.Indent` 设计令牌。
- 遵循 C# 13 语法规范，避免在多行赋值时堆叠使用 `var`，以行内注释为主。
- 任务步骤紧凑合并，所有单测与回归验证在最后集中执行。

## Review Focus

1. Konsole 文件缺少部分 Section（如无 `Color0Intense` 或无 `[Foreground]`）时的容错与 Fallback。
2. RGB 字符串包含多余空格（如 `Color = 35, 38, 39`）或超出 0-255 范围时的格式化健壮性。
3. 内置主题在编辑时自动识别为副本（不可直接覆盖内置预置），防止脏数据写坏预置 ID。
4. 调色板选色后，Demo 实时预览屏上文本与彩条对应画刷的即时动态联动。
5. 导入 Konsole 配色后直接呼起编辑窗口时，主题名称的自动提取与用户自定义修改能力。

---

### Task 1: Core - BuiltIn 预置模块化拆分

**Files:**
- Create:
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Gui/VsCodeDarkGuiPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Gui/OneDarkGuiPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Gui/NordGuiPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Gui/SolarizedDarkGuiPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Gui/VeritasHareGuiPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Gui/VeritasChihiroGuiPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Gui/VeritasMakiGuiPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Gui/VeritasKotamaGuiPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/MonokaiTerminalPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/DraculaTerminalPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/GruvboxTerminalPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/SolarizedDarkTerminalPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/SolarizedLightTerminalPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/TomorrowNightTerminalPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/VeritasHareTerminalPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/VeritasChihiroTerminalPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/VeritasMakiTerminalPreset.cs`
  - `src/Kei.Term.Core/Models/Profiles/BuiltIns/Terminal/VeritasKotamaTerminalPreset.cs`
- Modify: `src/Kei.Term.Core/Models/Profiles/BuiltInPresets.cs`

**Interfaces:**
- Produces: 保持 `BuiltInPresets.DefaultGuiProfiles`、`BuiltInPresets.DefaultTerminalProfiles` 与原常量 ID 签名完全不变。

- [ ] **Step 1: 建立独立预置类并重构 BuiltInPresets**
  - 分别创建各主题独立的预置类，暴露 `Instance` 静态属性。
  - 重写 `BuiltInPresets.cs`，将其常量 ID 指向各个具体静态类，`DefaultGuiProfiles` 与 `DefaultTerminalProfiles` 聚合各独立预置类实例。

---

### Task 2: Core - Konsole 配色方案解析器与单元测试

**Files:**
- Create: `src/Kei.Term.Core/Services/KonsoleColorSchemeParser.cs`
- Create: `tests/Kei.Term.Tests/KonsoleColorSchemeParserTests.cs`

**Interfaces:**
- Produces: `public static TerminalProfile Parse(string content, string? defaultName = null)`

- [ ] **Step 1: 编写 Konsole 配色解析单元测试**
  - 测试完整标准的 `.colorscheme` INI 内容解析。
  - 测试包含不规则空白、缺漏部分色块时的 Fallback 机制。
- [ ] **Step 2: 实现 KonsoleColorSchemeParser**
  - 逐行扫描 INI 节与键值对，提取 `[General]`、`[Background]`、`[Foreground]`、`[Color0..7]`、`[Color0..7Intense]`。
  - 将 `R,G,B` 转换为标准化 6 位十六进制色值 `#RRGGBB`。
  - 补齐默认光标色与选区高亮色。

---

### Task 3: App - 终端调色板与实时 Demo 预览窗口

**Files:**
- Create: `src/Kei.Term.App/ViewModels/TerminalProfileEditViewModel.cs`
- Create: `src/Kei.Term.App/Views/TerminalProfileEditWindow.axaml`
- Create: `src/Kei.Term.App/Views/TerminalProfileEditWindow.axaml.cs`

**Interfaces:**
- Produces:
  - `TerminalProfileEditViewModel` 暴露 `Name`、`Background`、`Foreground`、`CursorColor`、`SelectionBackground`、`AnsiColors`（16 元素集合）、`SelectedColorIndex` 及确定/取消命令。
  - `TerminalProfileEditWindow` 提供统一的编辑调色与 Demo 渲染面板。

- [ ] **Step 1: 实现 TerminalProfileEditViewModel**
  - 初始化时拷贝或新建 `TerminalProfile` 数据。
  - 提供 16 色与基础色值的选色/改色逻辑。
  - 提供保存结果 `ResultProfile`。
- [ ] **Step 2: 实现 TerminalProfileEditWindow XAML 布局**
  - 左侧：Profile 基本信息（名称）+ 基础色输入行 + 8 色 Normal / 8 色 Bold ANSI 色块矩阵与当前选中色修改控件。
  - 右侧：Terminal Demo 预览屏幕（背景色直绑 Background，展示 ANSI 16 色测试色阶条 + 带高亮色彩的模拟 Shell 目录列表与提示符）。
  - 底部：取消与确定按钮。

---

### Task 4: App - 设置外观与主菜单工作流串联

**Files:**
- Modify: `src/Kei.Term.App/ViewModels/Settings/AppearanceSettingsPage.cs`
- Modify: `src/Kei.Term.App/Views/SettingsWindow.axaml`
- Modify: `src/Kei.Term.App/ViewModels/MainViewModel.cs`
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml`
- Modify: `src/Kei.Term.App/Resources/Strings.resx`

- [ ] **Step 1: 本地化文本补充**
  - 增加关于“新建终端主题”、“编辑主题”、“从 Konsole 导入”、“Konsole 配色方案 (*.colorscheme)”等中英文描述。
- [ ] **Step 2: 外观设置页增设操作入口**
  - 在终端主题选项下方增设「新建」、「编辑」、「从 Konsole 导入」按钮。
  - 点击后唤起 `TerminalProfileEditWindow`，若用户保存则将其加入 `ProfileManagerService` 并激活。
- [ ] **Step 3: 主菜单增设导入入口**
  - 在「文件」或「工具」菜单增设导入入口，允许快速打开 `.colorscheme` 并弹窗预览。

---

### Task 5: 验证与回归测试

- [ ] **Step 1: 运行全量单元测试与编译**
  - 执行 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`。
  - 执行 `dotnet build Kei.Term.slnx` 确保 0 警告 0 错误。
