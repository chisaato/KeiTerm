# 传输队列表格视图、列宽可调与列显隐

Type: task
Status: resolved
Blocked by: 无

## 问题

每个传输任务是一张三行卡片，只显示文件名和状态文字；VM 已有速度、已传/总字节、本地路径、完整错误等数据却未展示。侧栏高度内只能放下约 2.5 项。

## Answer

- `RemoteFileManagerView.axaml` 传输页改为固定表头 + 22–24px 行：方向 | 名称(*) | 远端路径 | 大小 | 进度 | 速度 | 状态 | 操作。名称 Tooltip 显示 本地→远端；失败状态 Tooltip 显示完整错误；保留未校验徽章。
- 行右键菜单：重试、取消、移除、复制错误、复制远端路径、定位到远端目录。
- 按状态显示（`FileTransferTaskItemViewModel`）：传输中显示 已传/总、整数百分比（向下取整）+ 2px 细条、即时速度（ETA 在 Tooltip）；完成只显示总大小，进度/速度留空；排队留空；失败/取消有部分进度时保留 已传/总 与百分比。数字列右对齐、等宽字体，单元格裁剪不溢出。
- `TransferTableLayout`：共享列状态同时绑定表头与行模板的 ColumnDefinition；表头把手拖拽调宽、双击恢复默认；每列最小宽度，名称列 ≥60px；宽度不足时可选列按比例压缩至最小宽度（只影响当前视图），仍不足（约 <340px）才水平滚动。
- 表头右键勾选可选列（远端路径/大小/进度/速度/状态），默认隐藏远端路径。
- 持久化：`AppSettings.FileTransfer.TransferTable`（`ColumnWidths` / `ColumnVisibility`），所有连接共用；仅拖拽结束、双击重置、切换显隐时保存。
- “监视文件”页行高收敛到 24px。

## 验证记录

- `TransferTableLayoutTests`：钳制、恢复默认、显隐、默认值、设置往返、损坏宽度修正、窄视口压缩、两视图共享宽度各自压缩。
- `FileTransferTaskItemViewModelTests` Headless：表头与行列宽一致；改列宽后两者 `ActualWidth` 同步；隐藏列后两者为 0；完成行不显示进度/速度。

## Comments

- 首轮实现时，表格 XAML 被并行任务对同一文件的编辑覆盖，子任务仍汇报完成；用户实机发现仍是卡片后重做。此后同一文件不再分配给并行写入者，并在 Headless 测试中断言渲染结构。
- 第二轮修复了用户实拍中的大小/速度截断与百分比文本溢出重叠，并加入列宽调整。2026-10-21 用户桌面验收通过。
