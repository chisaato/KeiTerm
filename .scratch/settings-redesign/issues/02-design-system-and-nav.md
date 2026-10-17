# 02 设置基础控件、分组导航与样板页

Status: resolved
Blocked by: 01

见 `../spec.md` 决策 2~8。

## 范围

- 基础控件（放 `Views/Settings/Controls/` 或现有 `Views/Controls/`）：
  - `SettingRow`：标题、说明、右侧内容。
  - `SettingsCard`：卡片标题 + 行集合 + 可选“显示高级选项”折叠区。
  - 摘要下钻行（摘要文本 + `编辑 ›`，可选行内开关）与下钻宿主：右侧内容区在主页与子页之间切换，页头面包屑可返回；切换分类时回到主页。
  - 页头：面包屑 + 标题 + 描述。
- 分组导航：`SettingsCategoryItem` 增加分组信息，`CategoryNavListBox` 支持不可选的分组标题、单行条目；按 spec 决策 2 的分组与顺序重排 `SettingsViewModel.Categories`。新增“会话管理器”“传输”“文件管理与编辑器”等分类名与页描述字符串（`Strings.resx` / `Strings.en.resx`）。
- 将“文件传输”拆为“传输”与“文件管理与编辑器”两页，作为样板页按新布局完成，含哈希/归档优先级下钻。
- 遵循 `avalonia-compact-tokens`：资源令牌、零裸值、紧凑密度。

## 验证

- 构建与测试通过；下钻行为（进入、面包屑返回、切换分类复位）若有可测逻辑放 VM 并补单元测试。

## Comments

### 控件 API 与用法示例（供工单 03 复用）

1. **`SettingRow`** (`Views/Settings/Controls/SettingRow.axaml`)
   - 属性：
     - `Title` (string): 左侧标题（13px，Primary 文本）。
     - `Description` (string): 标题下方说明行（12px，Muted 文本，为空自动隐藏）。
     - `Content` (object): 右侧控件，自动右对齐。
   - 用法：
     ```xml
     <controls:SettingRow Title="并发传输数" Description="同一连接同时传输的文件数">
         <NumericUpDown Value="{Binding Concurrency}" Minimum="1" MinWidth="160" HorizontalAlignment="Right"/>
     </controls:SettingRow>
     ```

2. **`SettingsCard`** (`Views/Settings/Controls/SettingsCard.axaml`)
   - 属性：
     - `Title` (string): 卡片标题（13px SemiBold，Primary 文本）。
     - `Content` (object): 卡片主体内容。
     - `AdvancedHeader` (string): 高级选项折叠头文本（默认 "显示高级选项"）。
     - `AdvancedContent` (object): 可选折叠区内容（未设置时自动隐藏 Expander）。
     - `IsAdvancedExpanded` (bool): 折叠展开状态（支持 TwoWay 绑定）。
   - 用法：
     ```xml
     <controls:SettingsCard Title="自动重试" AdvancedHeader="显示高级选项">
         <controls:SettingsCard.Content>
             <controls:SettingRow Title="遇到暂时性网络错误时自动重试">
                 <ToggleSwitch IsChecked="{Binding AutoRetryEnabled}"/>
             </controls:SettingRow>
         </controls:SettingsCard.Content>
         <controls:SettingsCard.AdvancedContent>
             <controls:SettingRow Title="退避方式">
                 <ComboBox ItemsSource="{Binding BackoffModeOptions}"/>
             </controls:SettingRow>
         </controls:SettingsCard.AdvancedContent>
     </controls:SettingsCard>
     ```

3. **`DrillDownRow`** (`Views/Settings/Controls/DrillDownRow.axaml`)
   - 属性：
     - `Title` (string), `Description` (string).
     - `Summary` (string): 摘要文本（如 `SHA-256 › MD5`）。
     - `ActionLabel` (string): 按钮文字（默认 `编辑 ›`）。
     - `HasSwitch` (bool): 是否显示行内前置开关（默认 false）。
     - `SwitchChecked` (bool, TwoWay): 开关选中状态。
     - `IsActionEnabled` (bool): 按钮是否可用。
     - `Command` (ICommand): 点击下钻命令。
   - 用法：
     ```xml
     <controls:DrillDownRow Title="传输后进行完整性校验"
                            Description="文件传输后校验两端哈希"
                            Summary="{Binding HashAlgorithmSummary}"
                            HasSwitch="True"
                            SwitchChecked="{Binding VerifyHashEnabled}"
                            IsActionEnabled="{Binding VerifyHashEnabled}"
                            Command="{Binding DrillDownHashAlgorithmsCommand}"/>
     ```

4. **`SettingsPageHeader`** (`Views/Settings/Controls/SettingsPageHeader.axaml`)
   - 属性：
     - `Group` (string): 分组名（面包屑第 1 级）。
     - `Title` (string): 页面标题（面包屑第 2 级，未在子页时作为大标题）。
     - `Description` (string): 页面描述（进入子页后自动隐藏）。
     - `SubPageTitle` (string): 子页标题（进入子页后显示在面包屑第 3 级并作为主大标题）。
     - `BackCommand` (ICommand): 返回上级命令（返回箭头及面包屑链接均绑定该命令）。
   - 用法：
     ```xml
     <controls:SettingsPageHeader Group="{loc:KeiString Settings.Groups.Files}"
                                  Title="{loc:KeiString Settings.Categories.Transfer}"
                                  Description="{loc:KeiString Settings.Categories.TransferDesc}"
                                  SubPageTitle="{Binding CurrentSubPage.Title}"
                                  BackCommand="{Binding NavigateBackCommand}"/>
     ```

5. **`DrillDownHost`** (`Views/Settings/Controls/DrillDownHost.axaml`)
   - 属性：
     - `Content` (object): 主页面内容（当 `SubPageContent == null` 时呈现）。
     - `SubPageContent` (object): 下钻子页内容（非 null 时隐藏 Content 并呈现子页）。

### 文件清单

- **新控件与样式**：
  - `src/Kei.Term.App/Views/Settings/Controls/SettingRow.axaml` (.cs)
  - `src/Kei.Term.App/Views/Settings/Controls/SettingsCard.axaml` (.cs)
  - `src/Kei.Term.App/Views/Settings/Controls/DrillDownRow.axaml` (.cs)
  - `src/Kei.Term.App/Views/Settings/Controls/DrillDownHost.axaml` (.cs)
  - `src/Kei.Term.App/Views/Settings/Controls/SettingsPageHeader.axaml` (.cs)
  - `src/Kei.Term.App/Views/Settings/SettingsStyles.axaml`
- **导航与分类**：
  - `src/Kei.Term.App/ViewModels/Settings/SettingsCategoryItem.cs`（增加 Group、IsHeader、CreateHeader）
  - `src/Kei.Term.App/Views/Controls/CategoryNavListBox.axaml` (.cs)（支持单行、不可选分组标题、防点中）
  - `src/Kei.Term.App/ViewModels/SettingsViewModel.cs`（重组导航分组，复位下钻状态）
  - `src/Kei.Term.App/Views/SettingsWindow.axaml`（MaxContentWidth=760，单层下钻视图模板注入）
- **样板页与视图模型拆分**：
  - `src/Kei.Term.App/ViewModels/Settings/ISettingsDrillDownHost.cs`
  - `src/Kei.Term.App/ViewModels/Settings/SettingsSubPageViewModel.cs`
  - `src/Kei.Term.App/ViewModels/Settings/TransferOrderedItemViewModel.cs`
  - `src/Kei.Term.App/ViewModels/Settings/TransferSettingsPage.cs` (.Capabilities.cs)
  - `src/Kei.Term.App/ViewModels/Settings/FileManagerSettingsPage.cs`
  - `src/Kei.Term.App/ViewModels/Settings/FileTransferSettingsPage.cs`（委托包装转发，保持向后兼容）
  - `src/Kei.Term.App/Views/Settings/TransferSettingsPageView.axaml` (.cs)
  - `src/Kei.Term.App/Views/Settings/FileManagerSettingsPageView.axaml` (.cs)
  - `src/Kei.Term.App/Views/Settings/HashAlgorithmsSubPageView.axaml` (.cs)
  - `src/Kei.Term.App/Views/Settings/ArchiveFormatsSubPageView.axaml` (.cs)
- **多语言资源**：
  - `src/Kei.Term.App/Resources/Strings.resx` / `Strings.en.resx`
- **测试**：
  - `tests/Kei.Term.Tests/SettingsDrillDownAndNavigationTests.cs`（新建单元测试覆盖下钻流转与摘要响应）
  - 适配与同步更新现有测试：`SettingsLayoutTests`、`FileSizeDisplayTests`、`SettingsGlobalDefaultsTests`、`TerminalFileDropTests`、`TransferConflictUiContractTests`、`TransferSettingsTests`、`CompactUiContractTests`。

### 验证结果

- `dotnet build Kei.Term.slnx`: 0 错误，0 警告。
- `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`: 1125 通过，0 失败，36 跳过（跳过项均为无外部 sshd/GUI 依赖的原有测试）。
