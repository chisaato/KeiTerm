# Issue 10: Telnet/Serial 带内 ZMODEM/YMODEM 文件传输

**What to build:** 在 Telnet/Serial 会话中支持 ZMODEM/YMODEM 带内传输；测试环境已有真实 lrz/rz 用例但因环境缺失跳过，此票补齐传输实现与环境可用时的证据。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] ZMODEM 至少可用且有自动化测试（虚拟对端）。
- [ ] 真实 lrz/rz 环境用例在有条件时转绿并保留命令与结果。
- [ ] 带内协议不影响终端流与协商状态机的既有契约。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
