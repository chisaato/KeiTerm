# KeiTerm SFTP / SCP 文件管理与本地追踪系统设计规范

- **日期**：2026-09-28
- **状态**：设计已批准（待转入实现规划）
- **适用项目**：KeiTerm (.NET 10 / C# 13, Avalonia 12.x, CommunityToolkit.Mvvm)

---

## 1. 目标与范围

为 KeiTerm 扩展远程文件传输与管理能力，支持 **SFTP** 与 **SCP** 双协议，提供类似 Wave / Warp 风格的终端平铺伴随式文件管理侧栏，并支持一键升格为全屏独立管理标签页（Tab）。解决外部编辑器修改远程文件时的句柄失效、丢失变更以及原子保存（Atomic Save）无法监视等业界通病。

### 核心范围
1. **协议层与通道模式**：
   - SFTP 支持三种模式：`智能协商（Auto，默认）`、`子系统复用（Subsystem）`、`独立连接（Dedicated TCP）`。
   - SCP 作为同级独立传输协议。
   - 会话属性扩展：在会话属性持久化配置中指定文件传输协议与 SFTP 通道模式。
2. **UI 与交互架构**：
   - 终端平铺伴随式折叠侧栏（Docked Split with `GridSplitter`）。
   - 顶部工具栏：路径导航、快捷操作（刷新/新建/上一级）、显示隐藏文件切换、**一键升格独立 Tab（`↗`）**。
   - 虚拟化文件列表、右键菜单、支持系统外部拖拽上传。
   - 底部迷你传输状态栏与任务进度。
3. **本地缓存与健壮文件监视引擎（`LocalFileTrackingService`）**：
   - 全局配置本地缓存目录（`AppSettings.FileTransfer.CacheDirectory`），提供缓存查看与一键清理。
   - 文件变更追踪模式（三选一）：`智能模式（Auto，默认）`、`系统原生（OSNative）`、`定时轮询（Polling）`，应对部分 Linux 发行版 `fs.inotify.max_user_watches` 配额极低或特殊文件系统环境。
   - 双击文件本地打开与追踪：防抖校验文件句柄占用，基于 SHA-256 指纹比对触发自动回写。
   - 远程并发修改冲突检测与弹窗防护。

---

## 2. 总体架构与工程分层

遵循项目架构约束，严守依赖边界：

```
┌────────────────────────────────────────────────────────┐
│  Kei.Term.App (Avalonia 12.x / MVVM)                   │
│  - RemoteFileManagerView & ViewModel (自包含控件)       │
│  - TerminalTabView (平铺侧栏与 GridSplitter)           │
│  - SettingsWindow (文件缓存配置与清理交互)              │
└──────────────────────────┬─────────────────────────────┘
                           │ 依赖接口
┌──────────────────────────▼─────────────────────────────┐
│  Kei.Term.Core (纯领域业务与数据抽象，零外部/UI依赖)   │
│  - IRemoteFileSystem / RemoteFileItem                  │
│  - IFileTransferManager / TransferTaskModels           │
│  - ILocalFileTracker (本地追踪抽象)                    │
│  - FileTransferOptions (Auto/Subsystem/Dedicated/Scp)  │
└──────────────────────────┬─────────────────────────────┘
                           │ 接口实现
┌──────────────────────────▼─────────────────────────────┐
│  Kei.Term.Ssh (SSH.NET 实现层)                         │
│  - SftpRemoteFileSystem (封装 SftpClient / 多路复用)   │
│  - ScpRemoteFileSystem (封装 ScpClient)                │
└────────────────────────────────────────────────────────┘
```

---

## 3. 详细设计

### 3.1 Core 抽象层 (`Kei.Term.Core`)

#### 1. 文件系统抽象 `IRemoteFileSystem`
```csharp
namespace Kei.Term.Core.Abstractions;

public record RemoteFileItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastModified,
    string Permissions,
    int UserId,
    int GroupId);

public interface IRemoteFileSystem : IAsyncDisposable
{
    bool IsConnected { get; }
    string WorkingDirectory { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoteFileItem>> ListDirectoryAsync(string path, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);
    Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default);
    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
    Task ChangePermissionsAsync(string path, int octalPermissions, CancellationToken ct = default);
    Task<RemoteFileItem?> GetItemAsync(string path, CancellationToken ct = default);
}
```

#### 2. 会话配置与传输设置模型
在会话定义中扩展文件传输属性：
```csharp
public enum FileTransferProtocol
{
    Sftp = 0,
    Scp = 1
}

public enum SftpChannelMode
{
    Auto = 0,        // 优先在已有会话开 subsystem，被拒则降级独立 TCP
    Subsystem = 1,   // 严格复用现有终端 SSH 连接通道
    Dedicated = 2    // 始终建立新的独立 TCP + SSH 连接
}
```

#### 3. 本地缓存与追踪配置 (`AppSettings`)
```csharp
public enum FileWatcherMode
{
    Auto = 0,       // 智能：优先 OS 原生事件，异常/不可用或特定文件系统时平滑回退到定时轮询
    OSNative = 1,   // 严格依赖操作系统原生事件（Linux inotify / Windows ReadDirectoryChangesW / macOS kqueue）
    Polling = 2     // 纯定时轮询（适合 inotify 配额耗尽、容器或网络共享虚拟盘）
}

public class FileTransferSettings
{
    // 本地缓存存储基路径，默认 ~/.local/share/KeiTerm/cache 或 %LOCALAPPDATA%/KeiTerm/cache
    public string CacheDirectory { get; set; } = string.Empty;

    // 文件监视模式（默认 Auto 智能模式）
    public FileWatcherMode WatcherMode { get; set; } = FileWatcherMode.Auto;

    // 轮询兜底/纯轮询模式间隔（秒）
    public int PollingIntervalSeconds { get; set; } = 3;

    // 文件写锁释放防抖等待时间（毫秒）
    public int WriteDebounceMilliseconds { get; set; } = 800;
}
```

---

### 3.2 SSH 协议与通道实现 (`Kei.Term.Ssh`)

#### 1. `SftpRemoteFileSystem` 实现机制
- **Auto 模式**：
  检查宿主 `ISshSession` 是否活跃且暴露底层 client。若支持通道开启，尝试调用 `CreateChannelSubsystem("sftp")` 或初始化内部分包处理；若不可行或鉴权/通道开启失败，立即采用已解析的会话凭证（`ResolvedSessionConfig` 与 `MaterializedAuthMethod`）初始化独立的 `Renci.SshNet.SftpClient`。
- **Subsystem 模式**：严格走已有 SSH 物理通道，若不支持则抛出明确的 `SubsystemNotSupportedException`，不产生多余的独立 TCP 握手。
- **Dedicated 模式**：直接利用凭证物化管线创建独立的 `SftpClient`，生命周期与终端完全隔离。

#### 2. `ScpRemoteFileSystem` 实现机制
- 包装 `Renci.SshNet.ScpClient`。
- 对于 `ListDirectoryAsync` 等目录操作，由于标准 SCP 协议不提供交互式目录树接口，通过 SSH Exec Channel 执行轻量标准化探测命令（`ls -la --time-style=+%s`）回填，上传与下载直走 SCP 流。

---

### 3.3 本地文件监视与防踩坑追踪引擎 (`LocalFileTrackingService`)

为彻底解决类似 SecureCRT 的句柄失效、原子覆盖丢失等问题，采用**多重保障追踪架构**：

1. **缓存目录隔离**：
   本地路径模板：`{CacheDir}/{SessionId}/{RemotePathHash}_{SanitizedFileName}`，避免并发或同名碰撞。
2. **三模式文件变更监视引擎**：
   - **智能模式（Auto，默认）**：针对包含该文件的父级目录挂载 `FileSystemWatcher`，监听 `NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size`（Linux 下对应 `inotify`，macOS 对应 `kqueue`）。同时启动低频轮询对比 LastWriteTimeUtc；一旦捕获到系统事件失效或异常（如 inotify 句柄耗尽引发 `IOException: The configured user limit (128) on the number of inotify instances has been reached`），自动无缝降级为纯轮询驱动，保证不丢失任何文件变更。
   - **系统原生模式（OSNative）**：完全基于 OS 原生事件通知，零后台轮询 CPU 损耗，适用于标准桌面环境。
   - **纯轮询模式（Polling）**：彻底绕过 OS 原生文件系统通知，采用轻量定时遍历与元数据比对。专为 inotify 实例配额极度严格受限的系统、容器、或 NFS/SMB 网络挂载缓存设计。
3. **句柄释放检测与防抖校验**：
   - 收到事件或检测到时间变动后，启动可重置的防抖定时器（默认 800ms）。
   - 防抖触发后，以 `FileShare.None, FileAccess.Read` 尝试排他性打开文件。若被外部编辑器占用锁定，指数退避重试（最多重试 5 次），防止读取到半截写入的损坏数据。
4. **SHA-256 变更与冲突判定**：
   - 计算本地文件最新 SHA-256，若与下载时的初始 SHA-256 相同则忽略；
   - 在触发回写上传前，调用 `IRemoteFileSystem.GetItemAsync(remotePath)` 获取远程当前 `LastModified`。
   - 若远程文件在本地编辑期间被第三方修改，触发冲突处理事件，提示用户 `[强制覆盖] / [保留双份另存为] / [放弃上传]`。

---

### 3.4 UI 与交互架构 (`Kei.Term.App`)

#### 1. 自包含控件：`RemoteFileManagerView` / `RemoteFileManagerViewModel`
- 可嵌入侧栏，也可作为独立 Tab 使用。
- **工具栏功能**：
  - 路径面包屑与可编辑地址栏（支持 Enter 确认跳转）。
  - `⬆` 上级目录、`↻` 刷新、`➕` 新建目录、`.*` 显隐隐藏文件。
  - **`↗` 升格独立 Tab** 按钮（侧栏模式特有，点击后在主窗口打开对应的独立 Tab，并收起侧栏）。
- **文件列表**：
  - 显示图标、文件名、大小、修改时间、权限字符串。
  - 支持双击：目录则进入；文件则调用 `LocalFileTrackingService` 下载到本地缓存并拉起系统默认编辑器，启动自动同步追踪。
  - **右键上下文菜单**：
    - **快捷下载**：提供「下载到桌面（Download to Desktop）」及「下载到指定目录...」；
    - 基础管理：重命名、删除、新建文件夹、修改权限（chmod）、复制绝对路径；
    - 剪贴板支持：快捷键 `Ctrl+C` 复制选中文件路径/元数据。
  - **拖拽支持**：
    - **向内拖拽（Drag In，Phase 1）**：监听 `DragDrop.DropEvent`，接收系统外（桌面/文件管理器）拖入的文件或目录，自动入队批量上传至当前远程路径。
    - **向外拖拽（Drag Out，远期规划）**：由于 Linux 与 Windows 底层拖拽流协议的差异，虚拟流边下边拖（Virtual Stream Drag-Drop）作为 Windows 平台专有增强在后续阶段演进。
- **迷你传输条**：
  - 列表底栏展示当前活跃传输项（方向、文件名、百分比、瞬时速率）；
  - 支持点击展开任务队列弹层（查看已完成/排队/失败项，支持重试/取消）。

#### 2. 终端平铺集成 (`TerminalTabView`)
- `TerminalTabView.axaml` 采用三列 `Grid`：
  - `Col 0`：`TerminalControl`（自适应填满剩余空间）；
  - `Col 1`：`GridSplitter`（宽度 4px，侧栏展开时可见且可拖拽，收起时设为 `Collapsed`）；
  - `Col 2`：`RemoteFileManagerView` 宿主面板（默认宽度 320px，支持动态拉伸）。
- 终端右上角提供折叠/展开小图标，一键开关。

#### 3. 设置界面扩展 (`SettingsWindow`)
- 在「常规」或新增的「文件传输」设置页，提供：
  - 缓存目录设置与路径选择器；
  - 缓存占用体积统计与「清空本地缓存」按钮。

---

## 4. 异常处理与边界测试策略

1. **测试用例（`tests/Kei.Term.Tests`）**：
   - `LocalFileTrackingServiceTests`：
     - 测试普通覆写触发回写；
     - 测试原子覆盖（先写临时文件再 rename）能否正确被捕获；
     - 测试多进程锁定状态下的防抖退避与成功读取；
     - 测试 SHA-256 无变更时的防抖截断；
     - 测试远程并发冲突识别逻辑。
   - `SftpChannelModeTests`：
     - 测试 Auto 模式在 Subsystem 失败时的降级回退行为；
     - 测试 Subsystem 与 Dedicated 的生命周期与释放。
2. **安全性与防护**：
   - 路径穿越防护：处理远程文件名时进行规范化，杜绝 `../` 越界注入。
   - 临时文件清理防护：应用退出时，若存在尚未同步完成的脏文件，提示用户阻止意外退出。

---
