# 桌面菜单启动时无法切换中文输入法：IME 环境兜底检测

Type: task
Status: resolved
Blocked by: 无

## 现象

Plasma Wayland + fcitx5 环境下，从桌面菜单（`KeiTerm.desktop`）启动的安装版里，终端和普通输入框都切不到中文模式；从终端启动的 Debug 版正常。

## 诊断

- 对比 `/proc/<pid>/environ`：安装版没有 `XMODIFIERS`，Debug 版有 `XMODIFIERS=@im=fcitx`（继承自终端）。两者都没有 `GTK_IM_MODULE` / `QT_IM_MODULE`。
- Avalonia X11 的 `X11DBusImeHelper.DetectInputMethod` 只凭这四个变量匹配 `fcitx` / `fcitx5` / `ibus`，四个全空就不注册 IME。
- fcitx5 `Controller1.DebugInfo` 里，正常的实例能看到 `frontend:dbus` 的输入上下文（Avalonia 建 IC 时 appName 为空）。

## Answer

已修复，代码随 `7e641a6` 提交：

- `src/Kei.Term.App/Services/LinuxImeEnvironment.cs`：仅 Linux 生效。上述四个变量都没声明输入法（`XMODIFIERS` 不含 `@im=`）时扫描 `/proc/*/comm`：发现 `fcitx5` / `fcitx` 就设 `AVALONIA_IM_MODULE=fcitx`（优先于 IBus，fcitx5 也提供 IBus 兼容接口）；只有 `ibus-daemon` 就设 `ibus`；都没有则不动。用户任何显式声明（包括 `none`）都不覆盖。
- `src/Kei.Term.App/Program.cs`：`BuildAvaloniaApp` 开头调用 `LinuxImeEnvironment.Apply()`，必须早于 X11 平台初始化。
- `tests/Kei.Term.Tests/LinuxImeEnvironmentTests.cs`：覆盖 fcitx 优先、仅 IBus、无守护进程、四类显式声明不覆盖、`XMODIFIERS` 不含 `@im=` 仍检测，共 9 个用例。

## 验证记录

- `LinuxImeEnvironmentTests` 9 个用例全部通过。
- 端到端：关掉其余 Debug 实例后，fcitx5 的 `frontend:dbus` IC 为 0；用 `env -u XMODIFIERS -u GTK_IM_MODULE -u QT_IM_MODULE` 启动 Debug 版后变为 1。用户在该实例里确认可以挂上输入法。
- 安装版（`~/.local/opt/KeiTerm`）要重新构建、部署后，从菜单启动才会生效。

## Comments

不改 `.desktop` 文件也不写系统环境，只用 Avalonia 专用的 `AVALONIA_IM_MODULE`，不影响子进程里的 GTK / Qt 应用。
