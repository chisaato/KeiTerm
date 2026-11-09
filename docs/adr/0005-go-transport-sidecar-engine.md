# ADR-0005: 引入 Go 可插拔传输引擎 (Sidecar / In-Process)

- **状态**: Accepted
- **日期**: 2026-11-06
- **参与者**: KeiTerm Core Team
- **关联规范**: `.scratch/go-transport-sidecar/spec.md`
- **实现快照与限制**: [Go 传输引擎实现与排障](../design/go-transport-sidecar.md)（2026-11-09；以下动机与收益不代表全部能力已实现）

---

## 1. 上下文与问题陈述

KeiTerm 现阶段全量基于 C# 与 SSH.NET。随着多平台支持与高级终端用户需求的发展，SSH.NET 面临几大痛点：
1. **硬件密钥与证书演进滞后**：缺少硬件 FIDO2/U2F（`ed25519-sk`）直连，OpenSSH 证书鉴权等现代安全特性；
2. **SFTP 吞吐瓶颈**：高延迟与大文件传输场景下缺乏成熟高效的 pipelining 调度；
3. **移动端与复杂代理场景稳定性**：需要跨桌面与移动端的稳健 SSH 核心。

## 2. 决策考量

- **无侵入集成**：不能影响 KeiTerm 现有的 Avalonia UI、ViewModel 层以及已有的 `TransferQueue`。
- **通信效率与开销**：既要满足 PTY 毫秒级低延迟响应，又要满足 SFTP 大文件几百 MB/s 的传输吞吐。
- **跨平台与移动端复用**：方案不仅要能在 Linux、macOS、Windows 上以 Sidecar 运行，还要能为将来的 Android / iOS 移动端形态提供代码资产复用。

## 3. 架构决策

1. **抽象解耦**：
   继续遵守 `Kei.Term.Core` 的接口约束，保持 `ISshSessionFactory`、`ISshSession`、`IRemoteFileSystem` 与 `IRemoteTransferChannel` 为一等公民。新增 `Kei.Term.Transport.Go` 项目实现上述接口。
2. **双形态运行模型**：
   - **桌面端（Linux / macOS / Windows）**：采用**单守护进程模式（Daemon Mode）**，通过本地 IPC（Unix Domain Socket / Windows Named Pipe）连接独立运行的 Go 编译产物 `kei-trans-daemon`。
   - **移动端（Android / iOS）**：核心 Go 逻辑设计为纯库形态，预留通过 C-Shared / Gomobile 打包为 in-process so/dylib，走本地内存桥接或进程内管道，避免系统限制子进程。
3. **IPC 协议规范**：
   - 控制信令：JSON-RPC 2.0，负责建连、认证、状态查询、文件管理元数据操作。
   - 数据流：紧凑二进制帧分路复用（Multiplexing），每个连接分配 StreamID，分别承载 PTY、SFTP 与端口转发数据。
4. **后备、共存与开关机制**：
   - 保持内置 SSH.NET 作为系统基础保底实现（无需额外外部依赖）。
   - **默认引擎配置为 .NET 原生内置引擎（`BuiltIn`）**，绝不破坏现有生产稳定性。
   - 提供全局设置与会话级属性的 `SshTransportEngine` 显式开关，用户可按需切到 Go 核心。

## 4. 后果与影响

- **积极影响**：
  - 轻松获得 Go 生态强大的 SSH/SFTP 性能、硬件密钥与现代密码学支持。
  - 核心网络层与 UI 隔离，即便网络引擎内部出现严重异常也不会直接导致 Avalonia UI 进程段错误崩溃。
  - 同一套 Go 核心可在未来移动端项目中复用。
- **妥协与风险**：
  - 增加了发布包构建复杂度（桌面各操作系统需要打包对应架构的 Go 二进制文件）。
  - 需要自行管理 Sidecar 的进程生命周期（监控、保活、正常退出、死锁检测）。
