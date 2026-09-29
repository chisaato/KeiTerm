# KeiTerm 研发路线图 (Roadmap)

> 最近更新：2026-09-29（后端审查见 [design/08](./design/08-architecture-review-2026-09.md)，拆分规划见 [design/10](./design/10-refactor-plan.md)，目录跟随见 [design/09](./design/09-cwd-tracking.md)）

KeiTerm 不追求与 Xshell / SecureCRT 1:1 复刻。设计理念：抛弃传统工具冗余陈旧的历史包袱，避开 Electron 类客户端的资源消耗，
结合现代 DevOps 工程师的实际工作流，聚焦**高可用网络穿透、直观交互、安全凭据与轻快体验**。

---

## 0. 当前状态快照

| 能力 | 状态 |
|---|---|
| 会话树（无限层级、拖拽、复制/剪切）、身份（有序多方法认证）、Vault（可选主密码） | ✅ |
| SFTP / SCP 文件侧栏、外部编辑器联动、终端配色（含 Konsole 导入）、撰写栏 | ✅ |
| **主机密钥信任库**（确认 / TOFU / 严格三策略，变更阻断，算法协商偏好，已知主机管理与导入导出） | ✅ |
| **跳板链 ProxyJump**（多级、每跳独立身份、文件通道复用跳板链） | ✅ 后端完成，会话编辑器的跳板下拉待做 |
| **导入 `~/.ssh/config`**（Host / IdentityFile / ProxyJump / Include） | ✅ |
| 保活、自定义 Agent（含 Pageant）、登录脚本 | ✅ 已接通（此前只存不用） |
| Agent 转发（`ssh -A`） | ❌ SSH.NET 不支持；候选 Tmds.Ssh，见 design/08 §6 |
| 多协议扩展点（`ITerminalSession`） | ✅ 接口就绪，提供者待接入 |
| 持久化：版本化迁移 + Dapper + WAL | ✅ |
| 终端内搜索、右键菜单、滚轮缩放、字体弹窗 | ⏳ 依赖 UI（见 `todo.md`） |
| 端口转发管理器、自动重连、本地 Shell / 串口 | ⏳ 建议下一步 |
| E2EE 同步、OS Keyring 静默解锁 | ⏳ 规划中 |

---

## 1. 竞品速览与差异化定位

| 产品 | 形态 | 值得借鉴 | KeiTerm 的取舍 |
|---|---|---|---|
| Xshell / SecureCRT | 原生、闭源、Windows 为主 | 会话树、撰写栏、ZMODEM、脚本 | 保留会话树与撰写栏；不做 VBScript 宏 |
| MobaXterm | 原生、Windows、一体化 | 多协议、SFTP 侧栏随 cwd、X11 | SFTP 侧栏已有；随 cwd 需 Shell 集成 |
| Termius | 跨端（含移动端）、订阅制 | 加密 Vault + 跨设备同步、Snippets、端口转发 UI | 同步做**用户自选存储**的 E2EE，不绑定自有云 |
| Tabby | Electron、开源、插件 | 分屏、Zmodem、插件生态 | 原生渲染 + 更低内存 |
| WindTerm | 原生、开源、极快 | 性能、大输出流畅度 | RoyalTerminal（Skia / Ghostty VT）同路线 |
| XPipe | 连接中枢 | 自动发现 Docker / K8s / WSL 并统一成"可连接目标" | **可作为中长期差异点**（见 Phase 3） |
| Warp / Wave 等 AI 终端 | 重交互 | 命令块（Blocks）、Shell 集成 | 借鉴 OSC 133 命令块；不做强制联网 AI 接管 |

**差异化结论**：KeiTerm 最有机会的定位是「**原生、安全默认、与 OpenSSH 生态无缝互通的运维终端**」——
直接读 `~/.ssh/config` / `known_hosts` / Agent，用户零迁移成本；安全默认（主机密钥、Vault）做对；
再用 Shell 集成（cwd 跟随、命令块）拉开与传统工具的体验差距。

---

## 2. 下一步建议（按优先级）

### P0：补齐「连接可信 + 可达」闭环（约 1–2 周，后端已就绪，主要是小型 UI）

0. **先拆 `MainViewModel` 的连接管线**（`IInteractionService` → `VaultSessionService` → `ConnectionOrchestrator`，见 [10](./design/10-refactor-plan.md)），否则下面 2–4 会继续堆进 2000 行的类。
1. ~~主机密钥确认弹窗 + 信任库管理页~~ ✅
2. **会话编辑器「跳板机」下拉**：选择另一会话作为跳板（后端已支持多级链与循环检测）。
3. **端口转发管理器**（Local / Remote / Dynamic SOCKS5）：SSH.NET 原生支持三种转发，
   在 `SshNetSession` 上挂 `ForwardedPort*` 即可；新增 `port_forwards` 表（迁移 v3），支持随会话自动挂载、端口冲突检测。
4. **自动重连**：`Disconnected` 事件 + 指数退避 + 标签状态；重连复用已物化材料（交互式/2FA 除外）。

### P1：终端核心体验（UI 为主，见 `todo.md`）

- 终端内搜索（Ctrl+F）、右键菜单（复制/粘贴/查找）、Ctrl+滚轮缩放、字体弹窗（字重/行距）。
- 关键字高亮规则（error/fail/IP/URL），规则引擎可放 Core 以便单测。

### P1：本地终端 / 串口 / Telnet（低成本、高覆盖）

RoyalTerminal 已提供 `Transport.Pty`（本地 Shell，含 Windows ConPTY）、`Transport.Serial`、`Transport.Telnet`。
接入后会话树即可统一管理本地 Shell 与网络设备串口——这是 Xshell/MobaXterm 用户的高频场景。
`ITerminalSession` 已抽出；每种协议实现一个 `ITerminalSessionProvider` 包装 RoyalTerminal 的 `ITerminalTransport`，
协议参数存 `session_protocol_settings` 多态 JSON（见 [10 §6](./design/10-refactor-plan.md)）。

### P2：现代化差异点

- **Shell 集成 / 目录跟随**：RoyalTerminal 已解析 OSC 7 与 OSC 133，无需自写解析器；tmux 需透传或标题方案（已实测），
  设计与待拍板问题见 [09](./design/09-cwd-tracking.md)。OSC 133 同时支撑命令块、跳转上一条命令、复制某条命令输出。
- **会话录制**：asciicast v2 格式落盘（审计/复盘），纯后端能力。
- **Snippets（参数化命令片段）+ 广播发送**：撰写栏扩展。
- **ZMODEM（rz/sz）**：对 Xshell 迁移用户是切换阻碍项，优先级视目标用户群调整。

### P3：同步与生态

- **E2EE 同步**：先做迁移 v3/v4（`revision` + `deleted_at` 墓碑），再落 AGE 格式容器与 WebDAV/S3/本地目录后端。
- **连接中枢化**：从已连主机自动发现 Docker 容器 / K8s Pod / WSL 发行版，作为可直接打开的"子会话"（`docker exec` / `kubectl exec` 经 SSH 通道执行）。
- OS Keyring 静默解锁；1Password / Bitwarden 凭据源；SSH 证书。

---

## 3. 架构演进（与功能并行）

详见 [design/08](./design/08-architecture-review-2026-09.md) §4：

1. 拆分 `MainViewModel`（~1900 行）为 `ConnectionOrchestrator` / `SessionTreeService` / `VaultSessionService`，弹窗委托收敛为 `IInteractionService`。**建议在做 P0-3/4 之前完成**，否则端口转发与自动重连会继续堆进同一个类。
2. 服务数增长后引入 `Microsoft.Extensions.DependencyInjection`。
3. SSH.NET 能力边界（无 sftp 复用、无 agent 转发、无 env 请求）：设置页对应项标注或隐藏；必要时向上游贡献。

---

## 4. 设计哲学与功能舍弃原则

1. **不做臃肿的遗留脚本堆砌**：不引入 VBScript / 复杂内部自动化宏，批量运维交给 Ansible/Terraform。
2. **不做过度激进的破坏性交互**：保持经典树状资产目录与标准终端操作习惯，不强行采用强制联网的 AI Agent 命令接管。
3. **坚持原生桌面质感**：Avalonia 原生渲染与轻量内存开销，避免 Web/Electron 容器。
4. **安全默认**：主机密钥校验、Vault 加密、机密不落明文日志——默认开启，而不是高级选项。
5. **与 OpenSSH 生态互通优先于自建格式**：配置、信任库、Agent 都优先读写用户已有的 OpenSSH 资产。
