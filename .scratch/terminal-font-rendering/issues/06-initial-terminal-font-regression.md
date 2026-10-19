# 首次连接字体过大：启动阶段默认字体草稿污染

Type: task
Status: resolved
Blocked by: 无

## 现象与根因

`e2b5ca4` 引入热重载后的回归：首次连接字号偏大，打开设置后才恢复磁盘配置字号。不是缩放问题。

App 在加载 `settings.json` 之前构造 `SettingsViewModel`。旧构造路径的 `Reload()` 会广播默认 14pt，污染 `ProfileManagerService.WorkingTerminalFontSnapshot`；随后虽已加载磁盘 11pt，新标签仍优先读取默认缓存。真正打开设置再次 Reload 才广播 11pt。

## Answer

已修复，随本办结记录一并提交：

- 构造函数执行 `Reload(broadcastTerminalFontDraft: false)`，只初始化表单，不发布启动阶段默认草稿。
- 公共 `Reload()` 保留广播，真实打开设置时发布已加载配置的草稿，不削弱设置预览能力。
- `TerminalInitialFontTests.cs` 按真实启动顺序先构造 VM，再加载磁盘设置、初始化 Profile 管理器，打开首个终端并模拟输出；在 1.25 缩放下比较打开设置前后及设置预览的真实 renderer / cell 指标。

## 验证记录

- 主会话复现记录：修前打开设置使 renderer 字号从 18.67 降至 14.67 DIP，cell 从 11×25 变成 9×17；修后前后相同。这些为该环境观察值，不作为跨字体/平台固定断言。
- 测试断言首次终端与 Reload 后状态一致，保存的 11pt、字体族和真实字重一致，并与 1.25 缩放的设置预览 renderer 字号及 cell 尺寸一致。
- 主会话报告 build 成功；1320 通过、36 跳过、0 失败；已有一处 CS8602 警告。本次文档任务未重跑。
- 已核对：[启动顺序](../../../../src/Kei.Term.App/App.axaml.cs)、[构造与 Reload 修复](../../../../src/Kei.Term.App/ViewModels/SettingsViewModel.cs)、[回归测试](../../../../tests/Kei.Term.Tests/TerminalInitialFontTests.cs)。

## Comments

本项已解决的是配置加载/草稿广播时序回归，不证明 [01](01-lcd-subpixel-vs-grayscale.md) 的字形略淡问题已解决。总体办结边界见 [spec](../spec.md)。
