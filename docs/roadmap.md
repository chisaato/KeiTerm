# KeiTerm 路线图

> 2026-10-01。未完成项以这篇为准，根目录不再另开清单。

原生、安全默认、直接读 OpenSSH 已有的 config / known_hosts / Agent。不做 VBScript 宏，不做强制联网的 AI 接管，不在远端装 helper。

设计文档：[08 架构审查](./design/08-architecture-review-2026-09.md) · [09 目录跟随](./design/09-cwd-tracking.md) · [10 拆分](./design/10-refactor-plan.md) · [11 双 SSH 后端](./design/11-dual-ssh-backend.md) · [12 终端宿主与会话选项](./design/12-terminal-host-and-session-options.md)

## 还没做完

| 项 | 说明 |
|---|---|
| 代理 / 防火墙 | ✅ 全局代理、会话防火墙下拉、目录树选会话、无认证 SOCKS5，SOCKS5 可带用户名口令。见 [规格](./superpowers/specs/2026-10-01-proxy-firewall-and-session-tree-design.md) |
| 端口转发 | ✅ Local / Remote / Dynamic，`port_forwards` 表，迁移 v5。见 [规格](./superpowers/specs/2026-10-01-port-forwards-design.md) |
| 自动重连 | ✅ 指数退避 `1, 2, 4, 8, 16, 30`，复用 `ReuseTarget`，默认关。见 [规格](./superpowers/specs/2026-10-01-auto-reconnect-design.md) |
| Agent 转发 | 双引擎已验证可行，等 Tmds.Ssh 发版，见 [11](./design/11-dual-ssh-backend.md) |
| 终端搜索 | ✅ Ctrl+F。见 [终端日常](./superpowers/specs/2026-10-01-terminal-daily-design.md) |
| 连接管理器 F2 | ✅ 树聚焦时原地重命名。见 [规格](./superpowers/specs/2026-10-01-session-tree-f2-rename-design.md) |
| 滚轮缩放 | ✅ Ctrl+滚轮每格 ±1pt，写回全局字号（8–36），已打开的标签一起变 |
| 在线调色器 | ✅ 主题编辑器点色块弹出调色器，拖动时右侧预览跟着变。没有屏幕取色 |
| 字体弹窗 | ✅ 字重、行距。见 [终端日常](./superpowers/specs/2026-10-01-terminal-daily-design.md) |
| 标签栏对比 | ✅ 未选中 / 选中 / 后台活动靠明度和字重分开。见 [终端日常](./superpowers/specs/2026-10-01-terminal-daily-design.md) |
| 256 色 / true color | ✅ 16–255 走 xterm 色立方；真彩色不另写解析器。见 [终端日常](./superpowers/specs/2026-10-01-terminal-daily-design.md) |
| 关键字高亮 | ✅ error / fail / IP / URL，规则在 Core。见 [终端日常](./superpowers/specs/2026-10-01-terminal-daily-design.md) |
| 本地 Shell / 串口 / Telnet | RoyalTerminal 已有传输层。每种协议一个 `ITerminalSessionProvider`，见 [10 §6](./design/10-refactor-plan.md) |
| 目录跟随 | 开关已有，跟随行为还没做。只收 OSC 7 和 tmux 标题，见 [09](./design/09-cwd-tracking.md) |
| 命令块 | RoyalTerminal 已解析 OSC 133 |
| 会话录制 | asciicast v2 |
| 命令片段与广播 | 撰写栏扩展 |
| ZMODEM | 视迁移用户再排 |
| E2EE 同步 | 先加 `revision` + `deleted_at`，再 AGE + WebDAV / S3 / 本地目录 |
| 连接中枢 | 从已连主机发现 Docker / K8s / WSL，经 SSH 执行，不装远端 agent |
| 凭据源 | OS Keyring、1Password / Bitwarden、SSH 证书 |
| 会话树拆分 | 连接管线已拆出。`SessionTreeViewModel` 和拖放还在 `MainViewModel`，见 [10](./design/10-refactor-plan.md) |

服务变多之后再引入依赖注入。SSH.NET 做不到的（sftp 复用、agent 转发、env 请求）在设置里标出来，不要假装可用。

## 已经落地、容易重复做的

会话树、身份与 Vault、SFTP/SCP、Konsole 配色导入、主机密钥信任库、ProxyJump、导入 `~/.ssh/config`、保活与自定义 Agent、标签标题跟随（OSC 0/2）、标签右键（重连 / 克隆 / 重命名 / 批量关闭）、批量修改会话、终端右键复制粘贴全选清屏、回滚滚动条与网格贴底。

## 竞品速览与差异化定位

| 产品 | 形态 | 值得借鉴 | KeiTerm 的取舍 |
|---|---|---|---|
| Xshell / SecureCRT | 原生、闭源、Windows 为主 | 会话树、撰写栏、ZMODEM、脚本 | 保留会话树与撰写栏；不做 VBScript 宏 |
| MobaXterm | 原生、Windows、一体化 | 多协议、SFTP 侧栏随 cwd、X11 | SFTP 侧栏已有；随 cwd 需 Shell 集成 |
| Termius | 跨端（含移动端）、订阅制 | 加密 Vault + 跨设备同步、Snippets、端口转发 UI | 同步做**用户自选存储**的 E2EE，不绑定自有云 |
| Tabby | Electron、开源、插件 | 分屏、Zmodem、插件生态 | 原生渲染 + 更低内存 |
| WindTerm | 原生、开源、极快 | 性能、大输出流畅度 | RoyalTerminal（Skia / Ghostty VT）同路线 |
| XPipe | 连接中枢 | 自动发现 Docker / K8s / WSL 并统一成「可连接目标」 | **可作为中长期差异点**（见上面的连接中枢） |
| Warp / Wave 等 AI 终端 | 重交互 | 命令块（Blocks）、Shell 集成 | 借鉴 OSC 133 命令块；不做强制联网 AI 接管 |

**差异化结论**：KeiTerm 最有机会的定位是「**原生、安全默认、与 OpenSSH 生态无缝互通的运维终端**」——
直接读 `~/.ssh/config` / `known_hosts` / Agent，用户零迁移成本；安全默认（主机密钥、Vault）做对；
再用 Shell 集成（cwd 跟随、命令块）拉开与传统工具的体验差距。

## 设计哲学与功能舍弃原则

1. **不做臃肿的遗留脚本堆砌**：不引入 VBScript / 复杂内部自动化宏，批量运维交给 Ansible/Terraform。
2. **不做过度激进的破坏性交互**：保持经典树状资产目录与标准终端操作习惯，不强行采用强制联网的 AI Agent 命令接管。
3. **坚持原生桌面质感**：Avalonia 原生渲染与轻量内存开销，避免 Web/Electron 容器。
4. **安全默认**：主机密钥校验、Vault 加密、机密不落明文日志——默认开启，而不是高级选项。
5. **与 OpenSSH 生态互通优先于自建格式**：配置、信任库、Agent 都优先读写用户已有的 OpenSSH 资产。
6. **零远端安装（agentless）**：不在服务器上部署 helper / daemon（区别于 Warp、Wave、VS Code Remote）；需要远端信息时走 SSH exec 通道执行普通命令。
