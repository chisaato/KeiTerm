# 端口转发设计规格

> 状态：与代理规格同一批实施。迁移必须是 **v5**。v4 已留给 `proxies` / `proxy_json`，不要占用，也不要修改 v4。

## 1. 目标

会话可以带 Local / Remote / Dynamic 转发。连接建立后启动，会话断开或标签关闭后全部停止。

这和「出站 SOCKS5 防火墙」不是一回事。Dynamic 是本机监听一个端口，供别的程序当 SOCKS 用。出站代理仍走 `proxy_json`。

## 2. 数据

迁移 v5，新表 `port_forwards`：

| 列 | 含义 |
|---|---|
| `id` | TEXT 主键 |
| `session_id` | 所属会话，外键 `tree_nodes(id)`，级联删除 |
| `name` | 显示名，可空 |
| `mode` | `local` / `remote` / `dynamic` |
| `bind_address` | 默认 `127.0.0.1` |
| `listen_port` | 1–65535 |
| `destination_host` | local / remote 必填；dynamic 为空 |
| `destination_port` | local / remote 必填；dynamic 为空 |

`PRAGMA foreign_keys` 仍只经 `SqliteConnectionFactory.OpenAsync`。仓储不自己 `CREATE TABLE`。

同一会话上 `bind_address + listen_port + mode` 重复则保存失败，错误写明端口已被该会话占用。

## 3. 生效

SSH.NET：`ForwardedPortLocal`、`ForwardedPortRemote`、`ForwardedPortDynamic`。在 `SshDialer` 把目标会话连上之后添加并 `Start`，与 RoyalTerminal 的做法相同。断开时 `Stop` 并移除，失败只记日志，不挡住会话释放。

跳板链上的转发挂在**目标会话**的客户端上，不挂在跳板的客户端上。出站 SOCKS5 只影响怎么拨到第一跳，不改变转发挂在谁身上。

某条转发 `Start` 失败：会话保持连接，标签状态里写明哪一条没起来。不要因为一条转发失败把整次 SSH 拆掉。

## 4. 界面

会话编辑器新增「端口」分类，不改连接页已有的防火墙行。列表加编辑对话框：模式、绑定地址、监听端口、目标主机、目标端口。Dynamic 隐藏目标主机和端口。

已打开的标签要重连后才应用新转发。编辑器不热更新正在听的端口。

## 5. 测试

- v5 在已有 v4 的库上只加表，不改 `proxies`。
- 保存后按会话读回三种模式；dynamic 的目标列为空。
- 重复监听端口被拒绝。
- 删除会话后级联删掉转发。
- 拨号器在目标客户端上启动转发；释放后这些端口不再处于已启动。用 SSH.NET 对象的状态断言，不要求外网。
