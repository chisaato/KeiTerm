# 内嵌串口核心来源与维护边界

- 来源：https://github.com/bugst/go-serial （Go 模块 `go.bug.st/serial`）。
- 固定版本：`v1.8.0`，commit `9b9e4a4b829f22f53181c84df19915b858599bbb`；已用本地上游 git tag/HEAD 核实。
- 许可：BSD-3-Clause，完整上游版权与条款保留于本目录 `LICENSE` 和各源文件头。二进制分发须随附该许可。
- 仅复制 Linux、Windows、macOS 基础串口运行文件和 Unix pipe 帮助代码；不含 enumerator、USB 元数据、cgo、portlist、上游测试、CI 或 git 元数据。仅使用工程已有 `golang.org/x/sys`，不增加外部 fork 服务或 replace。

## KeiTerm 修改（2026-11-09）

- 增加自动 `None` / `RtsCts` / `XonXoff`。Unix 使用对应平台 CRTSCTS 位；XON/XOFF 为 IXON+IXOFF、清 IXANY、DC1/DC3。Windows 设置 CTS 输出门控及 RTS handshake；软件双向流控使用 DCB InX/OutX。
- 初始 DTR/RTS 改为独立 nullable bool；nil 保留驱动现值，显式请求失败直接返回，不吞 PTY ENOTTY。自动 RTS/CTS 与手动 RTS 的冲突由公共包拒绝。
- 增加真正两态 `SetBreakState(bool)`，直接调用 Unix break ioctl / Windows SetCommBreak/ClearCommBreak，不用 sleep 模拟持久状态。
- Unix 保持非阻塞 fd，以 poll + 关闭管道同时打断 read/write；关闭前等待活动 I/O 退出，避免 fd 重用。Windows 使用 CancelIoEx 取消 overlapped I/O，等完成后释放句柄。
- 移除端口枚举入口；详细枚举及 RPC 不属于本核心。公共 API 位于 `pkg/serial`，context 取消拥有整个端口生命周期。
- 保留上游波特率、校验、停止位与平台 ioctl 实现；平台不支持的配置返回错误，不作降级。
- 公共包现使用原生 `SetReadTimeout` 作为轮询间隔（默认 100ms），超时继续读取而非返回 EOF；每次写有独立预算（默认 500ms，范围均为 1..5000ms）。写预算耗尽只终结本端口并返回 `ErrWriteTimeout`，原子状态与回调等待避免已完成写的残留 timer 关闭后续操作；终结原因区分用户关闭、context 取消和写超时。

## 验证边界

Linux socat PTY 可验证字节流、生命周期、软件 XON/XOFF、termios 位和 break ioctl 接受，但不能证明物理 RTS/CTS、DTR/RTS 引脚电平、Break 波形或硬件拔插。Windows/macOS 交叉构建不等于运行验证；真实硬件验收须另行进行。
