# 平台原生菜单与应用身份

本文记录 KeiTerm 在 macOS / Linux 上接入「原生菜单栏」的机制、踩坑与限制。
内容来自源码阅读 + 本机实测（macOS 27 / Apple Silicon / Avalonia 12.1.2）。

---

## 1. 总体方案：一份定义，两端适配

菜单**只在 `MainWindow.axaml` 的 `NativeMenu.Menu` 中定义一次**，由 `NativeMenuBar` 控件按平台分发：

```xml
<!-- MainWindow.axaml：Row0 -->
<NativeMenuBar Grid.Row="0" Background="{DynamicResource Kei.Bg.PanelAlt}"/>
```

`NativeMenuBar` 内部的 `Menu` 控件显隐由 Avalonia 源码决定（`src/Avalonia.Controls/NativeMenuBar.cs`）：

```csharp
menu.Bind(IsVisibleProperty, topLevel.GetBindingObservable(NativeMenu.IsNativeMenuExportedProperty)
    .Select(v => !v.GetValueOrDefault<bool>()));
```

即内部菜单的 **`IsVisible = !IsNativeMenuExported`**。外层 `NativeMenuBar.IsVisible` 仍可为 true，但菜单隐藏后其布局高度为 0：

| 平台 | `IsNativeMenuExported` | 表现 |
| --- | --- | --- |
| macOS | 置真 | 菜单导出至系统全局菜单栏，窗口内整条隐藏 |
| Linux + 有 Global Menu 消费方 | 置真 | 同上（见 §4） |
| Windows / 无消费方的 Linux | 保持假 | 就地渲染为窗口内菜单栏，与改造前一致 |

顶部 chrome 因此从 **96px 降到 66px**（28px 原生标题栏 + 38px 工具栏）。

**约束**：`NativeMenuItem.Gesture` 只是交给平台导出器的展示属性，**本身不注册热键**。Windows/Linux 的热键仍依赖 `Window.KeyBindings`；macOS 则由原生菜单承载，故 `MainWindow.SetUpPlatformKeyBindings()` 在 macOS 上清空 `KeyBindings`，避免 XAML 里的 `Ctrl+Q` 被解释成 ⌘Q 与系统「退出」冲突。

---

## 2. 坑：嵌套 `NativeMenuItem` 会在 XAML 填充期崩溃

**Avalonia 12.1.2 下，`NativeMenuItem` 的隐式内容嵌套会抛 `NullReferenceException`**，堆栈指向 `MainWindow.!XamlIlPopulate`，且**剥离全部 `{Binding}` 后依然复现**——所以与绑定无关，是嵌套结构本身。

必须写成显式的 `<NativeMenuItem.Menu><NativeMenu>…</NativeMenu></NativeMenuItem.Menu>`：

```xml
<NativeMenuItem Header="{loc:KeiString Menu.File}">
    <NativeMenuItem.Menu>
        <NativeMenu>
            <NativeMenuItem Header="{loc:KeiString Menu.File.NewSession}" Command="{Binding CreateSessionCommand}"/>
        </NativeMenu>
    </NativeMenuItem.Menu>
</NativeMenuItem>
```

同理，`NativeMenuItem` **不是 `Control`**，XAML 编译器不支持在其上写 `Click="方法名"`（报 `AVLN3000: Unable to find suitable setter or adder for property Click`）。点击处理只能：
- 用 `Command="{Binding ...}"`（编译期绑定，见下），或
- 在代码后置按 `Header` 资源键匹配后 `item.Click += ...`（`MainWindow.WireMenuClickHandlers()` 与 `App.WireAppMenuHandlers()` 采用此法）。

**关于绑定**：本项目 `AvaloniaUseCompiledBindingsByDefault` 生效为 **true**（`Avalonia.props` 默认 true），因此 `NativeMenu` 虽不在可视树中、`NativeMenuItem` 也没有 `DataContext` 属性，`{Binding}` 仍由 XAML 编译器按根对象的 `x:DataType` 强类型解析并在运行期正确求值。已实测：写入不存在的属性路径会在**编译期**报 `AVLN2000`。

---

## 3. macOS 应用菜单与应用身份

### 3.1 "About Avalonia" 的成因与修法

Avalonia 仅在**应用未配置菜单**时注入内置的 `About Avalonia`，且该项标题在后端硬编码（`Avalonia.Native.dll` 中含字面量 `"About Avalonia"`、`"Hide "`、`"Hide Others"`、`"Services"`、`"Quit"`），**无法通过属性改写**。

修法是在 **`Application`（而非 Window）** 上声明应用菜单，见 `App.axaml`：

```xml
<NativeMenu.Menu>
    <NativeMenu>
        <NativeMenuItem Header="{loc:KeiString Menu.Help.About}"/>
        <NativeMenuItemSeparator/>
        <NativeMenuItem Header="{loc:KeiString Menu.Tools.Settings}"/>
    </NativeMenu>
</NativeMenu.Menu>
```

注意 `Hide <名称>` 会拼接 `Application.Name`，而 `About` 项不会——这是上游不一致，只能靠显式声明绕过。`Services` / `Hide` / `Quit` 由 Avalonia 自动补齐，符合 macOS 惯例。

### 3.2 应用名的两个来源

| 运行方式 | 菜单栏应用名取自 |
| --- | --- |
| 裸可执行文件（`dotnet run`） | `App.axaml` 的 `Application.Name` |
| `.app` bundle | `Contents/Info.plist` 的 `CFBundleName` |

**两者必须一致**，否则「关于」菜单与 Dock 名称会对不上。`CFBundleName` 上限 15 字符，超出须改用 `CFBundleDisplayName`。
Dock 名称与图标则**只有 bundle 才生效**——裸可执行文件在 Dock 显示进程名（`Kei.Term.App`）与通用图标。

---

## 4. Linux / KDE Plasma

### 4.1 Avalonia 自带 DBusMenu 导出（无需自行实现）

`Avalonia.FreeDesktop.dll` 中含 `com.canonical.AppMenu.Registrar`、`com.canonical.dbusmenu`、`/net/avaloniaui/dbusmenu/` 与 `DBusMenu UnregisterWindowAsync` 逻辑。对应上游：

- `src/Avalonia.FreeDesktop/DBusMenuExporter.cs` 实现 `com.canonical.dbusmenu` 并以 `RegisterWindow(xid, path)` 注册
- `src/Avalonia.X11/X11Window.cs`：`if (platform.Options.UseDBusMenu) _nativeMenuExporter = DBusMenuExporter.TryCreateTopLevelNativeMenu(_handle);`
- `X11PlatformOptions.UseDBusMenu` **默认 true**
- `UsePlatformDetect()` 在 Linux 上**只加载 X11**，不加载 Wayland 后端

### 4.2 Plasma 侧的关联机制

Plasma 6 的 Global Menu applet 不直接查 registrar：它从 libtaskmanager 读活动任务的
`ApplicationMenuServiceName` / `ApplicationMenuObjectPath` 角色（由 KWin 提供），再经 `com.canonical.dbusmenu` 拉取菜单；两者皆空则隐藏自身。

窗口↔菜单关联在 **X11/XWayland** 上走 `com.canonical.AppMenu.Registrar`：注册时 Plasma 的 kded 模块把 `_KDE_NET_WM_APPMENU_SERVICE_NAME` / `_KDE_NET_WM_APPMENU_OBJECT_PATH` 写到窗口属性上，KWin 读取后喂给 applet。这与 Qt 在 `xcb` 上的做法一致。

**因此在 Plasma Wayland 会话下，KeiTerm 实际经 XWayland 运行，正是全局菜单可用的配置。**

### 4.3 已知限制：Wayland 原生

Wayland 原生窗口无法导出菜单：关联需要 KWin 私有协议 `org_kde_kwin_appmenu_manager`（需 toolkit 持有 `wl_surface`），跨合成器标准（[wayland-protocols MR !52](https://gitlab.freedesktop.org/wayland/wayland-protocols/-/merge_requests/52)，2020 年开启）至今未合并。
相关 KDE bug（[424485](https://bugs.kde.org/show_bug.cgi?id=424485)）2020 年开启、至今 REOPENED、12 个重复，标记 wayland-only。

> **结论**：全局菜单在 X11/XWayland 上可用；Wayland 原生下不可用，根因在上游（缺标准协议 + Avalonia 无 Wayland 导出器），不是 KeiTerm 的问题。

### 4.4 尚未验证

以上基于上游源码与 KDE 文档，**未在真实 KDE 环境实测**。验证方法：

```bash
dbus-monitor --session "interface='com.canonical.dbusmenu',interface='com.canonical.AppMenu.Registrar'"
```

面板放置 Global Menu widget 后启动 KeiTerm（X11/XWayland），观察是否出现 `RegisterWindow` 调用。

### 4.5 可选的 KDE 轻量适配（未实施）

- **桌面检测**：`XDG_CURRENT_DESKTOP` → `KDE_FULL_SESSION`
- **配色**：读 `~/.config/kdeglobals` 的 `[Colors:*]`，或订阅 KDE 设置的 D-Bus 通知以支持实时换肤
- **图标**：遵循 FreeDesktop 图标主题；尺寸 16/22/32，单色用 `-symbolic` 后缀
- **装饰**：Wayland 下不要自绘标题栏（KWin 经 `xdg-decoration-v1` 协商 SSD），否则会与合成器 chrome 重复

### 4.6 可配置的全局菜单

「设置 → 常规 → 使用系统全局菜单栏」保存到 `AppSettings.UseNativeGlobalMenu`，默认开启。

| 平台 | 开关行为 |
| --- | --- |
| Linux / X11 / XWayland | 可切换；开启允许桌面全局菜单组件接管，未接管时仍显示窗口内菜单；关闭始终显示窗口内菜单。保存后重启生效。 |
| macOS | 勾选且禁用，始终使用系统菜单。 |
| Windows | 未勾选且禁用，始终使用窗口内菜单。 |

`Program.BuildAvaloniaApp` 在创建窗口前调用 `NativeMenuSettings.CreateX11Options`，使用与设置页相同的 `JsonSettingsService` 读取设置，并映射到 `X11PlatformOptions.UseDBusMenu`。首次启动、旧配置与损坏文件均回退为允许桌面接管。手动编辑 JSON 时应使用准确键名 `UseNativeGlobalMenu`；现有读取器区分大小写。

`GeneralSettingsPage.IsGlobalMenuChecked` 负责显示平台实际行为，`UseNativeGlobalMenu` 保留用户的 Linux 偏好。在 macOS / Windows 打开或保存设置不会因禁用复选框的显示状态覆盖该偏好。

2026-10-06 的验证结果：

- 全局菜单专项测试 7 项全部通过，覆盖真实设置页保存、重新加载、启动选项、缺失/旧/损坏配置及实际 Avalonia 控件绑定。
- 使用临时配置与实际 `App` / `MainWindow` / `SettingsWindow` 完成 macOS 原生启动验证：`UseDBusMenu=false`，菜单仍导出到系统，窗口内菜单隐藏、菜单栏高度为 0，复选框勾选且禁用。验证未读取或修改用户数据库、配置。
- 完整回归 407 项：396 通过、5 失败、6 跳过。全局菜单测试全部通过；失败仍是 [本地缺口清单](../local-setup-gaps.md) E1/E2 中的认证测试旧预期、Linux sshd 测试在 macOS 写 `/run`，以及终端布局线程亲和异常。后者在两个既有测试中出现，失败数会随并行调度变化。
- KDE / XWayland 的桌面组件接管仍待 Linux 实机验证。

---

## 5. macOS 打包

`just bundle-macos <rid>` 产出真正的 `.app`（`pack-macos-arm64` / `pack-macos-x64` 已指向它）：

```
KeiTerm.app/Contents/
  Info.plist              ← packaging/macos/Info.plist
  MacOS/                  ← dotnet publish 自包含单文件产物
  Resources/KeiTerm.icns  ← 由 packaging/macos/AppIcon.png 经 sips + iconutil 生成
```

两个签名坑：

1. **`.pdb` 必须移除**。codesign 要求 `Contents/MacOS/` 下全部是可签名的 Mach-O 代码，托管程序集的 `.pdb` 会导致 `code object is not signed at all`。recipe 中 `find … -name '*.pdb' -delete`。
2. **必须由内向外逐层签名**。bundle 内嵌的第三方原生可执行文件（`royalterminal-pty-spawn`，来自 `RoyalApps.RoyalTerminal.Avalonia`）与各 `.dylib` 若未签名，直接签外层会失败。顺序：先签所有 `.dylib` 与可执行文件，再签 bundle。

当前为 **ad-hoc 签名**（`codesign --sign -`），仅够本机运行；对外分发需 Developer ID 证书 + 公证。

---

## 6. 相关文件

| 文件 | 作用 |
| --- | --- |
| `src/Kei.Term.App/App.axaml` | `Application.Name` + macOS 应用菜单 |
| `src/Kei.Term.App/App.axaml.cs` | 应用菜单点击挂接（`WireAppMenuHandlers`） |
| `src/Kei.Term.App/Views/MainWindow.axaml` | 菜单本体 + `NativeMenuBar` |
| `src/Kei.Term.App/Views/MainWindow.axaml.cs` | 平台快捷键、菜单手势与点击挂接 |
| `src/Kei.Term.App/Services/NativeMenuSettings.cs` | 启动期读取菜单设置并创建 X11 选项 |
| `src/Kei.Term.App/Views/SettingsWindow.axaml` | 全局菜单开关与平台说明 |
| `packaging/macos/` | `Info.plist`、`AppIcon.png`、`make-icon.py` |
| `justfile` | `bundle-macos` recipe |
