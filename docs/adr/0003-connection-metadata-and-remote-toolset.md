# 连接元数据独立成列；远端命令按平台工具集生成

**连接元数据**存于 `session_details.metadata_json`（迁移 v8）。它是本机自动发现的信息，与 `options_json`（用户意图，`SessionOverrides`）分开：

- 按键定向读写（`json_set` / `json_remove`），不整体覆盖。会话保存路径不得读写这一列，保存会话也不得清掉它（禁止 `INSERT OR REPLACE` 整行）。
- ~~不参与同步~~（已由 ADR-0004 改为随会话同步、按键合并）；后台写入仍不改所属会话的 `revision`。
- 每个键对应 Core 中一个强类型模型，第一个键是 `transferProbe`。以后新增元数据只加键，不再迁移。

远端哈希与归档命令不在调用处拼字符串，而是由**远端工具集**（`IRemoteToolset`，按 `RemotePlatform` 选择）生成和解析。设置与探测结果只记录算法/格式（`Sha256`、`Zstd`……），不记录命令，因而与平台无关。本次只实现 POSIX 工具集，面向 Linux；Darwin、Windows 后续各加一个工具集即可。

## Considered Options

- **把探测结果放进 `options_json`**：放弃。会话编辑窗口保存时会用旧 JSON 覆盖后台写入的探测结果；探测还会改动同步版本，造成同步噪音。
- **单独加 `transfer_probe_json` 列或新表**：放弃。每类元数据各占一列/一表，迁移会越来越多。
- **配置里直接写命令（如 `sha256sum`）**：放弃。命令随平台变化（Windows 是 `certutil` / `Get-FileHash`），写进配置就等于假定目标是 Linux。
