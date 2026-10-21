# 文件管理加载条导致路径栏与列表抖动

Type: task
Status: resolved
Blocked by: 无

## 问题

用户反馈：打开目录时出现加载动画条，结束后隐藏，路径栏和下方文件列表随之上下抖动。用户认为加载条本身有价值，要求改良呈现而非移除。

## 根因

- `RemoteFileManagerView.axaml` 的 `FileManagerLoadingProgressBar` 独占标题栏 Grid 第二行（`Height=3`、`Margin=0,4,0,0`），以 `IsVisible="{Binding IsLoading}"` 显隐，标题栏高度随之变化约 7px。
- 目录树节点模板中的 20px 加载条位于横向 StackPanel，出现时顶开节点内容。

## 期望

- 加载条改为约 2px 叠层细条，位于标题栏与路径栏分隔线处，以 Opacity 渐变显隐，不占布局。
- 延迟约 150ms 才显示、显示后至少保持约 300ms，避免快速加载闪烁；逻辑可注入 `TimeProvider` 并有单元测试。
- 目录树节点加载指示不改变节点布局。
- 刷新同一目录时保留旧列表直至新数据返回（如适用）。

## 验收

- Headless：`IsLoading` 切换前后 `FileManagerPathInput` 的位置不变。
- 延迟显示 / 最短停留单元测试。
- 用户桌面确认不再抖动。

## Answer

- `FileManagerLoadingProgressBar` 移出标题栏 Grid 的独立行，改为与标题栏同格叠放、贴底 2px、`IsHitTestVisible=False`，以 `fileLoadingBar.loading` 类切换 Opacity（120ms 渐变）；不确定动画绑定 `IsLoadingIndicatorVisible`，透明时不运行，避免空闲重绘。
- 新增 `DelayedBusyIndicator`（可注入 `TimeProvider`）：忙碌超过 150ms 才显示，显示后至少保持 300ms，停留期内再次加载不闪；`RemoteFileManagerViewModel` 与 `RemoteDirectoryNodeViewModel` 暴露 `IsLoadingIndicatorVisible`（节点指示器首次加载时才创建）。
- 目录树节点移除 20px 小进度条，改为文件夹图标 Opacity 呼吸动画，节点宽度恒定。
- 文件列表加载期间降至 0.6 不透明度。
- 调查结论：`RefreshDirectoryCoreAsync` 本就在 `ListDirectoryAsync` 返回后才清空并填充，加载期间旧列表一直在、失败时不动列表，故未改刷新逻辑。切换目录时旧条目在加载期间仍可点击，属原有行为，未处理。

## 验证记录

- `DelayedBusyIndicatorTests`：快速完成不显示、150ms 后显示、结束后保持到 300ms、连续加载不闪、隐藏后重新加载需再等 150ms；VM 刷新期间保留旧列表、完成后为新数据、失败保留旧列表。
- `RemoteFileManagerLayoutTests`：加载条显隐前后 `FileManagerPathInput` 的 Y 与标题栏高度不变，条高 2px，不遮挡按钮。
- 主会话：build 0 错误；全量 1429 通过、36 跳过、0 失败（第二次运行；首次 `SidebarTransitionTests` 偶发失败一次，见 [09](09-flaky-sidebar-transition-test.md)）。

## Comments

2026-10-21 用户桌面验收通过。
