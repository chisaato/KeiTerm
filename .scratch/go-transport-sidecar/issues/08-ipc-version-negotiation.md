# Issue 08: IPC 协议版本与能力协商

**What to build:** 应用与 daemon 当前必须配套发布；为帧协议引入版本握手与能力协商，使升级路径可解释、可拒绝而非帧解析崩溃。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] 握手阶段交换协议版本与能力集合，不匹配时给出明确错误。
- [ ] 新旧组件混配的可观测行为有测试覆盖。
- [ ] 既有 9 字节帧格式向后兼容或提供明确迁移说明。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
