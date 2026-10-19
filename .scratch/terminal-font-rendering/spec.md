# 终端字体、配色与已打开终端预览交付

Status: resolved

## 范围与办结口径

本 spec 为本轮已经实施的交付补录：主体依据提交 `e2b5ca4` 和当前代码；首次连接字体过大的回归修复随本办结记录一并提交。`resolved` 表示下列实施范围已办结，不表示所有渲染差异研究均已解决。

## 已交付

- [02 主字体真实字重、Konsole 导入与独立字重回退链](issues/02-font-weight-import-and-fallback.md)：真实字体字重、旧族名归一化、Konsole profile 仅导入字体、回退链及旧配置迁移。
- [03 配色与字体解耦、用户配色标签删除及事务回滚](issues/03-palette-separation-and-deletion.md)：配色不再携带字体，删除仅限用户方案，保留确认与提交/取消语义。
- [04 字体设置入口、紧凑下拉及符号预览](issues/04-font-settings-picker-and-symbols.md)：导入菜单、紧凑字体选择器、符号图例与 Nerd Font（NF）预览图标。
- [05 已打开终端的外观草稿热重载与回滚](issues/05-open-terminal-preview-and-rollback.md)：字体和配色草稿实时生效，取消、关闭或应用失败恢复已提交状态。
- [06 首次连接字体过大回归修复](issues/06-initial-terminal-font-regression.md)：构造设置 VM 不发布尚未加载磁盘配置的默认字体；真实打开设置才广播当前草稿。

## 不在已完成范围

- [01 字形略淡的渲染差异研究](issues/01-lcd-subpixel-vs-grayscale.md)仍为 `wontfix`（暂缓）；LCD / 灰度归因未严格确认，历史 ROI 及裸 Skia 实验有明确证据边界。
- 用户不接受自动字重或字形补偿；不以自动加粗掩盖差异。
- 真实终端控件的光标闪烁全局覆盖暂不支持。草稿快照中保存/传播 `CursorBlink` 不等于真实控件已实现该能力。

## 验证记录与来源

- 文档补录已对照当前源码、对应测试及 `git show --stat e2b5ca4` 核对范围；不将该提交中其他无关改动纳入本 spec。
- 主会话提供的当前验证结果：build 成功，测试 1320 通过、36 跳过、0 失败；已有一处 CS8602 警告。本次文档任务未重跑构建或测试。
- 本次仅校验文档相对链接、状态和源码/测试事实一致性；最终检查与提交由主会话负责。
