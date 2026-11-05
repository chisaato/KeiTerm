# Issue 03: C# 传输适配层 (Kei.Term.Transport.Go)

Status: ready-for-agent
Role: ready-for-agent
Type: task

## 目标
在 KeiTerm C# 方案中实现 `ISshSessionFactory` 及其配套对象，透明桥接 Go Sidecar。

## 交付内容
1. 新建 `src/Kei.Term.Transport.Go/Kei.Term.Transport.Go.csproj`。
2. 实现 Sidecar 守护进程生命周期管理器 `GoSidecarProcessManager`（按需拉起、健康检查、崩溃自动重启、进程随父退出）。
3. 实现 IPC 客户端驱动：
   - `GoIpcClient`（处理 JSON-RPC 与 Stream 分路分发）。
4. 实现 Core 抽象接口：
   - `GoSshSessionFactory : ISshSessionFactory`
   - `GoSshSession : ISshSession, ITerminalSession`
   - `GoRemoteFileSystem : IRemoteFileSystem`
   - `GoRemoteTransferChannel : IRemoteTransferChannel`
