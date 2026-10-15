# 09 - 跟随终端当前目录

> 已实现：OSC 7 目录上报、复用器标题兼容、自动跟随与手动同步。
> 不安装远端 helper，不修改远端 shell 或复用器配置；以下片段由用户自行选择配置。

## 设置入口与优先级

- **设置 → 文件传输 → 文件管理**：设置新连接的全局默认。
- **会话属性 → 会话行为**：目录跟随和多路复用器兼容都可以“继承全局”，或覆盖本会话。
- **文件管理面板顶部 → 同步终端目录**：仅执行一次导航，不修改任何策略。没有收到可信目录时不可用；自动跟随关闭时仍可使用。

配置按 **会话覆盖 → 全局默认** 解析，连接时生效，重连重新解析。会话覆盖存入已有 `options_json`，不新增表或修改旧迁移。

| 目录跟随 | 行为 |
|---|---|
| 关闭（默认） | 不自动导航，仍记录有效目录供手动同步 |
| 打开时同步一次 | 每次连接第一次打开文件面板时同步；尚无目录时等待第一个信号，关闭再打开不重复 |
| 永远跟随 | 面板可见时跟随目录变化；重新打开时同步到最近目录 |

| 多路复用器兼容 | 接受的信号 |
|---|---|
| 关闭 | 仅标准 OSC 7 目录上报 |
| 自动检测（默认） | OSC 7、tmux / Screen 带标记标题、内置 `host:/absolute/path` / `user@host: /absolute/path` 标题 |
| tmux（含 Byobu） | OSC 7、tmux 带标记标题、内置标题；不采用 Screen 带标记标题 |
| GNU Screen（含 Byobu） | OSC 7、Screen 带标记标题、内置标题；不采用 tmux 带标记标题 |

自动检测基于收到的信号，不扫描远端进程、不猜测 Byobu 后端，也不会自动配置远端。Byobu 使用 tmux 或 GNU Screen，可用 `byobu-select-backend` 查看或切换。

## 普通 shell：OSC 7

终端无法通过 SSH 协议直接取得交互式 shell 的工作目录。远端需要通过 OSC 7 或含目录的标题报告目录。

zsh 示例（普通 shell；`PWD` 中有百分号、空格或非 ASCII 字符时应采用能正确进行 URI 编码的既有 shell 集成）：

```zsh
__kei_osc7() { printf '\033]7;file://%s%s\007' "$HOST" "$PWD" }
autoload -Uz add-zsh-hook
add-zsh-hook precmd __kei_osc7
```

OSC 7 由 RoyalTerminal 解析，不从可见提示符文本猜测目录。标准 URI 中的百分号编码由终端库解码。

## tmux / Byobu 的 tmux 后端

tmux 默认会消费 pane 内 shell 的 OSC 7，外层终端未必收到。推荐使用活动 pane 的标题报告，写入 `~/.tmux.conf`；Byobu 用户把同样设置放入自己加载的 tmux 用户配置（通常是 `~/.byobu/.tmux.conf`）。

```tmux
set -g set-titles on
set -g set-titles-string 'kei;tmux;#{host};#{pane_current_command};#{pane_current_path}'
```

在已有会话里加载相应配置，或重启/重新进入复用器。设置只对实际加载了该配置的 tmux server 生效。

该格式包含当前活动 pane 的主机、前台命令与完整绝对路径。`ssh`、`mosh`、`mosh-client` 或 `telnet` 作为前台命令时暂停采用外层 pane 的目录，避免把内层远端目录映射到外层 SFTP。

也兼容旧配置：

```tmux
set -g set-titles on
set -g set-titles-string '#{host}:#{pane_current_path}'
```

旧格式没有前台命令，无法判断 pane 中的嵌套 SSH，建议使用带标记格式。标题会参与现有标签标题跟随；若不希望显示这段标题，可关闭会话的“标签标题跟随远端”。

另一条路是 tmux `allow-passthrough` 配合 shell 的 DCS 包装。复用器解包后，KeiTerm 接收的是普通 OSC 7，不必在 KeiTerm 再次解包。该方案可能收到其他可见 pane 的上报，因此活动 pane 的标题方案优先。

## GNU Screen / Byobu 的 Screen 后端

Screen 不提供与 `pane_current_path` 等价的标题变量，需由 shell 在提示符时报告。以下 zsh hook 把含主机和目录的标题经 Screen DCS 透传给外层终端；在 KeiTerm 选择自动检测或 GNU Screen：

```zsh
__kei_screen_cwd() {
  [[ -n $STY && -z $TMUX ]] || return
  printf '\033P\033]2;kei;screen;%s;%s\007\033\\' "$HOST" "$PWD"
}
autoload -Uz add-zsh-hook
add-zsh-hook precmd __kei_screen_cwd
```

路径按标题原文传递，支持空格、中文、冒号与分号，不做 URI 解码。这个 hook 应仅在实际运行 Screen 的外层服务器配置，不能把另一个 SSH 目标的目录当作当前 SFTP 目录。

**限制**：Screen 透传报告来自最后一次输出提示符的 shell；切窗口本身不一定重发，多个可见 region 也可能交错上报。切换后显示新提示符再同步。此方案不能保证像 tmux 的活动 pane 标题一样精确识别当前 region；单独选择 Screen 也不会消除这一限制。

## 跟随与生命周期

- 仅接受无控制字符的绝对路径，不展开 `~`，不把普通命令输出当路径。
- 首个有效报告的主机名是本连接的基准，允许同主机短名称/FQDN；此后遇到其他主机则清空可同步目录并暂停，原主机重新上报后恢复。IP 登录、SSH 别名与远端 hostname 不必一致。若连接首次上报就来自嵌套 SSH，单靠被动信号无法确认外层主机；应在外层提示符先上报一次。
- 最近一次有效信号更新目录。复用器未转发新信号时无法凭空推断目录；手动同步也只使用最近报告。
- 自动导航 300 ms 去抖；后续信号取消旧导航和列目录请求。即便远端忽略取消，迟到结果也不能覆盖新目录列表。
- 隐藏面板停止自动导航但保留最近目录。断开、关闭和重连会取消在途跟随；新连接清空主机基准和“一次同步”状态。
- 文件系统通道仍经现有 `SshDialer` 创建，目录跟随不额外创建连接或向交互式终端注入命令。

## 实现与验证

Core `TerminalDirectoryTracker` 负责路径校验、主机基准和内置标题解析。App `TerminalTabViewModel.DirectoryFollow` 接收 OSC 7 / 标题并协调跟随；文件管理器统一处理导航取消和迟到结果。

测试覆盖真实终端控件输出解析、手动按钮、跟随模式、重连、错误主机、两种复用器信号、设置与会话覆盖持久化，以及列目录响应乱序。Headless 信号测试不等同于所有版本的 Byobu/tmux/Screen 实机测试。

参考：[tmux 官方手册](https://man.openbsd.org/tmux.1)（`set-titles-string`、`allow-passthrough`）、[GNU Screen 手册](https://www.gnu.org/software/screen/manual/screen.html)（Control Sequences / DCS）、[Byobu 源码](https://github.com/dustinkirkland/byobu)（后端与用户配置）。
