# 12 - 终端宿主、标签与会话级设置（2026-09-29）

> 本轮落地：回滚滚动条、本地清屏、网格贴底、标签标题跟随远端、标签右键菜单与原地重连、批量修改会话。
> 顺带修复两处底层缺陷：终端查询无应答、SSH 输入字节重复。所有结论均在 Headless（Skia 实绘）+ 本机 sshd + tmux 3.4 上实测。

---

## 1. 终端宿主

### 1.1 滚动条

`TerminalControl` 实现 `ILogicalScrollable`，外包一层 `ScrollViewer`（`VerticalScrollBarVisibility=Visible`、`AllowAutoHide=False`）即得回滚滚动条——与 RoyalTerminal 自带的 `TerminalPaneLayout.CreatePaneScrollViewer` 同一做法。
常驻显示而非 Auto：Auto 会在首屏写满时出现滚动条、挤窄列数触发重排。

### 1.2 「tmux / byobu 下面多一行」

实测 PTY 行数（`stty size`）与渲染行数一致，**不是尺寸同步错误**。控件按 `floor(高度 / 行高)` 取行数，不足一行的余量默认留在底部；
行高 22 px、区域 505 px 时余量 21 px，看上去就是状态栏下方空出一整行。

处理：`TerminalGridAlignment.TopInset` 把余量设为控件顶部内边距（少留 0.01 px 防浮点误差少算一行），网格整体贴底；
余量区域由外层 `Border` 用配色背景填充。控件的指针命中已扣除内边距（`TryTranslatePointToTerminalContent`），选区不受影响。

### 1.3 清屏

旧实现向远端发送 `clear\r`：会把命令敲进 vim 等全屏程序、冲掉半行输入，且回滚缓冲区仍在（"清屏后还能滚"）。
现改为纯本地操作：`ClearHistory()`（保留光标所在行）+ `ClearScrollback()`（备用屏幕下前者不动主屏，仍需清主屏回滚）。另提供「仅清除回滚缓冲区」。

### 1.4 VT 处理器回调（DA / DSR / BEL / 标题）

RoyalTerminal 只在**控件自带传输层**（`StartSessionAsync`）时给 VT 处理器设置 `ResponseCallback` / `BellCallback` / `TitleCallback`。
KeiTerm 经 `AttachEndpoint` 接入自有 SSH 会话，三者一直为空，后果：

| 缺失 | 影响 |
|---|---|
| 应答 | 远端的终端查询（DA1/DA2、DSR 光标位置、OSC 10/11 颜色）石沉大海；fish 4 等启动时等待 DA 应答的程序会卡顿 |
| BEL | 响铃被丢弃，无法做后台标签活动提示 |
| 标题 | `TitleChanged` 事件永不触发，OSC 0/2 标题无从获取 |

`TerminalControlFactory` 用 `HookingVtProcessorFactory` 包装默认处理器工厂（取自一个默认构造的原型控件，保留 Kitty 图形等内部配置），
在每个处理器创建时挂上回调；应答直接写回会话，响铃与标题投递回 UI 线程。OSC 7 / 133 走 `ShellIntegrationEventReceived`，两种接入方式均已挂接，不受影响。

### 1.5 SSH 输入字节重复（并发 Flush）

接上应答后，tmux 面板里出现 `^[[?62;1;6;22c^[[>1;10;0c` 乱码。排查过程：

1. 记录发出的应答：每条只发一次，时序正常（查询后 25 ms）。
2. Python 伪终端直连 tmux、经 bash 启动 tmux、加 20–300 ms 延迟：均**无法复现**——tmux 本身能正确吃掉这些应答。
3. 差异在 KeiTerm 的发送路径：`SshNetSession.SendInputAsync` 为 `WriteAsync` + `FlushAsync`，而 SSH.NET `ShellStream` 未重写 `FlushAsync`，
   沿用 `Stream` 基类实现——**在线程池上执行 `Flush()`**。`Write`/`Flush` 均不加锁，连续几次发送会让多个 `Flush` 并发读写同一写缓冲区，同一段字节被发出两次。
   tmux 收到第一份 DA 应答后置位 `TTY_HAVEDA`，第二份不再识别，当作按键转进面板。

修复：会话内单消费者输入队列（`Channel`），按序同步 `Write` + `Flush`；调用方仍可"发出即不管"。
同一竞态也作用于键盘输入（快速连续按键偶发重复字符）。集成测试以"每批 4 段连发、批间 1 ms"的节奏重复 6 轮，旧实现 3/3 失败、新实现 3/3 通过。

## 2. 标签

| 能力 | 说明 |
|---|---|
| 标题 | `Title = FollowRemoteTitle && 远端标题非空 ? 远端标题 : TabName`；远端标题去控制字符、截断 256 字符；远端清空标题即回退会话名 |
| 标题跟随开关 | 全局默认（设置 › 终端 › 会话行为，默认开）→ 会话覆盖（继承 / 跟随 / 固定会话名）→ 标签右键临时切换 |
| 重命名 | 只改本标签；重命名后停止跟随远端标题；重连不会冲掉 |
| 活动标记 | 后台标签有输出或 BEL：标题斜体 + 强调色圆点；切到该标签清除。输出回调只在状态需要翻转时投递 UI 线程 |
| 右键菜单 | 重新连接（原地，保留终端历史，打印分隔行）/ 断开 / 克隆会话（新标签）/ 重命名 / 标题跟随远端 / 关闭 / 关闭其他 / 关闭右侧；中键关闭 |
| 悬停提示 | 完整标题、会话名（与标题不同时）、`user@host:port`、状态信息 |

原地重连经 `ConnectionRequest.ReuseTarget`：认证材料收集**之后**才复位旧标签，用户取消认证时旧标签保持原状。
已保存会话按最新会话配置重新解析；快速连接等无节点的标签沿用原配置并重新询问认证（不在标签上保留密码）。

注意：Debian/Ubuntu 默认 bashrc 会在每个提示符把标题设为 `user@host: 目录`，开启跟随后标签显示的就是它（与 SecureCRT 一致）；需要固定会话名的会话可单独覆盖。

## 3. 会话级行为覆盖（SessionOverrides）

```csharp
public sealed class SessionOverrides     // 每项 null = 继承全局设置
{
    public bool? FollowRemoteTitle { get; set; }
    public CwdFollowMode? CwdFollow { get; set; }
}
```

- 以 JSON 整体存于 `session_details.options_json`（迁移 v3）；枚举存名称，null 项不落盘，全部继承时列为 NULL。
- 损坏或来自更新版本的未知值不会让整棵树加载失败：解析失败按全部继承处理。
- 解析在 `SessionConfigBuilder`（会话值 → 全局设置），结果进入 `ResolvedSessionConfig`。
- **新增覆盖项**：`SessionOverrides` 加可空属性 → `AppSettings` 加全局默认 → `SessionConfigBuilder` 解析 → 会话编辑窗口与设置页各加一个下拉（下拉项统一取自 `SessionBehaviorOptions`）。**无需迁移**。
- 适合放这里的：Agent 转发三态（见 [11](./11-dual-ssh-backend.md)）、会话级回滚行数、保活覆盖等"可继承的行为开关"；
  协议参数（串口波特率等）仍按 [10 §6](./10-refactor-plan.md) 放 `session_protocol_settings`。

## 4. 批量修改会话

入口：工具 › 批量修改会话；会话树右键 › 批量修改（按当前选中项预勾选，文件夹 = 其下全部层级会话）。

```
Core  SessionBatchField          怎么读、怎么写（Key + getter/setter），无界面信息
      SessionBatchFields.All     已登记字段：身份、用户名、标签标题、目录跟随
      SessionBatchEditor         Summarize（混合/一致）、Apply（返回实际变化的会话）
App   BatchFieldDefinition       标签 + 编辑器形态（Choice / Text）+ 选项生成
      BatchSessionEditViewModel  勾选会话（筛选、按文件夹、勾选可见）+ 勾选字段 + 应用
Infra ITreeRepository.SaveNodesAsync  单事务：全部成功或全部不生效
```

- 只有勾选「修改」的字段才写入；编辑了值会自动勾选。每个字段下显示所选会话的现状（一致值 / 多个不同的值）。
- 值本来就相同的会话不计入变更，不保存、不刷新 `updated_at`。
- 保存失败：窗口保持打开并显示原因；关闭后主窗口从库重载会话树，丢弃内存中未落盘的修改。
- 已打开的标签不受影响，重连后生效。

**新增批量字段**（例：端口、终端类型、跳板机、配色）：

1. Core：`SessionBatchFields` 加一项 `new SessionBatchField<T>("key", n => …, (n, v) => …)` 并放入 `All`；
2. App：`BatchFieldDefinitions.All` 加一项（`Label`、`Kind`、`Choices` 或 `Placeholder`）。

窗口按定义自动生成编辑行。数值类（如端口）可复用 Text 形态，后续若需要校验再加 `BatchEditorKind.Number` 与定义上的 `Validate` 委托。

## 5. 测试

- Headless（`Avalonia.Headless` + Skia，xunit v2 下经自定义 `TestFramework` 在运行前预热，保证 `Dispatcher.UIThread` 归属 Headless 会话线程）：
  网格贴底、清屏后无可滚动内容、回滚行数上限、OSC 2 → 标签标题、后台 BEL → 活动标记、DSR 查询得到应答。
- 纯逻辑：贴底余量、标题规则、覆盖项解析与持久化（含损坏 JSON）、批量编辑 Core 与 ViewModel、`SaveNodesAsync` 原子性、设置保存保留未编辑字段。
- sshd 集成：连续未等待输入恰好一次且有序。
