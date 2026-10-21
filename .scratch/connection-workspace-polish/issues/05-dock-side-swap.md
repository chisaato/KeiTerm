# 恢复左右换边：按钮、右键菜单与标题栏拖拽

Type: task
Status: resolved
Blocked by: 无

## 调查结论

- 文件管理标题栏的 `FileManagerDockButton`（DockSwap 图标，“切换左/右侧停靠”）由 `ae57078` 引入，在 `d8c608c`（“feat: 添加凯伊和爱丽丝配色”）中随工具栏精简被删除，提交说明未写原因。
- 底层能力一直保留：`TerminalTabViewModel.ToggleDockPosition`、`RemoteFileManagerViewModel.ToggleDockPositionRequested`、`TerminalConnectionView` 三列 Grid、全局设置 `FileTransferSettings.IsFileManagerOnLeft` 与 `FileManagerDockChangedMessage`。
- “任意排布”另有“在标签页中打开”（`PromoteToTabCommand`，ADR-0001）可进入 Dock 分屏，但入口不显眼。

## Answer

- 恢复 `FileManagerDockButton`，仅侧栏模式可见（文件标签/最大化时隐藏）。
- 终端与文件管理标题栏右键菜单：移到左侧 / 移到右侧（当前侧禁用）、在标签页中打开、隐藏文件管理。新增 `SetDockPosition`、`MoveFileManagerToLeft/Right`、`MoveToLeft/RightCommand`。
- 标题栏空白处拖拽换边（`TerminalConnectionView.DockDrag.cs`）：超过 8px 阈值才进入拖拽，排除按钮/输入框/GridSplitter 命中；越过中线时 `DockDropOverlay` 高亮目标侧，松手提交。判定逻辑在纯函数 `DockDragDropHelper`。
- 语义：按钮/菜单/拖拽只改当前连接；全局设置决定新连接默认侧，设置变更仍广播到已开连接。

## 验证记录

- `DockDragDropHelperTests`（目标侧判定、阈值）；`RemoteFileManagerLayoutTests`、`FileManagerWorkspaceTests`、`TerminalFileManagerLifecycleTests` 通过。

## Comments

2026-10-21 用户桌面验收通过。
