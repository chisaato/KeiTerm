# 05 代码命名与 GLOSSARY 对齐（文件视图 / 文件标签 / 目录标签 / 提升）

Status: resolved
Blocked by: 03

见 `GLOSSARY.md` 与 `docs/adr/0001-connection-owns-views.md`。

## 范围

只改 App 层内部的类型名、成员名、消息名和注释，让代码用语对上 GLOSSARY。已知的不一致：

- `FileManagerTabViewModel`、`IsWorkspaceTab`、`CreateDirectoryTab`、`_directoryTabs`、`_resourceOwner` 各用一套说法。"把侧栏移出成文件标签"（提升）和"从目录新开文件标签"（目录标签）这两个概念需要区分清楚。
- 注释和本地化文案里的"升格"统一改成"提升"；"文件侧栏"只在确实指侧栏形态时使用。

不改：

- 设置的持久化键名，例如 `IsFileManagerOnLeft`。
- 公开的 Core / Ssh 抽象。
- 用户可见的行为。

## 验收

- 纯重构，现有测试全部通过，不新增功能。
- XAML 绑定的路径同步更新，XAML 契约测试通过。

## Comments

没有把 `FileManagerTab`、`DirectoryTab`、`WorkspaceTab` 收成同一个词。它们不是同一件事：

- `WorkspaceTab` / `ActiveWorkspaceTab`：任意工作区标签（连接标签、文件标签、起始页等）。保持原名。
- `FileManagerTabViewModel`：文件标签的工作区壳。提升出去的侧栏和目录标签都用它承载，所以不能改成 DirectoryTab，也不并进 WorkspaceTab。类型名仍含 FileManager，是因为改名会波及 Dock 模板和大量工作区测试，而它和另外两个概念的边界已经用消息分开。
- `CreateDirectoryTab` / `_directoryTabs` / `IsDirectoryTab`：就是目录标签，保持原名。注释里的「独立目录页」已改成目录标签。

已对齐的误导名称：

- `IsWorkspaceTab` → `IsFileTab`。表示这份文件视图当前是文件标签，不是侧栏。XAML 的停靠、全屏、提升按钮绑定已同步。
- `_resourceOwner` → `_owningFileView`。非空表示本实例是目录标签，指向连接创建的那份文件视图；文件通道由那份视图随连接释放，目录标签不释放通道。
- 一条 `FileManagerTabRequestedMessage` 拆成 `FileViewPromoteRequestedMessage`（提升现有侧栏）和 `DirectoryTabRequestedMessage`（新开目录标签）。关闭仍是 `FileTabCloseRequestedMessage`。

文案：侧栏上的提升按钮改为「提升为文件标签」。目录菜单的 `FileManager.OpenInTab` 改为「打开目录标签」，与提升分开。显示/隐藏「文件侧栏」、关闭侧栏仍指侧栏形态，未改。`IsFileManagerOnLeft` 等持久化键未改。Core / Ssh 公开 API 未改。

未改、也不猜的项：

- `RemoteFileManagerViewModel` 仍是文件视图类型名。GLOSSARY 允许功能上称文件管理器，改类型名会波及全部文件视图绑定，没有额外澄清提升和目录标签。
- 设置文案「文件侧栏目录跟随」仍指侧栏里那份文件视图的目录跟随，不是目录标签。Core 的 `CwdFollow` 注释未改。
- 历史设计文档里的「升格」未改。App 源码里已经没有「升格」。

### 状态（2026-10-17）

App 层命名已按上文对齐。最终回归尚未通过，Status 保持 `ready-for-agent`，不标 resolved。`GLOSSARY.md` 的续传、部分文件、传输队列已改成与现行传输行为一致，不把空历史说成未变化。
