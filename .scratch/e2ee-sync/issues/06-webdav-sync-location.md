# 06 WebDAV 同步位置

Status: resolved
Blocked by: 03

见 `../spec.md`「同步位置」。

**要做什么：** 用户可以把同步位置设为 WebDAV（Nextcloud、群晖、坚果云等），在同步页填写地址、用户名、密码，测试连接后即可同步。

- [x] 基于 `WebDav.Client` 实现同步位置：读写主同步包、历史同步包的列出、读取、写入与删除、测试连接；目标目录不存在时自动创建。
- [x] 默认按 ETag 条件写（`If-Match` / `If-None-Match`），返回 412 时由同步协调器重新执行整轮；配置项可关闭条件写，关闭后改走读回检查。ETag 原样回传，不自行加减引号，以兼容坚果云。
- [x] 密码存进保险库，同步位置配置属于本机专属项。
- [x] 同步页增加 WebDAV 的配置表单与「测试连接」，界面交给 @designer。
- [x] 同步位置契约测试在 `KEITERM_SYNC_BACKEND_TESTS=1` 时连接本机的 WebDAV 服务运行并通过；测试文件头部注释写明如何起服务。

## Comments

### 公开 API 与行为

- Infrastructure `WebDavSyncLocation(url, user, password, conditionalWrites)` 实现 `ISyncLocation` 与 `IDisposable`，使用 WebDav.Client 2.9.0。主包为 `keiterm-sync.age`，历史位于 `history/`；历史名拒绝路径穿越、URL 转义后请求，支持中文及 `#?%`。
- 开启条件写时 null 标签发送 `If-None-Match: *`，其余发送原始 `If-Match`，412 转成 `SyncWriteConflictException`。处理器用 `TryAddWithoutValidation` 保留无引号 ETag；读取没有 ETag 时明确报错并建议关闭条件写。关闭时不发条件头、`SupportsConditionalWrites=false`，不改协调器的读回检查。
- 目标目录 MKCOL，409 时递归创建父目录；测试连接写删唯一探针，不触碰主包。网络错误转换为 IOException，调用方取消正常传播。
- 共用 `SyncLocationSettings` / `SyncLocationSettingsStore` / `SyncLocationFactory.Create(settings, webDavPassword, s3SecretKey)`；配置保存于本机 `sync-location.json`，优先读取 JSON，没有时兼容旧 txt，保存后移除旧 txt。配置无密码、Secret Key 字段。
- Core `ISyncLocationCredentialStore` 由 InternalVaultManager 实现；WebDAV 密码与 S3 Secret Key 分别用固定 GUID 保存到 `local_vault_secrets`，复用 Vault 加密、锁定与轮换。同步页输入留空复用保险库值，非空保存后清空输入框。

### 验证、偏离与边界

- 默认目录 `dotnet build Kei.Term.slnx`：0 错误 / 0 新警告，只有原有 `TerminalProfileApplicationTests.cs:948` CS8602。
- 定向过滤 `SyncLocationContractTests|ConditionalHeaderTests|SyncLocationSettingsTests|LocalFolderSyncLocationTests`：13 通过、4 个 opt-in 跳过；开启后端变量后两组真实契约共 4 通过，其中 WebDAV 2 通过。含主包二进制、有效标签更新、空/过期标签拒绝、历史列读写删及特殊文件名。
- 实际用 Podman 运行 bytemark/webdav（Apache），不是忽略 If-Match 的 rclone；rclone 初次验证失败已确认属于服务能力不符。HttpListener 测试覆盖带/不带引号 ETag、412 与关闭条件头；Vault 测试覆盖重启、锁定、换主密码后的凭据读取。
- 本任务创建的 rclone、Apache、S3Mock 容器均已停止并由 --rm 移除。未提交；按最新分工不跑全量，由主会话统一验证；不覆盖 04/08 改动。
- UI 仅功能表单沿用 SettingsStyles，未做视觉验收；请将 `Views/Settings/SyncSettingsPageView.axaml` 的布局、层次与响应式打磨交给 @designer。Nextcloud/群晖/坚果云真实服务未验证。

### 集成审查跟进：配置提交与历史名称

- 已证实原 OpenLocationAsync 在工厂验证前保存 JSON、覆盖非空新凭据。修复为先从草稿/保险库取值并构造位置，校验成功后才保存凭据和配置；构造失败保留原文件、两项保险库凭据与草稿输入，后续保存失败会释放已构造位置。此处只保证校验失败不提交，不声称 JSON 与 Vault 跨介质事务原子性；没有明文泄密或重试风暴的证据。
- 新增 SyncLocationConfigurationSafetyTests，经页面 TestConnectionCommand 输入 ftp WebDAV、ftp S3、语法错误的 http S3 地址，断言原 JSON 字节、两项 Vault 凭据及输入框均不变。定向测试合计 16 通过，0 失败，仅原有 CS8602 警告。
- 历史名称“双重编码”疑点为误报：PROPFIND href 解码为逻辑文件名，后续请求再 URL 编码一次是正确边界。未改生产转义；契约扩展为空格、中文、#、?、% 以及字面量 %23，直接取 ListHistoryAsync 返回的 Name 做读取与删除闭环，Apache/bytemark 真实契约 2 通过。本次容器已停止并自动移除；未跑全量、未提交、未修改 04/08 或 UI 视觉。
