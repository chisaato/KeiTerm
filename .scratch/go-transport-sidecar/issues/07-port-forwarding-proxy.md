# Issue 07: Go 引擎端口转发与代理：-L / -R / -D SOCKS5

**What to build:** Go 引擎支持本地/远端端口转发与动态 SOCKS5 代理，与 .NET 内置引擎行为对齐；当前 Go 侧显式拒绝这些配置。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] -L 本地转发、-R 远端转发、-D 动态 SOCKS5 在 Go 引擎下可用。
- [ ] 与 .NET 引擎行为差异（如关闭语义、多通道复用）有测试与文档。
- [ ] 转发生命周期纳入共享 IPC 故障传播边界，如实验证不得宣称单流隔离。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
