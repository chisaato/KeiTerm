# Specification: 终端调色预览器、Konsole 配色导入与 BuiltIn 预置模块化重构（R2 修订）

> 修订状态：**规格及计划已获批准，产品实现与自动化回归已完成**；实际 GUI、DPI 与像素级渲染检查仍待实测。
> 修订日期：2026-09-29。R2 依据用户新确认的补充设计，原位改正 R1 中与现状冲突的描述，并补充配色实际应用链路与验收项。

## 0. R2 修订摘要（相对 R1）

1. 导入入口位置改正：必须位于**顶层「外观 → 导入 → Konsole」**，不得放在「文件」菜单。
2. 明确**字体配置与终端配色独立**：配色导入/切换不得改动字体、字号、回退字体等。
3. 明确**共用同一 Demo 组件**：设置页用其替代原单行字体预览，独立调色弹窗仍保留 Demo。
4. 明确**配色实际应用链路当前缺失**，并给出静态诊断证据；不得将根因归为用户 `LSCOLORS`。
5. 明确 **Konsole 选区色的 alpha 通道**语义与解析/控件通道适配测试要求。
6. 明确**同一 ID 编辑的刷新语义、取消/应用回滚基准、草稿隔离、内置保护**。
7. 修正设计令牌与文件路径引用：不存在 `KeiBrush-KeiClassic.axaml`，正确位置为 `Themes/KeiTokens.axaml`、`Themes/KeiControls.axaml`、`Services/UiDesignSystemService.cs`。
8. 明确 **RoyalTerminal API 未验证**，实现前须按本地依赖确认，不得编造接口。

## 1. 概述与目标

为提升 KeiTerm 的外观可定制性与终端使用体验，本次改造包含以下目标：

1. **预置结构解耦**：将 `BuiltInPresets.cs` 拆分至独立主题定义类（按 GUI 与 Terminal 分目录、按主题拆文件），对外保持 `BuiltInPresets` 聚合接口与常量 ID 签名兼容。
2. **Konsole 配色导入解析**：在 `Kei.Term.Core` 解析 KDE Konsole `.colorscheme`（INI 格式）并映射为 `TerminalProfile`。
3. **终端调色器 + 实时 Demo**：独立 `TerminalProfileEditWindow`，包含名称、4 项基础色、16 色 ANSI 调色板与实时 Demo 预览。
4. **工作流串联与真实生效**：设置外观页与主菜单均可触发导入/编辑，且配色选择必须真实作用于已打开标签与新标签（目前此链路缺失，见 §3）。
5. **字体与配色解耦**：字体设置保持全局权威，配色操作不得影响字体。
6. **共用 Demo 组件**：设置页与独立调色弹窗复用同一 Demo 组件，字体与颜色组合展示。

## 2. 架构设计与目录组织

### 2.1 Core 层：内置主题目录结构重构

```
src/Kei.Term.Core/Models/Profiles/
├── BuiltIns/
│   ├── Gui/            (VsCodeDark / OneDark / Nord / SolarizedDark / VeritasHare / VeritasChihiro / VeritasMaki / VeritasKotama)
│   └── Terminal/       (Monokai / Dracula / Gruvbox / SolarizedDark / SolarizedLight / TomorrowNight / VeritasHare / VeritasChihiro / VeritasMaki / VeritasKotama)
└── BuiltInPresets.cs   (保留对外常量 ID 与 DefaultGuiProfiles / DefaultTerminalProfiles 聚合属性)
```

**契约保证**：`BuiltInPresets.GuiVsCodeDarkId` 等所有 `public const string` ID 不变；`DefaultGuiProfiles` / `DefaultTerminalProfiles` 继续暴露 `IReadOnlyList<T>`。

### 2.2 Core 层：Konsole 配色解析器

文件：`src/Kei.Term.Core/Services/KonsoleColorSchemeParser.cs`（实现已存在，见 §3 诊断）。

- **格式**：INI 风格，节包含 `[General] Description`、`[Background]`、`[Foreground]`、`[Color0]`~`[Color7]`、`[Color0Intense]`~`[Color7Intense]`。
- **取值格式**：`Color=R,G,B`（允许空格，如 `Color = 35, 38, 39`），每通道 0–255。
- **映射**：
  - `[General] Description` → `Name`；为空时回退文件名。
  - `[Background] Color` → `Background`；`[Foreground] Color` → `Foreground`。
  - `[Color0..7]` → `AnsiColors[0..7]`；`[Color0..7Intense]` → `AnsiColors[8..15]`。
  - 光标色：无专用字段时取 `Foreground`。
  - 选区色：无专用字段时取蓝色系变体。
- **颜色字符串规范（R2 明确）**：
   - 普通 RGB 解析输出为 **6 位 `#RRGGBB`**；需要透明度时统一使用 **8 位 `#AARRGGBB`**，不混用 RGBA 字符串。
   - 自动生成的半透明选区色采用 `#50RRGGBB`，RGB 来自蓝色槽位；不得再用 `AnsiColors[4] + "50"`。控件适配层依据经验证的实际 API 转换，并测试 alpha 与 RGB 的独立通道值。旧自定义八位色不凭外观猜测并批量重排。
  - **当前实现事实**：`KonsoleColorSchemeParser.cs:105` 使用 `profile.AnsiColors[4] + "50"` 生成 `#RRGGBB50`；这是必须整改的冲突点（详见 §6 验收项「实际 alpha 通道」）。
- **容错**：缺失字段使用 Fallback（默认黑底白字或标准 ANSI 色值）。
- **测试要求（R2 强化）**：除正则/文本解析外，必须覆盖 **颜色值 → 颜色模型 → 控件通道** 的适配路径测试，覆盖：6 位与 8 位混用、alpha 实际生效值、256 色下表的行为边界。**不得声明“所有 256 色都随 ANSI 改变”**；256 色立方区是否随调色板变化取决于控件实现，须以实测为准，未验证前只声明 0–15 的映射。

### 2.3 App 层：配色实际应用链路（R2 新增，含静态诊断）

**静态诊断（仅阅读代码，未运行验证）**：

| 位置 | 现状 | 缺口 |
| --- | --- | --- |
| `ViewModels/TerminalTabViewModel.cs:97-110` | 构造 `TerminalControl` 时仅设 `FontFamilyName` / `TerminalFontSize` | 未传入任何配色 |
| `ViewModels/MainViewModel.cs:1064` | 创建标签仅传 `settings.FontFamily` / `settings.FontSize` | 未传配色，未读会话的 `TerminalProfileId` |
| `Services/ProfileManagerService.cs:132-135` | `SetDefaultTerminalProfile` 仅保存 ID，`DefaultTerminalProfile` 仅用于读取 | 无广播/刷新机制 |
| `Services/ProfileManagerService.cs:72-80` | `GetTerminalProfile(profileId)` 已存在 | 终端创建路径从未调用 |
| `ViewModels/Settings/AppearanceSettingsPage.cs:69-70` | `SelectedTerminalProfile` 无 `On...Changed` 回调（GUI 方案有） | 选择配色不会实时生效 |
| `Views/TerminalProfileEditWindow.axaml:188-291` | Demo 为 XAML 手写模拟，字体硬编码 `Cascadia Mono, Consolas, monospace` | 非真实终端渲染，且不读取真实字体 |

**结论**：配色确实未被应用到终端，根因是应用链路缺失，**不是用户 `LSCOLORS` 配置问题**。

**设计要求**：

- 建立统一解析入口：`会话显式 TerminalProfileId` → 全局默认配色 → 内置默认，三级回退。
- 终端创建与配色变更走**同一适配路径**，避免仅覆盖新标签。
- 具体 API 通道（颜色集合/主题对象/逐色属性）取决于 `RoyalApps.RoyalTerminal.Avalonia` 0.5.2 的实际公开接口。**该 API 尚未验证，不得编造属性名**；实现前须按本地依赖（本地 NuGet 包/反射/官方文档）确认可用通道，再据此完成映射。

## 3. 交互设计

### 3.1 共用 Demo 组件（R2 新增）

- 新增可复用的 Demo 终端预览组件（控件或 UserControl + 共享 ViewModel），**设置页与独立调色弹窗共用同一实现**。
- 设置页：用 Demo 组件**替代原单行字体预览**（`SettingsWindow.axaml` 中 `PreviewSampleText`/`PreviewFontFamily` 的 `TextBlock`）。
- 独立调色弹窗：保留 Demo，展示**字体 + 颜色组合**效果。
- Demo 数据来源：
  - 颜色取自当前编辑/选中的配色方案；
   - 设置页使用**当前尚未保存的字体草稿**（主字体、字号、回退字体、粗细与斜体等已支持项），修改后立即刷新 Demo；从设置页打开调色弹窗时传入同一字体草稿快照。
   - 从主菜单独立导入时使用已应用的全局字体设置；不从配色方案的旧字体字段取值，也不写回字体设置。
- Demo 约束：**不启动 SSH、不执行外部命令**，仅静态/本地渲染；尽量复用真实终端渲染与同一配色适配路径。
- 从主菜单导入时必须能直接打开调色弹窗，**无需先打开设置**。

### 3.2 字体与配色独立（R2 明确）

- 字体（Family / Size / Fallback / CursorBlink）与终端配色**独立配置、独立保存**。
- 导入 Konsole 配色、切换配色、编辑配色**不得修改**字体、字号、回退字体、光标闪烁等。
- **全局现行字体设置为权威**；配色方案的字体字段不作为终端字体来源。
 - 旧 `profiles.json` / bundle 中的 `TerminalProfile.FontFamily`、`FontSize`、`FontWeight`、`IsItalic`、`LineHeight`、`CursorBlink` 保留反序列化和序列化兼容；编辑配色时原样保留这些旧字段，但不将其应用到字体设置或终端字体。不得因重建对象遗漏字段。不做无必要的版本升级或数据清洗。

### 3.3 窗口尺寸与布局目标（R2 明确）

- 独立调色弹窗：**默认 840×620，最小 760×520**（R1 的 900×620 作废）。
- 设置窗口：目标默认 840×620、最小 760×520（现状 760×560 / 680×480 需调整）。
- **不得只靠放大窗口规避裁切**：
  - 下拉框独占一行并横向拉伸（`HorizontalAlignment="Stretch"`，取消 `MinWidth=180` 固定挤占）。
  - 操作栏置于下一行并允许换行（`WrapPanel` 或等价布局）。
  - 默认尺寸、最小尺寸、可用缩放范围内均可完整操作，无控件被裁切或不可达。

### 3.4 入口与菜单（R2 改正）

- **主菜单**：顶层菜单「外观」下新增子菜单「导入 → Konsole 配色方案 (*.colorscheme)...」。
  - R1 的「文件 / 工具 菜单」描述作废；`Menu.File.ImportSecureCrt` 保持现状不动，`Menu.File.ImportKonsole` 需迁出「文件」菜单。
- **设置页**：终端配色分区与入口文案/结构保持一致（同一分区内提供 新建 / 编辑 / 从 Konsole 导入），当前仅支持 Konsole 一种导入格式，界面不得暗示其他格式。
- 两处入口共用同一调色弹窗与解析器。

### 3.5 生效、刷新与回滚语义（R2 新增）

1. **实时生效**：设置页选择配色后，**所有继承全局方案的已打开标签**立即刷新；随后新开标签读取当前有效方案。
2. **显式覆盖**：会话节点显式设置了 `TerminalProfileId` 的标签，继续使用其专属方案，不被全局切换覆盖。
3. **同一 ID 编辑刷新**：编辑已有用户方案并保存后，须通知所有使用该 ID 的标签刷新；**不能只在更换 ID 时才生效**。
4. **设置取消回滚**：设置页「取消」回滚到**最后一次成功应用的状态**；「应用」成功后即更新回滚基准（R1 “回滚到打开时快照”作废，当前实现 `SettingsViewModel.CancelAsync` 只回滚 GUI 方案、未回滚终端配色 ID，属缺口）。
5. **调色弹窗草稿隔离**：弹窗内未确认的编辑仅为草稿，**不写入真实会话、不持久化**；确认后才交回父流程（设置页/主菜单流程）。
6. **取消不污染共享对象**：取消不得改写内置或自定义方案的共享实例，也不得让内置对象变成脏数据。
7. **内置保护**：编辑内置主题时自动创建副本（新 ID），不得就地修改内置实例。
8. **导入名称**：导入后自动填充可编辑名称，保留用户在弹窗中改名的能力。
9. **持久化失败提示**：保存/应用/导入落盘失败时须给出用户可见提示，不得静默失败或假装成功。

## 4. 设计令牌、树模板与命名（R2 修正）

- `Kei.Tree.Line`（`Themes/KeiTokens.axaml:77`）、`Kei.Tree.Indent`（`Themes/KeiTokens.axaml:76`、`Themes/KeiControls.axaml:30,68`、`Services/UiDesignSystemService.cs:64`）以及共用 TreeView 模板**不得破坏**。
- R1 中引用的 `src/Kei.Term.App/Themes/KeiBrush-KeiClassic.axaml` **不存在**，作废；正确文件为 `Themes/KeiTokens.axaml` 与 `Themes/KeiControls.axaml`。
- 在现有代码库中**未发现任何 `Compat` 相关文件**；不得再将 “Compat” 描述为“废弃主题”。
 - 补齐用户此前已要求的目录命名重构：将 `src/Kei.Term.App/Themes/` 迁为 `src/Kei.Term.App/DesignSystem/`，保留 `KeiTokens.axaml` / `KeiControls.axaml` 文件名与资源键。同步更新加载 URI、代码引用、契约测试和当前维护文档；不得改变树模板及令牌的行为。本文其他 `Themes/` 路径仅用于说明迁移前现状。

## 5. 自动化测试与验证要点

1. `BuiltInPresetsRefactorTests`：拆分完整性、常量 ID/名称/色值有效性。
2. `KonsoleColorSchemeParserTests`：正常解析、大小写不敏感、RGB→Hex、缺字段 Fallback；**新增颜色通道适配测试**（6/8 位、alpha 实际值、0–15 映射边界；256 色行为标注为“待实测”）。
3. `TerminalProfileEditViewModelTests`：新建/复制/改色/保存；**新增**同 ID 编辑、取消不污染、字体字段不被覆盖。
4. 应用链路测试：全局继承标签刷新、显式 `TerminalProfileId` 覆盖、新标签读取有效方案。
5. 布局验证：默认 840×620 与最小 760×520 下无裁切、可操作。
6. 所有运行验证在实现完成后集中执行；本轮不运行、不声明通过。

## 6. 本轮验收标准（R2 新增，全部 pending）

- [ ] 菜单正确：Konsole 导入位于顶层「外观 → 导入」，不在「文件」菜单。
- [ ] 设置页配色分区与入口文案一致，且未暗示 Konsole 之外的格式。
- [ ] 默认 840×620 与最小 760×520 下无裁切、控件与操作栏可达（含缩放）。
- [ ] 设置页与独立弹窗共用同一 Demo 组件；Demo 读取**真实当前字体与颜色**，不启动 SSH、不执行外部命令。
- [ ] 选择配色后，继承全局方案的**已打开标签实时刷新**，新标签读取有效方案。
- [ ] 显式设置 `TerminalProfileId` 的会话继续使用专属方案。
- [ ] **同一 ID** 编辑保存后通知相关标签刷新（非仅换 ID 生效）。
- [ ] 设置「取消」回滚到最后成功应用状态，「应用」更新回滚基准。
- [ ] 调色弹窗未确认编辑仅为草稿，不污染真实会话、不持久化；取消不污染共享内置/自定义对象。
- [ ] 内置主题编辑自动创建副本，内置实例不被改写。
- [ ] 从主菜单导入可取消且不产生副作用；导入名称自动填充且可编辑。
- [ ] 导入/切换/编辑配色**不覆盖**字体、字号、回退字体、光标闪烁。
- [ ] 选区色 alpha 通道语义明确且实测有效（不再隐式依赖 `#RRGGBB50`）。
- [ ] 持久化失败有用户可见提示。
- [ ] `Kei.Tree.Line` / `Kei.Tree.Indent` 与共用树模板未被破坏。
 - [ ] 设计系统资产迁至 `DesignSystem/`，运行时加载路径与契约测试同步更新，无失效引用。

## 7. 非目标与未决风险

 - **非目标**：不做 ProfileBundle 版本升级，不实现 Konsole 之外的其他配色格式导入，不进行与配色及设计系统命名无关的重构。
- **未决风险 1（高）**：`RoyalApps.RoyalTerminal.Avalonia` 0.5.2 的配色 API 未验证；若不存在逐色/调色板注入通道，实时应用方案需调整（例如改用整体主题对象或自绘层），须在实现前确认。
- **未决风险 2（中）**：8 位 hex 的 alpha 通道在 Avalonia 与控件渲染之间可能不一致；需要端到端实测。
- **未决风险 3（中）**：256 色（16–255）是否随 ANSI 调色板联动未实测，不能预先断言。
 - **迁移风险 4（低）**：`Themes/` 改为 `DesignSystem/` 必须同步资源加载 URI 与契约测试；不能只移动文件。

## 8. 本轮阶段清单

1. ✅ 规格修订完成（本文档 R2）。
2. ✅ 用户书面审阅已通过。
3. ✅ R2 实施计划已获批准并执行。
4. ✅ 产品实现与自动化回归完成：197 项测试通过，构建零警告、零错误。
5. ⏳ 实际 GUI/DPI、选区像素混合及完整原生资源释放未实测；字体回退链尚未接通，不将主字体预览视为回退支持。

### 交付证据

- 最终执行：`dotnet build Kei.Term.slnx --no-restore`；`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --no-build --no-restore`；`git diff --check`，全部通过。
- 整体审查后的关闭门控、忙状态表单禁用、bundle 内容变更广播遗漏均已修正并纳入最终回归。
- 本文前述静态诊断与未验证 API 为设计时背景；公开 Renderer 选区颜色恢复通道现已编译并完成通道测试，但未声称实际屏幕效果已验收。
