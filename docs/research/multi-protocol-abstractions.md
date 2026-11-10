# KeiTerm 多协议演进：协议、传输引擎与解耦调研报告

> **核对日期**：2026-11-09  
> **代码基线**：`feat/go-transport-sidecar` (`cea95b3`)  
> **调研目标**：为 KeiTerm 支持 Telnet、Serial 等非 SSH 会话演进做仓库内现状调研与技术评估。本报告重点分析代码库中既有抽象的复用性与强耦合点，梳理协议特性、传输引擎与物理承载的考量维度，客观评估 Go 与 .NET 双实现的演进先后，提出候选解耦边界、兼容性风险与验证路线，不实现生产代码。  
> **范围与约束**：本调研保持既有代码及现有未提交改动（`docs/design/go-transport-sidecar.md`、ADR-0005 链接变更）严格只读。

---

## 一、事实与证据：现有代码全链路追踪

本节基于当前仓库代码，按连接建立与生命周期的调用链，记录各模块的实际代码现状与文件行号证据。

### 1. `ITerminalSession`：基础终端会话抽象
- **文件位置**：`src/Kei.Term.Core/Abstractions/ITerminalSession.cs` (第 5-19 行)
- **实际成员定义**：
  ```csharp
  public interface ITerminalSession : IAsyncDisposable
  {
      Guid SessionId { get; }
      bool IsConnected { get; }

      // 远端/子进程输出（可能来自后台线程，订阅方负责切回 UI 线程）
      event Action<byte[]>? OutputReceived;

      // 会话结束：null 为正常退出，非 null 为异常断开
      event Action<Exception?>? Disconnected;

      Task ConnectAsync(CancellationToken ct = default);
      Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
      Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default);
  }
  ```
- **预留协议标识**（第 21-29 行）：
  ```csharp
  public static class SessionProtocols
  {
      public const string Ssh = "ssh";
      public const string Local = "local";
      public const string Serial = "serial";
      public const string Telnet = "telnet";
  }
  ```
- **事实与差异**：
  - `SessionId`、`IsConnected`、`OutputReceived`、`Disconnected`、`ConnectAsync`、`SendInputAsync` 构成了终端双向字节流的基础契约。
  - `ResizeTerminalAsync`（第 18 行）直接定义在核心接口上。
  - **调用点证据**：`src/Kei.Term.App/Terminals/TerminalSessionEndpoint.cs:33-38`：
    ```csharp
    public void SetSize(int widthPx, int heightPx)
    {
        int cols = _terminal.Columns;
        int rows = _terminal.Rows;
        _ = _session.ResizeTerminalAsync(cols, rows, widthPx, heightPx);
    }
    ```
    当控件布局改变时，端点桥会无条件发起 `ResizeTerminalAsync` 调用。
  - **协议差异事实**：
    - Serial 是本地字符设备流，物理链路本身没有动态调整终端行列大小的带内协商信令。
    - Telnet 规范中的窗口大小协商（RFC 1073 NAWS）是可选的子协商选项，需服务端响应与确认；在未协商成功或对端拒绝时，无法进行动态 resize。

### 2. `ISshSessionFactory`：会话工厂抽象
- **文件位置**：`src/Kei.Term.Ssh/Abstractions/ISshSession.cs` (第 62-89 行)
- **事实与绑定**：
  - 当前工厂接口将三种能力合并在单一接口中：
    1. 交互式终端会话：`CreateSessionAsync` (第 65-69 行)
    2. 远程文件系统：`CreateFileSystemAsync` (第 73-78 行)
    3. 专用传输连接：`CreateTransferChannelAsync` (第 82-88 行)
  - **方法参数强绑定**：各方法入参均要求 `ResolvedSessionConfig`、`IReadOnlyList<MaterializedAuthMethod>`（密码/私钥认证材料）以及 `SshConnectOptions`（内部承载 `JumpHosts`、`Socks5Host`、`PortForwards`、`AgentSocketPath`）。
- **多引擎分发实现**：
  - `src/Kei.Term.Transport.Go/CompositeSshSessionFactory.cs:8-30`：双引擎切换目前是在 `ISshSessionFactory` 粒度下进行的，根据 `TransportEngineOverride ?? _defaultEngine()` 分发至 `_builtIn` 或 `_go.Value`。
- **Go Sidecar 端对应定义**：
  - `src/Kei.Term.Transport.Go/engine/pkg/ipc/rpc.go:77-81`：Go 端定义的内部 `Session` 接口包含 `OpenShell` 与 `OpenSFTP()`；`ConnectParams`（第 52-61 行）包含 `Endpoint.Auth`（密码/私钥）、`JumpHosts`、`HostKeySHA256`。

### 3. `IRemoteFileSystem`：文件系统抽象与挂载
- **文件位置**：`src/Kei.Term.Core/Abstractions/IRemoteFileSystem.cs` (第 6-76 行)
- **事实与实现情况**：
  - `IRemoteFileSystem` 是目录树操作（ListDirectory, OpenRead, OpenWrite, Delete, Rename, GetItem 等）的通用抽象，目前在代码库中仅由 SSH 相关的 SFTP（`SftpRemoteFileSystem`、`GoRemoteFileSystem`）与 SCP（`ScpRemoteFileSystem`）实现。
  - **编排器挂载事实**：`src/Kei.Term.App/Services/Connection/ConnectionOrchestrator.cs:265-271, 386-411`：
    ```csharp
    // 行 265-271
    StartFileSystem(
        target,
        resolved,
        current.ToList(),
        session!,
        BuildConnectOptions(timeout, jumpHops, socks5Host, socks5Port, socks5Username, socks5Password, forwards, attempt));
    ```
    `StartFileSystem` 内部将文件系统创建放入后台任务：
    ```csharp
    // 行 393-409
    _ = Task.Run(async () =>
    {
        try
        {
            IRemoteFileSystem fileSystem = await _sshFactory.CreateFileSystemAsync(config, materials, session, options);
            (ITransferEndpoint? transfers, string? unavailable) = await CreateTransferEndpointAsync(config, materials, session, options);
            if (transfers == null && unavailable != null)
            {
                target.ReportWarning(unavailable);
            }
            await target.AttachFileAccessAsync(fileSystem, transfers, session, unavailable);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化远程文件系统侧栏失败");
        }
    });
    ```
  - **观察**：`StartFileSystem` 包含 try-catch 保护，失败时记录日志且不阻断终端会话，但编排器在连接成功后对所有会话无差别地派发了文件系统初始化流程。
  - **带内传输抽象**：`src/Kei.Term.Core/Abstractions/ITerminalFileUploader.cs:19-31` 声明了 `ITerminalBinaryTransferSession` 与 `ITerminalBinaryChannel`（目前仅由 `SshNetSession` 实现并用于 ZModem/YModem 上传）。带内文件传输依赖对端系统安装对应工具（如 `lrzsz`）及当前会话具有二进制透明读写通道。

### 4. `ConnectionOrchestrator`：连接编排与重连流程
- **文件位置**：
  - `src/Kei.Term.App/Services/Connection/ConnectionOrchestrator.cs` (第 23-460 行)
  - `src/Kei.Term.App/Services/Connection/ConnectionOrchestrator.Reconnect.cs` (第 8-85 行)
- **依赖与流程事实**：
  1. 编排器构造函数直接注入 `ISshSessionFactory _sshFactory`（第 33 行）。
  2. 拨号前（第 95-129 行）：执行 `PrepareExitAsync` 检查出口代理、`_collector.CollectTargetAsync` 收集 Vault 密码/私钥认证材料、`_collector.CollectJumpHopsAsync` 构建跳板链。
  3. 异常处理（第 305-315 行）：捕获 `HostKeyRejectedException`，转调 `_hostKeyTrust: HostKeyTrustService` 触发主机密钥人工确认与放行重连。
  4. 重连逻辑（`ConnectionOrchestrator.Reconnect.cs:8-85`）：围绕 SSH 会话的重试计数与凭据更新设计。

### 5. 配置、持久化、SecureCRT 导入与同步现状
- **文件位置**：
  - `src/Kei.Term.Core/Models/TreeNodes.cs` (第 98-171 行)
  - `src/Kei.Term.Core/Services/SessionConfigBuilder.cs` (第 11-70 行)
  - `src/Kei.Term.Core/Services/SecureCrtImporter.cs` (第 125-169 行)
  - `src/Kei.Term.Infrastructure/Storage/Schema/SchemaMigrations.cs` (第 46-58, 65-77, 133-135 行)
  - `src/Kei.Term.Infrastructure/Storage/SqliteTreeRepository.cs` (第 26-31, 190-218, 343-356 行)
- **事实记录**：
  1. **SQLite 表结构**：
     - `tree_nodes` 表具备 `protocol TEXT NOT NULL DEFAULT 'ssh'` 列（`SchemaMigrations.cs:72, 134`）。
     - `session_details` 表（`SchemaMigrations.cs:46-58`）结构固定：`node_id`、`host TEXT NOT NULL`、`port INTEGER`、`username TEXT`、`identity_id TEXT`、`file_transfer_protocol INTEGER`、`sftp_mode INTEGER`、`options_json TEXT` 等。
  2. **内存模型字段传递**：
     - `SessionNode` 包含属性 `public string Protocol { get; set; } = SessionProtocols.Ssh;`（`TreeNodes.cs:104`）。
     - 但在解析产物 `ResolvedSessionConfig`（`TreeNodes.cs:139-171`）中，**未声明 `Protocol` 属性**。
     - `SessionConfigBuilder.Build`（`SessionConfigBuilder.cs:11-61`）未读取 `node.Protocol`，且在 `node.Port` 为空时，统一回退为 `settings.DefaultPort`（全局默认端口，初始配置通常为 SSH 22）。
  3. **SecureCRT 导入实际行为**：
     - `SecureCrtImporter.cs:138-141`：当配置中协议为 `"Serial"` 时，将 `protocol` 赋值为 `SessionProtocols.Serial`。
     - 但对于网络主机和端口，代码通过正则表达式匹配：
       ```csharp
       // 行 26-27, 152-160
       private static readonly Regex HostRegex = new(@"S:""Hostname""=(.*)", RegexOptions.Compiled);
       private static readonly Regex PortRegex = new(@"D:""\[SSH2\] Port""=([0-9a-fA-F]+)", RegexOptions.Compiled);
       ```
     - 在 SecureCRT 的 Serial 会话配置文件中，通常不含 `[SSH2] Port` 和 `Hostname`。导入器匹配失败后，将 `port` 赋值为兜底默认值 `22`，`host` 为 `""`。代码**并未解析** SecureCRT 专有的 `S:"Port"=COMx` 或波特率字段。
  4. **`options_json` 序列化与未知字段表现**：
     - `SqliteTreeRepository.cs:27-31` 定义的 `OverridesJson` 仅配置了 `JsonStringEnumConverter` 与 `WhenWritingNull`。
     - `SessionOverrides`（`SessionOverrides.cs:16-40`）是一个普通 C# 类，**未包含 `[JsonExtensionData]` 字典**。
     - **序列化事实**：如果高版本在 JSON 中写入了未知字段，运行低版本代码的客户端反序列化时能正常忽略未知字段（具有向前读兼容性）；但一旦该低版本客户端重新执行保存（`SerializeOverrides`），**未在模型中声明的属性将在写回时被丢弃**。
     - **同步快照事实**：`SqliteTreeRepository.ExportSyncEntitiesAsync`（第 343-356 行）将 `SessionNode` 导出至同步快照实体。

### 6. 终端生命周期与视图交互
- **文件位置**：
  - `src/Kei.Term.App/Terminals/TerminalSessionEndpoint.cs` (第 8-48 行)
  - `src/Kei.Term.App/ViewModels/TerminalTabViewModel.cs` (第 197-337 行)
  - `src/Kei.Term.App/ViewModels/TerminalTabViewModel.FileDrop.cs` (第 50-115 行)
  - `src/Kei.Term.Ssh/Services/TerminalFileUploader.cs` (第 44-77 行)
- **事实记录**：
  - `TerminalSessionEndpoint` 监听 `OutputReceived` 并写入 `TerminalControl.WriteOutput`，键盘输入通过 `SendInputAsync` 发送。
  - `TerminalTabViewModel.OnSessionDisconnected`（第 301-337 行）订阅会话的 `Disconnected` 事件。断开事件携带可空异常对象（`Exception?`），由视图模型判断是正常退出还是异常中断，并在启用自动重连时派发 `UnexpectedDisconnectAsync`。
  - **未来适配契约**：对于 Serial 设备（如 USB 转串口热拔除），由于当前代码库尚无 Serial 驱动适配器，设备断开检测依赖未来具体的 Serial 实现主动捕获 I/O 错误并触发 `Disconnected` 事件。
  - **文件拖放事实**：`TerminalTabViewModel.FileDrop.cs:89-105` 中，拖入文件且用户选择 Upload 时，转调 `TerminalFileUploader.UploadAsync`。该方法在协议为 SFTP/SCP 时显式校验 `session is not SshNetSession` / `ISshSession`（`TerminalFileUploader.cs:46, 73`），非 SSH 连接会直接抛出 `NotSupportedException`。

### 7. 词汇表（`GLOSSARY.md`）现状
- **文件位置**：`GLOSSARY.md` (第 7-9 行)
- **文本事实**：
  ```markdown
  **连接**:
  一次已登录的 SSH 会话，是终端视图、文件视图与传输队列的唯一所有者；连接断开或关闭时，其名下的一切随之关闭或复位。
  _Avoid_: 会话（指会话树中的配置节点时除外）、Connection Unit
  ```
  该统一定义将“连接”与“已登录的 SSH 会话”完全绑定。本调研指出该领域词汇限定，但遵守任务只读约束，不直接改动 `GLOSSARY.md`。

---

## 二、分析视角：协议类型、传输引擎与承载通道

为了避免把尚未确认的架构设想当作既定事实，本报告从三个正交维度进行技术分类与边界分析：

```
+-------------------------------------------------------------------------+
|                        1. 协议 / 连接类型 (Protocol)                     |
|      - SSH：多通道、密码学握手与认证、PTY Resize、伴生 SFTP/SCP、端口转发    |
|      - Telnet：面向字节流、IAC 选项协商 (NAWS/TTYPE 等)、明文交互认证       |
|      - Serial：本地硬件/虚拟字符设备流、物理链路参数配置、无协议级握手      |
|      - (未决范围：Local Shell、TLS Telnet、自动登录脚本等)                |
+-------------------------------------------------------------------------+
                                     |
                                     v
+-------------------------------------------------------------------------+
|                     2. 传输引擎实现 (Transport Engine)                  |
|      - .NET 内置 (BuiltIn)：进程内托管实现 (如 SSH.NET、System.IO.Ports)   |
|      - Go 核心 (GoCore)：独立 Sidecar 守护进程 / 未来 In-Process 预留     |
+-------------------------------------------------------------------------+
                                     |
                                     v
+-------------------------------------------------------------------------+
|                      3. 物理与数据承载 (Carrier Channel)                 |
|      - TCP Socket (网络直连)                                            |
|      - 本地 OS 字符设备句柄 (如 Windows COM 口、Unix /dev/ttyUSB*)       |
|      - 转发隧道 / 代理流 (如 SOCKS5、SSH direct-tcpip 隧道)              |
+-------------------------------------------------------------------------+
```

### 概念边界说明
1. **Serial 的本质**：Serial 在通信分层上是一种连接类型与硬件设备通道（OS 字符设备 I/O），而非像 SSH 那样的多路复用应用协议。它不具备网络寻址的主机/端口概念，核心配置为串口名、波特率、校验位、数据位、停止位和硬件流控。
2. **Telnet 的特性**：Telnet 基于 TCP 承载，但本身定义了双向字节流上的 IAC（`0xFF`）信令协商协议。其终端尺寸控制（RFC 1073）依赖对端会话中实时的子协商（NAWS），与 SSH 客户端主动发送 PTY `window-change` 信令机制不同。

### 协议与连接类型特性矩阵（技术特征与现状比对）

| 特性维度 | SSH (现行实现) | Telnet (技术特征与现状) | Serial (技术特征与现状) |
| :--- | :--- | :--- | :--- |
| **基础终端 I/O** | 现已支持（经 `ITerminalSession`） | 理论契合基础流；代码库目前**未实现** | 理论契合基础流；代码库目前**未实现** |
| **动态尺寸调整 (Resize)** | 现已支持（PTY 信令） | 条件支持（依赖 RFC 1073 NAWS 握手） | **物理链路不支持**（无协商通道） |
| **协议握手认证** | 现已支持（密码/私钥/证书/Agent） | 协议握手无凭据（通常为屏幕交互登录） | 物理连接无认证（通常为目标固件提示） |
| **主机身份与指纹** | 现已支持（HostKey 探测与信任） | 协议无主机指纹机制 | 无主机公钥或指纹概念 |
| **伴生文件系统** | 现已支持（SFTP / SCP） | **无伴生文件系统协议** | **无伴生文件系统协议** |
| **带内文件传输** | 部分支持（ZModem/YModem 上传） | 理论可行（需具备透明信道与对端工具支持） | 理论可行（需具备透明信道与对端工具支持） |
| **跳板与出口代理** | 现已支持（SSH 多跳、SOCKS5） | 理论可通过 SOCKS5 或 SSH 隧道承载 | 仅限本地设备通道（非网络连接） |
| **硬件物理参数** | 无 | 无 | 强依赖（波特率/校验/停止位/流控） |

---

## 三、解耦候选方案与技术权衡

### 1. 终端尺寸调整（Resize）的解耦权衡
针对 Serial 无 resize、Telnet 条件 resize 的现实，存在以下候选方案：

- **方案 A：保持接口统一，非支持协议提供空实现（No-op）**
  - *做法*：在 Serial 或未协商 NAWS 的 Telnet 会话中，`ResizeTerminalAsync` 返回 `Task.CompletedTask`。
  - *优势*：调用方（如 `TerminalSessionEndpoint`）完全无需改动。
  - *代价与权衡*：上层无法感知底层是否真正生效；掩盖了能力缺失，把“不支持”混同于“设置成功”。
- **方案 B：能力接口拆分（Interface Segregation）**
  - *做法*：定义 `ITerminalResizeCapability`，仅支持动态调整的会话实现该接口；端点桥在调用前通过模式匹配（`_session is ITerminalResizeCapability resizable`）进行检查。
  - *优势*：契约精确，不强迫硬件串口或哑终端假装具备窗口尺寸调整能力。
  - *代价与权衡*：增加了接口层级，调用方需做类型判断。
- **方案 C：能力特性查询对象（Capability Flags）**
  - *做法*：在 `ITerminalSession` 上暴露只读属性 `ITerminalCapabilities Capabilities { get; }`，包含 `bool CanResize`、`bool HasRemoteFileSystem` 等。
  - *优势*：属性直观，避免频繁类型向下转换。
  - *代价与权衡*：需要在会话生命周期内维护动态能力标志（例如 Telnet 在 NAWS 协商成功前后状态的变更）。

### 2. 编排器解耦与协议路由
为了解决 `ConnectionOrchestrator` 将 SSH 认证、跳板与文件系统强加给所有连接的问题，建议的边界如下：

- **抽取协议连接适配器（Protocol Connection Handler）**：
  将“如何发起一次连接”从 `ConnectionOrchestrator` 中剥离，编排器退居为上层协调者（管理标签创建、UI 状态置位、统一异常报告与重连定时器）：
  ```csharp
  public interface IProtocolConnectionHandler
  {
      string Protocol { get; }
      Task<ConnectionOutcomeResult> ConnectAsync(ConnectionContext context, CancellationToken ct);
  }
  ```
- **职责清晰化**：
  - **SSH Handler**：执行 Vault 认证提取、多跳解析、主机密钥校验、SFTP 挂载；
  - **Telnet Handler**：解析网络地址与端口，建立 TCP 通道，初始化 Telnet 选项协商状态机；
  - **Serial Handler**：解析本地物理串口配置，校验设备存在性，打开 OS 字符流句柄；不执行网络拨号，不弹 Vault 认证。
- **文件伴生能力的按需挂载**：
  编排器仅在 Handler 返回有效 `IRemoteFileSystem` 实例时才触发 `target.AttachFileAccessAsync`。若返回 null，则跳过文件系统挂载，避免无效的后台任务执行。

### 3. 配置存储与同步兼容性分析

#### (1) 设备专属项 vs 可同步项的划分
- **本机专属项（Machine-local）**：
  物理串口的设备路径（如 Windows 上的 `COM3`、Linux 上的 `/dev/ttyUSB0`、macOS 上的 `/dev/cu.usbserial`）是**强设备绑定的**。
  根据 `GLOSSARY.md:131`（“*本机专属项: 只属于当前设备、不进入同步包的数据...*”），若直接将物理串口路径写入参与 E2EE 同步的数据结构，在跨平台多设备同步后将产生无效路径，甚至引发配置覆盖。
- **可同步项（Syncable）**：
  通信参数（如波特率 115200、8 数据位、无校验、1 停止位）属于对端设备（如嵌入式交换机控制台）的客观属性，可作为跨设备同步项。

#### (2) 持久化载体权衡
- **候选方案 A：利用 `session_details.options_json` 承载**：
  - *现状验证*：`session_details.options_json` 已用于存储 `SessionOverrides`。
  - *兼容性限制*：当前 `SessionOverrides` 缺乏 `[JsonExtensionData]` 支持。在多设备同步或低版本客户端参与的场景下，低版本读回后再写盘会丢失高版本扩展的未知 JSON 键。如果选用此方案，需同步补足未知键的保活机制。
- **候选方案 B：独立扩展 SQLite 表结构或从表**：
  - *现状验证*：需要在 `SchemaMigrations.cs` 中增加版本迁移脚本。
  - *兼容性限制*：SQLite DDL 变更需确保级联约束与同步快照数据转换的完整性。

---

## 四、Go 与 .NET 双引擎实现先后评估

根据要求，未来两种引擎均需具备支持能力，当前仅客观评估演进顺序的利弊与技术门槛，不作定案。

### 1. 路径对比

#### 路径一：.NET 原生内置先行（.NET First, Go Second）
- **技术实现方式**：
  - Telnet：在 C# 进程内使用 `System.Net.Sockets.TcpClient` / `Socket`，编写轻量状态机处理 IAC 转义与 NAWS。
  - Serial：在 C# 进程内使用 `System.IO.Ports.SerialPort`（或调用平台原生 termios/Win32 句柄的跨平台包）。
- **优势**：
  1. **研发链路短**：完全在 .NET 进程内闭环，不涉及 Go 守护进程、IPC 二进制帧与 JSON-RPC 路由的同步修改。
  2. **调试与测试简便**：可通过 `dotnet test` 进行单进程调试，便于针对 Telnet/Serial 状态机编写单元测试。
  3. **与默认引擎策略对齐**：当前全局默认引擎配置为 `BuiltIn`（.NET），优先推进 .NET 可直接在默认配置下提供能力。
- **劣势与技术风险**：
  1. **跨平台设备 I/O 差异**：`System.IO.Ports` 在不同操作系统（尤其是 Linux/macOS）下的句柄释放和取消令牌支持存在平台特定行为差异，需充分的硬件兼容性实测。
  2. **GoCore 用户在此期间无法使用**：若用户显式配置了 `GoCore` 引擎，尝试连接非 SSH 协议时将因 Go 端未实现而无法建立会话。

#### 路径二：Go Sidecar 核心先行（Go First, .NET Second）
- **技术实现方式**：
  - Go 核心：在 `engine/pkg/transport` 引入 Go 社区串口库（如 `bugst/go-serial`）与终端/Telnet 库。
  - IPC 契约：扩展 `engine/pkg/ipc/rpc.go`，新增例如 `session.connectTelnet`、`session.connectSerial` 等 RPC 方法；复用已有 Frame 协议传输数据。
  - C# 适配：通过 `GoIpcClient` 桥接流，组装为 `ITerminalSession`。
- **优势**：
  1. **硬件操作进程隔离**：串口驱动交互或系统级阻塞 I/O 运行在独立的 `kei-trans-daemon` 子进程中，底层故障不会直接冲击 Avalonia UI 托管进程。
  2. **跨平台设备底层抽象成熟**：Go 语言底层针对 POSIX termios 与 Windows COM 端口的封装在多平台上行为较为统一。
- **劣势与技术风险**：
  1. **双端联调与 IPC 扩展开销**：必须同步改动 Go 守护进程路由、C# IPC 通信层与进程管理逻辑，增加联调面。
  2. **移动端预期差异**：根据 ADR-0005 与现有实现快照，Go 核心目前在移动端尚无落地的 In-Process 运行架构；过早将非 SSH 协议完全压在 Go 端，无法直接复用于未来的移动端原生环境。

### 2. 引擎能力缺失的处理原则（保留不回退原则）
根据 `docs/design/go-transport-sidecar.md:12` 及 ADR-0005 的架构约定：**“不存在失败后自动回退 .NET 的逻辑”**。
因此，无论选择哪种先后路径，若某一引擎在当前阶段尚未实现特定协议，必须**显式返回明确的未支持能力异常（如 `NotSupportedException`）并向用户清晰提示**，严禁引入静默降级或隐式跨引擎回退。

---

## 五、可验证的 Tracer-Bullet 演进路线

为确保每一步演进均有可独立运行、可验证的测试凭据支撑，建议按以下阶段递进推进：

### Tracer 0：基础抽象解耦与配置透传（纯代码级契约打通）
- **目标**：
  1. 在 `ResolvedSessionConfig` 中补充 `Protocol` 属性；修改 `SessionConfigBuilder.Build` 使其不再强制缺省端口为 22。
  2. 为 `ITerminalSession.ResizeTerminalAsync` 解除强耦合（通过方案 A 的默认实现或方案 B 的能力接口拆分）。
  3. 解耦 `ConnectionOrchestrator`，引入协议路由接口。
- **验证手段**：
  - 编写 `SessionConfigBuilderTests`，针对 `SessionProtocols.Telnet` 与 `SessionProtocols.Serial` 节点断言生成的配置包含正确的协议标识与默认端口策略。
  - 验证 SSH 既有连接测试不发生回归。

### Tracer 1：首个非 SSH 协议验证 —— 基础 Telnet 贯通
- **目标**：
  1. 按照定案的引擎顺序（.NET 或 Go），实现最小化的 TCP 拨号与 RFC 854 基础字符收发。
  2. 实现 `TelnetConnectionHandler`，跳过 Vault 凭据、跳板链与主机密钥拦截，不触发 `StartFileSystem`。
  3. 挂载至 `TerminalSessionEndpoint`，实现画面双向字符交互与断开生命周期。
- **验证手段**：
  - 编写测试用 Mock Telnet Server（在本地监听 TCP 端口），模拟服务端发送文本欢迎语、回显用户输入并由客户端/服务端主动关闭连接，断言 `OutputReceived` 与 `Disconnected` 准确触发。

### Tracer 2：硬件连接类型验证 —— 基础 Serial 贯通
- **目标**：
  1. 确定串口参数在配置层与 UI 层的表达（波特率、串口名）。
  2. 实现 Serial 连接生命周期，包括端口打开、字节读写循环与物理断开捕获。
  3. 验证无网络、无密码、无伴生文件系统场景下的纯字符传输。
- **验证手段**：
  - 在 Linux CI/开发机上利用 `socat` 或虚拟 PTY 建立虚拟串口对，模拟串口打开、收发数据、对端断开，检验会话生命周期与异常抛出契约。

### Tracer 3：UI 交互与动态能力自适应
- **目标**：
  1. 在 `SessionEditWindow` 中增加协议切换支持，根据协议动态显示对应配置字段。
  2. 落实非 SSH 会话下的侧栏 UI 策略（依用户最终决策执行隐藏或占位）。
- **验证手段**：
  - 编写 ViewModel 与 UI 控件交互测试，验证协议切换时配置模型与视图状态的正确绑定。

### Tracer 4：双引擎功能对称与端到端回归
- **目标**：
  1. 补齐另一引擎的对应实现，达成 `BuiltIn` 与 `GoCore` 的功能对齐。
  2. 统一错误提示与异常处理策略。
- **验证手段**：
  - 双引擎下的集成测试回归与端到端场景验收。

---

## 六、待用户决策项清单 (Pending Decisions)

以下属于架构方向与产品体验层面的关键抉择，本报告客观列出选项，不替用户做定案选择：

- [ ] **决策项 1：Go 与 .NET 的实现优先级**
  - *选项 A*：**.NET 内置先行** —— 在 C# 进程内快速跑通 Telnet/Serial 验证闭环，零 IPC 改造成本，便于单进程测试；后续再移植到 Go 守护进程。
  - *选项 B*：**Go Sidecar 先行** —— 优先扩展 Go 核心与 IPC 协议，利用独立子进程实现设备 I/O 隔离，保护 UI 主进程；后续再补充 .NET 内部实现。
- [ ] **决策项 2：Serial 物理端口名称与同步策略**
  - *选项 A*：将串口名（如 `COM3`、`/dev/ttyUSB0`）视为**本机专属项**，不随同步包漫游到其他设备；仅波特率、数据位等通用参数参与同步。
  - *选项 B*：串口名依然随会话漫游，由目标设备在打开失败时提示用户修正本机端口映射。
- [ ] **决策项 3：协议专属配置在持久化层的形态**
  - *选项 A*：沿用 `session_details.options_json`，但需补充未知 JSON 属性的保活机制以防低版本重写丢失。
  - *选项 B*：在 SQLite 中为串口等连接类型追加独立列或独立的扩展从表。
- [ ] **决策项 4：非 SSH 连接时的文件侧栏 UI 呈现**
  - *选项 A*：**完全隐藏** —— 当连接无伴生文件系统的会话时，工作区不显示文件管理侧栏及切换图标。
  - *选项 B*：**禁用或占位** —— 侧栏保留展示位，但内容区呈现提示（如“当前协议不支持远程文件浏览”）。
- [ ] **决策项 5：首批演进范围收敛确认**
  - *确认事项*：首批仅聚焦于**基础未加密 Telnet** 与**基础串口通信**；对于 TLS Telnet (TelnetS)、Local Shell、自动化登录脚本以及带内 Modem（ZModem/YModem）传输，确认作为后续扩展阶段，不纳入首批交付范围。
- [ ] **决策项 6：`GLOSSARY.md` 术语更新时机**
  - *确认事项*：确认何时将“连接”在规范文档中的狭义定义（“一次已登录的 SSH 会话”）正式扩充为支持非 SSH 会话的通用用语。

---

## 七、核对路径与检查确认

### 调研涉及的仓库文件核对清单
- `AGENTS.md`
- `GLOSSARY.md` (第 7-9 行)
- `docs/adr/0001-connection-owns-views.md`
- `docs/adr/0002-transfer-channel-and-queue.md`
- `docs/adr/0003-connection-metadata-and-remote-toolset.md`
- `docs/adr/0004-e2ee-sync-single-age-snapshot.md`
- `docs/adr/0005-go-transport-sidecar-engine.md` (第 1-47 行)
- `docs/design/go-transport-sidecar.md` (第 1-116 行)
- `src/Kei.Term.Core/Abstractions/ITerminalSession.cs` (第 5-29 行)
- `src/Kei.Term.Core/Abstractions/IRemoteFileSystem.cs` (第 6-76 行)
- `src/Kei.Term.Core/Abstractions/ITerminalFileUploader.cs` (第 5-31 行)
- `src/Kei.Term.Core/Models/TreeNodes.cs` (第 98-171 行)
- `src/Kei.Term.Core/Models/SessionOverrides.cs` (第 1-40 行)
- `src/Kei.Term.Core/Services/SessionConfigBuilder.cs` (第 11-70 行)
- `src/Kei.Term.Core/Services/SecureCrtImporter.cs` (第 25-30, 125-169 行)
- `src/Kei.Term.Ssh/Abstractions/ISshSession.cs` (第 62-89 行)
- `src/Kei.Term.Ssh/Services/TerminalFileUploader.cs` (第 44-77 行)
- `src/Kei.Term.App/Services/Connection/ConnectionOrchestrator.cs` (第 23-460 行)
- `src/Kei.Term.App/Services/Connection/ConnectionOrchestrator.Reconnect.cs` (第 8-85 行)
- `src/Kei.Term.App/Terminals/TerminalSessionEndpoint.cs` (第 8-48 行)
- `src/Kei.Term.App/ViewModels/TerminalTabViewModel.cs` (第 197-337 行)
- `src/Kei.Term.App/ViewModels/TerminalTabViewModel.FileManager.cs` (第 42-80 行)
- `src/Kei.Term.App/ViewModels/TerminalTabViewModel.FileDrop.cs` (第 50-115 行)
- `src/Kei.Term.Transport.Go/CompositeSshSessionFactory.cs` (第 8-30 行)
- `src/Kei.Term.Transport.Go/engine/pkg/ipc/rpc.go` (第 52-61, 77-81 行)
- `src/Kei.Term.Infrastructure/Storage/Schema/SchemaMigrations.cs` (第 46-58, 65-77, 133-135 行)
- `src/Kei.Term.Infrastructure/Storage/SqliteTreeRepository.cs` (第 26-31, 190-218, 343-356 行)
