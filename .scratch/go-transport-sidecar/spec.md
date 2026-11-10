# KeiTerm Go 传输引擎 Sidecar (Pluggable Transport Engine) 规格说明书

**Status:** approved  
**Owner:** Orchestrator / KeiTerm Core Team  
**Date:** 2026-11-06  

---

## 1. 目标与背景

KeiTerm 现阶段使用纯 C#（基于 SSH.NET）作为默认 SSH、SFTP 与端口转发实现。为了在不推翻现有 UI（Avalonia + Ghostty/Xterm 渲染）和上层 ViewModel 架构的前提下，解决高吞吐并发 SFTP 性能、硬件 SK 密钥（FIDO2 / U2F）直连、高可靠动态代理（SOCKS5）与 OpenSSH 证书等高级能力，决定引入**独立可插拔传输引擎**（Pluggable Transport Engine）。

同时，**必须严格兼顾移动端 App（Android / iOS）的架构复用能力**：
- 桌面端（Linux / macOS / Windows）：采用**独立 Sidecar 进程**（守护进程模式），通过 IPC 通信。
- 移动端（Android / iOS）：同一套 Go 核心逻辑通过 `gomobile bind` 或 C-Shared 动态库导出为 In-Process 绑定，暴露同构接口，避免进程拉起受限。

---

## 2. 核心架构与设计原则

```
+------------------------------------------------------------------------+
|                      KeiTerm UI & ViewModels                           |
|       (Avalonia 12.x / TerminalViewModel / RemoteFileManager)          |
+------------------------------------------------------------------------+
                                  |
                                  v
+------------------------------------------------------------------------+
|                       KeiTerm.Core 契约抽象层                          |
|  ISshSessionFactory / ISshSession / IRemoteFileSystem / IRemoteChannel  |
+------------------------------------------------------------------------+
           |                                              |
           v                                              v
+-----------------------+                    +---------------------------+
|    Kei.Term.Ssh       |                    |  Kei.Term.Transport.Go    |
| (SSH.NET 内置纯托管)  |                    | (Go 驱动传输适配器实现)   |
+-----------------------+                    +---------------------------+
                                                          |
                                      +-------------------+-------------------+
                                      | (桌面平台: IPC)                       | (移动平台: In-Process)
                                      v                                       v
                          +------------------------+             +------------------------+
                          |   kei-trans-daemon     |             |  libkeiterm-core.so    |
                          |  (Go Sidecar 独立进程) |             |  (Go C-Shared/Gomobile)|
                          +------------------------+             +------------------------+
```

### 2.1 契约无侵入原则与引擎开关
- 完全实现现有 `ISshSessionFactory`、`ISshSession`（`ITerminalSession`）、`IRemoteFileSystem` 与 `IRemoteTransferChannel`。
- **双层开关机制**：
  1. **全局设置**：在 `AppSettings` 中提供 `SshTransportEngine` 开关（枚举：`BuiltIn` = 纯 C# SSH.NET 原生，`GoSidecar` = Go 核心引擎），**默认值严格保持为 `BuiltIn`**。
  2. **会话级覆盖**：在会话属性中支持继承全局或显式覆盖，支持针对特定目标主机试验或锁定引擎。
- 上层 View / ViewModel / `TransferQueue` 无需改动核心逻辑，仅通过工厂路由切换传输提供者。

### 2.2 进程与通信模型
- **桌面端**：**单守护进程模式**（Daemon Mode）。
  - KeiTerm 启动或首次需要 Go 引擎时按需拉起 `kei-trans-daemon`。
  - 通过本地 IPC 管道（Linux/macOS 为 Unix Domain Socket，Windows 为 Named Pipe）通信。
  - 控制面：基于 JSON-RPC 的结构化控制信令（认证、文件列表、重命名、权限、状态上报）。
  - 数据面：紧凑二进制分帧流（带 StreamID 的多路复用 Multiplexing），保证 PTY 击键、回显与 SFTP 大块数据的高吞吐低延迟。
  - 进程生命周期：父进程保活监控，异常退出重连，主程序退出时优雅终止。
- **移动端架构解耦**：
  - Go 核心设计为纯模块化 Package（`pkg/transport`），不直接绑定 CLI/Daemon 外部入口。
  - 导出层支持抽象 Stream（`io.Reader` / `io.Writer`），桌面端桥接 IPC，移动端可直接桥接 C-ABI / JNI 流。

---

## 3. 功能特性规划

1. **终端交互 (PTY)**：
   - 交互式 Shell 分配（支持 `pty-req`、window-change 事件、环境变量透传）。
2. **高速 SFTP 传输**：
   - 优化包流水线并发（Pipelining & Sliding Window），多块并行上传/下载，充分利用万兆与高延迟链路。
3. **高级认证**：
   - 密码、公私钥、Keyboard-Interactive；
   - 硬件安全密钥（FIDO2 / U2F `ed25519-sk`）直连支持；
   - OpenSSH CA 证书鉴权支持。
4. **端口转发 & 代理**：
   - 本地端口转发（-L）、远端端口转发（-R）、动态 SOCKS5 代理（-D）；
   - JumpHost 多跳跳板链。

---

## 4. 交付清单与排期

- [x] **01-spec-and-adr**: 落地 Spec 与 ADR-0005 架构决策。
- [x] **02-go-core-package**: 构建 Go 传输引擎核心库（`src/Kei.Term.Transport.Go/engine`），实现基础 SSH Client、PTY 与 SFTP 封装。（2026-11-09 完成）
- [x] **03-ipc-protocol-and-daemon**: 实现桌面 IPC 协议（UDS/NamedPipe + 多路复用帧）及 Go Sidecar Daemon。（2026-11-09 完成）
- [x] **04-csharp-transport-adapter**: 在 C# 端实现 `Kei.Term.Transport.Go` 项目，实现 `ISshSessionFactory` 与进程监管。（2026-11-09 完成）
- [x] **05-integration-and-tests**: 集成测试与故障传播测试完成（真实 sshd 端到端冒烟通过）；性能基准比对未做，已转留存工单 `issues/09-cross-engine-benchmark.md`。
