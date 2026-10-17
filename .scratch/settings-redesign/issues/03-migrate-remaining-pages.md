# 03 其余页面迁移到新布局

Status: resolved
Blocked by: 02

见 `../spec.md` 决策 2、6、7，复用 02 的控件与样板页模式。

## 范围

- 应用：常规（快捷键作为卡片保留；会话树排序迁出）、外观（界面回退字体下钻）、会话管理器（原会话树 + 排序）。
- 终端：行为、配色与字体（终端回退字体下钻）、标签栏（变量参考下钻，模板输入与预览留在主页）。
- 连接：SSH、代理（整页主从列表）、安全。
- 原 `*Tip` 转为说明行；低频项进入“显示高级选项”。

## 验证

- 构建与测试通过；所有原有设置项可达、保存与取消行为不变。

## Comments

### 页面迁移与重构记录

1. **应用 / 常规 (`GeneralSettingsPageView.axaml`)**
   - 窗口行为与会话管理器固定移至卡片布局，原说明转为行内说明；
   - 原生系统菜单保留在常规页卡片内；
   - 快捷键卡片：命令面板录制行 + 重置按钮、标签页切换修饰键下拉，下方保留内置只读快捷键概览表；
   - 会话树排序迁出至“会话管理器”页。
2. **应用 / 外观 (`AppearanceSettingsPageView.axaml`)**
   - 布局模式与主题档案卡片化；
   - 界面主字体保留在主卡片，界面回退字体（优先级列表与增删调序）做成**摘要行 + 下钻编辑**（`UiFallbackFontsSubPage` / `UiFallbackFontsSubPageView`）；
   - 右侧实时保留 UI 字体完整渲染样例面板。
3. **应用 / 会话管理器 (`TreeSettingsPageView.axaml`)**
   - 整合从常规页迁入的“会话树排序规则”卡片；
   - 密度预设保留在卡片主体，精确几何微调（行高、字号、图标大小、缩进）收纳进卡片内的“高级微调”折叠区。
4. **终端 / 行为 (`TerminalSettingsPageView.axaml`)**
   - 回滚缓冲行数放在主体设置行；默认终端仿真类型（`DefaultTerminalType`）作为低频项进入折叠区。
5. **终端 / 配色与字体 (`TerminalAppearanceSettingsPageView.axaml`)**
   - 终端配色方案档案、主字体、字号、光标闪烁在主卡片；
   - 终端回退字体做成**摘要行 + 下钻编辑**（`TerminalFallbackFontsSubPage` / `TerminalFallbackFontsSubPageView`）；
   - Konsole、JSON 导入按钮本地化整理；右侧保留实时终端黑框渲染预览。
6. **终端 / 标签栏 (`TabBarSettingsPageView.axaml`)**
   - 标签栏位置与标题行为卡片化；
   - 标题模板输入框与实时计算预览保留在主卡片；详细的变量参考手册（`{remote_hostname}`, `{remote_path}`, `{remote_ip}` 等）做成**摘要行 + 下钻**（`TabBarVariablesSubPage` / `TabBarVariablesSubPageView`）。
7. **连接 / SSH (`SshSettingsPageView.axaml`)**
   - 默认端口、用户名、连接超时、默认身份、保活与重连各自卡片化；
   - 系统 Agent、转发开关在主卡片，自定义 Agent 套接字路径低频项折叠。
8. **连接 / 代理 (`ProxySettingsPageView.axaml`)**
   - 整页式主从列表，按卡片容器包装，不下钻。
9. **连接 / 安全 (`SecuritySettingsPageView.axaml`)**
   - 主密码设置/移除与保管库锁定超时、本机快速解锁卡片化。

### 遗留修复与清理

- 移除了未被引用的旧视图 `Views/Settings/FileTransferSettingsPageView.axaml` (.cs) 及在 `SettingsWindow.axaml` 中的老数据模板。
- 本地化修复：`DrillDownRow.ActionLabel`、`SettingsCard.AdvancedHeader`、`NoAlgorithmsEnabled`、`NoFormatsEnabled`、`ArchiveFormat.Tar`、`UnknownEditor`、`DatabaseOperationFailed` 以及 Konsole/iTerm 导入等文案全量加入中英文 resx。
- 契约与单元测试全量绿灯通过。
