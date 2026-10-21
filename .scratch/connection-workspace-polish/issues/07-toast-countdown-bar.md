# Toast 倒计时进度条

Type: task
Status: resolved
Blocked by: 无

## 问题

验收 [01](01-graded-toast-notifications.md) 时用户反馈：自动消失已生效，但没有可见的倒计时，第一反应不知道 Toast 何时消失。属于小改动，非根本性功能更新。

## 期望

- 会自动消失的 Toast（Success/Info 4s、Warning 8s）底边显示约 2px 的倒计时条，颜色取级别状态色，从满到空线性收缩；Error 不显示。
- 进度与真实计时同源（`ToastItemViewModel` 的 `TimeProvider` / `ITimer`）：悬停暂停时停住，移出后从剩余位置继续，不跳变不漂移。
- 重复消息（`ToastManagerViewModel.Show` 去重分支）应把倒计时重置为满；现状只调用 `ResumeDismiss`，对未暂停的项不会重置计时。

## 验收

- `ToastNotificationTests` 覆盖：剩余比例随时间递减、暂停不变恢复继续、重复消息重置、Error 无倒计时。
- 用户桌面确认倒计时条可见且节奏正确。

## Answer

- `ToastItemViewModel` 新增 `RemainingTime` / `RemainingFraction`，读取时由真实计时状态（注入的 `TimeProvider`）计算，暂停时停住、恢复后续算，Error 恒为 0；不触发 PropertyChanged。
- 新增 `RestartDismiss()`；`ToastManagerViewModel.Show` 去重分支改调它，重复消息把倒计时重置为满（悬停中则保持暂停，移出后从满值开始）。
- `ToastItemView`：底边 2px `CountdownBar`，颜色复用 `LevelToBrush`，`HasAutoDismiss` 为 false 时隐藏；控件加载后以 33ms `DispatcherTimer` 按 `RemainingFraction` 设置宽度，卸载时停止。

## 验证记录

- `ToastNotificationTests` 新增 5 例：线性递减（Info 4s / Warning 8s）、暂停不变恢复继续、重复消息重置且原到期后仍在、悬停中重复消息重置并保持暂停、Error 无倒计时。
- 主会话：build 0 错误 0 警告；全量 1421 通过、36 跳过、0 失败。

## Comments

2026-10-21 用户桌面验收通过。
