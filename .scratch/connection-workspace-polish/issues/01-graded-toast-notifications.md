# 右下角分级 Toast 替代顶部消息

Type: task
Status: resolved
Blocked by: 无

## 问题

`MainViewModel.StatusMessage` 显示在 Classic 与 Modern 两种布局的顶部工具栏中央，所有 `IInteractionService.NotifyAsync` 也写入这里：永不自动清除、固定 `Kei.Status.Connecting` 色、无级别、单行截断无法看全文。仓库此前没有任何 toast / InfoBar 设施。

## Answer

- 新增 `NotificationLevel`（Info / Success / Warning / Error），`IInteractionService.NotifyAsync(title, message, level = Info)`；所有调用点显式指定级别（连接失败、上传失败、保管库错误为 Error；缺文件、认证材料不可用、拖入目录为 Warning；导入、复制会话、批量编辑为 Success，带警告的导入降为 Warning）。
- `ToastManagerViewModel` / `ToastItemViewModel`：最多 4 条，超出淘汰最旧；相同标题+内容+级别去重并刷新计时；Success/Info 4s、Warning 8s 自动消失，Error 常驻；悬停暂停计时；长文本可展开、可复制全文。时间源可注入。
- `ToastItemView` 挂在主窗口工作区右下角；两处工具栏中的 StatusMessage 文本与 `MainViewModel.StatusMessage` 属性移除，原直接赋值点改走同一通道。
- 批量编辑仅在 `ChangedCount > 0` 时提示成功（原先判断 `ApplyAttempted`，保存失败也会提示完成）。
- 新增 `Kei.Status.Info` token。

## 验证记录

- `tests/Kei.Term.Tests/ToastNotificationTests.cs`：容量淘汰、去重、各级别自动消失、Error 不自动消失、悬停暂停与恢复、展开条件、复制全文。
- XAML 正文字号统一 ≥12（`CompactUiContractTests`）。

## Comments

2026-10-21 用户桌面验收通过。验收反馈：自动消失缺少可见提示，追加倒计时进度条（见 [07](07-toast-countdown-bar.md)）。
