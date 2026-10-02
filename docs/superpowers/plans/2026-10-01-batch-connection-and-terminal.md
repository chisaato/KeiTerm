# 连接与终端日常批次实施顺序

> **For agentic workers:** 用户已指定用子代理实现。文件所有权重叠的车道串行。不要提交，除非用户另说。

**Goal:** 在已通过的代理规格之外，把自动重连、终端日常、端口转发、连接管理器 F2 重命名做完。

**Architecture:** 当前工作区有未提交改动，不能另开干净 worktree 当基线。同一目录并行 `dotnet build` / `dotnet test` 会抢 `bin`/`obj`。因此即使源文件不重叠，也一次只跑一个会编译的实现代理。

**Spec:** 各功能规格见下表。代理规格已审过，是绑定文本。

## 顺序与文件所有权

| 顺序 | 车道 | 规格 | 独占文件 | 为什么不能并行 |
|---|---|---|---|---|
| 1 | 代理 / 防火墙 + 会话树选择器 + SOCKS5 拨号 | `docs/superpowers/specs/2026-10-01-proxy-firewall-and-session-tree-design.md` | `SchemaMigrations.cs`（只追加 v4）、`SshDialer.cs`、`SshConnectOptions`、`ConnectionOrchestrator.cs`、`SessionEdit*`、`MainWindow.axaml` / `.axaml.cs`、设置里的新代理页 | 迁移 v4；主窗口树抽取 |
| 2 | 端口转发 | `docs/superpowers/specs/2026-10-01-port-forwards-design.md` | `SchemaMigrations.cs`（只追加 v5）、会话编辑器新增「端口」分类、`SshDialer` 上挂转发 | 等 v4 落地后再加 v5；拨号器已被车道 1 改过 |
| 3 | 自动重连 | `docs/superpowers/specs/2026-10-01-auto-reconnect-design.md` | `ConnectionOrchestrator.cs`、`TerminalTabViewModel` 的断线回调、`MainViewModel.TabActions.cs` | 等车道 1 不再改编排器 |
| 4 | 连接管理器 F2 | `docs/superpowers/specs/2026-10-01-session-tree-f2-rename-design.md` | `MainWindow.axaml` / `.axaml.cs` 的树键盘 | 等车道 1 的树抽取结束 |
| 5 | 终端日常 | `docs/superpowers/specs/2026-10-01-terminal-daily-design.md` | 终端面板、配色适配、标签栏样式。不改 `SchemaMigrations`、`SshDialer`、`ConnectionOrchestrator`、会话编辑器连接页 | 标签栏在 `MainWindow.axaml`，等 F2 结束再动主窗口 |

车道 5 里不碰主窗口的部分（查找栏、字体弹窗、关键字规则、256 色）理论上可与车道 1 并行，但会同时编译 `Kei.Term.App`。本批不并行编译。

## 全局约束

- 不提交。
- 已发布迁移不改。代理是 v4，端口转发是 v5。
- 口令不进 JSON、不进日志。
- 注释用行内注释。
- 测试必须能因生产行为出错而失败。不写只断言调用顺序的测试。
- 子代理不要再派子代理。

## Review Focus

- 跳板会话自己的 SOCKS5 只包那一跳的最外层 TCP，不包 `127.0.0.1` 转发端口。
- 迁移后再保存，`jump_host_id` 被清空且 `proxy_json` 仍在。
- 用户主动断开、认证取消、主机密钥拒绝、交互式 / 2FA，不自动重连。
- 端口转发在会话断开时停掉，不留监听端口。
- F2 在文本框聚焦时不抢走按键。
