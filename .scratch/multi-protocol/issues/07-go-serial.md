# 07: Go Serial 功能对齐

**What to build:** 用户选择 Go 引擎时复用已有设备绑定与参数，打开串口并使用同等流控、引脚、Break 与断开行为。

**Blocked by:** 04 — .NET Serial 端到端连接；05 — Go Sidecar 可诊断性与故障传播。

**Status:** ready-for-human

- [x] 选定并核实串口实现支持自动 RTS/CTS 与 XON/XOFF；手动引脚接口不得冒充自动流控，库缺口须补齐或换实现。
- [x] 扩展端口枚举、打开、参数、引脚和 Break IPC 契约，复用通用设备配置，不在 Go 侧另建同步真相源。
- [x] 明确 macOS 详细枚举的 cgo/SDK 约束和实际构建策略；基础枚举与 USB 元数据能力区分。
- [x] 真实 daemon 加虚拟 PTY 验证双向流、取消、关闭、重新打开与共享 IPC 错误传播。
- [ ] 真机验证自动流控、引脚、Break 与拔插；未验证平台或硬件如实标注。
- [x] 无网络拨号、SSH 凭据、文件系统和假 resize；不静默回退 .NET。

## 验收与阻塞

基础名称枚举、真实 daemon/PTY 双向流、重开和 Serial/Telnet 共享 IPC 结束已实际通过；Go race/vet 与无 cgo 六目标构建通过，许可/来源文件随 App 输出。内嵌核心补齐自动流控，独立基础枚举不引入 USB 元数据组件。未勾条款需物理硬件，Windows/macOS 也尚无真实运行证据。见 `docs/design/multi-protocol-validation.md`。
