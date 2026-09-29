# 11 - 双 SSH 引擎（SSH.NET + Tmds.Ssh）可行性评估

> 状态：**可行**，待 Tmds.Ssh 发布包含 agent 转发的版本后接入（2026-09-29 评估）。
> 目标：默认继续使用 SSH.NET；会话勾选「Agent 转发」时改用 Tmds.Ssh。

---

## 1. 结论

| 问题 | 结论 | 依据 |
|---|---|---|
| 两个库能否共存于同一进程 | ✅ | 依赖完全一致（BouncyCastle.Cryptography 2.7.0、Microsoft.Bcl.Cryptography、Logging.Abstractions），命名空间不冲突（`Renci.SshNet` / `Tmds.Ssh`）；实测同进程加载并连接成功 |
| Tmds.Ssh 的 agent 转发是否可用 | ✅（主干） | 以主干 `5e960e3`（2026-09-25）构建，对本机 sshd 实测：`ForwardAgent=false` 时远端 `ssh-add -l` 报无法连接 agent；`true` 时远端 `SSH_AUTH_SOCK` 存在且列出本机 ED25519 密钥 |
| 发布状态 | ⏳ | NuGet 最新 0.24.0（2026-08-18）不含 `ForwardAgent`；PR #510 于 2026-09-17 合并 |
| 安装体积 | +约 720 KB | `Tmds.Ssh.dll` 739 KB（主干 Release 构建） |

## 2. 能力映射（Kei 概念 → Tmds.Ssh）

| Kei | Tmds.Ssh | 说明 |
|---|---|---|
| 密码材料 | `PasswordCredential(string)` | |
| Vault / 文件私钥（内存明文 + 口令） | `PrivateKeyCredential(char[] rawKey, string? password)` | 支持内存中的私钥内容，与 Vault 物化结果直接对应 |
| Agent 方法 | `SshAgentCredentials()` | 只连默认 agent：`SSH_AUTH_SOCK`，Windows 下为 `openssh-ssh-agent` 命名管道。**不支持 Pageant**；自定义 socket 路径的构造函数为 internal |
| SK 硬件密钥（经 agent） | agent 认证不按算法过滤（源码注释：允许"可能经 SSH Agent 可用的算法"） | 理论可行，**需 YubiKey 实机验证**（AGENTS.md 硬约束） |
| keyboard-interactive（2FA / OTP） | ❌ **不支持**（源码 `SshConfigParser`：*keyboard-interactive auth is not supported*） | 最大缺口，见 §3 |
| 主机密钥校验 | `HostAuthentication` 异步委托；`ServerKey.Key.ToString()` 输出 `ssh-ed25519 AAAA…`（实测） | 可还原公钥 blob 构造 `PresentedHostKey`，复用现有 `IHostKeyVerifier` 与信任库；把 `UserKnownHostsFilePaths` / `GlobalKnownHostsFilePaths` 置空，让 Kei 信任库成为唯一来源 |
| 主机密钥算法偏好 | `ServerHostKeyAlgorithms`（`AlgorithmList : IList<string>`） | 可按已记录类型重排，与 SSH.NET 侧同一套 `HostKeyAlgorithmPreference.Order` |
| 跳板链 | `Proxy.Chain(new SshProxy(SshClientSettings), …)` | 每跳独立的凭据与主机密钥校验；通道内承载，**无需**回环临时端口（优于 SSH.NET 方案） |
| 终端 | `ExecuteShellAsync(new ExecuteOptions { AllocateTerminal, TerminalType, TerminalWidth, TerminalHeight })` → `RemoteProcess` | `WriteAsync` / `ReadAsync` / `SetTerminalSize`（实测改变 `tput cols`） |
| 文件侧栏 SFTP | `SshClient.OpenSftpClientAsync()` | **复用同一连接**（实测列出 `/`），不再二次登录 |
| SCP 模式 | 无 SCP 客户端 | 走 Tmds 引擎的会话，文件侧栏统一用 SFTP |
| 保活 | `KeepAliveInterval` / `KeepAliveCountMax` | |
| 会话环境变量 | `ExecuteOptions.EnvironmentVariables` | 需服务器 `AcceptEnv` 放行（实测未放行时不生效，属正常） |
| 附带能力 | `AutoReconnect`、X11 转发（主干 2026-09-25）、本地/远程/SOCKS 转发 | |

## 3. 引擎选择规则

```
会话启用 Agent 转发？
 ├─ 否 → SSH.NET（现状，全部能力）
 └─ 是 → 身份方法里是否只能靠 keyboard-interactive（交互式方法 / 仅密码且服务器只开 KI）？
          ├─ 否 → Tmds.Ssh（密码 / 私钥 / Agent / 跳板 / SFTP 均可）
          └─ 是 → 仍用 SSH.NET，不转发，并在标签上提示「此会话需要交互式认证，Agent 转发不可用」
```

- 判断时机在**认证物化之后、建连之前**（此时已知材料种类），不在连接失败后回退，避免对服务器多次失败尝试。
- 另留一个高级设置「SSH 引擎：自动 / 强制 SSH.NET / 强制 Tmds.Ssh」，便于排障与日后评估整体迁移。

## 4. 代码落点（接入时）

本轮重构后，所有 SSH 细节已收敛在 `Kei.Term.Ssh`，App 与 Core 只见 `ISshSessionFactory` / `ISshSession` / `IRemoteFileSystem` / `IHostKeyVerifier`：

```
Kei.Term.Ssh/
├── Services/SshSessionFactory.cs     # 变为路由：按 SshConnectOptions.ForwardAgent / 引擎设置选择后端
├── SshNet/…                          # 现有 SshDialer / SshNetSession / SftpRemoteFileSystem / ScpRemoteFileSystem（搬目录，不改逻辑）
└── Tmds/
    ├── TmdsSettingsBuilder.cs        # 物化材料 + 选项 → SshClientSettings（含跳板链、主机密钥委托、算法偏好）
    ├── TmdsSshSession.cs             # ISshSession：ExecuteShellAsync + 读循环 + SetTerminalSize
    └── TmdsSftpRemoteFileSystem.cs   # IRemoteFileSystem：基于同连接的 SftpClient
```

- `SshConnectOptions` 增加 `ForwardAgent`（与 `AgentSocketPath` 并列）；会话模型增加「Agent 转发：继承全局 / 开 / 关」三态，全局默认关。
- 安全提示：开启转发即意味着服务器管理员在会话期间可使用你的 agent（Tmds 文档同样强调），设置处需写明「仅对信任的服务器开启」。

## 5. 测试

- 现有 sshd 集成测试参数化为两个引擎各跑一遍（主机密钥 TOFU / 变更阻断 / 算法偏好 / 两跳跳板 + 登录脚本 + SFTP）。
- 新增：`ForwardAgent` 端到端（启动 `ssh-agent` 并 `ssh-add`，远端执行 `ssh-add -l` 断言列出密钥）——即本评估所用的实验。
- 选择规则（§3）为纯逻辑，放 Core 单测。

## 6. 风险

| 风险 | 缓解 |
|---|---|
| Tmds.Ssh 为 0.x，API 可能破坏性变更 | 适配代码集中在 `Kei.Term.Ssh/Tmds/`；锁定版本，升级时跑双引擎集成测试 |
| 两套实现的行为差异（错误信息、超时语义） | 统一映射为 `HostKeyRejectedException` / 认证失败类型，UI 不感知引擎 |
| 无 keyboard-interactive | §3 规则自动回落 SSH.NET 并提示 |
| 无 Pageant / 自定义 agent 路径 | 转发会话要求使用默认 agent；设置页注明 |
| SK 密钥经 agent 未实测 | 接入前用 YubiKey 实机验证，作为发布门槛 |

## 7. 复现实验

```bash
git clone --depth 1 https://github.com/tmds/Tmds.Ssh && cd Tmds.Ssh
dotnet build src/Tmds.Ssh/Tmds.Ssh.csproj -c Release -p:TargetFrameworks=net10.0 -o ../tmds-main
eval $(ssh-agent -s) && ssh-add ~/.ssh/id_ed25519
# SshClientSettings { Credentials = [new SshAgentCredentials()], ForwardAgent = true }
# ExecuteShellAsync(AllocateTerminal) 后写入: echo $SSH_AUTH_SOCK; ssh-add -l
```
