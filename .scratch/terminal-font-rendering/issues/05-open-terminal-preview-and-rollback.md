# 已打开终端的字体/配色草稿热重载与回滚

Type: task
Status: resolved
Blocked by: 无

## 交付要求

设置中的字体和配色草稿作用到已打开终端；取消、关闭设置或应用失败时恢复已提交外观，不把预览误当持久化。

## Answer

已在 `e2b5ca4` 完成：

- 字体快照统一广播到已打开标签，包含主字体、字重、字号与有独立字重的回退链；连续草稿变化合并到渲染调度，惰性标签不被强制创建控件。
- 默认配色草稿影响继承默认方案的标签，显式绑定标签保留自身方案；方案内容编辑、删除及恢复同步到真实控件。
- 取消、关闭和应用失败通过已提交基准恢复工作草稿及终端外观；应用成功后才推进提交基准。
- 能力边界：`CursorBlink` 会随快照传播，但真实终端控件的光标闪烁全局覆盖暂不支持，不计为本项已交付能力。
- 启动阶段默认快照污染另由 [06](06-initial-terminal-font-regression.md)修复，不将其归为缩放问题。

## 验证依据

- 源码：[字体广播](../../../../src/Kei.Term.App/ViewModels/MainViewModel.TerminalFonts.cs)、[草稿与回滚](../../../../src/Kei.Term.App/ViewModels/SettingsViewModel.AppearancePreview.cs)。
- 测试：`TerminalAppearanceHotReloadTests.cs` 中的 `TerminalProfileApplicationTests` 验证真实已打开控件的配色切换/编辑/删除/取消、字体合并刷新，以及关闭/应用失败恢复。
- 总体验证来源见 [spec](../spec.md)；本次仅核对文档，未重跑测试。
