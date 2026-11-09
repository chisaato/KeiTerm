# Issue 05: Go SFTP 文件能力缺口：断点续传、SCP、lstat、chmod、安全递归删除

**What to build:** 用户在 Go 引擎下获得与 .NET 内置引擎对齐的文件能力：非零偏移断点续传、SCP、lstat、chmod 与安全递归删除；当前 SupportsResume=false 且部分操作显式拒绝。

**Blocked by:** None (can start immediately).

**Status:** ready-for-agent

- [ ] Go 侧 sftp RPC 与 C# GoRemoteFileSystem 支持非零偏移续传，SupportsResume 按真实能力返回 true 并有传输中途断开重连续传的端到端证据。
- [ ] SCP 或明确的产品级替代决策落盘；不伪造能力。
- [ ] lstat、chmod、安全递归删除按现有 IRemoteFileSystem 契约接入并测试。
- [ ] 文件连接不再借 SSH Shell 会话的结论或现状边界写入文档。

## Comments

2026-11-10 由用户确认从原规格/验收文档的「等遇到了再做」清单转为留存工单；非当前优先级，作为后续快查指针。来源：go-transport-sidecar spec 功能规划、docs/design/go-transport-sidecar.md 能力边界、multi-protocol spec Out of Scope。
