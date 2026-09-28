# SFTP / SCP 文件管理与本地追踪系统实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 KeiTerm 实现基于 SFTP/SCP 的远程文件管理系统，包含终端伴随式平铺侧栏（Wave/Warp 风格）、一键升格独立 Tab、以及三模式（智能/原生/轮询）健壮外部编辑器文件追踪回写服务。

**Architecture:** 严格遵循分层规范：`Kei.Term.Core` 负责文件系统抽象 `IRemoteFileSystem`、配置模型与 `LocalFileTracker` 引擎；`Kei.Term.Ssh` 负责基于 `SSH.NET` 的通道多路复用与 SFTP/SCP 落地；`Kei.Term.App` 负责 Avalonia 12 MVVM 界面交互（自包含 `RemoteFileManagerView`、终端 `GridSplitter` 伴随侧栏与独立 Tab 升格）。

**Tech Stack:** .NET 10 (C# 13), Avalonia 12.x, CommunityToolkit.Mvvm, SSH.NET, Microsoft.Extensions.Logging.

**Spec:** `docs/superpowers/specs/2026-09-28-sftp-scp-file-manager-design.md`

## Global Constraints

- 严禁在 `Kei.Term.Core` 中引入 Avalonia 或平台特化 UI 依赖。
- 所有新增代码使用行内注释，遵循 `var` 约束规范（避免大量连续单调 var 堆叠）。
- SFTP 模式支持三选一：`Auto`（默认）、`Subsystem`、`Dedicated`。
- 本地文件监视支持三选一：`Auto`（默认）、`OSNative`、`Polling`。
- 最终统一步骤进行全量回归与测试验证，不产生中间多余 Commit。

## Review Focus

1. **Linux inotify 配额耗尽或虚拟挂载盘降级**：Auto 模式在捕获 inotify 创建异常时平滑降级为轮询，不中断监控。
2. **编辑器写锁与原子保存拦截**：外部编辑器通过创建 `.tmp` 并原子 rename 替换原文件时，父目录级 watcher 配合防抖和排他读锁准确捕获最终落盘内容。
3. **远程文件并发修改冲突拦截**：本地编辑回写前检查远程 `mtime`，发生变动时阻断静默覆盖并提示。
4. **终端分屏尺寸同步**：侧栏展开/收起/拖拽宽度时，`TerminalControl` 自动平滑重算列数与行数（`TrySyncTerminalSize`）。
5. **路径规范化与穿越防御**：所有远程与本地路径拼接前均执行规范化清洗，杜绝 `../` 越界漏洞。

---

### Task 1: 核心领域抽象与数据模型 (`Kei.Term.Core`)

**Files:**
- Create: `src/Kei.Term.Core/Abstractions/IRemoteFileSystem.cs`
- Create: `src/Kei.Term.Core/Models/FileTransferModels.cs`
- Create: `src/Kei.Term.Core/Services/LocalFileTracker.cs`
- Modify: `src/Kei.Term.Core/Settings/AppSettings.cs`
- Modify: `src/Kei.Term.Core/Models/TreeNodes.cs` (或会话配置属性)

**Interfaces:**
- Produces:
  - `enum FileTransferProtocol { Sftp, Scp }`
  - `enum SftpChannelMode { Auto, Subsystem, Dedicated }`
  - `enum FileWatcherMode { Auto, OSNative, Polling }`
  - `record RemoteFileItem(...)`
  - `interface IRemoteFileSystem : IAsyncDisposable`
  - `interface ILocalFileTracker : IAsyncDisposable`
  - `class LocalFileTracker`

- [ ] **Step 1: 定义传输模型与枚举**
  在 `src/Kei.Term.Core/Models/FileTransferModels.cs` 中实现 `FileTransferProtocol`, `SftpChannelMode`, `FileWatcherMode`, `RemoteFileItem`, `FileTransferStatus`, `FileTransferProgress`。

- [ ] **Step 2: 定义远程文件系统抽象 `IRemoteFileSystem`**
  在 `src/Kei.Term.Core/Abstractions/IRemoteFileSystem.cs` 中实现包含 `ListDirectoryAsync`, `OpenReadAsync`, `OpenWriteAsync`, `DeleteAsync`, `RenameAsync`, `CreateDirectoryAsync`, `ChangePermissionsAsync`, `GetItemAsync` 的异步接口。

- [ ] **Step 3: 扩展设置与会话模型**
  - 在 `AppSettings.cs` 中添加 `FileTransferSettings`（包含 `CacheDirectory`, `WatcherMode`, `PollingIntervalSeconds`, `WriteDebounceMilliseconds`）；
  - 在会话节点定义或配置中增加 `FileTransferProtocol` 与 `SftpChannelMode` 字段。

- [ ] **Step 4: 实现健壮的本地文件追踪引擎 `LocalFileTracker`**
  在 `src/Kei.Term.Core/Services/LocalFileTracker.cs` 中实现：
  - 隔离目录路径计算：`{CacheDir}/{SessionId}/{RemotePathHash}_{FileName}`；
  - `Auto / OSNative / Polling` 三模式自适应切换与异常降级；
  - 目录级 `FileSystemWatcher`（监听 `LastWrite | FileName | Size`）；
  - SHA-256 脏检查与写锁检测（`FileShare.None` 指数退避重试）。

---

### Task 2: SSH 协议通道与传输服务 (`Kei.Term.Ssh`)

**Files:**
- Create: `src/Kei.Term.Ssh/Services/SftpRemoteFileSystem.cs`
- Create: `src/Kei.Term.Ssh/Services/ScpRemoteFileSystem.cs`
- Modify: `src/Kei.Term.Ssh/Abstractions/ISshSession.cs`
- Modify: `src/Kei.Term.Ssh/Services/SshSessionFactory.cs`

**Interfaces:**
- Consumes: `IRemoteFileSystem`, `RemoteFileItem`, `SftpChannelMode`, `FileTransferProtocol` from Task 1.
- Produces:
  - `class SftpRemoteFileSystem : IRemoteFileSystem`
  - `class ScpRemoteFileSystem : IRemoteFileSystem`
  - `ISshSession.CreateSftpFileSystem(...)` / `ISshSessionFactory` 扩展

- [ ] **Step 1: 扩展底层通道能力**
  在 `ISshSession` 中暴露底层物理 Client 访问或创建 Subsystem 通道的方法接口，以支持 Subsystem 复用。

- [ ] **Step 2: 实现 `SftpRemoteFileSystem`**
  - 基于 `Renci.SshNet.SftpClient`；
  - 实现 `Auto` 模式：先尝试宿主连接的 `subsystem sftp`，失败或无宿主时采用物化凭证新建独立连接；
  - 实现 `Subsystem` 严格复用模式与 `Dedicated` 独立模式；
  - 封装目录列表、流读写、删除、重命名、权限修改与元数据读取。

- [ ] **Step 3: 实现 `ScpRemoteFileSystem`**
  - 基于 `Renci.SshNet.ScpClient`；
  - 封装文件上传与下载流，针对目录列表操作通过 SSH Exec 通道执行轻量命令（`ls -la --time-style=+%s`）进行标准化结构解析回填。

---

### Task 3: 本地缓存与外部编辑器调度服务 (`Kei.Term.App`)

**Files:**
- Create: `src/Kei.Term.App/Services/FileEditorLauncher.cs`
- Modify: `src/Kei.Term.App/ViewModels/Settings/GeneralSettingsPage.cs` (或新增文件传输设置项)
- Modify: `src/Kei.Term.App/Views/SettingsWindow.axaml`

**Interfaces:**
- Consumes: `ILocalFileTracker`, `FileTransferSettings`, `IRemoteFileSystem` from Task 1 & 2.
- Produces:
  - `FileEditorLauncher`（调用系统默认程序拉起本地缓存文件，并将文件注册给 `LocalFileTracker` 启动双向监听与回写）。

- [ ] **Step 1: 实现系统默认编辑器唤醒器 `FileEditorLauncher`**
  跨平台拉起本地文件（Linux 使用 `xdg-open` / `Process.Start`，Windows 使用 `shell:open`，macOS 使用 `open`），并绑定追踪事件。

- [ ] **Step 2: 绑定远程回写与冲突处理**
  当 `LocalFileTracker` 触发文件修改事件时，检查远程 `mtime`；若发生冲突派发 UI 冲突事件，若无冲突调用 `IRemoteFileSystem.OpenWriteAsync` 自动上传回写。

- [ ] **Step 3: 扩充设置界面**
  在偏好设置中增加文件缓存目录路径选择、缓存体积显示、「清理缓存」按钮，以及文件监视模式（智能/原生/轮询）下拉选择框。

---

### Task 4: UI 界面与终端平铺伴随集成 (`Kei.Term.App`)

**Files:**
- Create: `src/Kei.Term.App/Views/RemoteFileManagerView.axaml`
- Create: `src/Kei.Term.App/Views/RemoteFileManagerView.axaml.cs`
- Create: `src/Kei.Term.App/ViewModels/RemoteFileManagerViewModel.cs`
- Modify: `src/Kei.Term.App/Views/MainWindow.axaml`
- Modify: `src/Kei.Term.App/ViewModels/TerminalTabViewModel.cs`
- Modify: `src/Kei.Term.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `IRemoteFileSystem`, `RemoteFileItem`, `FileEditorLauncher` from Tasks 1-3.
- Produces:
  - 可复用的通用文件管理器控件 `RemoteFileManagerView`；
  - 终端侧栏平铺伴随布局（`GridSplitter` 支持自由拖动宽度，折叠/展开）；
  - 顶部 `↗` 一键升格为全屏独立 Tab 的调度逻辑。

- [ ] **Step 1: 构建 `RemoteFileManagerView` 及 ViewModel**
  - **顶部工具栏**：路径输入框（支持输入跳转与上级 `⬆`）、刷新 `↻`、新建文件夹 `➕`、显隐隐藏文件 `.*`，以及侧栏专有的升格独立 Tab 按钮 `↗` 与关闭按钮 `✕`；
  - **文件列表区**：展示图标、名称、大小、修改时间、权限；支持双击进入目录或唤醒本地编辑器；
  - **右键上下文菜单**：包含「下载到桌面」、「下载到指定目录...」、「重命名」、「删除」、「新建文件夹」、「修改权限」、「复制路径」；
  - **向内拖拽上传（Drag In）**：监听 `DragDrop.DropEvent`，解析拖入的本地文件列表，自动批量上传至当前远程路径；
  - **底部迷你传输条**：展示当前传输速率与进度。

- [ ] **Step 2: 平铺集成至终端视图**
  - 修改终端 Tab 视图，将 `TerminalControl`、`GridSplitter` 和包裹了 `RemoteFileManagerView` 的容器并排在同一 `Grid` 中；
  - 侧栏展开/收起/拖拽宽度时，触发 `TerminalTabViewModel.TrySyncTerminalSize()`，确保终端列数平滑无损适配；
  - 终端标签页工具栏增加侧栏开关图标按钮。

- [ ] **Step 3: 实现升格独立 Tab 逻辑**
  在 `RemoteFileManagerViewModel` 中响应升格事件，通过 `MainViewModel` 新建一个独立的 `FileManagerTabViewModel`（复用当前的 `IRemoteFileSystem` 会话状态与当前路径），并自动收起当前终端的侧栏。

---

### Task 5: 综合测试与全量回归验证

**Files:**
- Create: `tests/Kei.Term.Tests/Services/LocalFileTrackerTests.cs`
- Create: `tests/Kei.Term.Tests/Services/SftpChannelModeTests.cs`

- [ ] **Step 1: 编写单元测试**
  - 测试 `LocalFileTracker` 在三种模式下的事件捕获；
  - 测试原子保存（临时文件写 + 重命名）的防抖与句柄安全捕获；
  - 测试 SHA-256 脏检查在内容无变动时的无害拦截；
  - 测试 `SftpChannelMode` 枚举与配置解析逻辑。

- [ ] **Step 2: 集中编译与回归测试**
  - 运行 `dotnet build Kei.Term.slnx` 确保零错误、零编译警告；
  - 运行 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj` 保证所有既有测试及新增测试全部通过。
