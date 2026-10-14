# 会话树 UI 数值调试

只在 **Debug** 构建里检查布局、试 Margin / Width。Release 不引用诊断程序集，F12 不会接通。

## F12：Developer Tools

Avalonia 12 已移除 `Avalonia.Diagnostics` / `AttachDevTools()`。本仓库用 `AvaloniaUI.DiagnosticsSupport` 2.2.3，在 `App.Initialize` 里于 `DEBUG` 下调用 `AttachDeveloperTools()`（默认手势 F12）。非 Debug 配置该包 `IncludeAssets=None`，调用不能拿到 `DEBUG` 外面，否则 Release 编不过，发布产物也会多一个诊断 DLL。

一次准备：

```bash
dotnet tool install --global AvaloniaUI.DeveloperTools
# 升级：dotnet tool update --global AvaloniaUI.DeveloperTools
dotnet run --project src/Kei.Term.App/Kei.Term.App.csproj
```

应用起来后按 **F12**。诊断包会拉起 `avdt` 并连到本机 `29414`。工具进程第一次要登录 Avalonia 账号激活，之后可离线。Linux 上若提示找不到 `avdt`，把 `$HOME/.dotnet/tools` 加进 `PATH`。

元素树里模板内容收在 `/template/` 下。常用操作：

| 操作 | 作用 |
| --- | --- |
| Ctrl+Shift+C | 点选应用里的控件，在元素树定位 |
| Ctrl+Shift+H | 悬停高亮（边距 / 内边距 / 边界） |
| 属性面板 | 改选中元素的 `Width`、`Margin`、`Height` 等，立刻作用于这次进程 |

属性面板的修改只活在当前进程，不写回 `.axaml`。满意的数值要自己抄回源码。会话树箭头在 `TreeViewItem` 的控件主题里，选中行后展开 `/template/`，找 `PART_ExpandCollapseChevronContainer`（占位）和 `PART_ExpandCollapseChevron`（箭头按钮）。

## XAML 热重载

`dotnet watch` 本身只会按 .NET Hot Reload 规则更新 C#。Avalonia 12 的 `.axaml` 原地热重载要另外引用授权包 `AvaloniaUI.DiagnosticsSupport.HotReload`（当前 1.0.1）。没有密钥时这个包一初始化就抛 `AvaloniaLicensingException`，所以工程**不会无条件引用它**。

密钥在构建期与运行期各走一遍什么校验、产物里会留下什么，见 [`avalonia-licensing.md`](avalonia-licensing.md)。

有 Plus / Pro / Enterprise（或仍覆盖该产品的旧 Accelerate）密钥时：

```bash
export AvaloniaUILicenseKey='从 Avalonia Portal 复制的运行时密钥'
dotnet watch run --project src/Kei.Term.App/Kei.Term.App.csproj
```

`Kei.Term.App.csproj` 在 Debug 且该属性非空时才加入包引用。同名 MSBuild 属性也可以，密钥不要写进仓库。包会自己注册，不需要启动代码。保存 `.axaml` 后，控件、样式、资源字典、数据模板会打进正在跑的进程。

没有密钥时不要指望原地热重载。`dotnet watch` 仍会在 `.axaml` 变更后重新编译；生成代码若被 .NET 判定为 rude edit，进程会重启，界面状态会丢。这不是官方热重载。

下面这些改动热重载覆盖不了，必须停掉进程重新构建：

- `const` 字段。`UiDesignSystemService` 的 `TreeChevronSlotWidth`、`TreeChevronGap`、`TreeRailClearance`，以及 `KeiTreeLine.ArrowClearance`，都会被编译进 IL。
- 方法签名、新增字段等 .NET Hot Reload 不接受的 C# 编辑。

## 会话树调参速查

箭头占位和引导线是两套数，必须一起改。模板在 `src/Kei.Term.App/DesignSystem/KeiControls.axaml` 的 `KeiTreeViewItem`（箭头槽约第 40 行，箭头按钮约第 41–48 行）。最小值在 `src/Kei.Term.App/Services/UiDesignSystemService.cs`。连接线逻辑在 `src/Kei.Term.App/Controls/KeiTreeLine.cs`。

| 位置 | 名称 | 当前值 | 含义 |
| --- | --- | --- | --- |
| `KeiControls.axaml` | `PART_ExpandCollapseChevronContainer` | `Width="16"` `Height="16"` `Margin="0,0,6,0"` | 箭头占位槽。叶子行隐藏箭头但保留宽度，用来对齐 |
| `KeiControls.axaml` | `PART_ExpandCollapseChevron` | `Width="12"` `Height="12"` | 槽内箭头按钮，水平垂直居中。槽宽 16 时按钮相对槽左缘内缩 2px |
| `UiDesignSystemService` | `TreeChevronSlotWidth` | `16` | 与槽 `Width` 一致 |
| `UiDesignSystemService` | `TreeChevronGap` | `6` | 与槽 `Margin` 的右边距一致 |
| `UiDesignSystemService` | `TreeChevronCenterOffset` | `8` | 箭头槽左缘到中心偏移量（16 / 2 = 8px） |
| `UiDesignSystemService` | `MinimumTreeIndent(iconSize)` | `16 + 6 + icon/2` | 最小缩进。默认图标 14 时为 **29** |
| `KeiTreeLine` | `ArrowGap` | `1.5` | SecureCRT 风格间隙：竖线在展开箭头字形上下各留出的断线缝隙；横线在箭头右侧或叶子图标左侧留出的断线缝隙 |
| `KeiTreeLine` | `GetLeftmostArrowSlotCenter` | — | 竖线正穿过子项展开箭头槽中心（压线对齐）；未布局时回退父箭头轴+缩进或图标中心 |
| `KeiTokens.axaml` | `Kei.Tree.Indent` | 资源默认 `16` | 启动后被 `ApplyTreeDensity` 换成 `EffectiveTreeIndent`。默认图标 14 时最小值是 **29**，DevTools 里看到的一般不是 16 |

`EffectiveTreeIndent` 用 `max(用户缩进, MinimumTreeIndent)`。用户把缩进调得比最小值还小，生效的仍是最小值。 SecureCRT 风格下，竖线引出的 X 坐标与所有直接子项展开箭头槽的水平中心严格对齐（中心落在竖线上）。

改了箭头占位的 **宽度或右边距**，要同步改 `TreeChevronSlotWidth` / `TreeChevronGap` 与 `MinimumTreeIndent`。然后跑：

```bash
dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter TreeGuideRenderingTests
```

只在 DevTools 里试出来的数，写回上述位置之前，连接线不会跟着变。
