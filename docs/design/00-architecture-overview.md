# Kei.Term 架构设计概览

本文档为 **Kei.Term**（基于 AvaloniaUI 的跨平台现代 SSH 终端）的高层设计规范。

---

## 1. 系统总体架构

Kei.Term 遵循严格的关注点分离（Separation of Concerns）与依赖反转原则（DIP）。UI 层仅依赖核心业务抽象，通信与加密均采用可插拔架构。

```
+-----------------------------------------------------------------------+
|                             Kei.Term.App                              |
|   (AvaloniaUI 11/12 + CommunityToolkit.Mvvm + RoyalApps.RoyalTerminal)|
|  - MainWindow / TabbedSessions / HostTreeView / Settings / SFTP View  |
+-----------------------------------+-----------------------------------+
                                    | 依赖注入 / MVVM 绑定
                                    v
+-----------------------------------------------------------------------+
|                            Kei.Term.Core                              |
|  - 领域模型 (FolderNode, SessionNode, Credential, SyncManifest)        |
|  - 核心接口 (ITerminalService, IVaultService, ISyncEngine)             |
|  - 树形结构管理与继承规则 (Cascade Settings Resolver)                 |
+-----------------------------------+-----------------------------------+
                                    ^
       +----------------------------+----------------------------+
       | 实现注入                                                | 实现注入
       v                                                         v
+-----------------------------+          +------------------------------+
|   Kei.Term.Infrastructure   |          |         Kei.Term.Ssh         |
| - SQLite (Dapper + 版本迁移) |          | - SSH.NET 客户端生命周期     |
| - Vault 双后端:             |          | - SshNet.Agent 集成          |
|   * 内置 AES-256-GCM 存储   |          |   (Unix Socket/Named Pipe)   |
|   * OS 原生 Keyring 适配器  |          | - RoyalTerminal 适配桥接     |
| - E2EE 云同步抽象与打包     |          | - SFTP 文件传输驱动引擎      |
+-----------------------------+          +------------------------------+
```

---

## 2. 模块划分与职责

| 模块工程 | 职责边界 | 外部关键依赖 |
| :--- | :--- | :--- |
| **`Kei.Term.Core`** | 纯净领域层。定义会话树、凭据抽象、同步接口、事件流。无任何 UI 或特定操作系统调用。 | 无框架外重度依赖 |
| **`Kei.Term.Ssh`** | 远程网络通信层。负责建立 SSH/SFTP 通道、对接系统 ssh-agent（含硬件 SK 密钥代理）、pty 协议适配。 | `SSH.NET`, `SshNet.Agent` |
| **`Kei.Term.Infrastructure`** | 数据持久化与安全实现层。负责 SQLite 存储、主密码派生加密、OS Keyring 对接、云同步序列化与解包。 | `Microsoft.Data.Sqlite` |
| **`Kei.Term.App`** | UI 表现与交互层。多会话标签页容器、侧边栏目录树、终端仿真渲染、设置面板与向导。 | `Avalonia`, `RoyalApps.RoyalTerminal.Avalonia` |

---

## 3. 各子系统设计详细文档索引

1. [会话目录树与存储设计 (01-session-tree-and-storage.md)](./01-session-tree-and-storage.md)
2. [凭据管理与安全库 (Vault) 设计 (02-vault-and-credentials.md)](./02-vault-and-credentials.md)
3. [SSH 通信与 Agent / SK 密钥架构 (03-ssh-and-agent.md)](./03-ssh-and-agent.md)
4. [终端仿真与会话流转设计 (04-terminal-and-lifecycle.md)](./04-terminal-and-lifecycle.md)
5. [E2EE 云同步与插件化扩展设计 (05-cloud-sync-and-plugins.md)](./05-cloud-sync-and-plugins.md)
6. [身份（Identity）与认证编排设计 (06-identity-and-auth.md)](./06-identity-and-auth.md)
7. [主机密钥信任与连接可达性 (07-host-keys-and-connectivity.md)](./07-host-keys-and-connectivity.md)
8. [架构审查与后端优化记录 2026-09（含 ORM 选型、SSH 库评估） (08-architecture-review-2026-09.md)](./08-architecture-review-2026-09.md)
9. [跟随终端当前目录（OSC 7/133、tmux 实测） (09-cwd-tracking.md)](./09-cwd-tracking.md)
10. [长文件扫描与拆分规划 (10-refactor-plan.md)](./10-refactor-plan.md)
11. [双 SSH 引擎（SSH.NET + Tmds.Ssh）可行性评估 (11-dual-ssh-backend.md)](./11-dual-ssh-backend.md)
12. [终端宿主、标签与会话级设置（含批量修改、VT 回调与输入竞态修复） (12-terminal-host-and-session-options.md)](./12-terminal-host-and-session-options.md)
