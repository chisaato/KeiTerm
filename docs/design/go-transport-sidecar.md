# Go 传输引擎：实现快照与排障

核对日期：2026-11-09；代码基线：`cea95b3`。本文记录已实现行为，不把 ADR 的目标当作已交付能力。
设计依据：[ADR-0005](../adr/0005-go-transport-sidecar-engine.md)；原始规格：`.scratch/go-transport-sidecar/spec.md`。

> 本文保留 `cea95b3` 时点的历史边界。后续多协议接入与 Sidecar 诊断变更见[多协议验收记录](multi-protocol-validation.md)，不能继续把下述日志缺口等基线描述当作当前实现状态。

## 架构与边界

- `engine/pkg/transport`：Go SSH、PTY、多跳拨号、SFTP 核心。
- `engine/pkg/ipc`：二进制多路复用及 JSON-RPC 路由。
- `engine/cmd/daemon`：桌面守护进程入口，UDS / Windows Named Pipe。
- `Kei.Term.Transport.Go`：C# 进程监管、IPC、SSH 会话与文件接口适配。
- `CompositeSshSessionFactory`：按会话覆盖值，否则全局 `TransportEngine` 选择实现；默认 `BuiltIn`，Go 为显式选择的 `GoCore`。**不存在失败后自动回退 .NET 的逻辑。**

移动端 In-Process 是设计预留，尚无已验证的 Gomobile / C-Shared 桥接。现有工厂仍是 `ISshSessionFactory`，不能因枚举已通用化就宣称支持 Telnet 或 Serial。

## 实际能力

- SSH：密码、普通私钥（含口令）、直连、多跳、PTY、Resize、KeepAlive、环境变量、启动脚本。
- SFTP：列表、stat、创建目录、单项删除、改名、二进制流上传下载。Go 核心使用 `pkg/sftp` 并发能力，但尚无跨引擎吞吐基准证明性能收益。
- C# 适配当前拒绝 Agent、硬件 SK、同类多份认证材料、SOCKS5、跳板代理及端口转发；未提供交互认证与独立 Exec RPC。
- 文件适配当前不支持 SCP、非零偏移续传、lstat、安全递归删除、chmod；`SupportsResume=false`。文件系统连接仍借助 SSH Shell 会话，不是独立的通用文件协议后端。

硬件 SK 必须遵守项目约束，经 `SshNet.Agent` 桥接；不能把“引入 Go”解读为已经实现或已经批准硬件直连。

## 主机信任与安全

`GoSshSessionFactory` 优先使用 `options.HostKeyVerifier`，回退工厂注入的 verifier；App 注入共享 `HostKeyTrustService`。

1. 按跳板顺序探测公钥，目标探测阶段不发送目标认证凭据；到达目标所需的已批准跳板仍需认证。
2. C# 组装 `PresentedHostKey` 并调用共享 verifier，复用持久化信任及进程内“仅本次接受”。
3. 不接受时原样抛 `HostKeyRejectedException`，由 `ConnectionOrchestrator` 在握手外确认、重试；工厂不自行弹窗或改写 Decision。
4. 正式建连将已批准的 SHA256 指纹发往 Go，每跳比对。探测和正式连接之间密钥变化会被拒绝。

目前 Go 未接入内置引擎的“已知密钥类型优先协商”策略；服务器提供多种密钥时，可能协商到信任库尚未保存的另一种类型。共享信任库不等于协商行为完全相同。密码学算法使用当前 `golang.org/x/crypto/ssh` 依赖的默认配置，不能假定与 SSH.NET 完全一致。

## IPC 契约

每帧为 9 字节大端帧头加载荷：`StreamID:uint32`、`Type:byte`、`Length:uint32`。`StreamID=0` 用于 JSON-RPC 2.0，其余为数据流。

- Type：`1 Data`、`2 WindowResize`、`3 Close`、`4 Error`、`5 JSONRPC`。
- 单帧最大 `4 << 20` 字节；数据写入按 32 KiB 分片。
- Go Stream 接收缓冲上限 16 MiB；这不是窗口协商式流控。超限错误可经 Router 返回，导致整个 IPC 连接结束，不能保证只影响单流。
- `Codec.WriteFrame` 对完整帧加锁，`Stream.Write` 另有写锁；stdout/stderr 并发复制不能据此推断为帧头损坏。
- 当前未有独立的协议版本/能力协商；应用与 daemon 应配套发布。

控制方法（实现以 `engine/pkg/ipc/rpc.go` 为准）：

```text
daemon.ping / daemon.shutdown
session.probeHostKey / session.connect / session.resize / session.disconnect
sftp.open / sftp.list / sftp.stat / sftp.mkdir / sftp.remove / sftp.rename
sftp.upload_stream / sftp.download_stream
```

建连返回 `sessionId, streamId`，文件流返回 `streamId`。C# 在唤醒 RPC 调用方前注册流，以接收紧随响应而来的数据。

## 构建与生命周期

`Kei.Term.Transport.Go.csproj` 在 MSBuild 中检测 Go、增量构建 daemon 并复制至应用输出。缺少工具链时警告并跳过，.NET 构建成功不代表输出中一定存在 daemon。跨平台发布产物仍需逐平台验证。

二进制查找顺序：构造参数 → `KEITERM_GO_SIDECAR_PATH` → `AppContext.BaseDirectory` → 从基目录向上定位源码下 daemon 目录 → `PATH`。显式配置了不存在的路径会报错，不静默跳过。

管理器按需启动并复用一个进程/IPC 连接；Unix 使用临时目录中的 socket（目录权限 0700），Windows 使用命名管道。启动预算 10 秒；Go 默认首次完整 IPC 帧期限 15 秒，读完后清除；无连接空闲 60 秒或父进程退出时关闭。释放时先尝试 `daemon.shutdown`，随后必要时终止进程并清理端点。

进程监管可尝试重启 daemon，但不恢复既有 SSH 会话。`GoSshSession` 断开后需要创建新对象。App 可按自动重连设置及认证条件另行重建连接，是否实际重连须看相邻日志，不能由一条异常推断。

## 日志与 EndOfStream 排障

当前 `GoSidecarProcessManager` **没有重定向 stdout/stderr，也没有 ILogger 接线**。Go 只有少量 stderr 输出，且 daemon 丢弃了 `Router.Serve` 返回错误；不存在 `-log-file` 参数或完整连接日志。GUI 启动时不能保证有可见控制台。

Linux 下从终端启动应用，捕获应用和子进程可继承的标准输出/错误：

```bash
mkdir -p /tmp/opencode/keiterm-go
dotnet run --project src/Kei.Term.App/Kei.Term.App.csproj \
  > /tmp/opencode/keiterm-go/console.log 2>&1
```

此方式不能补回未记录的 Router 错误。不要记录认证材料、RPC 全量参数或终端内容。

单独观察 daemon 的启动/退出输出（前台运行，Ctrl+C 结束）：

```bash
mkdir -p /tmp/opencode/keiterm-go
(cd src/Kei.Term.Transport.Go/engine && \
  go build -o /tmp/opencode/keiterm-go/kei-trans-daemon ./cmd/daemon)
/tmp/opencode/keiterm-go/kei-trans-daemon \
  -socket /tmp/opencode/keiterm-go/debug.sock -parent-pid 0 -idle-timeout 10m \
  2> /tmp/opencode/keiterm-go/daemon.log
```

**应用不会连接此手动启动的 socket。** `KEITERM_GO_SIDECAR_PATH` 仅选择可执行文件；管理器仍会自行启动进程并创建随机端点，没有外部 socket 接入开关。

`ReadExactlyAsync` 抛 `EndOfStreamException` 说明 IPC 在完整帧头/载荷读到前结束，不能单凭堆栈确定进程崩溃、Router 协议错误或其他关闭原因。它也不证明自动回退或自动重连。

对上一轮排障结论的更正：

- 首帧 deadline 已有完整帧后清除逻辑，未复现“15 秒后必断”。
- 帧写入有互斥保护，未证明 stdout/stderr 导致协议损坏；SSH 的扩展数据读取本身不应被当作错误。
- 所报真实连接的 EOF 根因仍未确认；上一轮承诺的日志接线和稳定性修复尚未实施。

优先补足可观测性：记录进程 PID/退出码、IPC 结束原因及上下文、Router 返回错误，再构造重现；不要先删 stderr 或改 deadline 碰运气。

## 验证与后续边界

```bash
dotnet build Kei.Term.slnx
dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter 'FullyQualifiedName~GoTransport'
(cd src/Kei.Term.Transport.Go/engine && go test ./...)
```

此前记录：GoTransport 17 项通过，构建 0 警告/错误；这是历史证据，本次仅文档收口，未重新运行。已有编解码、并发写、信任、路由等自动化测试不等于用户真实服务器端到端验收。

仍需验证：真实 sshd 的 Go 终端/SFTP 端到端、大文件边界、daemon 被杀后的多标签影响、App 重连链、Windows/macOS 原生运行及发布、跨引擎性能比较。

下一阶段才研究：协议种类与实现引擎的独立建模、终端能力与 SSH 特有能力拆分、Telnet/Serial 生命周期及设备语义、Go/.NET 先后顺序。本文不预先决定新抽象或扩展已交付范围。
