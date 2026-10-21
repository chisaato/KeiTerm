# 评估接入 Avalonia.Wayland 原生后端

Type: research
Status: needs-triage
Blocked by: 无

## 背景

现在只引用了 `Avalonia.Desktop`，Linux 上 `UsePlatformDetect` 只会选到 X11，Wayland 会话下由 XWayland 承载。[01](01-ime-environment-fallback.md) 修好之后，X11 路径的输入法已经可用，所以这里不是阻塞项。

## 已知事实（基于 12.1.3 反编译）

- NuGet 上有 `Avalonia.Wayland`（12.1.0–12.1.4），依赖 `NWayland` 0.11.0 和 `Avalonia.FreeDesktop`。
- 接法：`builder.UsePlatformDetect().UseWaylandWithFallback()`，Wayland 初始化失败时回落到 X11。`Program.cs` 里已有的 `KEITERM_BACKEND=x11` 可以作为回退开关。
- IME 只走 text-input-v3（`WaylandTextInputV3`），没有 DBus fcitx 回退。KWin 的「虚拟键盘」必须选 fcitx5。
- 不导出 DBusMenu：`UseNativeGlobalMenu`（`MainWindow.axaml` 里的 `NativeMenu`）在 Wayland 后端失效。当前会话的日志反复出现 `DBusMenu UnregisterWindowAsync failed: ServiceUnknown`，说明全局菜单其实也没显示。

## 待确认

- Dock 浮动窗口、弹出层、拖放在没有全局坐标时的行为。
- 候选框定位、preedit 渲染与 RoyalTerminal `TextInputMethodClient` 的配合。
- 是否默认启用，还是只作为环境变量 / 设置项提供。

## Comments
