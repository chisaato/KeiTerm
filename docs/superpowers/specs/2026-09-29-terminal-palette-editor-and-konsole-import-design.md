# Specification: 终端调色预览器、Konsole 配色导入与 BuiltIn 预置模块化重构

## 1. 概述与目标

为提升 KeiTerm 的外观可定制性与终端使用体验，本次改造包含三大核心目标：
1. **预置结构解耦（BuiltIn Presets Refactoring）**：将单一臃肿的 `BuiltInPresets.cs` 拆分至独立的主题定义类中（按 GUI 与 Terminal 分目录、按具体主题拆分文件），对外保持 `BuiltInPresets` 聚合接口与常量 ID 签名完全向后兼容。
2. **Konsole 配色导入解析（Konsole Color Scheme Parser）**：在 `Kei.Term.Core` 中实现对 KDE Konsole `.colorscheme`（INI 格式）的健壮解析，将其映射为 `TerminalProfile`。
3. **终端调色器与实时 Demo 预览窗口（Terminal Profile Edit & Preview Window）**：
   - 提供独立的 `TerminalProfileEditWindow` 交互窗口。
   - 界面包含：主题名称输入、核心基础色配置（背景、前景、光标、选区）、16 色 ANSI 调色板网格（参考 SecureCRT 的 8 Normal + 8 Bright 色块点击交互）。
   - 界面右侧/下方提供实时 Demo 终端屏幕（参考 Tabby 风格），动态实时渲染 ANSI 色阶条、仿 `ls --color` 目录列表及带色彩的高亮命令行输出。
4. **工作流串联（Workflow Integration）**：
   - 「设置 -> 外观」支持对自定义主题的“新建”、“编辑”、“复制”以及“从 Konsole 导入”。
   - 主窗口顶部菜单支持直接触发“从 Konsole 配色导入...”。
   - 导入后不直接静默入库，而是呼起预览编辑窗口，填充解析出的名称与颜色，允许用户微调后保存。

---

## 2. 架构设计与目录组织

### 2.1 Core 层：内置主题目录结构重构

```
src/Kei.Term.Core/Models/Profiles/
├── BuiltIns/
│   ├── Gui/
│   │   ├── VsCodeDarkGuiPreset.cs
│   │   ├── OneDarkGuiPreset.cs
│   │   ├── NordGuiPreset.cs
│   │   ├── SolarizedDarkGuiPreset.cs
│   │   ├── VeritasHareGuiPreset.cs
│   │   ├── VeritasChihiroGuiPreset.cs
│   │   ├── VeritasMakiGuiPreset.cs
│   │   └── VeritasKotamaGuiPreset.cs
│   └── Terminal/
│       ├── MonokaiTerminalPreset.cs
│       ├── DraculaTerminalPreset.cs
│       ├── GruvboxTerminalPreset.cs
│       ├── SolarizedDarkTerminalPreset.cs
│       ├── SolarizedLightTerminalPreset.cs
│       ├── TomorrowNightTerminalPreset.cs
│       ├── VeritasHareTerminalPreset.cs
│       ├── VeritasChihiroTerminalPreset.cs
│       ├── VeritasMakiTerminalPreset.cs
│       └── VeritasKotamaTerminalPreset.cs
└── BuiltInPresets.cs (保留对外的常量ID与聚合属性，如 DefaultGuiProfiles, DefaultTerminalProfiles)
```

**契约保证**：
- `BuiltInPresets.GuiVsCodeDarkId` 等所有 `public const string` ID 保持不变。
- `BuiltInPresets.DefaultGuiProfiles` 与 `DefaultTerminalProfiles` 继续暴露 `IReadOnlyList<T>`，内部通过聚合各个单独类中的实例构成。

### 2.2 Core 层：Konsole 配色解析器

新建 `src/Kei.Term.Core/Services/KonsoleColorSchemeParser.cs`：
- **格式规范**：KDE Konsole 的 `.colorscheme` 为 INI 风格文件，支持 `[General]` (Description), `[Background]`, `[Foreground]`, `[Color0]` ~ `[Color7]`, `[Color0Intense]` ~ `[Color7Intense]` 等节。
- **色彩格式**：`Color=R,G,B`（如 `Color=35,38,39`）。
- **映射规则**：
  - `[General] -> Description` -> `TerminalProfile.Name`（若空则使用文件名）。
  - `[Background] -> Color` -> `TerminalProfile.Background`。
  - `[Foreground] -> Color` -> `TerminalProfile.Foreground`。
  - `[Color0] ~ [Color7]` -> `TerminalProfile.AnsiColors[0..7]`。
  - `[Color0Intense] ~ [Color7Intense]` -> `TerminalProfile.AnsiColors[8..15]`。
  - 光标色：若无专用字段，默认采用 `Foreground` 或前景色对比高亮。
  - 选区色：若无专用字段，默认采用 `Color4`（Blue）或主对比色的半透明变体（如 `#526E9F50`）。
- **容错处理**：缺失字段自动应用 Fallback 兜底（回退至默认黑底白字或标准 ANSI 色值）。

---

## 3. App 层：调色器与实时 Demo 预览交互设计

### 3.1 窗口与 ViewModel 规划

- **窗口视图**：`src/Kei.Term.App/Views/TerminalProfileEditWindow.axaml(.cs)`
- **ViewModel**：`src/Kei.Term.App/ViewModels/TerminalProfileEditViewModel.cs`
- **窗口尺寸与布局**：
  - 推荐尺寸：`Width="900" Height="620"`，支持响应式与自适应。
  - 采用左右分栏（或紧凑桌面双面板）：
    - **左侧控制面板 (Palette Controls, Width ~420px)**：
      - 主题基本信息：Profile 名称文本输入框、是否只读（如果是内置主题则提示“内置主题不可直接修改，已自动为您创建副本”或仅供查看）。
      - 核心基础色系（4 项）：
        - 背景色 (`Background`)
        - 前景色 (`Foreground`)
        - 光标色 (`CursorColor`)
        - 选区色 (`SelectionBackground`)
        - 提供 16 进制颜色输入框 + 紧凑色块预览按钮，点击弹出颜色拾取弹层（或 Avalonia ColorPicker 控件）。
      - ANSI 16 色调色板（SecureCRT 风格）：
        - **Normal (标准 8 色)**：Black, Red, Green, Yellow, Blue, Magenta, Cyan, White 8 个色块。
        - **Bright / Bold (高亮 8 色)**：Bright Black 到 Bright White 8 个色块。
        - 每个色块支持悬停高亮、选中后在下方/右侧微调颜色 Hex 或使用颜色微调器。
    - **右侧实时 Demo 终端预览屏 (Demo Terminal Preview, Grid.Column="1")**：
      - 背景色直接绑定当前编辑的 `Background`。
      - 模拟真实的终端外观，包含：
        1. **调色板测试条**：两排 8 色色块方阵（Normal 0~7 和 Bright 8~15），直观比对阶梯过渡。
        2. **模拟 Shell 提示符与命令输出**：
           ```bash
           user@keiterm:~$ uname -a
           Linux keiterm 6.10.0 #1 SMP PREEMPT_DYNAMIC x86_64 GNU/Linux
           user@keiterm:~$ ls -la --color=auto
           drwxr-xr-x 4 user user 4096 Sep 29 10:00 .
           drwxr-xr-x 6 user user 4096 Sep 29 09:30 ..
           -rw-r--r-- 1 user user  220 Sep 29 09:30 .bashrc
           drwxr-xr-x 2 user user 4096 Sep 29 09:35 src
           -rwxr-xr-x 1 user user 8192 Sep 29 09:40 build.sh
           user@keiterm:~$ git status
           On branch main
           Changes to be committed:
             modified:   BuiltInPresets.cs
           user@keiterm:~$ █
           ```
        3. 上述文字使用对应的 ANSI 颜色画刷（目录为 Blue，可执行脚本为 Green，压缩包/符号链接为 Cyan/Magenta，错误/警告为 Red/Yellow），光标渲染为 `CursorColor`，带选中文本渲染为 `SelectionBackground`。
        4. 调色器中任意色值变更时，Demo 区域即时响应重绘。

### 3.2 导入工作流细节

1. **入口 1：设置窗口 (SettingsWindow)**
   - 在「外观」选项卡的「终端外观」区域，新增操作按钮组：
     - `[新建主题]` -> 打开空的 `TerminalProfileEditWindow`。
     - `[编辑]` -> 打开选中 Profile 的 `TerminalProfileEditWindow`（如果是内置主题，自动深拷贝并重命名为 `xxx (副本)`）。
     - `[从 Konsole 导入...]` -> 呼起 OpenFileDialog（过滤 `*.colorscheme`） -> 解析为 Profile -> 打开 `TerminalProfileEditWindow` 供用户预览与确认。
2. **入口 2：主菜单栏 (MainWindow Menu)**
   - 在顶部菜单「文件」或「工具」中，新增菜单项：`导入 -> Konsole 配色方案 (*.colorscheme)...`。
   - 同样在打开文件并解析后，弹出 `TerminalProfileEditWindow` 进行预览与保存。

---

## 4. 自动化测试计划

1. **`BuiltInPresetsRefactorTests.cs`**：
   - 验证所有内置 GUI / Terminal Profile 是否已全部被正确拆分并完整聚合在 `BuiltInPresets` 中。
   - 验证原有内置常量 ID、预置名称、色值有效性。
2. **`KonsoleColorSchemeParserTests.cs`**：
   - 编写针对典型 Konsole `.colorscheme` 文本的单元测试。
   - 覆盖正常解析、大小写不敏感键值、RGB 转 Hex 正确性、以及部分字段缺失时的 Fallback 机制。
3. **`TerminalProfileEditViewModelTests.cs`**：
   - 验证新建、复制内置主题副本、颜色同步修改、保存等逻辑的正确性。

---

## 5. 约束与规范自查

- 严格遵循 .NET 10 + C# 13 规范，避免多行 var 堆叠，采用行内注释。
- `Kei.Term.Core` 严禁引入 Avalonia 命名空间，保持纯 C# 模型与逻辑。
- 绝不破坏 `src/Kei.Term.App/Themes/KeiBrush-KeiClassic.axaml` 中已有的 `Kei.Tree.Line` 与 `Kei.Tree.Indent` 设计令牌，保证其它会话的 TreeView 对齐线功能完好无损。
