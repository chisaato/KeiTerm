# 终端设置拆分与外观左右分栏设计规格 (R3)

> 状态：**设计规格草案**。落实用户关于“设置分类拆分、终端外观左右分栏、Demo 包含 CJK 样本、多工具导入区置于左下、窗口适当放大”的明确诉求。

---

## 1. 背景与目标

在前期改动中，配色方案管理与字体配置堆叠在单列纵向布局中，导致以下问题：
1. **实时终端 Demo 预览被挤压或隐藏**：由于字体列表、回退字体占位较大，必须纵向滚动甚至在默认高度下完全看不见右侧/下方的 `TerminalShellPreviewView`。
2. **终端行为与视觉外观混杂**：回滚缓冲区、默认 TERM 等底层仿真参数与字体、调色板混在一起。
3. **导入工具扩展性受限**：当前单放了一个“从 Konsole 导入...”按钮，未来扩展 iTerm2、VSCode 等导入时缺乏规整空间。

**目标**：
1. 将设置左侧导航拆分为 **「终端」**（仿真与回滚）与 **「终端外观」**（字体、配色、预览、导入）两个大类。
2. **「终端外观」** 采用**左右分栏**布局，左侧为配置与导入，右侧为全高度实时真实终端 Demo。
3. 扩展 Demo 静态 ANSI 内容，加入包含 **中日韩 (CJK) + 拉丁文** 的完整等宽测试文本。
4. 增大 `SettingsWindow` 默认尺寸至 `980×660`（最小 `880×580`），微调 `MainWindow` 默认尺寸至 `1280×820`。
5. 导入区域置于左侧下方，组织为可扩展的外部工具导入列表/卡片。

---

## 2. 界面与交互规格

### 2.1 窗口尺寸

- **SettingsWindow**:
  - `Width="980"` / `Height="660"`
  - `MinWidth="880"` / `MinHeight="580"`
- **MainWindow**:
  - `Width="1280"` / `Height="820"`
  - `MinWidth="960"` / `MinHeight="600"`

### 2.2 设置分类大类拆分

在 `SettingsViewModel.Categories` 中：
1. **「终端」 (Terminal)**:
   - 包含：回滚行数 (`NumericUpDown`)、默认终端类型 (`TextBox`)。
   - 保持简洁独立的终端系统行为配置。
2. **「终端外观」 (Terminal Appearance)**:
   - 左右两列网格布局：`ColumnDefinitions="380, *"` 或 `ColumnDefinitions="400, *"`，带适度 `ColumnSpacing="20"`。
   - **左侧列**：
     - **分组 1：终端配色**：配色下拉框、新建按钮、编辑/调色按钮。
     - **分组 2：终端字体**：主等宽字体、字号、光标闪烁、字体倾斜、回退字体优先级列表及增删上下移。
     - **分组 3：导入外部配色方案**：位于左侧底部，提供“从 Konsole 导入 (*.colorscheme)...”、“从 iTerm2 导入 (预留)”等清晰操作项。
   - **右侧列**：
     - **实时效果预览 (Live Preview)**：
     - 标头提示：“实时效果预览 (Live Preview)”。
     - 包含 `TerminalShellPreviewView`，垂直拉伸对齐，撑满右侧可视高度。

### 2.3 CJK 实时 Demo 预览样本

更新 `TerminalShellPreviewView.DemoAnsiSample` 字节流，补充包含 CJK 谚文、假名、常用汉字及 ANSI 彩色效果的测试内容：

```text
ANSI Palette:
[16色色块条]

user@keiterm:~$ uname -srm
Linux 6.10.0-keiterm x86_64

user@keiterm:~/docs$ cat cjk-lorem.txt
[ZH] 天地玄黄 宇宙洪荒 • 繁星落入深渊，终端静静流淌。
[JA] いろはにほへと 散りぬるを • 我が世谁ぞ常ならむ
[KO] 다람쥐 헌 쳇바퀴에 타고파 • 별빛이 흐르는 은하수
[EN] The quick brown fox jumps over the lazy dog. 1234567890

user@keiterm:~/repo$ git status -s
 M src/DesignSystem/KeiTokens.axaml
?? docs/cjk-preview.md

user@keiterm:~/repo$ █
```

---

## 3. 架构与改动范围

1. **ViewModels**:
   - `SettingsViewModel.cs`:
     - 新增 `TerminalAppearanceSettingsPage`（或将原 `TerminalSettingsCombinedPage` 拆分出纯终端行为与纯外观）。
     - 更新 `Categories` 列表，加入「终端外观」条目。
2. **Views**:
   - `SettingsWindow.axaml`:
     - 窗口尺寸提升为 980×660。
     - 终端页面模板重写为纯行为参数。
     - 新增终端外观 DataTemplate，组织左右两栏。
     - 保持原有 `IsEnabled="{Binding !IsBusy}"`、`NotificationTextBlock` 错误呈现与关闭事务保护。
   - `MainWindow.axaml`:
     - 默认尺寸调整为 1280×820。
   - `TerminalShellPreviewView.axaml.cs`:
     - 更新 `DemoAnsiSample`，注入完整 CJK 文本。
3. **Strings.resx**:
   - 补充「终端外观」分类名称、说明及相关国际化键。

---

## 4. 验证计划

- **单元与契约测试**：
  - 验证 `SettingsViewModel.Categories` 包含「终端」与「终端外观」且页面类型对应。
  - 验证静态契约，保证 `SettingsWindow` 的关闭防护、`IsBusy` 绑定不受破坏。
  - 全量回归现有 197 个测试用例，且新增契约测试全数通过。
- **构建与代码检查**：
  - `dotnet build Kei.Term.slnx` 0 警告 0 错误。
  - `git diff --check` 无空白或换行瑕疵。
