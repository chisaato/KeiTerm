# Issue 09: TLS Telnet

**What to build:** Telnet 当前为基础明文连接并在 UI 提示；为需要加密的 Telnet 设备提供 TLS 通道（如 STARTTLS/TelnetS 的实际语义在此票内调研确定）。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] 加密 Telnet 连接可用，证书校验接入既有信任体系。
- [ ] 明文/加密模式在配置与 UI 上有明确区分与安全提示。
- [ ] 协商状态机与加密层的交互（何时切换）有测试。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
