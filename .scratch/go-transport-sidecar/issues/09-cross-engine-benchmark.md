# Issue 09: 跨引擎 SFTP/终端性能基准

**What to build:** Go 侧使用 pkg/sftp 并发能力但无任何吞吐数据；建立可重复的双引擎基准，为设置页与文档的性能表述提供依据。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] 可重复基准脚本覆盖 SFTP 吞吐（分块并发）与终端往返延迟，双引擎同机同参对比。
- [ ] 结果与运行环境记录入 docs/design；设置页与文档措辞与数据一致。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
