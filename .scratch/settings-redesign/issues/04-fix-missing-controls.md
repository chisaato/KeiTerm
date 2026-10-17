# 04 修复：新布局下大量设置控件不显示

Status: resolved
Blocked by: —

见 `../spec.md`。WIP 提交之后由用户桌面验收发现（2026-10-18）。

## 现象

- 终端配色与字体：“终端配色方案”卡片只剩“新建… / 编辑 / 调色…”按钮，方案下拉不见了；“终端字体”卡片只剩几条空分隔线，字体和字号控件都没了。
- 标签栏：“布局”卡片是空的，标签位置下拉不见了。
- 外观：“界面模式”“界面主题”卡片是空的，“界面字体”卡片只剩回退字体下钻行。
- 其他页面估计也有同样的问题，需要逐页核对。

## 初步判断（未验证）

`Views/Settings/Controls/SettingRow.axaml` 是 `UserControl`，它自己的布局 Grid 放在 `Content` 里；调用方在 XAML 里塞进子控件时，同样写的是 `Content`，于是把 Grid 替换掉了。内部 `ContentPresenter` 又把 `Content` 绑回自身，结果整行渲染成空白。`SettingsCard`、`DrillDownRow` 等控件也要检查是否有同样的问题。

修复方向：把这些控件改成 `ContentControl` / `TemplatedControl` + `ControlTemplate`（样式放 `SettingsStyles.axaml`），或者把右侧内容改为独立的属性（例如 `Control`），不要占用 `Content`。

## 验收

- 逐页对照 `69b6a3c`（重构前）的 `SettingsWindow.axaml`，确认每一个设置项都能在新布局中看到并操作。
- 补一个测试：能因“设置行内容不显示”而失败（例如在 headless 环境里实例化 `SettingsWindow`，切换到各页，断言关键控件在视觉树中可见）。

## Comments

### 根因调查（只读诊断，未改源码；2026-10-18）

**结论：工单里的猜测方向对，但机制不同；真正原因是 `SettingRow` / `SettingsCard` 用 `new` 遮蔽了 `ContentControl.Content`。**

#### 1. 根因与证据

- `Views/Settings/Controls/SettingRow.axaml.cs:18-19` 声明了 `public static new readonly StyledProperty<object?> ContentProperty`，`:35-40` 又声明了 `[Content] public new object? Content`。`SettingsCard.axaml.cs:27-28`、`:58-63` 同样如此。
- 这是**另一个**属性，不是 `ContentControl.ContentProperty`。UserControl 的模板里是 `ContentPresenter`，它读基类 `Content`，所以永远是 `null`。
- XAML 里 `[Content]` 特性的解析针对的是运行时类型。`SettingRow.axaml:6` 根元素下的 `<Grid>` 因此也被写进了**遮蔽属性**，没有写进基类 `Content`；后来调用方的 `<ComboBox>` 再把遮蔽属性覆盖掉。两边都进不了基类 `Content`，整行因此没有渲染。
- `SettingRow.axaml:22-25` 的内部 `ContentPresenter` 绑定的是遮蔽属性，但它根本不在可视树里，因为 `Grid` 从未成为基类 `Content`。
- 为什么有些行正常：正常的行**没有用 `SettingRow` / `SettingsCard`**。
  - `TabBarSettingsPageView.axaml:47-63` 的“命名规则”输入框、`:75-88` 的“预览”、`:101` 的 CheckBox，都是手写 Grid / 原生控件。
  - `DrillDownRow`（变量下钻行、回退字体下钻行）、`SettingsPageHeader` 没有遮蔽 `Content`，也不接受子内容，所以正常。
  - 卡片外壳是 `<Border Classes="settingsCard">`（不是 `SettingsCard` 控件），因此标题和分隔线能显示，里面的 `SettingRow` 却是空的。这正好对应“只剩标题和空分隔线”。
- 我之前列的其他嫌疑都排除了：
  - `DataContext="{Binding Appearance}"` 正常。运行时 ComboBox 的 `ItemCount` 为 3~4，说明 `ItemsSource` 绑定本身没问题。
  - 没有编译绑定错误，build 0 警告 0 错误。
  - `IsVisible` 条件、DataTemplate 匹配都不是原因。

**运行时验证**（`/tmp/diag` 的 headless 程序，在仓库外，实例化 `SettingsWindow`、遍历全部分类）：

```
SettingRow '终端配色方案:' base.Content=null new.Content=ComboBox visualDesc=1 bounds=344x0
SettingRow '主等宽字体:'   base.Content=null new.Content=AutoCompleteBox visualDesc=1 bounds=345x0
DrillDownRow '高级：回退字体' visualDesc=11 bounds=345x503
SettingsCard '传输与排队' baseContent=null visualDesc=1 bounds=360x0
```

- 每一个 `SettingRow` 的基类 `Content` 都是 `null`，高度恒为 0，可视子孙只有 1 个（空的 ContentPresenter）。遮蔽属性里放着调用方的控件，但没有被渲染。
- `DrillDownRow` 有 11 个可视子孙，高度正常。
- 三个 `SettingsCard` 高度也是 0，整张卡片（标题加内容）都是空的。

#### 2. 受影响范围

`SettingRow`（全部实例，共约 35 处）：

| 页面 | 受影响行 |
|---|---|
| General | 撰写栏模式、标签页切换修饰键、数据目录 |
| Appearance | 布局模式、主题模式、界面配色方案(GuiProfile)、界面字体族 |
| TerminalAppearance | 终端配色方案、主等宽字体、字号（含字体对话框按钮）、光标闪烁 |
| Terminal | 回滚行数、默认终端类型 |
| TabBar | 标签位置 |
| Tree | 排序顺序、密度档位、行高、字号、图标大小、缩进 |
| Ssh | 默认端口、用户名、超时、默认身份、心跳、自动重连、系统 agent、Agent 转发、自定义 agent 路径、HostKey 策略 |
| Security | 自动锁定分钟数 |
| Transfer | 并发、下载目录、冲突策略、续传、自动重试、重试次数、退避方式、初始/最大间隔 |
| FileManager | 缓存目录、文件变动监视模式 |

- `SettingsCard`：只有 `TransferSettingsPageView.axaml` 用了（3 张卡片），整张卡片都空了；高级折叠 `AdvancedContent` 不是遮蔽属性，但外壳不渲染，所以也不会出现。
- 不受影响：`DrillDownRow`、`SettingsPageHeader`、`DrillDownHost`（未遮蔽 `Content`），手写 Grid 的行，以及 `Border.settingsCard` 卡片里的原生控件（按钮、复选框、列表等）。
- Proxy 页整页是原生控件，不受影响。

#### 3. 修复方案

**推荐 A：把 `SettingRow`、`SettingsCard` 改成 `ContentControl`（`TemplatedControl`）加 `ControlTemplate`**

- 模板写在 `SettingsStyles.axaml`，用 `TemplateBinding` 取 `Title`、`Description`、`HasDescription`；右侧用标准 `ContentPresenter`，直接使用基类 `Content`。
- 删掉 `new ContentProperty`、`new Content` 和 `[Content]`，使用 `ContentControl` 自带的 content 语义（已有 `[Content]`），调用方的 XAML 完全不用改。
- `SettingsCard` 同理；`AdvancedContent` 在模板里放在 Expander 内。
- 取舍：改动集中在两个控件；去掉 UserControl 的 `InitializeComponent`、`RelativeSource AncestorType` 绑定，更符合 Avalonia 习惯，也能被 Fluent 样式覆盖。需要保证 `SettingsStyles.axaml` 在所有使用处都已被加载。

**备选 B：保留 UserControl，仅改成不占用 `Content` 的槽位属性**

- 例如 `[Content] public object? Editor`，并在 axaml 里用 `<UserControl.Content><Grid>…` 显式写属性元素，让 Grid 进入基类 `Content`。
- 取舍：改动最小，调用方 XAML 不变；但 `[Content]` 特性的解析细节容易再踩坑，必须配合测试，不如 A 稳。

**备选 C：删掉两个控件，每处内联手写 Grid**

- 取舍：最直接，但会复制 35 处布局，重复代码多，不推荐。

#### 4. 新布局里真正被删掉的设置项（对照 `69b6a3c` 的 `SettingsWindow.axaml`）

逐项把旧文件里的绑定路径与新 `Views/Settings/**` 比对（命令、选中项、数据源、开关）。**设置项本身几乎没有被删**，全部都在新 XAML 里：

- 下钻后在子页中的：回退字体的新增/上下移/删除（`TerminalFallbackFontsSubPage`、`UiFallbackFontsSubPage`）；哈希算法与归档格式的选择、排序（`HashAlgorithmsSubPage`、`ArchiveFormatsSubPage`）；标题变量表（`TabBarVariablesSubPage`）。
- 其余的编辑器列表、文件关联、代理列表、快捷键显示和录制、主密码、快速解锁、导入导出等，都能在新页里找到对应绑定。

真正丢失的只有：

1. **`IsBusy` 禁用交互**：旧版内容区 `ScrollViewer IsEnabled="{Binding !IsBusy}"`（旧 `:90`）和底部按钮容器 `IsEnabled="{Binding !IsBusy}"`（旧 `:1316`），在新 `SettingsWindow.axaml` 里已经没有了，`Views` 下 grep `IsBusy` 无结果。保存、导入过程中不再禁用交互，建议补回。
2. 旧版按 VM 类型分发的 `DataTemplate`（`Categories`、`SelectedCategory.Page`）被换成了新的导航和页面机制，属于有意重构，不是丢项。

另外说明：这类“看不见”的情况，全部是 `SettingRow` / `SettingsCard` 渲染问题，不是 XAML 里没写。修好后需要的验收方式：headless 下遍历每页，断言每个 `SettingRow`、`SettingsCard` 的 `Bounds.Height > 0` 且基类 `Content` 非空（可参考 `SettingsLayoutTests.cs` 的做法，我的诊断程序在 `/tmp/diag/Program.cs`）。

### 全窗口 headless 轮查定论（Skia 渲染 + 运行期统计）

方法：`/tmp/diag/src/Program.cs`（仓库外），设置窗口 900/1280 各一次，遍历每个分类页和下钻子页（进入再返回）；另实例化 `Views/` 下 33 个其它窗口和 MainWindow（900/1280）。截图在 `/tmp/diag/shots/`（现状）、`/tmp/diag/shots-old/`（`69b6a3c`）、`/tmp/diag/shots-fix/`（临时 worktree 里按方案 A 改 SettingRow/SettingsCard 后的效果，已删除 worktree，仓库未改）。明细 TSV 在 `/tmp/diag/out/`。所有窗口都能构造，没有跳过项。

**设置窗口 1280 宽，可见可交互控件数（TextBox/ComboBox/CheckBox/ToggleSwitch/NumericUpDown/Button/ListBox，不含导航）**

| 页 | 旧 | 现状 | 方案 A 后 | 现状 0 尺寸 | 绑定错误 |
|---|---|---|---|---|---|
| 初始页/General | 13 | 9 | 13 | 3 | 0 |
| Appearance | 9 | 6 | 10 | 4 | 2 |
| Tree | 9 | 3 | 5 | 2 | 0 |
| Terminal | 5 | 3 | 4 | 1 | 0 |
| TerminalAppearance | 13 | 9 | 14 | 4 | 2 |
| TabBar | 6 | 6 | 7 | 1 | 2 |
| Ssh | 13 | 3 | 12 | 9 | 0 |
| Proxy | 7 | 7 | 7 | 0 | 0 |
| Security | 4 | 3 | 4 | 1 | 0 |
| Transfer（旧为 FileTransfer 合并页 44） | 44 含文件管理 | 3 | 14 | 3 | 2 |
| FileManager | 含在上面 | 16 | 19 | 2 | 0 |

900 宽结果与 1280 一致。Transfer + FileManager 方案 A 后 14+19=33，旧版合并页 44 是把下钻列表（HashAlgorithms/ArchiveFormats 各 11）内联统计的结果，下钻页本身两个版本都有 11，不是丢控件。

**其它窗口（33 个）**：可交互控件数全部正常，0 尺寸项都是误报或空内容：
- `ColorChooserWindow` 7 项、Ssh/Transfer 里 ToggleSwitch 的 `PART_On/OffContentPresenter`：模板部件，设计上一个为 0 宽，且它们是 `ContentPresenter` 自带的 `Content`，不是自定义遮蔽。
- `SessionEditWindow` Ports 页 `Border#border` 492x0：端口转发列表为空。
- `IdentityManagerWindow` 的 `StackPanel` 0x32、`TerminalThemeJsonImportWindow` 的 `StackPanel` 0x0：空列表/空提示，无内容丢失。
- 结论：重构之外的窗口没有问题。

**运行期绑定错误**（`LogArea.Binding`，Warning 以上）：
- `AppearanceSettingsPage`/`TerminalAppearance`/`TabBar`/`Transfer`：`CurrentSubPage.Content` 与 `CurrentSubPage.Title`，值为 null。是 `SettingsPageHeader` 的 `SubPageTitle` 和宿主 `ContentControl` 在未下钻时绑定到 null 子页，属于无害噪声，但建议绑定加 `FallbackValue` 或用 `CurrentSubPage?.` 之类避免。
- `KnownHostsManagerWindow`：`SelectedEntry.IsRevoked` 在无选中项时为 null，噪声，重构前就有。
- `MainWindow`：`DockControl` 的 `Layout.*` 在布局未加载时为 null，噪声，与本次重构无关。
- 没有任何错误指向“控件消失”。

**定论**
1. 缺失控件的唯一主因是 `SettingRow`/`SettingsCard` 用 `new` 遮蔽 `ContentControl.Content`，基类 Content 为 null，内部 `ContentPresenter` 绑定的是遮蔽属性但模板在 UserControl 的 Grid 下取到的是基类，结果高度为 0。方案 A 在临时 worktree 里验证：设置窗口 0 尺寸控件由 3~9 个降到 0（仅剩下面的 DrillDownRow 问题），可交互控件数回到或超过旧版（Tree 5/9 和 Terminal 4/5 的差异见下条）。
2. `DrillDownHost` 同样 `new` 遮蔽 `Content`，但全仓库没有任何使用方（grep 无引用），不影响渲染；建议顺手删除或一并改成 ContentControl。全 src 扫描 `new` 遮蔽 AvaloniaProperty 只有这三处。
3. 不是原因：DrillDownHost 宿主、Panel IsVisible 切换、ScrollViewer 高度、MaxWidth——这些在方案 A 后都表现正常。
4. 方案 A 后的残留差异：
   - Tree 5（旧 9）、Terminal 4（旧 5）：旧版把代理等设置内联，新版移到 Proxy 页，页数和分组不同；Proxy 页 7 个控件正常。需要产品确认这是有意重构，不是丢项（此前对照旧 axaml 已确认设置项没有被删）。
   - `DrillDownRow` 在 900 宽下右侧 `StackPanel` 为 0x39，且在 Appearance/TerminalAppearance 截图里标题列被挤到很窄，行高被撑到 500+。这是独立的第二个布局问题，与 SettingRow 无关。

**修复清单（按根因）**
1. 根因 A（SettingRow/SettingsCard 遮蔽 Content）：改成 `ContentControl` + `ControlTemplate`（放进 `SettingsStyles.axaml` 或各自 axaml 的 `ContentControl.Template`），删除 `new ContentProperty`/`new Content`，模板里用 `TemplateBinding Content`。SettingsCard 同理，`AdvancedContent` 保留。已在临时 worktree 验证可行。
2. 根因 B（DrillDownRow 在窄宽度下列宽分配）：标题列与摘要列都用 `*`，右侧操作用 `Auto`，给摘要加 `TextTrimming` 或 `MaxWidth`，并让 Grid 列用 `MinWidth`；改后需用 900 宽截图验证（现状截图 `SettingsWindow-AppearanceSettingsPage-900.png`）。
3. 次要：补回 `IsBusy` 禁用交互（旧 `:90`、`:1316`）；消除 `CurrentSubPage` 的 null 绑定噪声；删除未使用的 `DrillDownHost`。
4. 验收：在 `SettingsLayoutTests.cs` 增加 headless 用例，遍历所有分类页和下钻页，断言所有 `SettingRow`/`SettingsCard` 的 `Bounds.Height > 0`，并断言各页可交互控件数不低于一个固定下限（按上表“方案 A 后”取值），900/1280 两个宽度各跑一次。


### 修复记录（2026-10-18）

改动：
- `SettingRow`、`SettingsCard` 改为 `ContentControl`，`DrillDownRow` 改为 `TemplatedControl`；删除遮蔽用的 `new ContentProperty` / `new Content` / `[Content]` 以及对应的 `.axaml`，三者的 `ControlTemplate` 统一放进 `Views/Settings/SettingsStyles.axaml`（`TemplateBinding`）。调用方 XAML 未改。
- `DrillDownRow` 列宽：标题 `3*`、摘要 `2*`（上限 200，省略号截断）、按钮 `Auto` 完整显示。
- 删除未使用的 `DrillDownHost`（axaml + cs）。
- `IsBusy` 禁用：`SettingsWindow.axaml` 里内容区 ScrollViewer 和底部按钮容器本来就已有 `IsEnabled="{Binding !IsBusy}"`，无需补回。
- 新增测试 `tests/Kei.Term.Tests/SettingsControlsRenderTests.cs`：900/1280 宽度，遍历所有分类页和下钻子页，断言可见的 SettingRow/SettingsCard/DrillDownRow 高度 > 0、Content 非空，DrillDownRow 按钮宽高 > 0 且行高 < 120。

验证：
- 修复前（只加测试）该测试在 900 和 1280 都失败（`GeneralSettingsPage/SettingRow: height is 0`）。
- 修复后 `dotnet build Kei.Term.slnx` 0 警告 0 错误；`dotnet test` 1127 通过 / 0 失败 / 36 跳过（有一次全量运行出现过 1 个未查明的偶发失败，重跑两次均通过）。
- 截图：`/tmp/diag/shots-after/`。

遗留：
- `CurrentSubPage.Content/Title` 空绑定噪声未处理。
- 900 宽下终端外观页左栏卡片较窄，行内标题列仍偏窄（两栏页布局问题，非本次根因）。

### 窄宽度布局与空绑定（2026-10-18，第二轮）

改动：
- 新增 `Views/Settings/Controls/AdaptiveColumnsGrid`（Grid 子类）：可用宽度 < 740 时双栏页（外观、终端外观）改单栏，预览落到设置卡片下方；≥ 740（1280 宽窗口）保持左右双栏。两页的 `Grid` 已替换，预览内容和配色未动。
- `SettingRow`：行宽 < 400 时加 `:narrow` 伪类，样式里改为上下排列（标题说明在上，控件在下并左对齐）。1280 宽双栏页的左栏行因此也会上下排列。
- 手写行（文件管理、常规、标签栏页里 `Margin="0,2,16,2"` 的 StackPanel）里的标题/说明补 `TextWrapping="Wrap"`，修复文件管理页说明被裁切。
- `CurrentSubPage.Title/Content` 空绑定：`FallbackValue` 对 "path 中间为 null" 的警告无效，改为在三个宿主 VM（AppearanceSettingsPage、TabBarSettingsProxyPage、TransferSettingsPage）提供非空属性 `SubPageTitle` / `SubPageContent`，视图改绑这两个。重跑诊断后设置窗口不再有该类 Binding 警告。
- 测试：`SettingsControlsRenderTests` 增加标题宽度 ≥ 120、标题高度 ≤ 两行、双栏页 900 下预览在设置下方/1280 下在右侧的断言。把 `AdaptiveColumnsGrid` 阈值和 `SettingRow` 上下排列关掉时（即上一轮的行为），900 和 1280 都失败（`SettingRow '布局模式:': title width 85 < 120` 等）。

验证：build 0 警告 0 错误，test 1127 通过 / 0 失败 / 36 跳过。截图在 `/tmp/diag/shots-after2/`。

遗留（非本轮范围）：
- 各页卡片宽度随内容变化（`SettingsWindow` 里内容 ContentControl 为 Left 对齐加 MaxWidth 760），如 Tree/Terminal/Security 页卡片窄于 SSH/传输页，建议统一拉伸。
- 子页面包屑里"外观配色/传输"按钮（hyperlink 样式）有深色背景方块，看起来像未设样式。
- 安全页在 headless 下只有"自动锁定"一行，卡片末尾多一条分隔线；主密码等控件应是依赖 vault 服务而未显示，未核实。
- 代理页列表表头列宽不对齐。
- 诊断里仍有回退字体子页 `FontFamily` 字符串转换警告（既有，非本轮引入）。

## Comments（第三轮）

改动：
- `SettingsWindow.axaml`：内容区 ContentControl 由 `HorizontalAlignment="Left"` 改为 `Stretch`（仍 `MaxWidth=760`），卡片拉伸到统一宽度，不再随内容收缩。1280 宽下内容区居中，900 宽下占满可用宽度；`AdaptiveColumnsGrid` 的单栏/双栏切换未改动，截图确认 900 单栏、1280 双栏。
- `SettingsStyles.axaml`：新增 `Button.hyperlink` 样式（模板内 ContentPresenter 背景透明、无边框、Accent 文字；悬停换 Accent.Hover 并加下划线；按下回落）。`SettingsPageHeader.axaml` 里该按钮改 `MinHeight=0`、顶对齐，与面包屑其余文字基线一致。
- 代理页表头：列宽（130/80/*/60）与边距（9）本就与行模板一致，未改；列表为空，行对齐只能按边距推算，未能在截图中实证。
- 测试：`SettingsControlsRenderTests` 新增跨页面、跨下钻子页收集所有可见 `settingsCard` 宽度，同组（单栏 / 双栏左栏 / 双栏右栏）内误差 ≤ 1px。修复前在当前代码上失败：900 宽 `286..658`，1280 宽 `286..756`（最宽 SshSettingsPage，最窄 AppearanceSettingsPage/DrillDownUiFallbackFontsCommand）。
- 过程中踩坑：样式里用 `TextElement.TextDecorations` 会让 App 的 XAML 加载失败，表现为所有无头测试 `SynchronizationContext may not be used as a TaskScheduler`；已改为选 ContentPresenter 内的 TextBlock 设 `TextDecorations`。

验证：build 0 警告 0 错误，test 1131 通过 / 0 失败 / 36 跳过。截图 `/tmp/diag/shots-after3/`（900、1280，全部设置页与下钻子页逐张看过）。

遗留：悬停下划线只能靠样式推断，headless 截图无法触发 pointerover；页面主标题相对面包屑有约 8px 缩进（既有，来自标题行返回按钮列占位）。
