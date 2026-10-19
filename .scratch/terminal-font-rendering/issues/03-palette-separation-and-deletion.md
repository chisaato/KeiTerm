# 配色与字体解耦、用户配色标签删除及事务回滚

Type: task
Status: resolved
Blocked by: 无

## 交付要求

配色方案不决定字体；允许删除用户配色标签，但不能删除内置方案，删除与默认方案切换遵守设置事务。

## Answer

已在 `e2b5ca4` 完成：

- `TerminalProfile` 与 bundle 序列化不再携带字体设置，终端字体由独立全局快照提供；切换配色不改字体，Konsole 字体导入不改配色。
- 用户配色删除需确认，内置方案、空选择及忙碌状态拒绝删除；删除先作用于工作草稿，应用才提交，取消恢复已提交方案与默认选择。
- 已打开终端对方案编辑、删除和回滚的响应见 [05](05-open-terminal-preview-and-rollback.md)。

## 验证依据

- 源码：[配色模型](../../../../src/Kei.Term.Core/Models/Profiles/TerminalProfile.cs)、[Profile 管理](../../../../src/Kei.Term.App/Services/ProfileManagerService.cs)、[设置方案命令](../../../../src/Kei.Term.App/ViewModels/SettingsViewModel.Profiles.cs)。
- 测试：`ProfileBundleSerializerTests`、`TerminalProfileDeletionTests`、`TerminalProfileApplicationTests`，覆盖模型解耦、删除保护、确认、提交和恢复。
- 总体验证来源见 [spec](../spec.md)；本次仅核对文档，未重跑测试。
