# Issue 11: 跨平台发布封装与真机运行验收

**What to build:** Sidecar MSBuild 按宿主机而非 RID 编译，跨 RID publish 未验证；Windows/macOS 仅有交叉构建证据，无真实运行。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] 发布链按 RID 提供/编译匹配 daemon 二进制，publish 产物含正确可执行文件。
- [ ] Windows、macOS 至少各完成一次真实运行冒烟并记录环境。
- [ ] 独立交叉构建不再被当作发布封装证据。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
