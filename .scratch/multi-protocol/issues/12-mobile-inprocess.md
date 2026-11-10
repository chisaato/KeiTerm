# Issue 12: 移动端 In-Process 核心与 USB 驱动

**What to build:** Go 纯模块化包已为移动端预留（io.Reader/Writer 抽象），但 Gomobile / C-ABI 桥接无任何验证；此票覆盖移动端 In-Process 集成与移动 USB 串口驱动。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] Go 核心以 C-Shared/Gomobile 产物形式可用并有最小桥接验证。
- [ ] 移动端 USB 串口驱动路径确定。
- [ ] 桌面 daemon 与 In-Process 共享同一 pkg 抽象，无分叉。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
