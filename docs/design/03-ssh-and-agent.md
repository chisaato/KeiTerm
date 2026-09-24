# 03 - SSH 通信与 Agent / SK 密钥架构 (SSH & Agent Integration)

## 1. 核心诉求与边界
1. **统一的 SSH 客户端连接通道**：统一处理密码认证、普通密钥（RSA / Ed25519 / ECDSA）认证，以及通过 SSH Agent 认证。
2. **SK（Security Key）硬件密钥代理**：
   - 依赖 `SshNet.Agent` 对接系统的 SSH Agent。
   - 包含 `sk-ssh-ed25519@openssh.com` 与 `sk-ecdsa-sha2-nistp256@openssh.com` 格式。
   - 硬件交互（PIN 码输入、触碰确认 Touch）完全交由底层 SSH Agent（或系统 WebAuthn 框架）驱动，Kei.Term 不直接操作底层 USB HID / CTAP2 协议，保证安全与跨平台一致性。
3. **跨平台 Agent 协议通道**：
   - **Linux / macOS**：Unix Domain Socket（默认读取环境变量 `SSH_AUTH_SOCK`，支持 1Password / KeePassXC / GPG Agent / 系统 OpenSSH Agent）。
   - **Windows**：
     - OpenSSH for Windows: Named Pipe `\\.\pipe\openssh-ssh-agent`。
     - PuTTY / Pageant: Named Pipe 或经典 `WM_COPYDATA` 消息通信。
4. **会话生命周期与 RoyalTerminal 桥接**：提供统一的 `ITerminalEndpoint` 桥梁，把远程数据流安全送到 `RoyalApps.RoyalTerminal` 的 VT 解析与渲染管道中。

---

## 2. 核心架构设计

```
+-------------------------------------------------------------+
|               Kei.Term.Ssh 连接工厂 (ISshSession)           |
+-------------------------------------------------------------+
                              |
      +-----------------------+-----------------------+
      |                                               |
      v                                               v
[ 直接凭据认证 ]                               [ Agent 代理认证 ]
  - 密码: PasswordAuthenticationMethod           - SshNet.Agent (SshAgent / Pageant)
  - 普通私钥: PrivateKeyAuthenticationMethod       - 查询可用标识 (RequestIdentities)
                                                 - 支持普通密钥及 SK 密钥:
                                                   * sk-ssh-ed25519@openssh.com
                                                   * sk-ecdsa-sha2-nistp256@openssh.com
                                                 - 触发硬件触碰/PIN (由 Agent 弹窗)
                                      |
                                      v
                      +-------------------------------+
                      |   Renci.SshNet.SshClient      |
                      |   - 握手、协商 Cipher/KEX     |
                      |   - 开启 ShellStream 交互通道 |
                      +---------------+---------------+
                                      |
                                      v
                      +-------------------------------+
                      |    SshTerminalBridge          |
                      | (实现 ITerminalEndpoint)      |
                      +---------------+---------------+
                                      |
                                      v
                      +-------------------------------+
                      | RoyalTerminal (TerminalControl|
                      |  - VT 解析与 Skia 渲染        |
                      +-------------------------------+
```

---

## 3. 核心接口与服务契约

```csharp
namespace Kei.Term.Ssh.Abstractions;

public interface ISshSessionFactory
{
    /// <summary>
    /// 根据解析后的会话配置与获取到的机密，构建可运行的 SSH 会话实例
    /// </summary>
    Task<ISshSession> CreateSessionAsync(
        ResolvedSessionConfig config,
        SecretPayload? secret,
        CancellationToken ct = default);
}

public interface ISshSession : IAsyncDisposable
{
    Guid SessionId { get; }
    bool IsConnected { get; }
    
    event EventHandler<byte[]> OutputReceived;
    event EventHandler<Exception> Disconnected;
    
    Task ConnectAsync(CancellationToken ct = default);
    Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task ResizeTerminalAsync(int columns, int rows, int widthPx, int heightPx, CancellationToken ct = default);
    
    /// <summary>
    /// 创建并关联绑定的 SFTP 客户端信道
    /// </summary>
    Task<ISftpChannel> OpenSftpChannelAsync(CancellationToken ct = default);
}
```

---

## 4. SSH-Agent 探测与跨平台通道适配

```csharp
namespace Kei.Term.Ssh.Agent;

public interface ISshAgentDetector
{
    bool IsAgentAvailable();
    AgentTransportType DetectActiveTransport();
    IEnumerable<AgentIdentityInfo> ListIdentities();
}

public enum AgentTransportType
{
    None = 0,
    UnixDomainSocket = 1,       // Linux / macOS $SSH_AUTH_SOCK, 1Password, KeePassXC
    WindowsOpenSshNamedPipe = 2, // \\.\pipe\openssh-ssh-agent
    WindowsPageant = 3           // PuTTY Pageant
}
```

### 4.1 探测流程
1. **Windows 平台**：
   - 首先探测 `\\.\pipe\openssh-ssh-agent` 是否存在。如果存在且可读写，则优先选用 Windows OpenSSH Named Pipe。
   - 其次探测是否存在 Pageant 窗口（`FindWindow("Pageant", "Pageant")`）或 Pageant 命名管道。
   - 若用户配置了自定义 Socket 路径（如 WSL 桥接），允许覆盖系统探测。
2. **Linux / macOS 平台**：
   - 检测环境变量 `SSH_AUTH_SOCK` 指向的文件是否为有效 Unix Domain Socket。
   - 若有效，直接初始化 `SshAgent(path)`。

### 4.2 SK 密钥认证流程说明
- 当调用 `agent.RequestIdentities()` 时，`SshNet.Agent` 返回的列表中可能包含 `SshAgentPrivateKey`（其内部标识算法为 `sk-ssh-ed25519@openssh.com` 或 `sk-ecdsa-sha2-nistp256@openssh.com`）。
- 该类型的私钥数据本身不出安全硬件。在 SSH 握手签名阶段：
  1. SSH.NET 发起签名请求。
  2. `SshNet.Agent` 将待签名哈希包装为 Agent 协议报文发往 Agent 管道。
  3. Agent 驱动安全硬件（此时硬件闪烁，提示用户触摸或弹出系统 PIN 输入窗口）。
  4. 用户完成触碰后，Agent 返回签名结果，完成 SSH 连接。
- **Kei.Term 无需直接管理 FIDO2 驱动，全面避免了跨平台 USB 访问权限与驱动不一致的问题。**
