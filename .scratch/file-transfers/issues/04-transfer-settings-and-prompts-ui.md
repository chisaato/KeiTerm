# 04 传输设置页、冲突询问弹窗与重试操作（界面）

Status: resolved
Blocked by: 03

见 `../spec.md`。交给 @designer 做，文案最后由主会话校对。

## 范围

- 设置窗口的文件传输分区，加入 spec 中所有新增设置项：并发数（最小 1、不设上限的数字输入）、默认下载目录（带"浏览"按钮）、默认冲突策略、续传开关、自动重试组（开关、次数、退避方式、初始间隔、最大间隔）。
- 冲突询问弹窗：列出源和目标的大小、修改时间；提供覆盖、跳过、目标较旧才覆盖、自动改名四个选项；带"对本批次剩余的冲突都这样处理"勾选。
- SCP 大文件确认弹窗。
- 现有活动抽屉里补上：每个任务的"重试"、队列的"重试全部失败项"，以及"已跳过"状态的展示。
- 文件视图右键菜单加"下载到…"。
- 只做最小的可用界面。传输队列放在界面哪里不在本 ticket 范围内，不要调整抽屉的整体结构。

## 验收

- 新增窗口和控件遵守 Avalonia 紧凑令牌（`avalonia-compact-tokens`），并通过 XAML 契约测试。
- 新增的文案进入本地化资源（中文和英文）。

## Comments

### 实施记录与验证

1. **设置窗口（文件传输页）**:
   - 在 `FileTransferSettingsPage` 与 `SettingsWindow.axaml` 中添加了完整的新增设置项：
     - 并发传输数（`Concurrency`，`NumericUpDown` 最小值为 1，无上限）
     - 默认下载目录（`DefaultDownloadDirectory`，输入框 + `Browse` 浏览文件夹按钮）
     - 默认冲突策略（`DefaultConflictPolicy`，下拉五选项：每次询问、覆盖、跳过、较旧才覆盖、自动改名）
     - 续传开关（`ResumeEnabled`，复选框）
     - 自动重试组（`AutoRetryEnabled` 开关联动禁用子项、`RetryCount` 次数输入、`BackoffMode` 退避方式下拉、`InitialRetryIntervalSeconds` 初始间隔、`MaxRetryIntervalSeconds` 最大间隔）
   - `SettingsViewModel.FileTransfer.cs` 补齐了与 `AppSettings.FileTransfer` 的读写同步。

2. **冲突询问与 SCP 大文件确认弹窗**:
   - `TransferConflictWindow.cs` 严格接入 KeiTokens（`Kei.Bg.Panel`、`Kei.Bg.Card`、`Kei.Border.Subtle`、`Kei.Text.Primary`、`Kei.Text.Secondary`、`Kei.Font.UI` 等）：
     - 显示源端与目标端的大小（格式化为 `FileSizeFormatter` 可读大小）与 UTC/Local 修改时间。
     - 提供覆盖（高亮 accent 按钮）、跳过、仅较旧时覆盖、自动改名四个操作。
     - 附带“对本批次剩余的冲突都这样处理”勾选框。
   - `ConfirmLargeScpAsync` 弹窗支持 SCP 大文件风险提示，包含取消与继续按钮。

3. **传输活动抽屉**:
   - 在 `RemoteFileManagerView.axaml` 的活动面板中：
     - 任务失败状态时展示“重试”按钮（绑定 `FileTransferTaskItemViewModel.RetryCommand`）。
     - 活动面板顶部工具栏增加“重试全部失败项”按钮（绑定 `RemoteFileManagerViewModel.RetryAllFailedTransfersCommand`）。
     - 支持“已跳过”（`Skipped`）状态的高亮样式与文本呈现。
     - 取消/移除按钮绑定文案更新。
   - `ConnectionTransferBoard.Status` 全面接入本地化词条。

4. **右键菜单与交互**:
   - `RemoteFileManagerView.axaml` 右键菜单加入“下载”与“下载到…”菜单项（绑定 `DownloadCommand` 与 `DownloadToCommand`）。

5. **本地化与契约测试**:
   - 新增词条完整写入 `Strings.resx`（中文）与 `Strings.en.resx`（英文）。
   - 通过 `CompactUiContractTests`、`IconSystemContractTests`、`SettingsFontListContractTests`、`TreeThemeContractTests` 针对性 XAML 契约测试。
   - 补充 `TransferConflictUiContractTests` 验证窗口与命令绑定契约。
   - 验证 `TransferSettingsTests` 覆盖设置页 ViewModel 绑定、修改与持久化往返。

### 状态（2026-10-17）

界面代码已落地。最终回归尚未通过，Status 保持 `ready-for-agent`，不标 resolved。设置里的并发数对 SCP 不生效，生产队列固定为 1；文案不把「大小相同」说成校验和。
