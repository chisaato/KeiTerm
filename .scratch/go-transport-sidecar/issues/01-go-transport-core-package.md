# Issue 01: Go 传输引擎核心库骨架 (pkg/transport)

Status: resolved
Role: ready-for-agent
Type: task

## 目标
创建独立、无依赖外部进程的 Go 传输引擎核心包（可在桌面 Daemon 或移动端 In-Process 编译产物中共用）。

## 交付内容
1. `go.mod` 初始化与工程结构目录组织（位于 `src/Kei.Term.Transport.Go/engine/`）。
2. 基础抽象封装：
   - SSH Client 管理（支持密码、公私钥、JumpHost 跳板链）。
   - PTY Shell 会话管理（输入输出 Stream、窗口尺寸调整）。
   - SFTP 客户端包装（目录遍历、文件属性、读写流）。
3. 单元测试验证核心建连与数据流机制。

## Answer
已在 `src/Kei.Term.Transport.Go/engine/pkg/transport/` 落地：
- `auth.go`：支持密码、加密私钥解密、Agent/FIDO2 扩展 Signer 注入。
- `dialer.go`：直连与 JumpHost 链式拨号，带 HostKey 校验与链路清理。
- `session.go`：PTY Request、Resize、Stdio 流绑定与会话生命周期管理。
- `sftp.go`：并发流式读写、分块加速、完整元数据操作。
- `transport_test.go`：全量单测通过（包含多跳、内存 SFTP 高并发读写、握手取消及 Shell 测试）。

