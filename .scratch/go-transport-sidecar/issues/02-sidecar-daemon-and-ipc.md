# Issue 02: 桌面 Sidecar Daemon 与 IPC 协议实现

Status: resolved
Role: ready-for-agent
Type: task

## 目标
实现可独立运行的桌面守护进程 `kei-trans-daemon`，支持 IPC 多路复用。

## 交付内容
1. 基于 Unix Domain Socket（Linux/macOS）与 Named Pipe（Windows）的监听器与双向通信通道。
2. 控制信令解析（JSON-RPC 2.0）：
   - `Connect`, `Disconnect`, `ListFiles`, `Stat`, `Mkdir`, `Remove`, `Rename` 等。
3. 数据流复用（Multiplexing）：
   - 自定义轻量 Frame 协议（Stream ID + Length + Payload），无缝承载 PTY 流与 SFTP 数据流。
4. 优雅关闭、孤儿进程超时自我销毁机制。

## Answer
已在 `src/Kei.Term.Transport.Go/engine/` 完成交付：
- `pkg/ipc/protocol.go`：实现 9 字节紧凑帧头（4B StreamID + 1B Type + 4B Length），提供线程安全读写与 Stream 虚拟通道流桥接。
- `pkg/ipc/rpc.go`：实现 JSON-RPC 2.0 路由器，支持 `session.connect`（强制每跳 hostKeySha256 校验）、`session.resize`、`session.disconnect`、`sftp.list/stat/mkdir/remove/rename`、`sftp.upload_stream`、`sftp.download_stream`、`daemon.ping` 与 `daemon.shutdown`。
- `cmd/daemon/main.go`：支持 `-socket`（UDS 或 Windows Named Pipe），支持 `-parent-pid` 孤儿自动退出与 `-idle-timeout` 空闲安全退出，优雅捕获系统退出信号。
- 单测 `pkg/ipc/ipc_test.go` 与 `go build ./cmd/daemon` 均已全部验证通过。

