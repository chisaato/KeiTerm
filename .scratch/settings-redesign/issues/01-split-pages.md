# 01 设置窗口按页拆分为 UserControl（无视觉变化）

Status: resolved
Blocked by: —

见 `../spec.md`。

## 范围

- 将 `src/Kei.Term.App/Views/SettingsWindow.axaml` 中每个分类页面的内容移到 `src/Kei.Term.App/Views/Settings/<Page>SettingsPageView.axaml`（+ code-behind），`SettingsWindow` 只保留导航、内容宿主和底部按钮。
- 页面中使用的 `x:Name` 及对应 code-behind 逻辑（录制快捷键、排序列表按钮等）随页面迁移；共享样式（如 `compactReorderList`、`popupList`）提到 `Views/Settings/SettingsStyles.axaml` 并在窗口引入。
- 编译绑定（`x:DataType`）保持有效。
- 更新引用 `SettingsWindow.axaml` 路径的契约测试（如 `Contracts/SettingsFontListContractTests`），让其扫描新文件集合，断言语义不变。

## 不做

- 不改任何视觉、文案、导航结构、VM 行为。

## 验证

- `dotnet build Kei.Term.slnx`、`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj` 通过。

## Comments

- 已按原 `SelectedCategory.Page` + VM 类型 `DataTemplate` 机制完成纯机械拆分，保留全部页面内容、绑定、命名控件及窗口底部操作；未修改 VM、视觉、文案或导航。
- 新文件位于 `src/Kei.Term.App/Views/Settings/`：
  - `GeneralSettingsPageView.axaml`、`GeneralSettingsPageView.axaml.cs`
  - `AppearanceSettingsPageView.axaml`、`AppearanceSettingsPageView.axaml.cs`
  - `TreeSettingsPageView.axaml`、`TreeSettingsPageView.axaml.cs`
  - `TerminalSettingsPageView.axaml`、`TerminalSettingsPageView.axaml.cs`
  - `TerminalAppearanceSettingsPageView.axaml`、`TerminalAppearanceSettingsPageView.axaml.cs`
  - `TabBarSettingsPageView.axaml`、`TabBarSettingsPageView.axaml.cs`
  - `SshSettingsPageView.axaml`、`SshSettingsPageView.axaml.cs`
  - `SecuritySettingsPageView.axaml`、`SecuritySettingsPageView.axaml.cs`
  - `FileTransferSettingsPageView.axaml`、`FileTransferSettingsPageView.axaml.cs`
  - `ProxySettingsPageView.axaml`、`ProxySettingsPageView.axaml.cs`
  - `SettingsStyles.axaml`
- 快捷键录制逻辑迁入常规页：页面挂载时订阅窗口隧道按键，卸载时解绑，保留焦点位于导航/底部按钮时的录制能力；删除原 `SettingsWindow.Shortcuts.cs`。字体对话框事件迁入终端外观页，仍使用所属窗口为 owner。排序按钮原为 VM 命令绑定，随 XAML 原样迁移，无额外 code-behind。
- 原窗口样式整体提取到 `SettingsStyles.axaml` 并在窗口引入；`popupList` 原依赖应用全局样式，保持原作用域，不复制或覆盖。
- `Contracts/SettingsFontListContractTests.cs` 改为扫描 `*SettingsPageView.axaml` 集合，保留六组字体列表的全部断言。`SettingsLayoutTests` 使用运行时视觉树遍历而非文件路径，无须改动，已随全量测试通过。
- 验证：`dotnet build Kei.Term.slnx` 成功，0 警告、0 错误；`dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj` 成功，通过 1122、失败 0、跳过 36（平台/集成条件限制），总计 1158。日志：`/tmp/opencode/settings-split-build.log`、`/tmp/opencode/settings-split-tests.log`。未执行 git commit。
