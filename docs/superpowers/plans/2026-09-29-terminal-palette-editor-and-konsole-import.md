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

> **⚠️ 历史记录边界（R1）**：以下 `Task 1` ~ `Task 5` 为 R1 历史计划，**仅作记录保留，不是本轮执行依据**。
> 其复选框状态**不代表实际完成情况**，本轮不据此勾选也不回填。
> 其中已被规格 R2 取代/作废的内容包括：`Themes/KeiBrush-KeiClassic.axaml` 路径、导入入口放「文件/工具」菜单、调色窗口 900×620、Demo 用 XAML 模拟等。
> 唯一权威执行计划见文末 **「R2 执行计划（权威）」**。

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

---

# R2 执行计划（权威）

> **状态：已获用户批准并实施，自动化回归完成；GUI 实测限制见文末交付记录。**
> **规格来源**：`docs/superpowers/specs/2026-09-29-terminal-palette-editor-and-konsole-import-design.md`（R2，已获用户书面批准）。
> 本章为唯一权威执行依据；文首 R1 块仅供历史追溯。

**Goal:** 让终端配色真正生效（真实终端 + 共用真实预览），完成 Konsole 导入的菜单/设置双入口、字体与配色解耦、设置事务回滚，并把设计系统资源目录更名为 `DesignSystem/`。

**Architecture:** Core 只做纯数据（解析、深拷贝、配置解析顺序补充）；App 层新增单一 `TerminalThemeAdapter` 把 `TerminalProfile` 映射为 `TerminalTheme`，真实终端与共用预览走同一条适配路径；`ProfileManagerService` 提供唯一变更广播，`MainViewModel` 单点订阅并统一管理标签刷新；设计系统迁移只移动文件与引用，不改行为。

**Tech Stack:** .NET 10 (C# 13), Avalonia 12.1.2, CommunityToolkit.Mvvm, RoyalApps.RoyalTerminal.Avalonia 0.5.2, xUnit 2.9.3。

## 执行偏好与角色（沿用）

- **主会话**负责协调推进、整合与最终验证；**UI 设计/布局/视觉资产**归 `@designer`；**非 UI 逻辑与引擎适配**归 `@fixer`。文中 “Native”（平台/引擎集成）指这类适配代码，**不等于主会话自己写 UI**。
- 任务高内聚，合并相近步骤；不做几十个碎任务；不在每步跑全量测试。
- 针对性失败/通过证据按包保留；最终只集中跑一次全量 build + test。
- **测试环境事实**：`tests/Kei.Term.Tests` 引用 App 工程但**未配置 Avalonia.Headless**（无该包），且本轮不新增纯验证依赖。因此控件级、DPI、预览 attach/detach 等证据为**人工检查或明确报告阻塞**，不得声称自动化通过。

## 依赖关系

```
Package A（颜色规范 + 适配器）
        ├──> Package B（App 状态 / 生效范围 / 设置事务）
        └──> Package C（UI 呈现：预览 / 布局 / 入口）   [B 提供 VM 契约]
Package D（Themes → DesignSystem 迁移）可与 A/B 并行；C 等 B/D 完成交接后开始
A + B + C + D ──> Package E（最终验证，主会话）
```

---

## Package A: 颜色规范与终端主题适配（owner: fixer）

**目标**：统一 `#RRGGBB` / `#AARRGGBB` 语义，新增单一适配器与字体快照，保证旧字体字段完整保留。

**Files**
- Modify: `src/Kei.Term.Core/Services/KonsoleColorSchemeParser.cs`
- Modify: `src/Kei.Term.Core/Models/Profiles/TerminalProfile.cs`（新增 `DeepCopy()`，不改动既有字段）
- Create: `src/Kei.Term.App/Services/TerminalThemeAdapter.cs`
- Create: `src/Kei.Term.App/Models/TerminalFontSnapshot.cs`
- Test: `tests/Kei.Term.Tests/KonsoleColorSchemeParserTests.cs`、`tests/Kei.Term.Tests/ProfileModelTests.cs`、新建 `tests/Kei.Term.Tests/TerminalThemeAdapterTests.cs`

**接口签名（实现必须精确一致）**

```csharp
// src/Kei.Term.App/Services/TerminalThemeAdapter.cs
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal.Theming;
using Kei.Term.Core.Models.Profiles;

namespace Kei.Term.App.Services;

public static class TerminalThemeAdapter
{
    // 仅接受 #RRGGBB 与 #AARRGGBB；其他长度一律 false，不猜测字节顺序
    public static bool TryParseArgb(string? hex, out uint argb);

    // 6 位补 0xFF alpha；8 位按 #AARRGGBB 直读
    public static uint ToArgb(string hex);

    public static TerminalTheme ToRoyalTheme(TerminalProfile profile);

    // ApplyTheme + InvalidateTerminal；不在此启动任何会话
    public static void Apply(TerminalControl terminal, TerminalProfile profile);
}
```

```csharp
// ToRoyalTheme 关键实现（Base16 顺序 = ANSI 0..15；uint 为 ARGB）
var base16 = new uint[16];
for (int i = 0; i < 16; i++)
{
    base16[i] = ToArgb(profile.AnsiColors[i]);
}

TerminalTheme theme = TerminalTheme.FromBase16(
    base16,
    ToArgb(profile.Foreground),
    ToArgb(profile.Background),
    cursorColor: ToArgb(profile.CursorColor),
    TerminalPaletteGenerationMode.Canonical, // 保持 Canonical 扩展色，不承诺 truecolor 随 16 色变
    TerminalOscColorReportFormat.Bit16,
    selectionForeground: null,
    selectionBackground: ToArgb(profile.SelectionBackground),
    boldColor: null,
    cursorTextColor: null);

terminal.ApplyTheme(theme);
terminal.InvalidateTerminal();
```

```csharp
// src/Kei.Term.App/Models/TerminalFontSnapshot.cs
namespace Kei.Term.App.Models;

// 纯值传递快照：仅表达“当前生效/当前草稿”的字体参数，不是第二套持久化配置
public sealed record TerminalFontSnapshot(
    string FontFamily,
    IReadOnlyList<string> FallbackFonts,
    double FontSize,
    bool IsItalic,
    bool CursorBlink);
```

```csharp
// TerminalProfile.DeepCopy()：逐字段保留，尤其 IsItalic 不可漏
public TerminalProfile DeepCopy() => new()
{
    Id = Id,
    Name = Name,
    IsBuiltIn = IsBuiltIn,
    Foreground = Foreground,
    Background = Background,
    CursorColor = CursorColor,
    SelectionBackground = SelectionBackground,
    AnsiColors = (string[])AnsiColors.Clone(),
    FontFamily = FontFamily,
    FontSize = FontSize,
    FontWeight = FontWeight,
    IsItalic = IsItalic,
    LineHeight = LineHeight,
    CursorBlink = CursorBlink
};
```

**Parser 修正**：`KonsoleColorSchemeParser.cs:105` 的 `AnsiColors[4] + "50"` 改为生成 `#50RRGGBB`（内部 8 位统一为 `#AARRGGBB`）。对**既有用户 8 位字符串不得猜测并重排**，仅按 `#AARRGGBB` 读取。

**Steps**

1. 先加断言（预期失败）到 `KonsoleColorSchemeParserTests`：
   ```csharp
   // Color4 = 36,114,200 → 蓝色槽位
   Assert.Equal("#502472C8", profile.SelectionBackground);

   // alpha 与 RGB 独立生效，防止 #RRGGBBAA 误读
   Assert.True(TerminalThemeAdapter.TryParseArgb(profile.SelectionBackground, out var argb));
   Assert.Equal(0x50u, argb >> 24);
   Assert.Equal(0x2472C8u, argb & 0x00FFFFFFu);

   // 6 位与 8 位一致
   Assert.Equal(0xFF1E1E1Eu, TerminalThemeAdapter.ToArgb("#1E1E1E"));
   Assert.Equal(TerminalThemeAdapter.ToArgb("#1E1E1E"), TerminalThemeAdapter.ToArgb("#FF1E1E1E"));

   // 非法输入不猜测
   Assert.False(TerminalThemeAdapter.TryParseArgb("#12345", out _));
   ```
2. 运行针对性（仅已存在测试，预期失败）：
   `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~KonsoleColorSchemeParserTests"`
3. 实现：parser 选区修正、`TerminalProfile.DeepCopy()`、`TerminalFontSnapshot`、`TerminalThemeAdapter`。
4. 补 `ProfileModelTests`：`TerminalProfileDeepCopy_PreservesLegacyFontFields`，断言含 `FontWeight="SemiBold"`、`IsItalic=true`、`FontSize=13.5`、`LineHeight=1.35`、`CursorBlink=false`，且 `AnsiColors` 为不同数组实例（改副本不影响原对象）。
5. 新建 `TerminalThemeAdapterTests`：Base16 顺序（`AnsiColors[0]` → `base16[0]`）、`Canonical` 模式、`FromBase16` 可构造不抛异常。**反射只证明成员存在**，本步证明可编译可运行。
6. 重跑针对性（预期通过）：
   `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~KonsoleColorSchemeParserTests|FullyQualifiedName~TerminalThemeAdapterTests|FullyQualifiedName~ProfileModelTests"`

**能力限制（必须向主会话报告，不得粉饰）**
- `TerminalControl` 仅公开 `FontFamilyName`（单一族名）与 `TerminalFontSize`，**未发现公开 fallback 链 API**；不得声称回退字体列表已作用于真实终端。快照中的 `FallbackFonts` 仅作值传递，供后续在确认渲染链支持方式后使用。
- 适配器**不读取也不写入** `TerminalProfile` 的 `FontFamily/FontSize/FontWeight/IsItalic/LineHeight/CursorBlink`。
- `TerminalTheme.FromBase16` 的 ARGB 语义、`ApplyTheme` 热重绘、alpha 混合、`WriteOutput` 无 endpoint 实际写入行为，反射不足证明；由本包 step 5 与 Package E 的本地运行复核，**不得对外声称已全部测试**。

**Verification owner:** fixer（针对性单测 + 本地 API 最小验证）。

---

## Package B: App 状态、生效范围与设置事务（owner: fixer）

**目标**：建立统一解析顺序、单点广播、标签生命周期订阅、设置草稿与回滚事务。

**Files**
- Modify: `src/Kei.Term.Core/Models/TreeNodes.cs`（`ResolvedSessionConfig` 增可选字段）
- Modify: `src/Kei.Term.Core/Services/SessionConfigBuilder.cs`
- Modify: `src/Kei.Term.App/Services/ProfileManagerService.cs`
- Modify: `src/Kei.Term.App/ViewModels/TerminalTabViewModel.cs`
- Modify: `src/Kei.Term.App/ViewModels/MainViewModel.cs`
- Modify: `src/Kei.Term.App/ViewModels/Settings/AppearanceSettingsPage.cs`
- Modify: `src/Kei.Term.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml.cs`、`src/Kei.Term.App/Views/SettingsWindow.axaml.cs`
- Test: 新建 `tests/Kei.Term.Tests/TerminalProfileApplicationTests.cs`

**接口签名**

```csharp
// ResolvedSessionConfig 末尾追加可选参数，保持既有构造点兼容
string? TerminalProfileId = null

// SessionConfigBuilder.Build(...) 内补：
TerminalProfileId: node.TerminalProfileId
```

```csharp
// ProfileManagerService：唯一广播源（区分“默认选择变化”与“同 ID 内容编辑”）
public enum TerminalProfileChangeReason
{
    DefaultSelectionChanged,
    ProfileContentEdited
}

public readonly record struct TerminalProfileChange(string? ProfileId, TerminalProfileChangeReason Reason);

public event Action<TerminalProfileChange>? TerminalProfileChanged;

// 解析顺序：显式 ID → 全局默认 ID → 内置默认
public TerminalProfile ResolveEffectiveTerminalProfile(string? explicitProfileId);

// 新增/覆盖同 ID 方案后广播 ProfileContentEdited(profile.Id)
public void AddOrUpdateCustomTerminalProfile(TerminalProfile profile);

// 默认选择变化后广播 DefaultSelectionChanged（profileId 可为新默认 ID）
public void NotifyDefaultProfileSelectionChanged(string? profileId);
```

```csharp
// TerminalTabViewModel：新增构造重载（旧 4 参重载保留，避免破坏现有调用点）
public TerminalTabViewModel(
    string title,
    TerminalFontSnapshot font,
    TerminalProfile profile,
    string? explicitProfileId,
    ILogger? logger = null);

public string? ExplicitProfileId { get; }
public string EffectiveProfileId { get; private set; } // 初始 = profile.Id

// 当前生效方案（供刷新断言与调试；随 ApplyTerminalProfile 更新）
public TerminalProfile CurrentProfile { get; private set; }

public void ApplyTerminalProfile(TerminalProfile profile);   // EffectiveProfileId 更新 + TerminalThemeAdapter.Apply
public void ApplyFontSnapshot(TerminalFontSnapshot snapshot); // 只设 FontFamilyName / TerminalFontSize
```

```csharp
// MainViewModel：唯一订阅点与刷新规则
// 构造末尾：_profileManager?.TerminalProfileChanged += OnTerminalProfileChanged;
// DisposeAsync：_profileManager?.TerminalProfileChanged -= OnTerminalProfileChanged;
private void OnTerminalProfileChanged(TerminalProfileChange change)
{
    // DefaultSelectionChanged → 刷新所有 ExplicitProfileId 为空的标签
    //    新默认取自 _profileManager.ResolveEffectiveTerminalProfile(null)
    // ProfileContentEdited  → 刷新 EffectiveProfileId == change.ProfileId 的标签（继承的与被显式指定的都刷新）
}
```
**不得**为每个 tag 挂 `ProfileManagerService` 事件；标签关闭仍走既有 `CloseTabCommand`（`CloseTabRequested` 解绑 + `Tabs.Remove` + 后台 `DisposeAsync`）。

```csharp
// AppearanceSettingsPage：选择即草稿预览（不落盘、不改 ActiveTerminalProfileId）
private Action<TerminalProfile?>? _draftPreviewHandler;

public void SetDraftPreviewHandler(Action<TerminalProfile?> handler) => _draftPreviewHandler = handler;

partial void OnSelectedTerminalProfileChanged(TerminalProfile? value)
{
    _draftPreviewHandler?.Invoke(value);
}
```

```csharp
// SettingsViewModel：只有持久化成功才更新“最后成功应用”基准
private AppSettings _lastAppliedSettings = new();
private string? _lastAppliedTerminalProfileId;
private readonly Dictionary<string, TerminalProfile> _lastAppliedProfileContent = new(StringComparer.OrdinalIgnoreCase);

private void CommitAppliedBaseline(AppSettings applied, TerminalProfile? selected); // 深拷贝方案内容
```

**事务与生效规则（与规格 §3.5 一一对应）**
1. 子编辑器确认后，把方案**深拷贝**并入父设置草稿（可预览），**不提前永久存盘**。
   设置选择回调必须立即更新管理器的有效预览状态并广播；同 ID 草稿确认也立即广播内容变更。真实标签及预览期间新标签从该有效状态解析配色，不能等到 Apply 才首次刷新。已提交状态与预览状态分别保存，取消恢复 ID、方案内容及新增草稿集合。
2. `ApplyChangesAsync` 写入设置与 `profiles.json` **成功后**才 `CommitAppliedBaseline` 并广播 `DefaultSelectionChanged` 与受影响的 `ProfileContentEdited`。
   两份存储部分成功不得推进基准或报成功；计划实施时覆盖第二次写入失败的恢复路径，保留原提交快照用于补偿，并让恢复失败也有明确提示。
3. 持久化异常：`ShowNotificationAsync` 可见提示，**不更新基准**，不假装成功。
4. 设置取消：回滚到 `_lastApplied*`（ID + 方案内容 + 移除本轮新增草稿方案），随后广播刷新；**预览期间新建的继承标签**同样被回滚刷新覆盖。
5. 同 ID 内容编辑：即使 ID 不变也要广播 `ProfileContentEdited(id)`；取消须恢复受影响标签。
6. 主菜单独立导入：确认后才 `AddOrUpdateCustomTerminalProfile` + 保存；失败可见提示且不改基准；取消无副作用。

**测试断言（`TerminalProfileApplicationTests.cs`，纯 VM/Core，不构造控件、不依赖 UI 线程）**
```csharp
// 1) 新旧标签 / 显式覆盖
// 3 个 tab（2 继承 + 1 explicit="Custom-X"）→ 默认切换 → 仅 2 个继承的 EffectiveProfileId 变化，显式不变
Assert.Equal("Custom-X", explicitTab.EffectiveProfileId);
Assert.Equal(newDefaultId, inheritingTab.EffectiveProfileId);

// 2) 同 ID 编辑（不换 ID 也刷新）
// 编辑 ID=Custom-X 的内容 → 使用该 ID 的继承标签与显式标签的 CurrentProfile 都刷新
Assert.Equal(editedForeground, explicitTab.CurrentProfile.Foreground);
Assert.Equal(editedForeground, inheritingTab.CurrentProfile.Foreground);

// 3) 解析顺序
Assert.Equal("Custom-X", mgr.ResolveEffectiveTerminalProfile("Custom-X").Id);
Assert.Equal(defaultId,    mgr.ResolveEffectiveTerminalProfile("不存在").Id);
Assert.Equal(BuiltInPresets.GetDefaultTerminalProfile().Id, mgr.ResolveEffectiveTerminalProfile(null).Id); // 无自定义默认时

// 4) apply 后 cancel / 预览期新建标签
// 应用 A → 应用 B → Cancel → 回到 B；期间新建的继承标签亦为 B
// 5) 草稿不污染内置
Assert.NotEqual(builtInBefore.Id, updated.Id);            // 内置编辑产生副本
Assert.Equal(builtInBefore.Background, builtInAfter.Background); // 内置实例未被改写
// 6) 字体字段保留（含 IsItalic）
Assert.Equal(before.FontFamily, after.FontFamily);
Assert.Equal(before.IsItalic, after.IsItalic);
Assert.Equal(before.FontWeight, after.FontWeight);
// 7) 存盘失败
// 注入 SaveSettingsAsync 抛异常的 ISettingsService：baseline 未变、ID 未变、ShowNotificationAsync 被调用
// 8) 订阅释放
// DisposeAsync 后触发 TerminalProfileChanged，刷新次数不再增加
```

**Verification owner:** fixer（逻辑单测；不声称控件级已验证）。

---

## Package C: UI 呈现（owner: designer）

**目标**：共用真实预览控件、尺寸/布局达标、入口迁移、字体草稿实时预览。**不擅自改视觉意图。**

**Files**
- Create: `src/Kei.Term.App/Views/Controls/TerminalShellPreviewView.axaml` / `.axaml.cs`
- Modify: `src/Kei.Term.App/Views/SettingsWindow.axaml`
- Modify: `src/Kei.Term.App/Views/TerminalProfileEditWindow.axaml`
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml`
 - Modify: `src/Kei.Term.App/Views/MainWindow.axaml.cs`、`src/Kei.Term.App/Views/SettingsWindow.axaml.cs`（B 交接完成后接入最终预览控件；不得与 B 同时写这些文件）
- Modify: `src/Kei.Term.App/ViewModels/TerminalProfileEditViewModel.cs`
- Modify: `src/Kei.Term.App/ViewModels/Settings/AppearanceSettingsPage.cs`（仅预览/草稿属性）
- Modify: `src/Kei.Term.App/Resources/Strings.resx`

**共用预览控件契约**
```csharp
// 内嵌真实 TerminalControl，通过 WriteOutput 写固定安全 ANSI 样本
public partial class TerminalShellPreviewView : UserControl
{
    public static readonly StyledProperty<TerminalProfile?> ProfileProperty;
    public static readonly StyledProperty<TerminalFontSnapshot?> FontProperty;
    // Loaded:   ApplyTheme(Profile) + 设 FontFamilyName/TerminalFontSize + WriteOutput(固定样本)
    // Unloaded / detach: 解绑事件并释放内嵌 TerminalControl，避免泄漏
    // 不接收、不转发用户输入；不启动 SSH / PTY / 外部 shell
}
```
- 先做**本地可行性验证**：若 `WriteOutput` 在该控件下不可用、或 attach/detach 无法安全释放，**必须显式报告限制并交主会话确认**，**不得静默退化为误导性的 XAML 模拟**。
- 设置页用该控件**替换原单行字体预览**（`PreviewSampleText`/`PreviewFontFamily` 的 `TextBlock`）；独立调色弹窗保留 Demo 但改用同一控件。
- 字体草稿：`AppearanceSettingsPage` **新增** `DraftFontSnapshot`（由 FontFamily / FontSize / IsItalic / CursorBlink / 回退列表组装），改字体立即刷新预览；设置页打开调色弹窗时把同一快照传入弹窗 VM。
- 主菜单独立导入使用**已应用**的全局字体设置。
- 注意：`AppSettings` 现有字段为主字体、字号、`CursorBlink`、`TerminalFallbackFontFamily`；`IsItalic` 目前仅存在于 `AppearanceSettingsPage`（未持久化），因此快照是**值传递**，**不得编造已有设置字段**。

**布局（规格 §3.3）**
- `SettingsWindow.axaml` 与 `TerminalProfileEditWindow.axaml`：`Width="840" Height="620" MinWidth="760" MinHeight="520"`。
- 终端配色下拉框独占一行并 `HorizontalAlignment="Stretch"`（去掉固定 `MinWidth=180` 挤占）；操作按钮移到下一行用 `WrapPanel`（或等价可换行布局）。
- 不得只靠放大窗口解决裁切；默认/最小尺寸与缩放范围内均可操作。

**菜单与资源（规格 §3.4）**
- `MainWindow.axaml`：在 `Menu.View`（115 行区）之后、`Menu.Tools`（124 行区）之前插入 `Menu.Appearance` → `Menu.Appearance.Import` → `Menu.Appearance.Import.Konsole`。
- `Menu.File.ImportKonsole` 迁出「文件」菜单；`Menu.File.ImportSecureCrt` 保持不动。
- `Strings.resx` 增加 `Menu.Appearance`、`Menu.Appearance.Import`、`Menu.Appearance.Import.Konsole`。**当前仓库仅有 `Strings.resx` 一个资源文件**，按实际存在语言处理，**不得声称中英文双份已实现**。

**验证 owner:** designer——默认 840×620、最小 760×520、缩放与 DPI 的人工检查；**不得**用“逻辑像素宽 ÷ 1.5”作为 150% DPI 证据，也不得在无 GUI 支持环境下声称通过（明确人工检查或报告阻塞）。

---

## Package D: 设计系统目录迁移 Themes → DesignSystem（owner: fixer）

**目标**：完成已批准的命名迁移，行为零变化。可与 A/B 并行；视图中的路径注释和 Demo 文案由 C 在交接后统一更新，D 不写 C 的视图文件。

**Files**
- Move: `src/Kei.Term.App/Themes/KeiTokens.axaml` → `src/Kei.Term.App/DesignSystem/KeiTokens.axaml`
- Move: `src/Kei.Term.App/Themes/KeiControls.axaml` → `src/Kei.Term.App/DesignSystem/KeiControls.axaml`
- Modify: `src/Kei.Term.App/Services/UiDesignSystemService.cs`（`LoadDictionary` / `LoadStyles` 的 URI → `avares://Kei.Term.App/DesignSystem/{fileName}`）
- Handoff to C: `src/Kei.Term.App/Views/MainWindow.axaml` 的路径注释与 `TerminalProfileEditWindow.axaml` 的 Demo 路径文案，D 仅报告位置，不修改。
- Modify: `tests/Kei.Term.Tests/Contracts/TreeThemeContractTests.cs`（路径拼接 `Themes` → `DesignSystem`，测试名同步）
- Modify: 当前维护文档中的路径引用（如 `README.md`、`AGENTS.md` 等实际存在且被更新时）

**约束**：`Kei.Tree.Line` / `Kei.Tree.Indent`、共用 TreeView 模板与资源键**不得改变**；只移动文件与更新引用，不做重构、不改视觉。

**Steps**
1. 迁移文件与 URI。
2. 更新契约测试路径与全部残留引用：`grep -rn "Themes/Kei\|Themes\\\\Kei" src tests`（排除 R1 历史块与规格说明）应为空。
3. 针对性运行：`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter "FullyQualifiedName~TreeThemeContractTests"` → 通过。

**Verification owner:** fixer（契约测试 + 引用 grep）。

---

## Package E: 最终验证（owner: 主会话）

依赖 A–D 完成后**集中一次**执行，不反复跑全量：
```bash
dotnet build Kei.Term.slnx
dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj
```
必须逐条核对并标明证据来源（自动化 / 人工 / 阻塞）：

| 验收项 | 证据来源 |
| --- | --- |
| 新旧标签、显式覆盖、同 ID 编辑、取消、apply 后 cancel、存盘失败、订阅释放、草稿不污染、字体字段保留 | Package B 单测 + 全量回归 |
| alpha 实际通道、6/8 位一致性、Base16 顺序 | Package A 单测 |
| 真实 Demo 与真终端一致 | 人工对照（二者必须走同一 `TerminalThemeAdapter`） |
| 菜单位置、配色分区、默认/最小尺寸与缩放无裁切 | designer 人工检查 + 可视复核 |
| 设计系统目录契约、`Kei.Tree.*` 未破坏 | Package D 契约测试 |
| 256 色 / truecolor 行为 | 标注待实测，不预先断言 |
| fallback 字体链 | 记录限制，主会话确认，不声称已生效 |

- 最后做一次独立 review（高影响任务，必要时交 `oracle`）。
- 布局/DPI 若环境不支持 GUI：明确人工检查或**报告阻塞**，不声称通过。
- 不新增纯验证依赖。

## 覆盖矩阵（规格验收 → 包）

| 规格 §6 验收项 | 负责包 |
| --- | --- |
| 菜单正确 / 配色分区一致 | C |
| 默认 840×620、最小 760×520 无裁切 | C（E 复核） |
| 共用 Demo 读真实字体与颜色、无 SSH/外部命令 | C（A 适配） |
| 继承标签实时刷新 / 新标签读有效方案 | B |
| 显式 `TerminalProfileId` 覆盖 | B |
| 同 ID 编辑刷新 | B |
| 取消回滚 / 应用更新基准 | B |
| 弹窗草稿隔离 / 取消不污染共享对象 | B（editor 改动在 C） |
| 内置保护 | B |
| 导入取消、名称自动填充可编辑 | B + C |
| 字体不被覆盖、旧字段保留（含 IsItalic） | A + B |
| alpha 通道实际有效 | A |
| 持久化失败提示 | B |
| `Kei.Tree.Line` / `Indent` 与树模板未破坏 | C + D |
| 设计系统迁 `DesignSystem/`、引用与契约同步 | D |

## 与规格的差异 / 能力限制（实现前须让主会话确认）

1. **fallback 字体链无公开 API**：仅确认主字体与字号（`FontFamilyName` / `TerminalFontSize`）。回退列表暂只作快照值传递；不承诺生效。需核对渲染链真实支持方式后再决定，**不得把项目原有未支持功能伪装为已完成**。
2. **无 Avalonia.Headless**：预览控件的 attach/detach/dispose、DPI、热重绘无自动化证据，只能本地运行人工验证或报告阻塞；本轮不加纯验证依赖。
3. **反射证据边界**：`ApplyTheme` / `InvalidateTerminal` / `WriteOutput` / `FromBase16` 的成员存在性已由反射确认，但**行为**（热重绘、alpha 混合、无 endpoint 写入）未被证明；由 Package A step 5 与 E 本地复核。
4. **256 色 / truecolor**：使用 `TerminalPaletteGenerationMode.Canonical`；不承诺 16–255 随 ANSI 变化，标注待实测。
5. **资源语言**：仓库仅有 `Strings.resx`，不承诺中英双语。
6. `Themes` → `DesignSystem` 迁移**已纳入本轮**（早前“仅记录不迁移”的说明作废）。

## 阶段清单

1. ✅ 规格修订完成（R2）。
2. ✅ 用户书面审阅已通过。
3. ✅ R2 实施计划已获批准并执行。
4. ✅ A–D 实现及集中修复完成；E 自动化回归：197 项测试通过，构建零警告、零错误，差异空白检查通过。
5. ⏳ GUI/DPI、实际混色及完整原生资源释放仍未实测；不据此宣称全部视觉验收通过。

## 最终交付记录

- 用户要求后续工作批量推进、一次性提交；最终采用分文件所有权并行实现、集中测试及整体审查，保留 R1 历史步骤而不伪造旧勾选。
- 审查中发现的事务隔离、取消恢复、存盘失败保护、关闭与错误提示接线已集中修正；残余 R1–R3 收口后完整测试为 197 通过、0 失败、0 跳过。
- 选区透明度通过公共 Renderer.SelectionColor 在统一适配器及 Loaded 时恢复，八位用户颜色不猜测重排；旧三位 RGB 保持兼容。
- 字体与配色独立，共享 Demo 使用同一主字体归一化和颜色适配路径；回退字体链、斜体等高级字体能力不作已生效承诺。
- 保留特性分支 `feat/terminal-appearance-r2`，仅本地提交，不自动合并或推送。
