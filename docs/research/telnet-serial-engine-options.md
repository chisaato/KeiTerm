# KeiTerm Telnet 与 Serial 传输引擎技术调研：首批实现选型评估与双引擎演进分析

- **调研日期**：2026-11-09
- **代码基线**：`cea95b3`（本地实测工具链：.NET SDK 10.0.401 / .NET Runtime 10.0.12，Go 1.26.10 linux/amd64）
- **依据与约束文档**：
  - [ADR-0005: 引入 Go 可插拔传输引擎 (Sidecar / In-Process)](../adr/0005-go-transport-sidecar-engine.md)
  - [Go 传输引擎实现与排障](../design/go-transport-sidecar.md)
  - [AGENTS.md](../../AGENTS.md)
- **定位**：第一方来源技术调研与决策参考。评估首批（Phase 1）实现选 Go 还是 .NET，为未来双引擎同时支持设计统一复用层与平滑演进路径；**不替用户作最终决策**。

---

## 1. 调研执行摘要（Executive Summary）

KeiTerm 的终端交互核心面向 `ITerminalSession`（`Kei.Term.Core`）解耦，上层终端标签与渲染组件消费字节流与大小调整事件。目前底层传输层已建立双引擎架构（默认内置 .NET，可选外部 Go 守护进程 Sidecar）。针对后续引入的 Telnet 与 Serial（串口）协议，并在未来实现双引擎并存支持，本调研核查了底层系统 API、官方运行时源码与 issue 跟踪记录、IETF RFC 规范、开源候选维护库及测试模拟设施。

核心结论摘要如下：
1. **Telnet 协议边界**：
   - Telnet 不能视为裸 TCP 套接字直通。RFC 854/855 明确规定了 `0xFF` (IAC) 转义与反转义语义，且 TCP 流式交付意味着 IAC 转义帧及子协商块可能跨越网络分包截断到达，必须依赖流式状态机（Streaming State Machine）渐进解析。
    - 协商防死循环机制：RFC 1143（Q-Method）提供一种避免选项协商循环的状态机实现方法，**并非** Telnet 互操作的强制唯一标准；其正文还明确说明，这不是 IETF Telnet 工作组的推荐。
   - 常用选项策略（BINARY 8位、ECHO 远端回显控制、SGA 抑制 Go-Ahead、NAWS 窗口动态通知、TTYPE 终端类型声明）及 NVT 换行（CRLF / CR-NUL）规范化处理，取决于对端服务端类型（如网络交换机、UNIX 登录守护进程或 MUD 服务器），属于常见终端客户端策略而非所有场景下无条件的强制要求。
2. **Serial（串口）通信实现与跨平台差异**：
   - 官方 `.NET System.IO.Ports` 在 Unix（Linux/macOS）与 Windows 平台的实现机制不同。依据 `dotnet/runtime` 历史 Issue 审计，其在处理取消未决读取、设备物理拔除及非标准波特率时曾报告多项平台特有缺陷（如 issue #135369 报告的拔插后自旋挂起、issue #30850 报告的 Windows 异步读取取消困难）。这些属于特定环境与版本下的 issue 报告，不能外推为所有 Linux/macOS 环境必然挂死；同样，采用后台工作线程包装亦无法证明能够无条件规避底层原生驱动未决 I/O 带来的所有阻塞风险。
   - Go 社区主流候选维护库（以 `go.bug.st/serial` 为代表）在 Unix 平台上采用独立的管道（Signaling Pipe）与 `select` 监听来辅助打断阻塞读取，但该机制仅为其库内特定设计，并不等同于 Windows 平台已调用 `CancelIoEx`，也不能推断为物理拔插在所有操作系统上均能永远优雅退出。此外，该库的 `Mode` 结构体主要配置波特率、数据位、校验位与停止位，未原生提供内置的硬件 RTS/CTS 或软件 XON/XOFF 自动流控状态机（目前需外部手动控制引脚或依赖尚未合并的社区补丁），手动设置 RTS 引脚电平不等于自动硬件握手。
   - `go.bug.st/serial/enumerator` 在 macOS 下获取详细 USB 元数据时依赖 cgo（调用 IOKit 框架），这会给纯交叉编译构建带来外部工具链依赖。
3. **硬件标识跨机同步风险**：
   - 物理串口路径（如 Linux `/dev/ttyUSB0`、macOS `/dev/cu.*`、Windows `COM3`）具有物理设备与主机拓扑特异性。若无差别随会话树（SyncSnapshot）跨机漫游，可能造成目标机器设备缺失或误连无关设备。将物理设备路径定义为本地绑定属性（Local Device Binding）属于应对此类风险的**候选策略**，具体数据结构变更需作为方案评估而非既成事实。
4. **推荐建议（候选顺序）**：
   - 提出三组具有明确触发条件的候选序列（如：混合分工、全量 .NET 先行、全量 Go 先行），说明各候选方案支配推荐的前提条件，并明确指出缺乏真实物理串口硬件验证时的确定性缺口。

---

## 2. 事实、Issue 观察、推断与待验证说明

本报告严格区分信息属性，确保技术追溯的可证伪性：
- **[事实 (Fact)]**：直接来源于 IETF 规范正文、.NET 官方源代码仓、Go 库代码实现、本机工具链实测输出及本仓库现有代码。
- **[Issue 观察 (Issue Observation)]**：来自官方 issue 跟踪系统或上游仓库的特定问题报告，记录特定运行时版本与测试配置下的现象，不代表普遍必然行为。
- **[推断 (Deduction)]**：基于当前架构边界推导的设计考量与权衡分析。
- **[待验证 (Unverified)]**：因缺乏特定硬件（如真实物理串口芯片、macOS 多版本真机）当前无法在本地直接确认的事项。

---

## 3. Telnet 协议规范与实现考量

### 3.1 Telnet 规范核心事实（RFC 体系）

- **RFC 854 (STD 8) & RFC 855** [事实 (Fact)]：
  - **IAC 字节转义**：字节 `0xFF`（255）定义为 `IAC` (Interpret As Command)。若普通数据流中传输字节值 255，发送方必须将其转义为双字节 `0xFF 0xFF`（`IAC IAC`）；接收方收到 `IAC IAC` 时必须解码还原为单字节 `0xFF`。
  - **分包解析（Packet Fragmentation）**：TCP 是字节流传输协议，`IAC` 指令、协商动词（`WILL`/`WONT`/`DO`/`DONT`）、选项码以及子协商块（`SB ... IAC SE`）在网络层可能被拆分到不同的 TCP 数据包中到达。解码器必须采用流式状态机，能够保留跨数据块的未决解析状态。
  - **协商动词**：
    - `WILL` (251) / `WONT` (252)：发送方提议开启 / 拒绝（或终止）开启自身侧的某选项。
    - `DO` (253) / `DONT` (254)：发送方要求接收方开启 / 要求接收方禁用（或停止）其自身侧的某选项。
- **RFC 1143 (The Q Method)** [事实 (Fact)]：
   - 标题为 *The Q Method of Implementing TELNET Option Negotiation*；正文将其描述为一种实验性实现方法，而非协议规范，并明确声明不是 IETF Telnet 工作组的推荐。
  - 该文档分析了双方循环发送确认导致的协商循环（Negotiation Loops）问题，并提出了一套使用 `NO`、`WANTNO`、`WANTYES`、`YES` 及队列位记录未决请求的状态机实现方案。
  - **规范属性澄清**：RFC 1143 是一套被广泛采用的防死循环推荐实现算法，但**不是**强制的唯一实现。RFC 854 本身仅给出了基本应答规则（如拒绝未支持选项必须发送 WONT/DONT，收到对已关闭选项的 DONT/WONT 不得再发应答），简单的单向受限应答客户端只要严格遵守 RFC 854 规则亦能正常工作。
- **常见选项处理策略** [事实 (Fact) 与 推断 (Deduction)]：
  - **RFC 856 (STD 27) TRANSMIT-BINARY**：请求/确认进入 8 位二进制数据路径传输，避开部分网关对高位的篡改。
  - **RFC 857 (STD 28) ECHO**：远端回显控制。常用于由服务端负责击键回显的交互场景。
  - **RFC 858 (STD 29) SUPPRESS-GO-AHEAD (SGA)**：抑制传统半双工的 GA 信号，适应全双工交互。
  - **RFC 1073 NAWS (Negotiate About Window Size)**：窗口大小子协商。格式为：
    `IAC SB 31 (NAWS) <width-16bit-MSB> <width-16bit-LSB> <height-16bit-MSB> <height-16bit-LSB> IAC SE`。按 RFC 855 规范，子协商负载中的 255 字节亦需转义为 `IAC IAC`。
  - **RFC 1091 TTYPE (Terminal Type)**：终端类型探测协商。
  - *策略说明*：上述选项组合是现代交互式终端（如连接 Linux 终端服务器）的典型配置，但并非所有 Telnet 设备（如工业控制仪表的原始控制口、简易交换机控制台）都支持或需要全量开启。客户端实现应支持可配置的选项策略。
- **NVT 换行规范** [事实 (Fact)]：
  - RFC 854 第 11 页规定：在默认 NVT ASCII 模式下，换行合并动作由 `CR LF` 表示；单纯的回车动作由 `CR NUL` 表示，接收端在默认模式下收到 `CR NUL` 应当剥离 `NUL`。在开启 `TRANSMIT-BINARY` 后，两端常直接透传原始控制字符，具体行为受对端实现影响。

### 3.2 候选开源库评估与维护证据

针对 Telnet 客户端实现，对 .NET 与 Go 生态的相关候选库进行了检索核实：

| 语言 | 候选项目 | 许可证状态 | 维护状态与版本证据 | 框架与功能考量 |
| :--- | :--- | :--- | :--- | :--- |
| **.NET** | [HarryCordewener/TelnetNegotiationCore](https://github.com/HarryCordewener/TelnetNegotiationCore) | **许可证存疑/未知**（仓库根目录缺失标准 LICENSE 文件，inspect.software 与 GitHub API 标注为 Custom / Other License；部分源文件注释提及 Apache-2.0，部分第三方打包站点标注为 Apache-2.0，官方许可证法律属性未知） | 活跃开发。<br>标签版本包括 [v2.4.2](https://github.com/HarryCordewener/TelnetNegotiationCore/releases/tag/v2.4.2)（2026-03-16）、[v2.5.0](https://github.com/HarryCordewener/TelnetNegotiationCore/releases/tag/v2.5.0)（2026-05-07）；NuGet 页面发布有后续更新。 | `TelnetNegotiationCore.csproj` 中 `TargetFrameworks` 包含 `netstandard2.0;net8.0;net10.0`（通过 PR #37 与 PR #30 适配）。基于 `System.Threading.Channels` 与 `System.IO.Pipelines`，支持 NAWS、TTYPE、SGA 等插件。 |
| **Go** | [reiver/go-telnet](https://github.com/reiver/go-telnet) | MIT License | 提交活跃度较低，主要代码形成于早期，近年主要是社区零星维护与分支（如 globalcyberalliance/telnet-go）。 | 接口设计类似 `net/http`，偏向服务端构建。客户端细粒度子协商扩展支持相对简易。 |
| **Go** | [ziutek/telnet](https://github.com/ziutek/telnet) | BSD-3-Clause | 维护频率低，发布有 v0.1.0 稳定版本（Debian 维护打包至 2025/2026）。 | 面向自动化脚本与 Expect 场景，未原生实现完整的 NAWS 动态子协商。 |

[推断 (Deduction)]：
- 评估自研轻量解析器：Telnet 基础流式协议包括 IAC 状态转移、`CR NUL` 转换与 NAWS 帧编码。若自研轻量状态机，可精确贴合 `Kei.Term.Core` 的接口形态，避免引入第三方许可证存疑（如 `TelnetNegotiationCore` 许可证未标准化）的供应链风险。
- 若采用 Go 方案承载 Telnet，解码后的纯字符流必须经由 Sidecar IPC 重新封装为二进制帧分发至 C#，且窗口变化需由 C# 通过 IPC 发送 RPC 至 Go，再由 Go 发送 NAWS。两端在吞吐与延迟上的差异取决于 IPC 管道开销，**具体性能表现需依赖实际基准测试（Benchmark），不能主观断言何者必然具有绝对吞吐优势**。

---

## 4. Serial（串口）通信第一方调研与跨平台现实

### 4.1 官方 .NET `System.IO.Ports` 文档、源码与 Issue 审计

1. **底层调用架构** [事实 (Fact)]：
   - Windows 平台：`System.IO.Ports.SerialStream.Windows.cs` 使用 Win32 `CreateFile`（启用 `FILE_FLAG_OVERLAPPED`）、`SetCommState`、`SetCommTimeouts`，异步读取由内核 Overlapped 事件驱动。
   - Unix 平台（Linux/macOS）：依赖独立编译的本机支撑库 `libSystem.IO.Ports.Native.so` / `.dylib`（源码位于 `src/native/libs/System.IO.Ports.Native/pal_termios.c`）。`SerialStream.Unix.cs` 内部维护一个后台 `IOLoop` 任务，在任务中使用 `poll` 轮询设备文件描述符，并将 I/O 请求放入 `_readQueue` / `_writeQueue` 进行分发。
2. **异步取消与阻塞行为的 Issue 记录** [Issue 观察 (Issue Observation)]：
   - **Windows 异步读取取消**（[dotnet/runtime#30850](https://github.com/dotnet/runtime/issues/30850)，状态：Closed as not planned / Future）：
     - 现象：在 Windows 下，`SerialPort.BaseStream.ReadAsync(buffer, offset, count, cancellationToken)` 传递的 `CancellationToken` 无法直接取消底层未完成的驱动读取操作。若物理设备无数据输入，调用方 Task 会持续等待，`cts.Cancel()` 无法打断已发出的底层 I/O。社区开发者常通过在取消回调中调用 `DiscardInBuffer()` 强制使底层产生错误以中止等待。
   - **Linux USB 设备热拔插挂起**（[dotnet/runtime#135369](https://github.com/dotnet/runtime/issues/135369)，报告配置：Linux x64, .NET 9.0.17 / System.IO.Ports 10.0.0，状态：Open / Tracked）：
     - 现象：当 USB 串口设备在数据未决状态下被拔出时，底层的 `poll` 返回错误，`IOLoop` 循环退出，但在该 issue 报告的分支中未清空 `_ioLoop` 引用。后续写操作若未设超时会阻塞，而在调用 `Dispose()` 时，其内部调用的 `SerialStream.Flush()` 会自旋等待写队列清空，由于循环已死导致 `Dispose()` 产生自旋等待。
   - **边界说明**：上述 Issue 为特定平台与环境下的问题报告。在具备明确超时控制且正常关闭端口的场景下，`System.IO.Ports` 仍被大量工业控制应用使用；不能由 Issue 报告外推为“所有 Linux/macOS 环境必然发生挂死”。
3. **后台阻塞线程包装的局限性** [推断 (Deduction)]：
   - 针对异步缺陷，常见的工程应对策略是将串口读取放在独立的长生命周期工作线程（Dedicated Thread）中执行同步 `Read(buffer, 0, count)` 并设置有限超时（`ReadTimeout`）。
   - 该方案能够避免依赖不可靠的托管异步取消状态机，但**并不能保证彻底杜绝**所有阻塞问题：若底层内核串口驱动在硬件拔出时进入未响应的 D 状态（Uninterruptible Sleep），即使同步阻塞系统调用也可能无法返回，且无法安全强制终止线程。
4. **波特率与 Termios 映射** [事实 (Fact)]：
   - 在 `pal_termios.c` 中，标准波特率通过 `TermiosSpeed2Rate` 转换为系统常量（如 `B9600`, `B115200`）。
   - 针对非标准波特率，代码中存在 `#if HAVE_TERMIOS2`（调用 `TCSETS2` / `BOTHER`）和 `#if HAVE_IOSS_H`（macOS 的 `IOSSIOSPEED`）适配路径（如 issue #80534, #64507）。但非标准波特率的具体可用性直接受底层驱动和内核版本支持限制。

### 4.2 Go 候选串口库（`go.bug.st/serial`）实现核查

经核查 `go.bug.st/serial`（v1.8.0，BSD-3-Clause）：

1. **取消与非阻塞实现机制** [事实 (Fact)]：
   - 在 Unix（`serial_unix.go`）实现中，`unixPort` 结构体包含一个私有管道：`closeSignal *unixutils.Pipe`。
   - `Read()` 方法通过 `select(2)` 同时监听串口自身的描述符与 `closeSignal.ReadFD()`。
   - 当调用 `port.Close()` 时，向 `closeSignal` 写入字节，促使处于 `select` 状态的阻塞读取退出并返回 `PortClosed` 错误。
   - *限制与澄清*：该设计是该库在 POSIX 用户态通过 `pipe + select` 构造的协同打断机制。对于 Windows 平台，其基于 Overlapped I/O 事件；这**不等于**在所有异常场景（如内核总线级驱动挂死、硬件突发断电）下都能“永远优雅退出”，亦不能脱离硬件环境宣称其“彻底消除”了死锁。
2. **流控与控制引脚现状** [事实 (Fact)]：
   - 查验 `serial.go` 中的 `Mode` 结构体：
     ```go
     type Mode struct {
         BaudRate          int
         DataBits          int
         Parity            Parity
         StopBits          StopBits
         InitialStatusBits *ModemOutputBits
     }
     ```
   - 库提供了 `SetRTS(bool)`、`SetDTR(bool)`、`GetModemStatusBits()`（读取 CTS/DSR/RI/DCD）以及 `Break(time.Duration)` 等底层引脚控制方法。
   - **流控澄清**：`Mode` 中**并未包含**开启操作系统级自动 RTS/CTS 硬件流控或 XON/XOFF 软件流控的配置字段。在 Unix 初始化中，代码显式调用了 `setTermSettingsCtsRts(false, settings)` 禁用了硬件流控标志。虽然社区有相关 PR（如 PR #201, #79）尝试引入流控，但当前主干版本并不支持自动硬件流控，手动设置 RTS/DTR 电平不能等同于自动 RTS/CTS 硬件握手。
3. **cgo 依赖与跨平台编译约束** [事实 (Fact)]：
   - 基础通信功能（Open, Read, Write, Close, SetMode）为纯 Go 与系统调用封装，不依赖 cgo。
   - 但其子包 `go.bug.st/serial/enumerator` 在 macOS 平台（`usb_darwin.go`）为了通过 `IOKit` 框架检索 USB 的 VID、PID 和序列号，**显式启用了 cgo**。
   - *编译影响*：若构建包含详细 USB 属性探测的 Go 守护进程，从 Linux 环境交叉编译 macOS 产物时需配置支持 Darwin 的 C 交叉编译器与 SDK，增加了 CI 流水线的维护复杂度。若仅调用基础端口列表（`serial.GetPortsList()`），则不依赖 cgo。

### 4.3 串口设备标识跨机同步风险与隔离策略

- **设备路径的平台与拓扑特异性** [事实 (Fact)]：
  - Windows：串口命名为 `COM1`、`COM3` 等；
  - Linux：通用串口常为 `/dev/ttyS*`，USB 串口常为 `/dev/ttyUSB*`、`/dev/ttyACM*`，或基于 udev 映射的 `/dev/serial/by-id/*`；
  - macOS：区分为呼入设备 `/dev/tty.*` 与呼出设备 `/dev/cu.*`。对于终端外联，通常倾向使用 `/dev/cu.*`（`/dev/tty.*` 设备若无 DCD 载波信号可能在 open 时产生阻塞），但两类节点在系统中并存。
- **跨机同步风险分析** [推断 (Deduction)]：
  - 在当前 E2EE 同步机制下，若将包含特定设备路径（如 `/dev/ttyUSB0`）的串口会话配置直接漫游至 Windows 或其他 Linux 机器，会导致目标环境因路径不存在而报错。更极端情况下，若另一主机恰好存在同名设备节点但连接了其他硬件，可能发生意外通信。
  - **候选应对策略（非既成需求）**：
    - 策略 A（本地属性隔离）：将 `PortName` 或硬件绑定路径定义为仅保存在本机的偏好属性（Local Device Binding），不进入 E2EE 同步快照；
    - 策略 B（元数据重定位）：同步时不记录绝对路径，而是记录 `USB VID:PID + SerialNumber`，目标主机在打开时根据本地枚举动态解析对应端口；若匹配失败则提示用户手动指定。

---

## 5. 跨平台相容性与工程约束对比

### 5.1 维度对比矩阵

| 评估维度 | .NET 10 BuiltIn 路径 | Go Sidecar 路径 | 事实与依据 |
| :--- | :--- | :--- | :--- |
| **本地工具链实测基线** | .NET SDK 10.0.401, Runtime 10.0.12 | Go 1.26.10 (linux/amd64) | [事实] 本机执行 `dotnet --info` 与 `go version` 实测验证。 |
| **组件分发与打包** | 纯托管单体进程输出，无额外独立二进制文件。 | 需分发独立可执行文件 `kei-trans-daemon`（各 OS/架构独立构建）。 | [事实] 当前 `Kei.Term.Transport.Go.csproj` 已定义 MSBuild 外部编译目标。 |
| **Telnet 实现路径** | 纯托管实现（自研状态机或依赖如 `TelnetNegotiationCore`），在应用进程内直接处理。 | Go 进程内实现状态机，通过 IPC（UDS / Named Pipe）转递解码后的终端流。 | [推断] 两者在吞吐与延迟上的差异受 IPC 管道与缓冲策略影响，待基准测试。 |
| **Serial 实现路径** | 依赖官方 `System.IO.Ports`（或自封装 P/Invoke）。 | 依赖 Go 串口库（如 `go.bug.st/serial`）并在 Sidecar 暴露控制 RPC。 | [事实] BCL 与 Go 库的调用层级差异。 |
| **macOS 构建约束** | 官方 NuGet 包包含对应 runtime dylib，无需开发者配置原生 SDK 交叉编译。 | 若使用 `enumerator` 获取 USB 详情，macOS 构建依赖 cgo；若仅基本枚举则无此约束。 | [事实] `go.bug.st/serial/enumerator/doc.go` 说明。 |
| **移动端支持现状** | `System.IO.Ports` 在移动端（Android/iOS/MacCatalyst）抛出 `PlatformNotSupportedException`。 | Go 核心理论上可通过 Gomobile 编译，但当前工程中尚未实测，子进程模式在移动端受限。 | [事实] 官方文档与 ADR-0005 明确记载。 |

---

## 6. Sidecar 故障面与架构开销评估

若选择将扩展协议纳入 Go Sidecar，需评估其在系统架构中的边界影响：

1. **IPC 契约扩展代价** [事实 (Fact)]：
   - 现有的 `engine/pkg/ipc/rpc.go` 围绕 SSH 会话与 SFTP 操作设计（方法包括 `session.connect`, `session.resize`, `sftp.*` 等）。
   - 接入 Telnet 需复用类似的数据流信令；接入 Serial 则需要定义端口扫描、打开、引脚控制及 Break 等专属信令。
2. **故障隔离与爆炸半径界定** [推断 (Deduction)]：
    - **用户态故障隔离**：Go 用户态未捕获 panic 可导致 `kei-trans-daemon` 退出，而逻辑死锁可能令它持续挂起，并不保证自动退出。独立进程可隔离直接的用户态崩溃，但宿主仍需有界等待、错误传播与恢复策略，才能避免业务操作一直等待。
   - **单进程多会话影响**：依据当前快照，所有会话共用单个守护进程实例。若守护进程异常退出，正在运行中的所有 SSH 会话与传输任务将一并断开。
   - **内核故障边界澄清**：对于底层硬件或内核驱动级故障（如内核死锁、系统调用陷入 D 状态），无论使用 Go 还是 .NET，进程均可能被阻塞在系统调用上，Sidecar 无法隔离内核级别的死锁。
3. **排障链路与日志** [事实 (Fact)]：
   - 如 `docs/design/go-transport-sidecar.md` 所述，当前 `GoSidecarProcessManager` 未重定向 stdout/stderr，未接驳 `ILogger`，子进程错误排查依赖外挂日志或终端启动捕获。

---

## 7. 测试设施与可验证性（PTY / 虚拟串口 vs 真实硬件）

在自动化测试与持续集成中，软件模拟与物理硬件之间存在明确差异：

1. **虚拟环境能力** [事实 (Fact)]：
   - Linux / macOS 可通过 `socat`（如 `socat -d -d pty,raw,echo=0 pty,raw,echo=0`）或 POSIX `openpty()` 建立配对的伪终端通道。
   - Windows 原生无等价的虚拟串口配对机制，通常需要安装第三方内核驱动（如 `com0com`），这在标准无特权 CI 环境中存在配置阻碍。
2. **硬件特性缺口** [事实 (Fact) 与 待验证 (Unverified)]：
   - **引脚电气特性**：虚拟 PTY 不具备物理 CTS/DSR/DTR/RTS 电气握手逻辑，对引脚状态查询通常返回固定值或产生错误。
   - **时钟与流控反压**：虚拟通道本质为内核内存缓冲区复制，无法精确复现特定物理波特率下的传输延迟、硬件 FIFO 溢出及字符丢失行为。
   - **物理断开**：软件关闭文件描述符不完全等同于 USB 控制器上报硬件拔出的物理中断事件。
   - *结论*：协议解析与状态机可通过单元测试覆盖；串口端到端传输可靠性必须依赖带有物理硬件的台架验证，不能仅凭虚拟测试判定无缺陷。

---

## 8. 双引擎演进与复用层设计提案（示意架构）

为了支撑未来双引擎并存演进，提出以下抽象分层方案作为**架构提案（Proposal）**，而非现有代码现状：

```text
+-------------------------------------------------------------+
|                     Kei.Term.App (UI)                       |
|   - TerminalTabViewModel (消费 ITerminalSession 字节流与事件) |
|   - 串口 / Telnet 参数设置页面                              |
+-------------------------------------------------------------+
                              |
+-------------------------------------------------------------+
|                     Kei.Term.Core (规划提案)                |
|   - 现有契约: ITerminalSession (SessionId, IsConnected,     |
|              OutputReceived, Disconnected, SendInputAsync,  |
|              ResizeTerminalAsync)                           |
|   - 规划配置模型: TelnetConnectionOptions,                   |
|                  SerialConnectionOptions                    |
|   - 规划服务契约: ITelnetSessionFactory,                    |
|                  ISerialSessionFactory                      |
+-------------------------------------------------------------+
            |                                     |
            v                                     v
+-----------------------+             +-----------------------+
|  Kei.Term.Transport.  |             |  Kei.Term.Transport.  |
|      BuiltIn (.NET)   |             |         Go            |
|   (托管协议与串口实现)  |             |  (IPC 桥接外部 Daemon)  |
+-----------------------+             +-----------------------+
```

*说明*：
- 现有 `ITerminalSession`（`src/Kei.Term.Core/Abstractions/ITerminalSession.cs`）仅定义了通用终端必须的收发、断开与尺寸调整接口，不包含协议特有字段。
- 各引擎的装配可根据实际依赖规模采用专属工厂，或者由统一的路由机制（类似于 `CompositeSshSessionFactory`）按配置调度，不必机械复制完全相同的复合工厂类。

---

## 9. 首批实现方案选型与候选顺序评估

### 9.1 候选顺序与支配条件

为不替用户作最终决策，提供以下三种候选实施顺序及各自的主导推荐条件：

#### 候选顺序 A（针对性分工 / 混合路径）：Telnet 选 .NET (BuiltIn)，Serial 选 Go (Sidecar)
- **方案特征**：
  - Telnet 采用 .NET 纯托管实现（直接集成到主程序，无需 IPC 封装）；
  - Serial 采用 Go 编写并由 Sidecar 提供服务（利用 Go 库的信号管道机制降低部分 Unix 阻塞风险）。
- **支配推荐的条件**：
  1. 团队在物理硬件台架验证中确认 .NET `System.IO.Ports` 在目标 Linux 发行版上存在不可接受的阻塞或拔插异常；
  2. 团队接受为 Serial 扩展 Go IPC 协议，并具备处理 macOS cgo 依赖或同意降级简化 USB 元数据枚举；
  3. Telnet 优先追求进程内直接调用的开发效率与调试便利性。

#### 候选顺序 B（全量先行 .NET）：Telnet 与 Serial 首批均在 .NET 内部实现
- **方案特征**：
  - 两个协议均由 .NET 原生/托管组件实现，发布时保持零额外 Go 编译分发依赖。
- **支配推荐的条件**：
  1. 发布流水线以简便和高移植性为第一优先级，避免跨平台 Go 二进制的构建与分发复杂性；
  2. 团队能够在 .NET 侧针对 Serial 实施防御性封装（如限定使用带超时的同步读取线程包装），且在实际目标设备上验证满足可用性要求；
  3. 优先验证 UI 与核心业务模型，双引擎的 Go 侧实现留待后续阶段迭代。

#### 候选顺序 C（全量先行 Go）：Telnet 与 Serial 首批均在 Go Sidecar 实现
- **方案特征**：
  - 充分利用已有的 Go Sidecar 进程，所有底层网络与硬件 I/O 统一下沉。
- **支配推荐的条件**：
  1. 架构目标明确推动将 KeiTerm 的网络与传输引擎全面独立于 UI 宿主进程；
  2. 团队已先行实施对 `GoSidecarProcessManager` 的日志流接线与健壮性改造，确保单会话或串口异常不至于导致守护进程崩溃而扩散影响其他会话。

---

## 10. 关键不确定性与风险备忘（Critical Uncertainties）

1. **真实串口硬件与驱动表现缺口** [待验证 (Unverified)]：
   - 虚拟 PTY 无法替代真实物理串口通信。针对 USB 转串口芯片（FTDI, CP210x, CH340 等）在拔插、特定波特率及长时间传输下的表现，必须在物理机上进行台架测试验证。
2. **第三方库许可证明确性**：
   - 若在 .NET 方案中考虑复用外部开源实现（如 `TelnetNegotiationCore`），鉴于其代码仓中正式 LICENSE 文件的缺失及平台元数据不一致，正式引入前需确认许可证法律风险，或评估自研轻量级状态机的可行性。
3. **macOS 跨平台交叉编译链路**：
   - 若 Go Sidecar 需包含详细 USB 属性探测，需在 CI 中验证 Linux 环境下构建 macOS cgo 产物的工具链支持度，或采用无 cgo 的替代枚举方案。

---

## 11. 来源索引与核验日志（Sources & Verification Dates）

| 来源分类 | 标的名称 / 编号 | 链接 / 引用路径 | 核验日期 |
| :--- | :--- | :--- | :--- |
| **IETF 规范** | RFC 854 | [RFC 854 - Telnet Protocol Specification](https://www.rfc-editor.org/info/rfc854/) | 2026-11-09 |
| | RFC 855 | [RFC 855 - Telnet Option Specifications](https://www.rfc-editor.org/info/rfc855/) | 2026-11-09 |
| | RFC 856 | [RFC 856 - Telnet Binary Transmission](https://www.rfc-editor.org/info/rfc856/) | 2026-11-09 |
| | RFC 857 / 858 | [RFC 857](https://www.rfc-editor.org/info/rfc857/), [RFC 858](https://www.rfc-editor.org/info/rfc858/) | 2026-11-09 |
| | RFC 1073 / 1091 | [RFC 1073](https://www.rfc-editor.org/info/rfc1073/), [RFC 1091](https://www.rfc-editor.org/info/rfc1091/) | 2026-11-09 |
| | RFC 1143 | [RFC 1143 - The Q Method](https://www.rfc-editor.org/info/rfc1143/) | 2026-11-09 |
| **.NET 源码与 Issue** | dotnet/runtime | [SerialStream.Unix.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Ports/src/System/IO/Ports/SerialStream.Unix.cs) | 2026-11-09 |
| | dotnet/runtime | [pal_termios.c](https://github.com/dotnet/runtime/blob/main/src/native/libs/System.IO.Ports.Native/pal_termios.c) | 2026-11-09 |
| | Issue #135369 | [Linux unplug hang / flush spinwait](https://github.com/dotnet/runtime/issues/135369) | 2026-11-09 |
| | Issue #30850 | [Windows cancellation support](https://github.com/dotnet/runtime/issues/30850) | 2026-11-09 |
| | Issue #64507 | [Baud rate 250k support](https://github.com/dotnet/runtime/issues/64507) | 2026-11-09 |
| **开源第三方库** | TelnetNegotiationCore | [HarryCordewener/TelnetNegotiationCore](https://github.com/HarryCordewener/TelnetNegotiationCore) | 2026-11-09 |
| | go.bug.st/serial | [bugst/go-serial](https://github.com/bugst/go-serial) | 2026-11-09 |
| **本地工具链与代码** | dotnet --info | .NET SDK 10.0.401, Runtime 10.0.12 (fedora 44, linux-x64) | 2026-11-09 |
| | go version | go version go1.26.10 linux/amd64 | 2026-11-09 |
| | 本地设计与代码 | `docs/design/go-transport-sidecar.md`, `src/Kei.Term.Core/Abstractions/ITerminalSession.cs` | 2026-11-09 |
