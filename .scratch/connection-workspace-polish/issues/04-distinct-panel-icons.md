# 区分文件管理侧栏、目录树与最大化恢复图标

Type: task
Status: resolved
Blocked by: 无

## 问题

终端标题栏 `TerminalFileManagerToggleButton`（切换整个文件管理侧栏）用 `Kei.Icon.SidebarRight`（Lucide panel-right），文件管理标题栏 `FileManagerTreeToggleButton`（显示/隐藏目录树）用 `Kei.Icon.Sidebar`（panel-left），两者只是分隔线左右不同；文件管理最大化后的恢复按钮也用 `SidebarRight`。

## Answer

- 终端按钮改用 `Kei.Icon.FileManager`（文件夹，与 Modern Rail 一致），带 `.open` 选中高亮，Tooltip 随可见性切换“显示/隐藏文件管理”（`TerminalTabViewModel.FileManagerToggleToolTip`）。
- 目录树按钮改用新增 `Kei.Icon.ListTree`（Lucide list-tree），保留选中态。
- 最大化恢复按钮改用 `Kei.Icon.Minimize`。
- 图标经 `third-party/lucide/manifest.json` 登记后由生成器写入 `KeiIcons.axaml`。

## 验证记录

- `UiRefinementsContractTests` 更新终端按钮选中态与动态 Tooltip 断言；`RemoteFileManagerLayoutTests` 覆盖最大化/标签模式下按钮显隐。
