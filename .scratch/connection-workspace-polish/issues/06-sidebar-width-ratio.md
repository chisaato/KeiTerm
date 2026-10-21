# 文件管理侧栏宽度按比例并可在设置中配置

Type: task
Status: resolved
Blocked by: 无

## 问题

侧栏宽度硬编码 460px（`TerminalConnectionView.axaml.cs` 的 `_lastFileManagerWidth` 与 `FileManagerColumnWidthConverter`），不随窗口缩放；拖动后的宽度只记在当前视图实例中，不持久化，也没有上下限。宽屏下明显偏窄。

## Answer

- 新增设置 `FileTransferSettings.FileManagerSidebarRatio`，默认 40%（用户确认），范围 20%–70%，设置页数字框步长 5%。
- `Kei.Term.Core/Settings/FileManagerSidebarSizing`：宽度 = 比例 × 连接区宽度，通常最小 320px；优先为终端保留 240px，并扣除 4px 分隔条，窗口极窄时侧栏可低于 320px。
- 容器缩放时按比例跟随；用户拖动分隔条后该视图改记自身比例，不回写全局设置。
- 设置变更通过消息通知已开连接（`TerminalTabViewModel.SidebarSizing.cs`）：未手动拖动的视图应用新比例，拖动过的保留自身比例。
- 删除不再引用的 `FileManagerColumnWidthConverter`；隐藏动画、最大化、仅文件视图占满行为保持。

## 验证记录

- `FileManagerSidebarSizingTests`：钳制、缩放跟随、拖动转比例、设置通知、JSON 往返；`SidebarTransitionTests` 覆盖左右侧隐藏动画。

## Comments

`SidebarTransitionTests` 左侧用例在全量高负载下偶发失败一次，见 [spec](../spec.md)。
