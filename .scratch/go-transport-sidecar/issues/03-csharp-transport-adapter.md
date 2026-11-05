# Issue 03: C# 传输适配层 (Kei.Term.Transport.Go)

Status: resolved
Role: ready-for-agent
Type: task

## 目标
在 KeiTerm C# 方案中实现 `ISshSessionFactory` 及其配套对象，透明桥接 Go Sidecar，并集成双层引擎开关。

## 交付内容
1. 新建 `src/Kei.Term.Transport.Go/Kei.Term.Transport.Go.csproj` 并接入 `Kei.Term.slnx`。
2. 双层引擎开关：
   - `AppSettings.SshTransportEngine` 全局开关，默认值严格为 `BuiltIn`（SSH.NET）。
   - `SessionNode` / `SessionOverrides` / `ResolvedSessionConfig` 会话级覆盖与继承配置。
3. 路由与生命周期管理器：
   - `CompositeSshSessionFactory`：按会话配置惰性分发。默认不唤醒 Go 进程，仅当会话显式指定 `GoSidecar` 时才初始化 Go 驱动。
   - `GoSidecarProcessManager`：按需拉起、健康保活、孤儿自毁与崩溃监管。
4. IPC 客户端与 Core 契约实现：
   - `GoIpcClient`：处理 9 字节紧凑帧与 StreamID 双向分路。
   - `GoSshSessionFactory`, `GoSshSession`, `GoRemoteFileSystem`, `GoRemoteTransferChannel`。
5. 单元测试验证：
   - `tests/Kei.Term.Tests/GoTransportTests.cs`（8 个测试全部通过）。

## Answer
已全部交付并验证通过：
- C# 适配层工程构建成功，无额外破坏性修改。
- 保证默认 .NET 原生 SSH.NET 行为 100% 不受影响。
- 完整单元测试集（1659 通过，0 失败）。

