# 02 同步包格式（age 口令模式）

Status: resolved
Blocked by: None (can start immediately)

见 `../spec.md`「同步包格式」，以及 ADR-0004。

**要做什么：** 一个可独立验证的组件，能把字节流用同步口令加密成 age v1 文件（单一 scrypt recipient），也能把 age 口令模式的文件解开；与标准 `age` 工具互通。

- [x] 先评估两种实现并在本文件的 Comments 里写结论：用项目已依赖的 NSec 自研 scrypt recipient 与 STREAM 分块，或引入 AgeSharp（预览版，依赖 BouncyCastle）。评估项包括：许可证、依赖体积、跨平台（含未来移动端）、维护风险。
- [x] 加密使用 age 默认工作因子；解密接受合理的工作因子范围，超过上限的拒绝，以防恶意文件耗尽资源。
- [x] 口令错误、文件截断、头部被篡改、非 age 文件时分别给出可区分的错误，且不产生部分明文输出（不可区分的密码学边界见 Comments）。
- [x] C2SP/CCTV 中与 scrypt recipient 和 STREAM 相关的测试向量全部通过。
- [x] 本机装有 `age` 时：我们加密、`age -d` 解密成功；`age -p` 加密、我们解密成功。未安装时这两项测试跳过。
- [x] 口令与派生出的密钥材料用完后清零。

## Comments

### 实现前评估

选择复用 NSec 的 scrypt，自研有限的 age v1 单一 scrypt recipient、头部 HMAC 与 STREAM 编解码；ChaCha20-Poly1305 / HKDF 使用 .NET BCL。NSec 为 MIT，已是生产依赖，不增加包体积；现有 libsodium 的桌面平台覆盖不变，未来移动端仍需单独验证原生资产/AOT。AgeSharp 为 MIT，但 0.2.0-preview.4 会新增 BouncyCastle 及预览版维护/升级风险；其纯托管方式对移动端有优势，但当前范围不值得新增依赖。自研只实现标准子集、不自创密码原语，维护风险由固定 CCTV 向量与 CLI 双向互通约束。默认工作因子 18；解密限定 1..18（最多约 256 MiB scrypt 内存），在派生前拒绝超限。

注意：对 scrypt salt、工作因子或包装密文的篡改，与错误口令在密码学上不可区分；只能统一为口令不匹配。成功解包后的头部 HMAC 不匹配可单独报告头部损坏，不能承诺识别所有头部篡改。

### 评估修正（TDD 发现）

NSec 26.4.0 的公开 scrypt API 强制 32 字节 salt，而 age 使用 44 字节（28 字节域分隔符 + 16 字节随机 salt），直接复用 API 不可行；红灯测试已证实。改为薄封装调用 NSec 已分发的 libsodium `crypto_pwhash_scryptsalsa208sha256_ll`（NSec 本身也调用此导出），仍不新增依赖、不自研 scrypt。原生库通过 NSec 程序集定位 runtime 资产；未来移动端必须验证动态加载，当前不承诺移动端支持。

### 公开 API 与行为

- Core：`ISyncPackage.Encrypt(ReadOnlySpan<byte>, ReadOnlySpan<char>)` / `Decrypt(ReadOnlySpan<byte>, ReadOnlySpan<char>)`，返回 `byte[]`；`SyncPackageException.Error` 提供 `NotAge`、`WrongPassphrase`、`CorruptOrTruncated`、`HeaderAuthenticationFailed`、`WorkFactorTooHigh`、`UnsupportedFormat`。
- Infrastructure：`AgeSyncPackage` 无状态实现。默认工作因子 18；接受 1..18，超限在 KDF 前拒绝；严格 canonical base64、单一 scrypt stanza、HMAC 与 STREAM 终块认证。只支持二进制 age v1；ASCII armor 与其他 recipient 不在该组件范围。
- 内存字节接口适用于本任务 KB..MB 整库快照，不依赖 seek/临时明文文件；失败绝不返回部分明文，并清零已分配的内部明文。口令 UTF-8 精确编码（不做 Unicode 归一化），内部口令字节和文件密钥/包装密钥/HMAC 密钥/STREAM 密钥在 finally 清零。输入 span 为借用数据，调用方负责其口令字符与输入/返回明文缓冲区的清零。
- 未增加 DI 注册或 JSON 快照模型；留给使用该抽象的后续工单。唯一实现偏离是上述 libsodium 低层桥接；无需新增 NuGet。

### 验证结果

- TDD：先写往返/错误分类测试，缺失 API 编译红灯；实现后先发现 NSec salt 限制，再由 CCTV 红灯捕获域分隔符长度与非法短尾块处理，修复后全部通过。
- 收录 CCTV 固定提交 `50a8ecf2a220f4c8bdc4f085789b8e85c26829e7` 全部 25 个 scrypt 与 28 个 STREAM 二进制向量（0BSD，来源/许可证随测试数据保留）；53/53 通过。STREAM 通过内部独立接缝与向量 file key 验证，不需要实现 X25519。失败向量不向调用方交付任何明文，较 CCTV 允许部分输出更严格。
- `dotnet build Kei.Term.slnx`：0 错误。
- `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj --filter FullyQualifiedName~AgeSyncPackageTests`：62 通过，0 失败，0 跳过；包含本机 age v1.3.2 双向互通。未安装 age 时自动跳过 CLI 测试；CLI 的交互口令还需要 Unix PTY/python3，不具备时明确跳过。
- 最后全量 `dotnet test tests/Kei.Term.Tests/Kei.Term.Tests.csproj`：1488 通过、36 跳过、3 失败。两个历史迁移测试 `PortForwardTests.V5_OnExistingV4Database_AddsPortForwards_AndLeavesProxiesUntouched` 与 `ProxyFirewallTests.V4_MigratesJumpHostIdToSessionKind_AndSaveClearsOldColumn` 因并行工单 01 的 `SchemaMigrations.ApplyLocalEntityStampsAsync` 遇到缺少 `known_hosts` 表失败；另一个 `DirectoryFollowTests.ForeignHostSignal_CancelsPendingAutomaticNavigation` 期望 `/` 实为 `/pending`，涉及未修改的导航路径。本工单 62 项全绿；上述三个失败不在本工单修改路径，未越界修复，未提交。
