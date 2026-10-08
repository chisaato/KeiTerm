# Desktop Layout Modes (Classic vs Modern) & Tab Experience Design Specification

| 文档版本 | 日期 | 状态 | 规范范围 |
| :--- | :--- | :--- | :--- |
| v1.1.0 | 2026-10-09 | 待用户确认 | 桌面双布局模式定义、工具栏/侧栏操作映射、标签页精致化、平台窗口装饰策略与保活约束 |

---

## 1. 意图与设计原则 (Intent & Principles)

### 1.1 背景与用户诉求
KeiTerm 主界面当前为传统桌面终端形态（SecureCRT / Xshell 风格）：顶部包含 Native 菜单栏与全宽工具栏，左侧为可折叠连接管理器，中部/右侧为基于 Dock 拓扑的多连接工作区。
用户明确希望引入 **Classic 与 Modern 两种布局模式共存**：
1. **Classic 模式（开箱默认）**：保留传统操作结构（全功能菜单栏与全宽工具栏），功能入口直接外露且零学习成本。由于当前尚未实现首次启动向导/OOBE，**Classic 必须作为默认形态**。
2. **Modern 模式（可选）**：面向现代 IDE / 终端习惯，采用左侧紧凑操作导轨（Activity Rail）与高度集成的顶部操作带；**保留丰富快捷操作与强可发现性工具栏，不走 Wave 极端精简路线**。
3. **标签页（Tab）统一精致化**：当前 Dock 标签页的关闭按钮 `✕` 带有复古大方框边框。本期在两模式下**统一升级 Tab 细节**（小尺寸无边框 `✕`、微圆角悬停浅底色、足够点击热区、键盘焦点/无障碍名称、连接状态指示与长标题尾部省略）。

### 1.2 核心设计原则
- **操作结构与行为兼容**：Classic 模式保持既有操作结构与交互行为；Tab 美化在两模式下作为通用样式统一生效。
- **外壳精简，不重构 Dock 拓扑**：本期重点为压缩外部外壳（Menu / Toolbar / Sidebar Rail）。**现阶段 Dock 标签宿主维持各组内部宿主，不提升为跨窗格的“全局连接标签”**；若未来需要全局连接标签 + 内部面板模型，将作为后续拓扑子项目推进。
- **运行期模式切换与保活契约**：
  - 用户在设置中切换模式时，仅保存目标配置（Pending Mode），并温和提示用户“下次启动应用时生效”；
  - 在当前运行生命周期内，主窗口保持初始有效布局（Effective Layout），**不执行半切换、不拆除活动 Visual Tree、不重新实例化活动终端与 WorkspaceCoordinator、不中断当前已建立的 SSH / SFTP 连接**；
  - 只有当用户主动退出并重新启动应用程序时，新的布局模式才在启动期作为 Effective Mode 渲染生效（此过程为正常应用生命周期退出，会话随进程退出自然关闭，不属于异常断连）。
- **防御性跨平台策略**：不预设“标题栏融合在所有平台上完全可控”。未经验证前，Linux 默认保留原生系统装饰窗口，macOS / Windows 避让安全区基于平台 API 动态 Insets 计算与实机实测，不作固定像素硬保证。

---

## 2. 范围与非目标 (Scope & Non-Goals)

### 2.1 本期范围 (In-Scope)
1. **两种布局外壳规范**：
   - **Classic Mode**：Native 菜单栏 + 全宽横贯工具栏 + 经典左侧栏面板 + Dock 工作区 + 底部共享撰写栏。
   - **Modern Mode**：左侧狭长活动导轨（44px Activity Rail）+ 可收起/展开的连接侧栏 + 顶部紧凑操作带 + Dock 工作区 + 底部共享撰写栏。
2. **工具栏与菜单入口映射**：确保现有 `MainViewModel` 操作在 Modern 模式下全部可触达。
3. **侧栏与 Rail 交互**：连接管理器支持钉住（Pinned）、抽屉浮层（Unpinned Peek）、以及常驻 Rail 图标入口。
4. **Dock 标签页视觉重塑**：小尺寸无边框 `✕`、24×24px 点击判定热区、10×10px 矢量图标、3px 微圆角悬停背景、尾部省略（CharacterEllipsis）与状态指示条。
5. **多平台窗口装饰适配规范**与安全区策略。

### 2.2 明确非目标 (Non-Goals)
- **不重构 Dock 拓扑**：不修改各窗格独立的标签栏机制，不实现跨组全局连接管理器混搭。
- **不做独立浮动窗口（No Floating Windows）与内置代码编辑器**。
- **不使用 Emoji 替换 Lucide 矢量图标**：保持现有 24×24px 矢量资产体系（`KeiIcons.axaml`）。
- **不引入额外快捷键冲突**：Modern 模式完全复用现有快捷键体系（`AppShortcuts`），不新增全局热键争抢终端输入。
- **不新增未读状态徽标等未定义业务功能**。

---

## 3. 布局形态与实现边界 (Layout Modes & Topology Boundaries)

### 3.1 Classic 模式（默认）
- **顶部**：`NativeMenuBar`（Row 0）+ 全宽工具栏 `Border`（Row 1，包含会话组、状态消息、视图切换）。
- **主体**：左侧 260px 连接管理器面板 + 垂直分割条 + 右侧 Dock 工作区（`TerminalWorkspaceView`）。
- **底部**：共享撰写栏（Compose Bar）。

### 3.2 Modern 模式（可选）
- **左侧**：44px Activity Rail，自上而下承载主菜单按钮、会话树展开切换、SFTP 开关、凭据管理与设置。
- **顶部**：紧凑操作带，包含当前活动组快捷操作（新建标签、快速连接、连接、断开、搜索/命令面板）。
- **主体与拓扑边界（关键规范）**：
  - **现有 Dock 标签宿主保持不变**：当前分屏（Split）各组内的 Tab 栏继续由 Dock 自身的 `DocumentControl` 渲染。
  - **实现边界**：Modern 模式仅压缩窗口最外层的 Menu / Toolbar / Rail 外壳，**不将组内标签提升为窗口级全局连接标签**，避免暗改现有工作区拓扑或引入多连接视图混乱。

---

## 4. 菜单与操作映射矩阵 (Action Mapping)

全部复用当前代码已存在的 ViewModel 命令与快捷键定义（见 `MainWindow.axaml`、`MainWindow.Shortcuts.cs`、`AppShortcuts.cs`）：

| 业务功能 | 现有命令 / 绑定源 | Classic 呈现 | Modern 呈现 | 现有按键/手势 |
| :--- | :--- | :--- | :--- | :--- |
| **主菜单** | `NativeMenu.Menu` | 顶部 `NativeMenuBar` | Rail 顶部菜单按钮（弹出菜单） | `Alt` 激活 (Win/Linux) |
| **新建标签** | `NewTabCommand` | 工具栏 `NewTabButton` | 顶部快捷组 `[ ➕ ]` | `Ctrl/Cmd + T` |
| **快速连接** | `QuickConnectCommand` | 工具栏 `QuickConnectButton` | 顶部快捷组 `[ ⚡ ]` | `Ctrl/Cmd + Q` (Mac: `Cmd+K`) |
| **新建会话** | `CreateSessionCommand` | 侧栏面板头操作组 | 侧栏面板头 / 菜单 | `Ctrl/Cmd + N` |
| **关闭标签** | `CloseCurrentWorkspaceTabCommand` | 菜单项 / 标签项 `✕` | 标签项 `✕` | `Ctrl/Cmd + W` |
| **会话侧栏开关**| `ToggleSessionManagerPinCommand` | 工具栏 `SessionManagerToggleButton` | Rail `[ 🖥 ]` 图标 | 无独立全局键（点选/悬停） |
| **SFTP 侧栏** | `ToggleFileManagerCommand` | 工具栏 `FileManagerToggleButton` | Rail / 顶部 `[ 📁 ]` 图标 | - |
| **凭据管理** | `OpenIdentityManagerCommand` | 工具栏按钮 / 菜单项 | Rail `[ 🔑 ]` 图标 | - |
| **全局设置** | `OpenSettingsCommand` | 工具栏按钮 / 菜单项 | Rail 底部 `[ ⚙ ]` 图标 | - |
| **命令面板** | `OpenCommandPaletteCommand` | 工具栏放大镜按钮 | 顶部快捷组 `[ 🔍 ]` | `Ctrl/Cmd + Shift + P` |

*(注：本期不新占终端输入快捷键，所有操作均复用现有已注册命令)*

---

## 5. 左侧活动导轨 (Activity Rail) 与侧栏交互

- **宽度与样式**：固定 `44px`，背景 `{DynamicResource Kei.Bg.PanelAlt}`，右边框 `{DynamicResource Kei.Border}`。
- **图标尺寸**：内部按钮统一使用现有 `iconBtn` 规范（30×30px，内部 Viewbox 16×16px，Lucide 矢量）。
- **交互逻辑**：
  - 点击 `SessionsRailButton`：
    - 若侧栏收起，则展开侧栏（宽度由现有 `_lastSidebarWidth` 决定）；
    - 若侧栏处于固定展开状态，则彻底收起以最大化终端视野。
  - 悬停浮层（Peek）：侧栏未固定时，悬停保持现有 `MainWindow.Sidebar.cs` 延迟响应逻辑（220ms 延时收起，`ESC` 恢复终端输入焦点）。

---

## 6. Dock 标签页 (Tab) 视觉重塑规约 (两模式统一生效)

针对 `KeiDockTheme.axaml` 中 `dc|DocumentTabStripItem` 的外观进行针对性优化：

1. **尺寸与间距**：
   - 标签高度设为 `30px`，项内边距 `6,2`，项外边距 `0,0,2,0`。
   - 选中与悬停颜色严格引用现有设计系统令牌（`Kei.Bg.TabInactive`、`Kei.Bg.Panel`、`Kei.Bg.Hover`、`Kei.Accent`）。
2. **关闭按钮 (`CloseButton`) 规范**：
   - **消除边框与背景**：常态 `Background="Transparent"`、`BorderThickness="0"`。
   - **图标几何与尺寸**：矢量 `PART_ClosePath` 保持 `10×10px` 紧凑尺寸，统一使用 Lucide `Close` 几何。
   - **点击热区**：外层容器保持 `24×24px` 充裕命中判定面，内层图标居中，满足防误触与易点击要求。
   - **悬停反馈**：悬停时呈现 `3px` 微圆角（`CornerRadius="3"`），底色为 `{DynamicResource Kei.Bg.Hover}`，前景为 `{DynamicResource Kei.Text.Primary}`；按下时呈现 `{DynamicResource Kei.Bg.Pressed}`。
   - **可访问性**：关闭按钮附加 `AutomationProperties.Name="{loc:KeiString Main.Tab.Menu.Close}"` 与 `ToolTip.Tip`。
3. **指示器与文本省略**：
   - **状态指示条**：宽度 `3px`，高度 `14px`，圆角 `1px`，由 `ConnectionStateBrush` 转换器驱动。
   - **后台活动指示**：`6×6px` 圆点，绑定 `HasActivity`。
   - **长标题截断**：`TextBlock` 显式设置 `TextTrimming="CharacterEllipsis"`（**统一尾部省略**，不作居中截断），`VerticalAlignment="Center"`，最大宽度 `200px`，通过 `ToolTip.Tip` 完整展示。

---

## 7. 多平台窗口装饰与系统安全区策略 (Cross-Platform Strategy)

### 7.1 平台差异与保守适配
Avalonia 在各平台的窗口边框扩展（`ExtendClientAreaToDecorationsHint`）机制与窗口管理器行为存在显著差异，采用如下策略：

1. **macOS**：
   - 启用客户端边框扩展。
   - **安全区处理**：左上角交通灯避让距离需结合 Avalonia 运行时 `WindowDecorationMargins` 或平台 Insets 动态计算（非固定常数），草图示意预留约 70~80px 保护区，实机根据当前系统缩放核对。
2. **Windows (11 / 10)**：
   - 启用客户端边框扩展。
   - **安全区处理**：右上角最小化/最大化/关闭按钮与 Snap Layout 区域通过系统 Insets 动态避让（草图示意约 130~140px），非可交互区域调用窗口原生拖动 API。
3. **Linux (X11 / Wayland)**：
   - **默认保守策略**：**保留原生系统标题栏（`ExtendClientAreaToDecorationsHint = false`）**。
   - **原因**：Linux 桌面环境（GNOME CSD、KDE SSD、平铺 WM）窗口装饰高度碎片化，强行客户区扩展可能导致窗口无法拖动或双击失效。本期不引入激进的标题栏融合，不增加复杂的实验性环境检测分支，优先保证稳定性。

---

## 8. 状态管理与会话保活契约 (State & Lifecycle)

### 8.1 模式切换生效机制
- **配置持久化**：新增 `AppSettings.LayoutMode`（`"Classic"` \| `"Modern"`），保存至用户配置。
- **本期推荐生效方式**：**修改设置后，提示用户“将在下次启动应用时生效”**。
  - 用户正常退出应用重新启动时，SSH 会话自然断开；
  - 但在运行期修改设置并保存配置的过程中，**严禁对当前活动窗口、WorkspaceCoordinator 或底层 SSH 连接进行任何重置或中断**。

### 8.2 核心会话绝对保活契约
- 无论何种界面调整，只要应用在同一运行生命周期内：
  - `WorkspaceCoordinator` 与其容纳的 `TerminalWorkspaceDocument` 必须保持单例；
  - 严禁触发终端底层控件重新实例化；
  - 严禁在 Visual Tree 中出现同一视图控件的双重挂载（Double-Attachment）。

---

## 9. 验证方案与职责划分 (Verification)

1. **Subagent (Designer) 职责**：
   - 负责本设计规约的静态契约推导、设计系统 Token 引用一致性核对、以及现有 XAML 架构与代码源的准确引用核实。
   - 不修改产品代码、不引入外部依赖、不执行 git 提交。
2. **主会话与用户验证**：
   - 主会话负责依据本 spec 审查后续实现计划；
   - 跨平台运行期视觉效果（macOS 交通灯边距、Windows 顶栏拖动、Linux 原生装饰协调度）由主会话在对应物理环境实机验证。

---

## 10. 核心实现策略确认 (Confirmed Strategy)

> **已确认的布局模式切换策略（选项 A - 稳健直接）：**
> - **设置持久化与提示**：模式切换修改持久化至 `AppSettings.LayoutMode`，设置界面给出友好提示：*“布局模式已更新，将在下次启动时生效”*；
> - **运行期不半切换**：当前窗口在生命周期内维持启动时锁定的有效模式，绝不进行运行期半切换或动态拆解 Visual Tree；
> - **常规外观即时刷新保持原样**：主题切换、GUI 配色方案、树密度等常规外观属性继续保持现有的实时热响应机制，不受布局模式“下次生效”策略影响；
> - **明确生命周期与断连界限**：在设置页修改布局配置时，活动 SSH / SFTP 会话和终端控件完全保活；后续用户主动退出并重启应用时，进程生命周期正常终结并断连，新进程启动按新布局渲染。
