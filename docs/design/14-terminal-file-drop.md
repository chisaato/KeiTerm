# 终端文件拖放与上传

文件管理器的工具栏可收起/展开目录树。收起会同时释放树和分隔条的列宽，展开恢复用户拖出的宽度；切换侧栏与文件管理文档保留此连接的布局状态。

## 两套独立协议

- 文件管理器仍使用会话的 `FileTransferProtocol`（SFTP / SCP）与 `SftpMode`。
- 终端拖放使用 `FileTransferSettings.TerminalUploadProtocol`（SFTP / SCP / Z Modem / Y Modem）。会话通过 `SessionOverrides.TerminalUploadProtocol` 覆盖，null 继承当前全局设置。

设置窗口的「文件传输」页面单列「终端文件拖放」分组；会话编辑的「文件传输」页面也分别展示文件管理器和终端拖放配置。会话覆盖沿用 `options_json` 保存，不增加数据库 schema。

## 拖放操作与记忆

本地文件拖入终端后，通过 `IInteractionService` 选择上传、粘贴完整路径或粘贴文件名。粘贴使用 shell 引号转义，拒绝控制字符，只发送输入、不追加回车。

- 下次继续询问：本次选择执行后即结束。
- 此连接不再询问：在当前连接记住所选操作，重连后重新询问。
- 全局不再询问：保存所选操作到 `FileTransferSettings.TerminalDropAction`。在设置中选择「每次询问」恢复询问。

连接级选择优先于全局默认。拖放进行期间拒绝第二次拖放；取消弹窗不输入、不上传、不保存。断开/关闭会取消并排空传输，旧弹窗不能向新连接发送输入。

## 传输通道

SCP 在已校验的 SSH 连接上打开 exec 通道；SFTP 通过该会话的 `SshDialer` 建立独立客户端，保留主机校验、保活与跳板语义，不采用文件管理器的协议选择。

Z/Y Modem 独占当前终端字节通道，屏蔽普通键盘输入与 VT 查询应答。发送前排空已经入队的输入；启动命令过滤 shell 回显，完成与取消都等待接收器结束标记后恢复终端输出。远端需要安装 lrzsz（`rz` / `rb`），并处于 shell 提示符。上传支持普通文件，Z Modem 单文件长度上限为 `uint.MaxValue` 字节；目录可粘贴路径/名称。

上传目的地优先使用终端 OSC 7 提供的目录；没有该信号时使用文件管理器当前目录，最后回退到 `.`。询问窗口显示协议和目的地。远端 shell 命令中的路径统一经 `ShellQuote`。

## 验证

常规单元测试覆盖实际 Avalonia 拖放路由、记忆与持久化、会话覆盖、取消排空、CRC、重传和 SCP 二进制内容。真实 lrzsz 互通可通过环境变量启用：

```bash
KEITERM_LRZSZ_RECEIVER=/path/to/lrz dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj
```

其中包括 Z/Y 多文件、中文空格名称、空文件、完整 shell 启动与中途取消。Linux 的 `KEITERM_SSHD_TESTS=1` 条件测试另覆盖 SCP/SFTP 实际上传，以及独占终端通道的屏蔽与恢复。
