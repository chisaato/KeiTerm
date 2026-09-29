# 09 - 跟随终端当前目录（CWD Tracking）设计草案

> 状态：待讨论（2026-09-29）。本文先讲清楚原理，再给出分层方案与需要拍板的问题。
> 文中 tmux 行为均为在 tmux 3.4 + zsh 5.9 上的**实测结果**（实验脚本见 §7）。

---

## 1. 背景知识：终端是怎么"知道"目录的

终端（KeiTerm、MobaXterm、iTerm2……）只看得到一条字节流：远端程序往 PTY 写什么，终端就收到什么。
终端**无法**直接问远端 shell"你现在在哪个目录"——SSH 协议里没有这个概念。
所以只有两条路：

1. **远端主动告诉终端**：shell 在每次显示提示符前，往输出里夹带一段"不可见的控制序列"，终端解析后不显示，只拿来更新状态。
2. **终端另开通道去问**：通过 SSH exec 通道执行命令（如读 `/proc/<pid>/cwd`），但这需要知道是哪个进程，比较脆弱。

### 1.1 什么是 OSC

控制序列里有一类叫 **OSC（Operating System Command）**，格式是：

```
ESC ] <编号> ; <内容> BEL          即字节  1B 5D ... 07
ESC ] <编号> ; <内容> ESC \        另一种结束符（ST）
```

常见编号：

| 序列 | 含义 | 谁在用 |
|---|---|---|
| `OSC 0` / `OSC 2` | 设置窗口/标签标题 | 几乎所有 shell 与 tmux |
| `OSC 7;file://主机/路径` | **报告当前目录**（含主机名） | macOS Terminal、VTE、WezTerm、kitty、fish 默认发送 |
| `OSC 133;A/B/C/D` | 语义提示符：提示符开始 / 输入开始 / 命令输出开始 / 命令结束（带退出码） | FinalTerm 协议，iTerm2/kitty/WezTerm/VS Code 支持 |
| `OSC 633;…` | VS Code 私有的 shell 集成 | VS Code |
| `OSC 1337;CurrentDir=…` | iTerm2 私有 | iTerm2 |
| `OSC 9;9;路径` | ConEmu / Windows Terminal | Windows |

shell 要发出这些序列，需要一段"hook"：zsh 的 `precmd`、bash 的 `PROMPT_COMMAND`、fish 默认内置。例如 zsh：

```zsh
precmd() { printf '\033]7;file://%s%s\007' "$HOST" "$PWD" }
```

### 1.2 我们手上已经有什么

RoyalTerminal 已内置解析，**不需要自己写 OSC 解析器**（已用反射实测）：

| 输入 | `TerminalControl.ShellIntegrationEventReceived` 事件 |
|---|---|
| `OSC 7;file://myhost/home/me/src` | `WorkingDirectoryChanged`，`WorkingDirectory=/home/me/src`，`Host=myhost` |
| `OSC 133;A` / `B` / `C` / `D;0` | `FreshLineNewPrompt` / `InputStarted` / `OutputStarted` / `CommandFinished`（`ExitCode=0`） |
| `OSC 633`、`OSC 1337`、`OSC 9;9`、标题里的路径 | **不识别** |

另有 `TerminalControl.TitleChanged`（标题），以及 `SshShellBootstrapCommandBuilder` 可生成 bash/zsh/fish/PowerShell 的 hook 脚本（同时发 OSC 7 与 OSC 133）。

---

## 2. 为什么 tmux + zsh 下会失效（实测）

tmux 自己就是一个终端：pane 里的 zsh 发出的 OSC 7 被 **tmux 接收并消化**，不会转发给外层（KeiTerm / MobaXterm）。
实测三种配置下外层终端收到的内容：

| 配置 | 外层收到的 cwd 信号 |
|---|---|
| A. tmux 默认 + zsh 发原生 OSC 7 | **无**（这就是 MobaXterm 等工具在 tmux 里失效的典型原因） |
| B. `set -g allow-passthrough on` + zsh 用 DCS 包装 OSC 7（`ESC P tmux; …ESC 加倍… ESC \`） | `OSC 7;file://vm/etc` ✅ |
| C. `set -g set-titles on` + `set -g set-titles-string '#{host}:#{pane_current_path}'`（zsh 无需任何 hook） | 标题 `vm:/etc`、`vm:/usr/share` ✅；默认 `status-interval 15` 下，`cd` 后 0.8 秒内即更新 |

两者的区别：

- **B（透传）**：每个 pane 的 shell 各自发，外层收到的是"**最后打印提示符的那个 pane**"的目录，不一定是你正在看的 pane；需要 tmux ≥ 3.3 且改 `.tmux.conf` 和 shell 配置两处。
- **C（标题）**：tmux 自己读取**当前活动 pane** 进程的 cwd，切 pane / 切窗口时也会刷新，**不依赖 shell hook**；只改 `.tmux.conf` 一处。
  缺点：若在 pane 里又 `ssh` 到别的机器，`pane_current_path` 是本机 ssh 客户端进程所在目录（错的）——可在格式串里带上 `#{pane_current_command}`，为 `ssh`/`mosh` 时不跟随。

---

## 3. 方案：分层信号源 + 统一的"目录跟踪器"

```
TerminalControl.ShellIntegrationEventReceived (OSC 7) ─┐
TerminalControl.TitleChanged（按规则解析标题）  ───────┼──► CwdTracker（Core，纯逻辑） ──► 文件侧栏 Navigate
（可选）exec 通道查询 tmux / /proc                ───────┘        │
                                                                   └── 主机校验 / 去抖 / 置信度
```

### 3.1 信号源优先级

1. **OSC 7**（最准确，含主机名）。
2. **标题解析**（tmux 方案 C，以及 Debian/Ubuntu 默认 bash 标题 `user@host: ~/dir`）；规则可配置为正则，默认内置 2～3 条。
3. **手动**：文件侧栏按钮「跳到终端所在目录」（使用最近一次已知目录），以及反向「在终端中 cd 到此目录」——
   只在 OSC 133 表明**处于提示符状态**时才发送 `cd '<路径>'\r`，避免把命令敲进 vim 之类的全屏程序。

### 3.2 主机校验（关键正确性）

文件侧栏的 SFTP 连的是**会话登录的那台机器**。用户在终端里再 `ssh` 到另一台时，OSC 7 / 标题里的主机名会变，此时**必须停止跟随**，
否则侧栏会在错误的机器上打开同名路径。做法：连接后记录远端 `hostname`（首个 OSC 7 或一次 exec），之后仅接受同主机的目录信号，
主机不同时在侧栏显示「终端当前在 xxx 上，已暂停跟随」。

### 3.3 如何让远端发出信号（需要拍板）

| 方式 | 侵入性 | 说明 |
|---|---|---|
| a. 用户在 `~/.zshrc` / `~/.bashrc` 加一行 `source`（KeiTerm 提供片段，可一键经 SFTP 写入 `~/.config/keiterm/shell-integration.{zsh,bash}` 并追加带标记的 source 行） | 低、可见、可撤销 | **推荐**。片段内检测 `$TMUX` 自动用透传包装；与 tmux 方案 C 并存不冲突 |
| b. 登录时自动注入（经登录脚本把 hook 函数敲进 shell） | 高 | 会出现在终端回显与 history 里；进入 tmux 后新 pane 不继承，**不推荐** |
| c. 仅 tmux 方案 C | 只改 `.tmux.conf` | tmux 重度用户最稳；KeiTerm 提供一键复制配置行 |

---

## 4. 待讨论的问题

1. 默认是否开启「跟随」？建议：检测到信号源后在侧栏显示一个跟随开关，默认开。
2. 片段安装（§3.3 a）是否做成一键写入远端，还是只提供复制？
3. 标题规则是否对用户开放为可编辑正则？
4. 跟随是"立即导航"还是"提示后导航"？（大目录列表较慢时，频繁 `cd` 会导致侧栏抖动——实现时至少要 300ms 去抖，并取消在途的列目录请求。）

## 5. 实施拆分（确认方向后）

1. Core：`CwdTracker`（输入信号 → 输出"可信目录 + 主机"），含主机校验、去抖、标题正则解析；纯逻辑、可单测。
2. App：`TerminalTabViewModel` 订阅 `ShellIntegrationEventReceived` / `TitleChanged` 喂给 tracker；`RemoteFileManagerViewModel` 增加跟随开关与「跳到终端目录」。
3. 片段：`shell-integration.zsh/bash/fish`（发 OSC 7 + 133，`$TMUX` 下自动透传），可基于 RoyalTerminal 的 bootstrap 输出改写。
4. 同一套 OSC 133 事件顺带支撑：命令块跳转、「上一条命令输出」复制、命令耗时/退出码标记。

## 6. 与 MobaXterm 的差异

MobaXterm 的具体实现未公开，这里不做推断；可以确定的是，任何"shell 发序列、终端收"的方案在 tmux 默认配置下都会失效（§2 实验 A）。
本方案的区别在于：同时接受标题与透传两条 tmux 友好的路径，并对主机做校验。

## 7. 实验脚本

```bash
# 外层"终端"用 script 录下 tmux 客户端输出的全部字节，再从中提取 OSC 序列
sleep 60 | script -qfc "tmux -L kt attach -t s" out.bin &
tmux -L kt send-keys -t s "cd /etc" Enter
tmux -L kt set -g allow-passthrough on                 # 方案 B
tmux -L kt set -g set-titles on                        # 方案 C
tmux -L kt set -g set-titles-string '#{host}:#{pane_current_path}'
```
