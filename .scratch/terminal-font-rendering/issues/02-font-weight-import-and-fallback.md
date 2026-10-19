# 主字体真实字重、Konsole 字体导入与独立字重回退链

Type: task
Status: resolved
Blocked by: 无

## 交付要求

按字体真实字重选择主字体；支持 Konsole profile 字体导入；回退字体按顺序逐项配置独立字重，兼容旧配置。不自动加粗或补偿字形。

## Answer

已在 `e2b5ca4` 完成：

- `TerminalFontWeight` 与 `TerminalFontSnapshot` 携带主字体字重，`TerminalTypefaceProvider` 选择实际 typeface；扫描器识别真实字重并归一化旧式族名。
- Konsole profile 导入解析字体族、字重及 pt 字号，只更新字体草稿，不导入配色；应用前不落盘，取消恢复。
- `TerminalFallbackFonts` 使用含 `Family` / `Weight` 的有序条目，支持增删、调整顺序与单项字重；旧 `TerminalFallbackFontFamily` 迁移为新列表，保存不再写旧字段，明确保存的空列表不被默认值重填。

## 验证依据

- 源码：[字体命名与解析](../../../../src/Kei.Term.Core/Fonts/FontFaceNaming.cs)、[回退迁移](../../../../src/Kei.Term.Core/Settings/AppSettings.cs)、[typeface 选择](../../../../src/Kei.Term.App/Terminals/TerminalTypefaceProvider.cs)、[导入入口](../../../../src/Kei.Term.App/ViewModels/SettingsViewModel.FontImport.cs)。
- 测试：`FontFaceNamingTests`、`TerminalFontWeightTests`、`FallbackFontSettingsTests`，覆盖真实字重、旧族名、只导入草稿、应用/取消和旧配置迁移。
- 总体验证来源见 [spec](../spec.md)；本次仅核对文档，未重跑测试。
