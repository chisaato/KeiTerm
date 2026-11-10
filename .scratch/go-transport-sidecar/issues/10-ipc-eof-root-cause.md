# Issue 10: Go IPC EOF 根因排障（历史用户报告）

**What to build:** 用户曾报告 ReadExactlyAsync EndOfStreamException；结构化诊断已接线但原始根因仍未复现。留档以便复现时直接回看诊断链。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] 利用已接线的 SidecarDiagnostic 与 daemon 退出原因记录构造或等待复现。
- [ ] 根因或不可复现结论写入验收文档；不把猜测当结论。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
