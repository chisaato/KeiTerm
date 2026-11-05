# Issue 04: 端到端测试与真实 SSH/SFTP 传输验证

Status: ready-for-agent
Role: ready-for-agent
Type: task

## 目标
建立针对 Go 传输引擎的单元与集成测试，验证对现有 `TransferQueue` 与终端会话的兼容性。

## 交付内容
1. 单元测试（针对 IPC 编解码、数据分帧多路复用与错误恢复）。
2. 在 `KEITERM_SSHD_TESTS=1` 环境下跑通真实 SSH 终端登录输入输出、SFTP 文件上传下载与断点续传。
3. 验证 Go 传输引擎与内置 SSH.NET 引擎双向切换无缝性。
