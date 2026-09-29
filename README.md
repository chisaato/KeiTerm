# KeiTerm (Kei.Term)

基于 AvaloniaUI 打造的跨平台现代 SSH 终端与凭据会话管理器。

---

## 特性

- **跨平台支持**：支持 Windows、Linux 与 macOS。
- **现代终端仿真**：基于 `RoyalApps.RoyalTerminal.Avalonia` 原生渲染，支持高帧率 Skia 渲染、PTY 与动态尺寸协商。
- **SK 系列硬件密钥**：原生打通系统级 SSH-Agent，支持 `ed25519-sk` / `ecdsa-sk` 硬件密钥认证（触碰与 PIN 交互）。
- **多种 ssh-agent 协议兼容**：
  - Windows: OpenSSH Named Pipe (`\\.\pipe\openssh-ssh-agent`)、PuTTY Pageant。
  - Linux / macOS: Unix Domain Socket (`$SSH_AUTH_SOCK`，完美兼容 1Password / KeePassXC / GPG Agent / 系统 OpenSSH)。
- **无限层级目录树**：经典树形会话目录，拖拽排序、复制/剪切，会话级配置回退全局默认。
- **主机密钥信任库**：首次连接记录、密钥变更强阻断（防中间人），兼容导入 OpenSSH `known_hosts`（含哈希条目）。
- **多级跳板机（ProxyJump）**：跳板本身即普通会话，逐跳独立身份认证；文件通道复用跳板链。
- **OpenSSH 生态互通**：一键导入 `~/.ssh/config`（Host / IdentityFile / ProxyJump / Include）。
- **多 Key 与凭据库 (Vault)**：
  - 凭据与主机解耦，支持密码、私钥明文、本地文件路径及 Agent 映射。
  - 可选主密码保护（PBKDF2-SHA512 + AES-256-GCM，KDF 已版本化，可平滑切换 Argon2id）。
  - OS 原生密钥环静默解锁、外部凭据源（1Password、Bitwarden、OpenBao 等）（规划中）。
- **E2EE 云同步（规划中）**：
  - 零知识端到端加密，全面参考 AGE (Actually Good Encryption) 密码学规范设计（分块流式 AEAD 加密）。
  - 存储介质插件化，支持扩展 WebDAV、S3、自建服务或本地同步。
- **便捷交互体验**：
  - Xshell 风格底部预输入/撰写栏（Compose Bar），支持多行检查与安全发送。
  - 内置 SFTP / SCP 文件侧栏，外部编辑器编辑后自动回传。

---

## 工程结构

```
KeiTerm.slnx
├── src/
│   ├── Kei.Term.Core/            # 领域模型、级联解析引擎、基础契约接口 (无 UI 依赖)
│   ├── Kei.Term.Infrastructure/  # SQLite 仓储（Dapper + 版本化迁移）、Vault 加密
│   ├── Kei.Term.Ssh/             # SSH.NET 通信封装、SshNet.Agent 集成、Shell 交互
│   └── Kei.Term.App/             # AvaloniaUI 宿主应用、MVVM 视图模型、终端桥接
├── tests/
│   └── Kei.Term.Tests/           # 单元测试 (级联解析器、SQLite 仓储 CRUD、API 校验)
└── docs/
    └── design/                   # 各模块架构设计规范详细文档
```

---

## 快速开发与构建

### 前置要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (或更高版本)

### 构建与测试

```bash
# 还原与编译整个解决方案
dotnet build Kei.Term.slnx

# 运行单元测试
dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj

# 可选：针对本机 sshd 的集成测试（Linux，需要 /usr/sbin/sshd 与 ssh-keygen）
KEITERM_SSHD_TESTS=1 dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj

# 运行桌面客户端
dotnet run --project src/Kei.Term.App/Kei.Term.App.csproj
```
