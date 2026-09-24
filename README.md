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
- **无限层级目录树**：经典树形会话目录，支持属性级联继承（端口、用户名、凭据、跳板机）。
- **多 Key 与凭据库 (Vault)**：
  - 凭据与主机解耦，支持密码、私钥明文、本地文件路径及 Agent 映射。
  - 双后端支持：内置主密码保护（Argon2id + AES-256-GCM）或直接托管于 OS 原生密钥环（Keychain / DPAPI / Secret Service）。
  - 支持免密自动静默解锁（在设备环境不变时通过原生 Keyring 缓存解锁凭证）。
  - 支持插件化扩展外部凭据源（如 1Password、Bitwarden、OpenBao 等）。
- **E2EE 云同步**：
  - 零知识端到端加密，全面参考 AGE (Actually Good Encryption) 密码学规范设计（分块流式 AEAD 加密）。
  - 存储介质插件化，支持扩展 WebDAV、S3、自建服务或本地同步。
- **便捷交互体验**：
  - Xshell 风格底部预输入/撰写栏（Compose Bar），支持多行检查与安全发送。
  - 内置 SFTP 文件通道支持（规划中）。

---

## 工程结构

```
KeiTerm.slnx
├── src/
│   ├── Kei.Term.Core/            # 领域模型、级联解析引擎、基础契约接口 (无 UI 依赖)
│   ├── Kei.Term.Infrastructure/  # SQLite 目录树仓储、加密套件、OS Keyring 适配
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

# 运行桌面客户端
dotnet run --project src/Kei.Term.App/Kei.Term.App.csproj
```
