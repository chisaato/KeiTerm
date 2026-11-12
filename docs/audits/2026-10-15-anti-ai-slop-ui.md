# UI / UX 审查：Anti AI Slop UI

审查日期：2026-10-15。基准提交：`6fbb060`，审查对象包含当前工作区文件。

依据用户提供的《Anti AI Slop UI》，结合仓库的 `avalonia-compact-tokens`、`avalonia-desktop-patterns` 规范。检查了 36 个 View XAML、共享设计系统、界面字符串及相关 ViewModel 交互路径；运行了现有无窗口布局测试，并用真实 Avalonia 控件做了临时验证。

共确认 **9 项问题：2 项 P1、6 项 P2、1 项 P3**。最先应处理文件管理器的错误反馈和目录切换状态。此次仅审查，未修改生产代码或测试源码。

## 已确认的问题

### 1. [P1] 文件管理器的多种失败不会显示在界面上

**对应规范：** 异常应进入用户可见的通知或状态反馈。

初始化、刷新目录、打开文件、删除文件和自动回写失败时，代码设置 `TransferStatusMessage`。但文件管理器 XAML 没有绑定该属性，其他视图也没有接收这些消息。传输队列能显示上传/下载任务失败，并不能覆盖上述操作的失败。

尤其是外部编辑器保存后的自动回写失败：监视文件列表仍只显示路径，用户看不到保存失败，也没有重试提示。

**验证：** 临时文件系统让目录请求返回 `IOException`。生产 ViewModel 的状态变成“刷新失败: AUDIT_DIRECTORY_ACCESS_DENIED”，真实视图中没有可见的错误文本，活动面板也未展开。

**建议：** 在文件管理器中加入持续可见的错误区域；监视文件按文件记录回写结果，提供重试入口。错误应保持到用户处理或后续成功，避免被无关状态覆盖。

**位置：** [目录失败处理](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/RemoteFileManagerViewModel.Navigation.cs:73)、[自动回写失败](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/RemoteFileManagerViewModel.Editing.cs:199)、[现有文件活动底栏](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Views/RemoteFileManagerView.axaml:477)。

### 2. [P1] 切换目录时，新路径与旧文件列表同时出现，旧文件仍可操作

**对应规范：** 切换与筛选应原子更新，或显示明确的容器加载状态。

导航先更新 `CurrentPath`，文件列表只在远端请求成功后才清空并重建。等待期间，地址栏显示新目录，列表仍显示旧目录的文件，且打开、下载、删除命令没有根据加载状态或列表所属目录进行限制。请求失败后，这个不一致状态继续保留。

**验证：** 请求被延迟时，`CurrentPath=/new`，列表条目为 `/old/same-name.txt`；`IsLoading=True`，文件列表和该条目的删除命令仍可用。随后请求失败，路径与旧条目仍然并存。

**影响：** 用户可能把旧目录的同名文件当作新目录文件打开、下载或删除。删除实际使用条目的 `FullPath`，因此会作用于旧目录。

**建议：** 分开维护输入中的目标路径与已加载路径，成功后一起提交路径和列表；或在切换目录时清空旧内容并覆盖加载状态，失败时显示重试/返回入口。

**位置：** [导航先修改路径](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/RemoteFileManagerViewModel.Editing.cs:27)、[成功后才替换列表](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/RemoteFileManagerViewModel.Navigation.cs:38)、[文件列表绑定](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Views/RemoteFileManagerView.axaml:235)、[按原 FullPath 删除](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/RemoteFileManagerViewModel.cs:245)。

### 3. [P2] 未实现的 Agent 转发仍是可操作、可保存的开关

**对应规范：** 不可用功能应隐藏、禁用或引导用户，不应作为正常可用操作展示。

SSH 设置中的“启用 Agent 转发（暂不支持）”没有禁用条件。用户可以勾选并保存，但该字段只进入设置草稿和持久化，没有进入实际 SSH 连接逻辑。Tooltip 用“当前 SSH 库尚不支持，此开关暂不生效”等实现说明解释无效开关。

**建议：** 在支持前移除可交互开关，或显示禁用的“Agent 转发暂不可用”。不要允许保存一个会被忽略的功能选择。

**位置：** [可勾选的开关](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Views/SettingsWindow.axaml:714)、[解释文案](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Resources/Strings.resx:932)、[保存设置字段](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/SettingsViewModel.cs:345)。

### 4. [P2] HTTP 代理可以新建、保存并绑定会话，直到连接时才被拒绝

**对应规范：** 在配置或选择阶段限制不支持的类型，避免用户走完无效流程。

代理类型列表包含 HTTP，保存校验接受 HTTP，且会话的“防火墙”选择器把所有代理直接列为可选项。实际出口解析遇到 `HttpProxyConfig` 必然返回失败。代理编辑页的提示为“尚未接入拨号”；在会话选择器中连这一提示也没有。

**建议：** 新建代理时隐藏/禁用 HTTP。为了兼容已有数据可以保留显示，但应标明“HTTP 代理暂不可用”，并阻止将其作为有效连接出口选中。文案使用用户能理解的功能状态。

**位置：** [类型列表](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/ProxyEditViewModel.cs:63)、[接受 HTTP 保存](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/ProxyEditViewModel.cs:226)、[会话无条件列入代理](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/SessionEditViewModel.cs:316)、[连接必然失败](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.Core/Services/SessionProxyExit.cs:29)。

### 5. [P2] 同行输入框与按钮高度不统一，尺寸令牌没有统一落到控件上

**对应规范：** 同行控件使用相同高度令牌。

全局输入框、下拉框和数字输入框高 28；普通按钮高 32。混排时如果页面未覆盖按钮高度，就产生 4px 差异。外部编辑器的“命令 / 路径 + 选择应用”是明确实例，主机密钥管理器的筛选/导入栏、设置中的缓存路径/清空缓存栏也使用这一组合。

**验证：** 真实外部编辑器窗口测得 `EditorPathTextBox.Bounds.Height=28`、`BrowseEditorButton.Bounds.Height=32`。两个控件位于同一行。

**建议：** 建立统一的表单行/工具栏控件类，引用 `Kei.Height.Control`；对话框底部操作按钮如需 32px，另设明确的尺寸令牌和类。

**位置：** [输入框高度](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/DesignSystem/KeiControls.axaml:124)、[普通按钮高度](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/DesignSystem/KeiControls.axaml:287)、[外部编辑器混排行](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Views/ExternalEditorEditWindow.axaml:22)、[主机密钥筛选栏](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Views/KnownHostsManagerWindow.axaml:19)。

### 6. [P2] 跟随浅色系统主题时，经典菜单出现深底黑字

**对应规范：** 控件应与自定义主题保持一致。

启动时选择 `UiTheme=System` 会使用系统主题变体，但 `UiDesignSystemService.Apply` 始终加载深色 Kei 令牌。未完整覆盖的 Fluent 控件会按浅色变体绘制。主窗口虽然设置了内部菜单 presenter 的前景色，其菜单项仍使用浅色主题的黑色文字。

**验证：** 将无窗口环境设为浅色主题变体，加载真实经典主窗口；六个可见顶层菜单项的 `Foreground` 均为 `Black`，窗口背景仍为深色 `#1E1E22`。截图可见“文件、编辑、查看”等菜单文字对比度不足。

该问题适用于使用窗口内菜单的经典布局；macOS 系统菜单栏的实际绘制由系统管理，本次没有对它作缺陷归因。

**建议：** 确定跟随系统时的完整配色策略，按主题变体提供对应令牌；如果界面配色保持深色，Fluent 控件也应使用匹配的深色变体。补充 System + Light 的渲染检查。

**位置：** [主题变体选择](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/App.axaml.cs:209)、[固定深色令牌](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Services/UiDesignSystemService.cs:45)、[仅覆盖菜单 presenter](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Views/MainWindow.axaml:92)。

### 7. [P2] 默认配置下“清空缓存”不执行清理，失败也没有反馈

**对应规范：** 操作应具备有效状态、明确结果和可见错误。

缓存目录的默认值为空，界面提示“留空使用应用标准缓存目录”。实际文件监视器会使用标准目录，但清理命令遇到空值直接返回。因此默认配置下，按钮看起来可用，点击却不会清理正在使用的标准缓存。目录无权限等清理异常也被空 `catch` 吞掉。

**建议：** 用同一个服务解析实际缓存目录并清理；显示清理结果。失败时说明原因并允许重试，无法执行时明确禁用或解释前置条件。

**位置：** [设置入口](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Views/SettingsWindow.axaml:872)、[空值提前返回与空 catch](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/Settings/FileTransferSettingsPage.cs:129)、[实际标准目录回退](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.Core/Services/LocalFileTracker.cs:72)。

### 8. [P2] 没有外部编辑器时，文件关联页面缺少前置条件引导

**对应规范：** 缺少前置条件时提供解决该条件的主操作。

文件关联的新增命令要求 `HasEditors`。没有配置外部编辑器时，关联区只显示“尚未配置文件类型关联”和三个不可用按钮，没有解释新增为何禁用，也没有就近提供添加编辑器入口。用户需要自行推断两个区块之间的依赖。

**验证：** 现有设置布局测试产生的空数据截图中，该区域的新增、编辑、删除均不可用，空状态没有前置条件说明。

**建议：** 空编辑器状态显示“先添加外部编辑器，再设置文件关联”，提供“添加外部编辑器”入口或跳转到上方编辑器配置区；关联为空但已有编辑器时，显示“添加文件关联”。

**位置：** [新增的前置条件](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/ViewModels/Settings/FileTransferSettingsPage.cs:143)、[现有空状态及操作](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Views/SettingsWindow.axaml:968)。

### 9. [P3] 欢迎页重复解释两项简单操作

**对应规范：** 删除重复副标题和不增加信息的说明。

“选择连接方式”下面解释“连接到远程主机，或新建会话以保存连接配置”，随后又分别在“快速连接”“新建会话”按钮下解释相同任务。两项简单操作共有三段说明，占用了空状态的视觉层级。

**建议：** 保留标题、两项操作和快捷键。若需要解释快速连接还支持选择已保存会话，只保留这一条新增信息。

**位置：** [欢迎页说明布局](/Users/0x33a/workspace/KeiTerm/src/Kei.Term.App/Views/NewTabView.axaml:10)。

## 文档清单对照

| 检查项 | 结果 |
| --- | --- |
| 文案、防御性说明、调试残留 | 有未实现功能的内部实现文案和欢迎页重复说明。主界面没有发现心跳演示、测试开关等调试残留。 |
| 同级页面骨架与布局复用 | 已有共享设计系统、分类导航、会话树条目、终端/文件工作区视图。没有确认需要因组件未抽取而单独列出的用户可见缺陷。 |
| 控件尺寸、同行对齐与间距 | 确认存在 28/32px 混排。常规表单使用 Spacing、RowSpacing 或 Margin，未确认连续输入控件零间距的普遍问题。 |
| 圆角与点击区域 | 现有文件活动布局测试覆盖了卡片内按钮边界和部分点击命中区域。没有仅凭缺少 ClipToBounds 就认定圆角缺陷；所有控件的悬停/拖放状态尚未逐一实机验证。 |
| 统一主题 | ComboBox、TextBox、NumericUpDown 有统一样式。System + Light 状态存在 Fluent 控件与深色令牌不一致的问题。 |
| 缺失前置条件、状态过渡与异常 | 文件管理器有两项 P1；未支持功能、缓存操作、关联空状态有具体缺口。 |
| 普通输入校验 | 快速连接的主机必填等校验使用内联提示，未发现将普通缺失输入包装为带错误日志导出的崩溃弹窗。 |
| 无实质意义的状态胶囊与重叠浮层 | 未发现“全部系统运行正常”之类无诊断价值的状态胶囊，也未确认文档描述的通知/历史浮层重叠问题。 |

文件关联区的“新增/编辑”位于明确的对象区块内，不按文档针对全局工具栏的“裸动词”规则直接判定违规。配色 JSON 示例及终端配色预览服务于实际编辑任务，也没有当作调试残留处理。主机指纹核验、凭据保存方式等会影响用户决策的说明应保留。

## 验证记录与边界

- .NET SDK：10.0.401；Avalonia：仓库锁定的 12.1.3。
- 执行已有 `SettingsLayoutTests`、`RemoteFileManagerLayoutTests`、`CompactUiContractTests`、`MainWindowLayoutModeTests`、`IdentityManagerVaultTests`：**38 项，37 通过，1 失败**。
- 失败项：`ModernMenu_ClickRoutesExitAndAbout_ClosesOwnedAboutWindow`，在 `MainWindowLayoutModeTests.cs:312` 的退出回调断言失败。单独重跑仍失败。macOS 菜单退出入口走 App 级退出服务，而该测试注入的是窗口级服务；本次记录为现有测试/路由验证缺口，不据此断言正式应用无法退出。
- 临时验证程序使用真实 Avalonia 控件和生产 ViewModel，仅以可控的文件系统服务模拟延迟与失败，确认了第 1、2、5、6 项。未建立真实 SSH 连接或操作用户文件。
- 截图及临时验证源文件保存于 `/private/tmp/keiterm-ui-audit-2026-10-15/`；测试日志保存于 `/private/tmp/keiterm-ui-audit-2026-10-15-tests.log`，单独重跑日志为 `/private/tmp/keiterm-ui-audit-2026-10-15-menu-recheck.log`。
- 本次为代码审查和 Headless/Skia 渲染验证，没有覆盖全部原生平台菜单、显示缩放、辅助技术及全部实际悬停/拖拽组合。

建议修复顺序：先处理第 1、2 项，确保文件操作状态可信；随后限制未实现功能，修复缓存操作和缺失前置条件的引导；再统一控件高度与系统主题策略，最后精简重复文案。
