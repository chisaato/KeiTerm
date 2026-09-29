# 10 - 长文件扫描与拆分规划（2026-09-29）

> 目标：在「端口转发 / 自动重连 / 目录跟随 / 多协议」这些功能落地**之前**把承载点拆开，
> 否则它们都会自然地堆进 `MainViewModel` 与 `RemoteFileManagerViewModel`。

## 0. 进度（2026-09-29）

| 步骤 | 状态 | 结果 |
|---|---|---|
| 1. `IInteractionService` | ✅ | 15 个 `Func<>` 弹窗属性 → 一个接口；`MainWindowInteractionService` 承接原 `WireDialogs`（MainWindow.axaml.cs 909 → 736 行） |
| 2. `VaultSessionService` | ✅ | 懒解锁 / 材料读写 / 口令缓存 / 空闲锁定判定；6 项测试 |
| 3. `ConnectionOrchestrator` | ✅ | 拆为 `AuthMaterialCollector`（用什么认证）+ `ConnectionOrchestrator`（怎么连）+ `IConnectionHost/IConnectionTarget`；13 项编排测试；顺带修复 OpenSSH 加密私钥误判为无口令 |
| 4. `SessionTreeViewModel` | ⏳ 下一步 | MainViewModel 现 1180 行，其中树 ~410 行、导入 ~130 行；标签动作与批量修改入口已放在 `MainViewModel.TabActions.cs` / `.BatchEdit.cs`，拆树时一并迁出 |
| 5. MainWindow 拖放控制器 | ⏳ | |
| 6. `RemoteFileManagerViewModel` 拆分 | ⏳ | 目录跟随之前完成 |
| — `TerminalTabViewModel` | ✅ | 标签功能加入后达 790 行，按职责拆为主体（会话生命周期 / 文件侧栏，357）+ `.Header.cs`（标题 / 活动 / 右键动作）+ `.Control.cs`（控件 / VT 回调 / 字体配色 / 贴底）；`ITerminalThemeSink` 独立文件 |

以下为原始规划（行号为拆分前）。

---

## 1. 扫描结果

| 文件 | 行数 | 判断 |
|---|---|---|
| `App/ViewModels/MainViewModel.cs` | **2052** | 🔴 必须拆：6 类职责、77 个方法、16 个 `Func<>` 弹窗委托 |
| `App/Views/MainWindow.axaml.cs` | **909** | 🔴 建议拆：弹窗装配 200 行 + 树拖放 200 行 + 标签拖放 150 行 |
| `App/ViewModels/RemoteFileManagerViewModel.cs` | 747 | 🟠 目录跟随落地前拆：导航 / 传输队列 / 外部编辑三块 |
| `App/ViewModels/SettingsViewModel.cs` | 675 | 🟡 可接受：主要是"预览-应用-取消"事务逻辑，已有充分测试；继续增长时抽 `SettingsTransaction` |
| `App/ViewModels/TerminalTabViewModel.cs` | 494 | 🟡 目录跟随会往这里加订阅，届时把终端外观应用（~120 行）移出 |
| `App/Services/ProfileManagerService.cs`、`Core/Services/LocalFileTracker.cs`、`Infrastructure/Vault/InternalVaultManager.cs` | 430～490 | 🟢 单一职责，长度来自业务本身 |

## 2. `MainViewModel` 职责地图（按当前行号）

| 行号 | 职责 | 约行数 | 拆到 |
|---|---|---|---|
| 199–383 | 构造、终端配色/字体变更广播到各标签 | 185 | `TerminalAppearanceCoordinator` |
| 384–444, 1499–1736 | Vault 懒解锁、自动锁定计时、身份材料读写、口令会话缓存 | 300 | `VaultSessionService` |
| 445–853 | 会话树：加载/建树/筛选、增删改、复制剪切粘贴、拖放移动、折叠 | 410 | `SessionTreeViewModel`（树面板子 VM） |
| 854–1031 | 各类对话框入口与导入（SecureCRT / Konsole / ssh_config / known_hosts） | 180 | 导入进 `SessionTreeViewModel`；对话框入口留在主 VM |
| 1032–1947 | **连接管线**：快速连接、身份解析、认证计划与物化、跳板物化、建连选项、重试循环、主机密钥确认、KI 回调、文件侧栏装配 | **915** | `ConnectionOrchestrator` |
| 1948–2052 | 标签排序/关闭、撰写栏、面板开关 | 105 | 留在 `MainViewModel` |

拆完后 `MainViewModel` 预计 ~350 行，只做组合与绑定。

### 2.1 关键接口

```csharp
// 取代 MainViewModel 上 16 个 Func<> 属性：一处实现（Avalonia），测试用假实现
public interface IInteractionService
{
    Task<AuthPromptResult?> PromptAuthAsync(string username, IReadOnlyList<VaultKeyOption> keys, AuthPromptMethod? preferred);
    Task<string?> PromptKeyboardInteractiveAsync(string prompt);
    Task<PassphrasePromptResult?> PromptPassphraseAsync(FilePrivateKeyMethod method);
    Task<string?> PromptMasterPasswordAsync(string? error);
    Task NotifyAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
    // 会话/文件夹编辑、文件选择等同理
}

// 连接编排：无 Avalonia 依赖，可对"认证失败 → 回弹 → 重试 / 主机密钥确认 → 重连"写单元测试
public sealed class ConnectionOrchestrator
{
    ConnectionOrchestrator(ISshSessionFactory, HostKeyTrustService, VaultSessionService, IIdentityRepository,
                           ISettingsService, IInteractionService, ILogger);

    // host 负责 UI 线程上的"建标签 / 挂载会话 / 报错"，编排器只驱动流程
    Task ConnectAsync(ResolvedSessionConfig config, IConnectionHost host, CancellationToken ct);
}
```

> 现状：连接管线（915 行）**没有任何单元测试**——它依赖 Avalonia 的 `Dispatcher` 与窗口委托。
> 拆出后第一件事就是补"重试次数上限 / 取消即中止 / 主机密钥确认后重连 / 文件通道用最终材料"这几条测试。

## 3. `MainWindow.axaml.cs`

| 块 | 拆到 |
|---|---|
| `WireDialogs`（200 行） | `AvaloniaInteractionService : IInteractionService` |
| 树拖放（`Tree_Pointer*` / `Tree_Drag*` / `Tree_Drop`，~200 行） | `TreeDragDropController`（附加到 TreeView 的行为类） |
| 标签拖放（`Tab_*`，~150 行） | `TabDragController` |
| 关闭确认、主题画刷、侧栏列宽 | 留在窗口 |

## 4. `RemoteFileManagerViewModel`

| 块 | 拆到 |
|---|---|
| 上传/下载执行、进度、队列状态（`Execute*TaskAsync`、`UpdateOverallTransferringState`） | `FileTransferQueue`（Core 可测：并发度、取消、失败重试） |
| 外部编辑与回传（`OpenWith*`、`StopTracking*`、`OnTrackedFileChanged`） | `ExternalEditSession` |
| 导航、列表、删除 | 留在 VM；目录跟随（[09](./09-cwd-tracking.md)）接在这里 |

## 5. 顺序与约束

1. `IInteractionService`（纯机械迁移，无行为变化）
2. `VaultSessionService`
3. `ConnectionOrchestrator` + 补测试 ← **做端口转发 / 自动重连之前完成**
4. `SessionTreeViewModel`
5. `MainWindow` 两个拖放控制器
6. `RemoteFileManagerViewModel` 拆分 ← **做目录跟随之前完成**

每一步单独提交，只搬代码不改行为，靠现有测试 + 手工冒烟（连接、认证失败回弹、树拖放、标签拖放）确认。

建议写入 AGENTS.md 的软约束（不做成失败的测试，避免"为长度而拆"）：

- 新增弹窗一律走 `IInteractionService`，不再往 ViewModel 上加 `Func<>` 属性。
- ViewModel 超过约 600 行时，先按职责拆分再加新功能。

## 6. 多协议扩展点（本轮已完成的部分与待做）

已完成：`Core.Abstractions.ITerminalSession`（标签、端点、撰写栏只依赖它）、`ISshSession : ITerminalSession`、`SessionProtocols` 常量。

待做（接入本地 Shell / 串口 / Telnet 时）：

```csharp
// 每种协议一个提供者；ConnectionOrchestrator 只处理 ssh，其余协议走各自提供者
public interface ITerminalSessionProvider
{
    string Protocol { get; }                     // SessionProtocols.*
    Task<ITerminalSession> OpenAsync(SessionNode node, IInteractionService ui, CancellationToken ct);
}
```

- 本地 Shell / 串口 / Telnet 直接包装 RoyalTerminal 自带的 `ITerminalTransport`（`PtyTerminalTransport` 等，已确认接口为
  `StartAsync / SendInput / Resize / OutputLeaseCallback / StopAsync`），写一个通用 `RoyalTransportSession : ITerminalSession` 即可。
- 持久化：协议参数不再给每种协议建 `session_details_*` 表，而是新增一张 `session_protocol_settings(node_id, settings_json)`，
  用与 `methods_json` 相同的 `$kind` 多态 JSON（`local/pty`、`serial/port`、`telnet/tcp`），加协议零 DDL。
- 文件侧栏对非 SSH 协议隐藏（`ITerminalSession` 不提供文件通道）；撰写栏、广播、录制天然适用于所有协议。
