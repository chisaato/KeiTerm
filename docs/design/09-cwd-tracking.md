# 09 - 跟随终端当前目录（CWD Tracking）设计草案

> 状态：方向已定（2026-09-29，见 §0），排在连接流程拆分之后实施。本文先讲清楚原理，再给出方案。
> 文中 tmux 行为均为在 tmux 3.4 + zsh 5.9 上的**实测结果**（实验脚本见 §7）。

---

## 0. 已定决策

1. **三种模式：关闭 / 打开时同步一次 / 永远跟随；全局设置 + 会话级覆盖（继承 / 三选一），全局默认关闭。**

   | 模式 | 行为 |
   |---|---|
   | 关闭 | 从不自动导航；仍可手动点「跳到终端所在目录」 |
   | 打开时同步一次 | **本次连接中第一次打开文件侧栏**时导航到终端当前目录，之后不再跟随：终端里继续 `cd`、关掉侧栏再打开，都不会再自动跳转 |
   | 永远跟随 | 侧栏打开期间，终端每次换目录都导航过去——包括用户在侧栏里手动去了别处之后；关掉再打开侧栏时也先同步到终端当前目录 |
2. **只做被动接收，不往服务器安装或写入任何东西**：不做远端 helper，不自动写 `~/.zshrc`，不提供可编辑的标题正则。
   信号源只有两个：OSC 7（用户自己配置了 shell 集成时）与 tmux 标题内置格式（§2 方案 C，一行 `.tmux.conf`）。文档中给出可复制的配置片段即可。
3. 定位为「文件侧栏与终端同窗」时的便利功能，而非核心卖点——Xshell / SecureCRT 的双窗口形态本就不需要它，
   MobaXterm 的同类功能在 tmux 下也长期失效，用户对它的依赖度低。

### 0.1 要不要像 Warp / Wave 那样在服务器上装一个小工具？

**不做，并把「零远端安装（agentless）」作为设计原则写入路线图。** 理由：

| 考量 | 说明 |
|---|---|
| 目标用户的服务器环境 | 生产机普遍有变更审计 / 基线扫描，往上面放未知二进制常被禁止；而 Xshell / SecureCRT 用户恰恰大量管理这类机器 |
| 覆盖面 | 需为 x86_64 / arm64 / musl / 旧 glibc / AIX / BusyBox 各自构建；网络设备、跳板堡垒机根本不能装 |
| 维护成本 | 版本协商、自动升级、残留清理、多用户共享目录的权限 |
| 攻击面 | 远端常驻组件即新的提权 / 供应链风险点 |
| 收益 | 就目录跟随而言收益很低（见决策 3） |

真正需要远端信息的功能（如轻量系统监控、远端文件搜索），优先走 **SSH exec 通道执行 POSIX 命令**（`cat /proc/loadavg`、`df -P` 等），
与 XPipe 的思路一致：零安装、用完即走、审计日志里就是普通命令。只有当某个功能被证明无法用这种方式完成、且收益足够大时，才重新评估远端组件，
且必须是可选、可一键清除的。

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

### 3.3 远端如何发出信号

按 §0 决策，Kei 不修改远端。文档提供两段可复制的配置，用户自行决定是否使用：

```zsh
# ~/.zshrc：发 OSC 7；在 tmux 内自动用透传包装（需 tmux ≥ 3.3 且 set -g allow-passthrough on）
# 已在 zsh 5.9 + tmux 3.4 实测：外层终端收到 OSC 7;file://<host>/<cwd>
__kei_osc7() {
  local esc=$'\e' bel=$'\a'
  local seq="${esc}]7;file://${HOST}${PWD}${bel}"
  # tmux 透传：ESC P tmux; <原序列中每个 ESC 加倍> ESC \
  [[ -n $TMUX ]] && seq="${esc}Ptmux;${seq//${esc}/${esc}${esc}}${esc}\\"
  printf '%s' "$seq"
}
autoload -Uz add-zsh-hook && add-zsh-hook precmd __kei_osc7
```

```tmux
# ~/.tmux.conf：tmux 用户只需这两行，shell 无需任何配置（推荐）
set -g set-titles on
set -g set-titles-string '#{host}:#{pane_current_path}'
```

## 4. 已决问题

| 问题 | 决定 |
|---|---|
| 默认是否开启 | 全局默认关；会话可覆盖（继承 / 关闭 / 打开时同步一次 / 永远跟随） |
| 片段是否一键写入远端 | 否，只在文档/设置页提供复制 |
| 标题规则是否可编辑 | 否，只内置 `host:path` 与 Debian 默认 `user@host: path` 两种格式 |
| 立即导航还是提示 | 开启即立即导航（用户已显式开启），300 ms 去抖并取消在途列目录请求 |
| 「同步一次」时终端还没报告过目录 | 侧栏先按默认目录打开，等到本次连接的**第一个**目录信号到达时导航一次，随后停止（仍只算一次） |
| 侧栏未打开时收到的信号 | 所有模式都只记录「最近一次终端目录」，不做导航；供打开侧栏或手动跳转时使用 |

## 5. 实施拆分（排在连接流程拆分之后）

0. 设置：`enum CwdFollowMode { Off, OnceOnOpen, Always }`；`AppSettings.CwdFollowMode`（默认 Off）+ `SessionNode.CwdFollowMode`（可空，null = 继承），迁移追加一列。
   跟随判定为 Core 纯函数（输入：模式、侧栏是否打开、本连接是否已同步过、信号），「一次」状态挂在单个连接/标签上，重连新会话时重置。
1. Core：`CwdTracker`（输入信号 → 输出"可信目录 + 主机"），含主机校验、去抖、两种内置标题格式解析；纯逻辑、可单测。
2. App：`TerminalTabViewModel` 订阅 `ShellIntegrationEventReceived` / `TitleChanged` 喂给 tracker；`RemoteFileManagerViewModel` 增加跟随开关与「跳到终端目录」。
3. 文档：§3.3 两段配置放进设置页的「如何让远端报告目录」说明里。
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
