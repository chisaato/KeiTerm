# 06: Go Telnet 功能对齐

**What to build:** 用户显式选择 Go 引擎时，可使用与 .NET Telnet 相同的配置、交互、协商和工作区能力行为。

**Blocked by:** 03 — .NET Telnet 端到端连接；05 — Go Sidecar 可诊断性与故障传播。

**Status:** resolved

- [x] 扩展 IPC 与 Go 路由实现 Telnet，保持 SSH 兼容且不强制 Telnet 实现 OpenSFTP。
- [x] 复用已确定的配置与可观察行为契约，覆盖 IAC/分片/NVT、选项协商、回显和 NAWS。
- [x] 通过真实 daemon 与本地 TCP 对端完成 C# 到 Go 到终端流的集成验证，不只测试 RPC Mock。
- [x] 用户关闭、对端结束、IPC 中断、daemon 退出正确反映连接状态并清理资源。
- [x] 文件能力、引擎错误与安全提示与 .NET 行为一致，不静默回退。
- [x] 聚焦测试证明 SSH RPC 与既有流处理未回归；报告实测环境。

## 验收

Linux 真实 daemon/TCP 链路、协商、取消与共享 IPC 结束通过；SSH.NET 回归及真实 Go SSH 终端/SFTP 冒烟通过。Go race/vet 通过；其他系统仅有构建证据。精确命令与报告见 `docs/design/multi-protocol-validation.md`。
