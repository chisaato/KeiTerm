# 外部编辑器与文件回写诊断

配置 VS Code 的 `code` 命令或应用路径后，KeiTerm 自动添加 `--wait`，等待本次打开的文件关闭。macOS 的 VS Code `.app` 使用内置 CLI，Windows 的 `Code.exe` / `bin/code.cmd` 使用内置 CLI host；不会把单实例应用的启动进程退出当作文件关闭。VS Code 的参数语义见[官方 CLI 文档](https://code.visualstudio.com/docs/configure/command-line#_core-cli-options)，Windows host 的启动方式与[官方 code.cmd](https://github.com/microsoft/vscode/blob/main/resources/win32/bin/code.cmd)一致。

等待进程成功退出后先检查最后一次保存，再停止该文件的监视。进程退出码非零或最终检查失败时继续监视。系统默认打开在 macOS 使用 `open -W`，等待关联应用退出；其他没有可靠关闭信号的启动方式保留监视，需点击文件行的停止按钮。编辑器没有持续持有文件句柄、文件长时间没有变化，都不表示编辑完成。

多文件按完整远端路径区分；重复打开正在监视的文件使用已有本地缓存。停止监视保留缓存，以便已排队的回写读取或失败后恢复。轮询比较内容摘要，因此原子替换、保留时间戳且长度不变的保存也能检测。

日志位于数据目录的 `logs/KeiTerm.yyyyMMdd.log`。本机 macOS 默认目录为 `~/Library/Application Support/KeiTerm/logs/`，当前日志记录 Debug 及以上级别，日志不记录文件内容。

复现时记录远端文件路径和操作时间，可搜索以下日志关键字：

| 关键字 | 诊断内容 |
| --- | --- |
| `File tracker started` | 实际监视模式、轮询周期、防抖时间、缓存目录 |
| `Launching editor` / `Editor process started` | 实际执行路径、是否等待关闭、PID、缓存路径 |
| `Waiting for editor close` / `Editor wait returned` | 等待进程与退出码 |
| `File polling tick` / `File check unchanged` | 轮询是否运行、文件是否有内容变化 |
| `FileSystemWatcher event` / `FileSystemWatcher renamed` | 原生事件与原子替换 |
| `File hash retry` / `Polling file check failed` | 文件写锁、读取重试或权限错误 |
| `File change detected` | 来源（原生、轮询、最终检查）、会话和路径 |
| `Queued file writeback` / `File writeback started` / `File writeback committed` | 保存事件是否排队、上传是否开始和完成提交 |
| `Editor closed` / `UnregisterTrackedFileAsync` | 自动关闭或手动停止监视 |

只有启动日志、没有保存检测：查看实际监视模式、轮询日志和原生 watcher 错误。检测到了保存、没有成功提交：查看回写错误，保留日志中指向的本地缓存。VS Code 关闭后仍监视：查看实际执行路径、`WaitForClose` 和等待退出码；通过系统默认方式启动时，macOS 等待的是整个应用退出。
