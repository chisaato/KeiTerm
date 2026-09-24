# 04 - 终端仿真与会话生命周期设计 (Terminal & Lifecycle)

## 1. 核心目标
1. 采用 `RoyalApps.RoyalTerminal.Avalonia` 作为核心呈现组件，确保高性能 Skia 单元格渲染和跨平台表现。
2. 建立标准的**会话桥接适配器（Terminal Bridge）**，将网络层的异步字节流解耦地送入终端控件，并将终端用户键盘输入/鼠标事件回传网络层。
3. 状态与尺寸自适应（Terminal Resize）：
   - 支持窗口缩放时的动态列数/行数与像素计算（SIGWINCH 信号同步）。
4. 多标签页（Tabbed Sessions）生命周期管控：
   - 连接中、就绪、断开重连、心跳保持（Keep-Alive）、退出清理。

---

## 2. 桥接架构设计 (Endpoint Bridge)

`RoyalTerminal` 提供了 `ITerminalEndpoint` 接口。Kei.Term 建立 `SshTerminalEndpoint` 承上启下：

```
+-----------------------------------------------------------+
|                      Avalonia UI                          |
|         TerminalView / TerminalControl (RoyalTerminal)   |
+-----------------------------+-----------------------------+
                              |
                     AttachEndpoint(endpoint)
                              |
                              v
+-----------------------------------------------------------+
|              SshTerminalEndpoint (适配器)                  |
|  - 实现 ITerminalEndpoint                                  |
|  - SendText(ReadOnlySpan<byte>) ----> 写入 SshClient 流    |
|  - SetSize(widthPx, heightPx)   ----> 调用 ShellStream.SendWindowChangeRequest
|  - SetFocus(focused)            ----> 聚焦事件通知         |
+-----------------------------+-----------------------------+
                              ^
                              | 触发 WriteOutput(data)
+-----------------------------+-----------------------------+
|              SSH 网络通道 (ISshSession)                    |
|  - 独立后台读取线程/异步循环                                 |
|  - 收到远端输出 -> 回调触发 TerminalControl.WriteOutput    |
+-----------------------------------------------------------+
```

### 2.1 桥接实现核心伪代码

```csharp
namespace Kei.Term.App.Terminals;

public sealed class SshTerminalEndpoint : ITerminalEndpoint
{
    private readonly ISshSession _sshSession;
    private readonly TerminalControl _terminalControl;

    public SshTerminalEndpoint(ISshSession sshSession, TerminalControl terminalControl)
    {
        _sshSession = sshSession;
        _terminalControl = terminalControl;

        // 监听 SSH 输出，通过 RoyalTerminal 内置线程安全通道压入终端
        _sshSession.OutputReceived += OnSshOutputReceived;
    }

    public void SendText(ReadOnlySpan<byte> utf8)
    {
        // 用户键盘击键/粘贴事件
        _ = _sshSession.SendInputAsync(utf8.ToArray());
    }

    public void SetSize(int widthPx, int heightPx)
    {
        // 动态计算行列号并同步给远程 SSH PTY
        int cols = _terminalControl.Columns;
        int rows = _terminalControl.Rows;
        _ = _sshSession.ResizeTerminalAsync(cols, rows, widthPx, heightPx);
    }

    public void SetFocus(bool focused)
    {
        // 支持 DECSET 1004 Focus Reporting
    }

    private void OnSshOutputReceived(object? sender, byte[] data)
    {
        // TerminalControl.WriteOutput 具备内部 UI 调度队列，后台调用安全
        _terminalControl.WriteOutput(data);
    }
}
```

---

## 3. 会话状态流转图 (Session State Machine)

```
       +--------------+
       |   Created    |
       +-------+------+
               | 用户点击打开
               v
       +--------------+
       |  Connecting  | (解析级联配置、向 Vault 索取凭据、建立 TCP 与握手)
       +-------+------+
               | 认证失败 / 超时
               +--------------------------------->+---------------+
               |                                  |     Error     |
               | 连接成功                         +-------+-------+
               v                                          |
       +--------------+                                   |
       |  Connected   |                                   |
       +-------+------+                                   |
               | 远端退出 / 网络中断 / 手动断开           | 点击重试
               v                                          |
       +--------------+                                   |
       | Disconnected | <---------------------------------+
       +-------+------+
               |
               v 释放资源
       +--------------+
       |   Disposed   |
       +--------------+
```

---

## 4. 多会话与 Tab 管理

在 ViewModel 层，通过集合驱动 Tab 视图：
- `TerminalTabViewModel`：
  - 维护 `Title`（默认为主机别名或 `user@host`）。
  - 维护 `Status`（Connecting, Connected, Disconnected）。
  - 关联当前活动的 `ISshSession` 与 `TerminalControl`。
  - 支持快捷键（`Ctrl+Shift+T` 新建、`Ctrl+W` 关闭当前 Tab）。
- 支持标签页拖拽与分屏（未来扩展点）。

---

## 5. Xshell 风格“预输入/撰写栏”（Compose / Quick Command Input Bar）

### 5.1 业务场景
许多运维工程师在多窗口操作、执行长命令或需要仔细确认命令避免误回车时，极度依赖 Xshell / SecureCRT 的预输入框。
- 允许在终端底部悬浮或固定一个多行/单行编辑框。
- 用户可在其中自由复制、编辑复杂的 shell 脚本/命令，按回车或“发送”按钮一次性推送到当前终端或选中的所有终端（广播模式）。

### 5.2 控件架构设计
```
+-----------------------------------------------------------+
| [Tab 1] [Tab 2]                                           |
+-----------------------------------------------------------+
|                                                           |
|                  TerminalControl (主终端区域)               |
|                                                           |
+-----------------------------------------------------------+
| [预输入栏 Compose Bar] (可通过快捷键 Ctrl+Shift+U 显示/隐藏)   |
| [ 输入要执行的命令 (按 Enter 发送, Shift+Enter 换行)...    ] [发送] |
+-----------------------------------------------------------+
```
- **配置项**：
  - 默认可设为隐藏（可由快捷键唤出，或在设置中勾选常驻显示）。
  - 支持“发送到当前标签”与“广播到所有打开的标签”（高级功能预留）。
- **执行方式**：
  将输入文本按终端当前编码转为 byte 数组后调用 `_sshSession.SendInputAsync()`，末尾追加 `\r`。这样无需侵入终端内部虚拟缓冲区。
