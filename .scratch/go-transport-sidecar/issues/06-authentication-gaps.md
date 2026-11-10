# Issue 06: Go SSH 认证缺口：Agent、硬件 SK、Keyboard-Interactive、CA 证书

**What to build:** Go 引擎补齐 .NET 已有的认证方式：SSH Agent、Keyboard-Interactive、OpenSSH CA 证书鉴权；硬件 SK 按仓库硬约束经 SshNet.Agent 桥接，不得直连底层库。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] SSH Agent 认证经 Go 引擎端到端可用。
- [ ] Keyboard-Interactive 交互挑战接入现有统一交互服务。
- [ ] OpenSSH CA 证书鉴权可用并有测试。
- [ ] 硬件 SK 明确走 SshNet.Agent 桥接路径；未实现时保持显式拒绝。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
