# Issue 02: 桌面 Sidecar Daemon 与 IPC 协议实现

Status: ready-for-agent
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
