# Issue 11: Local Shell、自动登录脚本与其他连接类型

**What to build:** 多协议规格 Out of Scope 一揽子：本地 Shell 会话、自动登录脚本，以及未来新连接类型的通用接入路径。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] Local Shell 会话走统一连接入口与终端契约。
- [ ] 自动登录脚本机制确定（如 expect 式或脚本式）并实现。
- [ ] 新协议接入路径（工厂/能力/配置）复用现有抽象，不新建平行体系。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
