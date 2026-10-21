# Linux 中文输入法（fcitx5 / IBus）接入

Status: resolved

## 范围与办结口径

本 spec 补录 Linux 输入法问题的诊断结论和修复。修复代码已随提交 `7e641a6` 合入。`resolved` 只表示 X11（XWayland）路径的 IME 接入已办结；Wayland 原生后端单独作为待评估项，见 [02](issues/02-wayland-native-backend.md)。

## 结论

- 链路本身完整：Avalonia 12.1.3 X11 后端默认 `EnableIme = true`，fcitx5 走 DBus（`org.fcitx.Fcitx`）。RoyalTerminal 0.6.2 的 `TerminalControl` 自带 `TextInputMethodClient`，支持 preedit 与候选框定位。`AppShortcutAwareInputAdapter` 透传了组合态。
- 根因是环境变量：Avalonia 只看 `AVALONIA_IM_MODULE` / `GTK_IM_MODULE` / `QT_IM_MODULE` / `XMODIFIERS` 来选 IME。Plasma Wayland 推荐不设 `*_IM_MODULE`，从桌面菜单启动的进程又拿不到 `XMODIFIERS`，四个变量全空，于是终端和普通输入框都切不出中文。

## 已交付

- [01 输入法环境兜底检测](issues/01-ime-environment-fallback.md)：未声明输入法时，按运行中的守护进程写入 `AVALONIA_IM_MODULE`。

## 待评估

- [02 Wayland 原生后端](issues/02-wayland-native-backend.md)：`Avalonia.Wayland` 通过 text-input-v3 接 IME，有全局菜单、窗口定位等代价。
