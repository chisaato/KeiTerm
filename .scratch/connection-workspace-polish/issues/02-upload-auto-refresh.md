# 上传完成自动刷新当前目录

Type: task
Status: resolved
Blocked by: 无

## 根因

`RemoteFileManagerViewModel.UploadLocalFilesAsync` 只把任务交给 `ConnectionTransferBoard.EnqueueUploads` 即返回；`OnTransferActivityChanged` 只更新计数与动画 revision，没有在上传完成时刷新。终端拖放上传之所以会刷新，是因为 `TerminalTabViewModel.FileDrop` 自己在 await 之后调用了 `RefreshDirectoryAsync`。

## Answer

- `RemoteFileManagerViewModel.Activity.cs`：任务 `State` 变为 `Completed` 且方向为 Upload 时，比较规范化后的 `task.RemotePath`（上传时为目标父目录，见 `TransferQueue.cs` 注释）与 `CurrentPath`，一致则 150ms 防抖后刷新；多个任务连续完成合并为一次；已 dispose 或已切换目录则放弃。
- 同时提供面板头部汇总（进行中数量 · 总速度）与“定位到远端目录”命令（下载取源文件父目录）。

## 验证记录

- `FileTransferTaskItemViewModelTests`：连续两个上传完成只刷新一次；目标目录不同不刷新；已销毁不刷新。

## Comments

“定位到远端目录”的多视图限制见 [spec](../spec.md)。
