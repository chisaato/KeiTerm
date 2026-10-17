# 04 修复：新布局下大量设置控件不显示

Status: ready-for-agent
Blocked by: —

见 `../spec.md`。WIP 提交之后由用户桌面验收发现（2026-10-18）。

## 现象

- 终端配色与字体：“终端配色方案”卡片只剩“新建… / 编辑 / 调色…”按钮，方案下拉不见了；“终端字体”卡片只剩几条空分隔线，字体和字号控件都没了。
- 标签栏：“布局”卡片是空的，标签位置下拉不见了。
- 外观：“界面模式”“界面主题”卡片是空的，“界面字体”卡片只剩回退字体下钻行。
- 其他页面估计也有同样的问题，需要逐页核对。

## 初步判断（未验证）

`Views/Settings/Controls/SettingRow.axaml` 是 `UserControl`，它自己的布局 Grid 放在 `Content` 里；调用方在 XAML 里塞进子控件时，同样写的是 `Content`，于是把 Grid 替换掉了。内部 `ContentPresenter` 又把 `Content` 绑回自身，结果整行渲染成空白。`SettingsCard`、`DrillDownRow` 等控件也要检查是否有同样的问题。

修复方向：把这些控件改成 `ContentControl` / `TemplatedControl` + `ControlTemplate`（样式放 `SettingsStyles.axaml`），或者把右侧内容改为独立的属性（例如 `Control`），不要占用 `Content`。

## 验收

- 逐页对照 `69b6a3c`（重构前）的 `SettingsWindow.axaml`，确认每一个设置项都能在新布局中看到并操作。
- 补一个测试：能因“设置行内容不显示”而失败（例如在 headless 环境里实例化 `SettingsWindow`，切换到各页，断言关键控件在视觉树中可见）。

## Comments
