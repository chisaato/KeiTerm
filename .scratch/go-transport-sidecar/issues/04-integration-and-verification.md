# Issue 04: 端到端测试与真实 SSH/SFTP 传输验证

Status: resolved
Role: ready-for-agent
Type: task

## 目标
建立针对 Go 传输引擎的单元与集成测试，验证对现有 `TransferQueue` 与终端会话的兼容性。

## 交付内容
1. 单元测试（针对 IPC 编解码、数据分帧多路复用与错误恢复）。
2. 在 `KEITERM_SSHD_TESTS=1` 环境下跑通真实 SSH 终端登录输入输出、SFTP 文件上传下载与断点续传。
3. 验证 Go 传输引擎与内置 SSH.NET 引擎双向切换无缝性。

## 验收与遗留（2026-11-10 收口）

IPC 编解码、分帧多路复用与故障传播测试已交付；真实 sshd 下 C# → 独立 Go daemon → 终端/SFTP 端到端冒烟 2 项通过（GoSshIntegrationTests）。原交付目标中的断点续传验证、性能基准比对与故障自愈（daemon 被杀恢复）未实现：断点续传/文件能力缺口转 `05-sftp-file-capabilities.md`，性能基准转 `09-cross-engine-benchmark.md`，daemon 杀进程后多标签影响仍属待验（快照与验收文档已如实记录）。
