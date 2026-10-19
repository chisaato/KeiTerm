# 字体设置导入菜单、紧凑下拉及符号预览

Type: task
Status: resolved
Blocked by: 无

## 交付要求

将字体导入纳入设置入口，提供紧凑字体下拉和可解释的字体符号标识，保留 NF 预览图标。

## Answer

已在 `e2b5ca4` 完成：

- 终端外观设置提供字体导入菜单及 Konsole profile 文件选择入口，移除原独立字体弹窗路径。
- 主字体与回退字体使用紧凑选择器、字体项目模板及符号图例；回退项可直接调整独立字重。
- NF 预览图标与设置字体符号模板落实到 XAML，并有真实控件布局/绘制测试覆盖。

## 验证依据

- 源码：[终端外观页](../../../../src/Kei.Term.App/Views/Settings/TerminalAppearanceSettingsPageView.axaml)、[回退字体页](../../../../src/Kei.Term.App/Views/Settings/TerminalFallbackFontsSubPageView.axaml)、[设置模板](../../../../src/Kei.Term.App/Views/Settings/SettingsStyles.axaml)、[字体图标](../../../../src/Kei.Term.App/DesignSystem/KeiFontIcons.axaml)。
- 测试：`SettingsFontPickerLayoutTests`、`SettingsFontSymbolsRenderingTests`、`SettingsPageInteractionBehaviorTests` 及 `Contracts/SettingsFontListContractTests`。
- 总体验证来源见 [spec](../spec.md)；本次仅核对文档，未重跑测试。
