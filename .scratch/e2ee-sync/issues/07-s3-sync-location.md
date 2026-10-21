# 07 S3 兼容存储同步位置

Status: resolved
Blocked by: 03

见 `../spec.md`「同步位置」。

**要做什么：** 用户可以把同步位置设为 S3 兼容存储（AWS S3、Cloudflare R2、MinIO、阿里云 OSS、腾讯云 COS），在同步页填写端点、区域、桶、前缀与访问密钥，测试连接后即可同步。

- [x] 基于 `AWSSDK.S3` 实现同步位置：支持自定义端点与 path-style 访问；读写主同步包、历史同步包的列出、读取、写入与删除、测试连接。
- [x] 条件写使用 `IfMatch` / `IfNoneMatch`，返回 412 或 409 时由同步协调器重新执行整轮。配置项可切换条件写；阿里云 OSS、腾讯云 COS 默认关闭，关闭后走读回检查。
- [x] 访问密钥存进保险库，同步位置配置属于本机专属项。
- [x] 同步页增加 S3 的配置表单与「测试连接」，界面交给 @designer；与 06 的表单共用同一套同步位置选择框架。
- [ ] 同步位置契约测试在 `KEITERM_SYNC_BACKEND_TESTS=1` 时连接本机的 MinIO 运行并通过；测试文件头部注释写明如何起服务。

## Comments

### 公开 API 与行为

- Infrastructure `S3SyncLocation(endpoint, region, bucket, prefix, accessKeyId, secretKey, pathStyle, conditionalWrites)` 使用 AWSSDK.S3 4.0.104.6，实现 `ISyncLocation` / `IDisposable`。支持自定义端点、签名区域（默认 us-east-1）、path-style、桶、前缀；主包 `<prefix>/keiterm-sync.age`，历史 `<prefix>/history/`，分页列出、读写删除与探针连接测试已实现。
- `PutObjectRequest.IfMatch` 原样传版本标签，null 用 `IfNoneMatch="*"`；412/409 映射为 `SyncWriteConflictException`。关闭条件写时不发送两种条件头并声明 false。`S3ConditionalWritePolicy` 对阿里云 OSS/腾讯云 COS 默认 false，其余 true，显式配置覆盖预设。
- 与 06 共用本机 JSON、工厂与类型选择表单。Access Key ID 在本机 JSON，Secret Key 独立存到 Vault 本机条目，不进入同步快照。Vault 锁定、重启及换主密码行为有定向验证。配置字段、工厂 API 与凭据接口见 06 Comments。

### 验证与已知偏离

- 构建 0 错误 / 0 新警告，仅已有 CS8602。默认目录定向测试 13 通过、4 跳过；后端 opt-in 契约 4 通过（WebDAV 2、S3 2）。未跑全量，由主会话统一验证，未提交。
- S3 实际验证后端为 Podman 的 Adobe S3Mock：二进制主包、有效 ETag 更新、空/过期标签拒绝、历史列读写删、中文及特殊文件名通过。MinIO 镜像拉取受本机镜像仓库证书失效/权限错误阻断，未声称 MinIO 通过，末项保持未勾选。测试文件头部同时保留 MinIO 命令和明确标注的 S3Mock 替代命令。
- 08 报告的字节不一致已定位为假服务器缺少 SigV4 `aws-chunked` 解码：原始体以 `3;chunk-signature=...` 开头。HttpListener 只解 HTTP transfer-encoding；现按 AWS chunk 帧解析并校验解码长度，未改生产 SDK 传输、未放宽字节断言。新增 200000 字节多块往返测试通过；412/409 映射与关闭头测试均通过。
- 所有本任务容器已停并移除。AWS/R2/OSS/COS 真实服务、MinIO 与 virtual-host-style 端到端仍属外部环境未验证；S3Mock 不替代这些结果。
- 页面只做功能，不做视觉设计；`Views/Settings/SyncSettingsPageView.axaml` 后续交给 @designer 打磨布局与交互。保留 04/08 改动，未改协调器。

### 集成审查跟进：无效配置不得覆盖已有配置

- 已证实共享页面原先在校验前保存位置和新凭据，现先构造/验证位置，成功后再持久化。S3 端点由简单前缀判断加强为绝对 HTTP(S) URI 与非空 Host 验证，错误地址返回 ArgumentException，避免 SDK URI 格式异常逸出页面处理。
- 页面行为测试覆盖 ftp WebDAV、ftp S3 和 `http://[`；错误草稿不改变原 JSON 字节、WebDAV 密码、S3 Secret Key，且输入框不清空。定向 16 通过，0 失败；无明文泄密或重试风暴证据，不扩展此类推断。跨文件/数据库的保存失败原子性不是本次修复声称的能力。
- WebDAV 历史名称疑点已用 Apache 实测排除，细节见 06 同名跟进；只补测试，不因猜测修改正确转义。未改 04/08、未跑全量、未提交。
