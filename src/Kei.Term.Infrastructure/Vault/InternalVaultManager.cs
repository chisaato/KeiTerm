namespace Kei.Term.Infrastructure.Vault;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;

// 内置 Vault：主密码可选。明文模式下字节直通。
//
// 数据密钥是随机 32 字节 Vault Key；主密码经 Argon2id 得到包装密钥，只用于
// XChaCha20-Poly1305 包装 Vault Key，不直接加密业务行。条目用 Vault Key 加密，
// AAD 绑定该行主键。旧 PBKDF2 库仍能打开，成功后在同一次解锁里迁移到 Vault Key。
public partial class InternalVaultManager : IVaultManager, IVaultSecretStore, IProxySecretStore, IQuickUnlockVault
{
    // vault_metadata 键
    private const string KeyPlainMode = "plain_mode";
    private const string KeyKdfSalt = "kdf_salt";
    private const string KeyVerifier = "verifier";
    private const string KeyKdf = "kdf";
    private const string KeyCleanupPending = "plaintext_cleanup_pending";
    private const string KeyVaultId = "vault_id";
    private const string KeyWrappedVaultKey = "wrapped_vault_key";
    private const string KeyRotationPending = "vault_key_rotation_pending";

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<InternalVaultManager> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    // 密钥轮换与秘密写入互斥，避免旧密钥密文在轮换提交后重新落库。
    private readonly SemaphoreSlim _secretWriteLock = new(1, 1);
    private readonly IDeviceQuickUnlockStore? _quickUnlockStore;
    // 新建或迁移时使用的 Argon2id 参数；测试注入更小值以避免跑 64 MiB
    private readonly Argon2Parameters _argon2Parameters;

    private bool _initialized;
    private bool _hasMasterPassword;
    private byte[]? _kdfSalt;
    private string _kdfId = VaultCryptography.Pbkdf2Sha512Id;
    // 数据密钥：新库为随机 Vault Key，尚未迁移的旧库暂为 PBKDF2 MEK
    private byte[]? _vaultKey;
    // 口令派生的包装密钥；仅口令解锁/设密后保留，与 Vault Key 一起在 Lock 时清零
    private byte[]? _wrappingKey;
    private byte[]? _vaultId;
    private bool _rotationPending;
    private bool _cleanupPending;
    private readonly object _keyStateLock = new();
    private long _lockVersion;

    public InternalVaultManager(string connectionString, ILogger<InternalVaultManager>? logger = null, IDeviceQuickUnlockStore? quickUnlockStore = null)
        : this(new SqliteConnectionFactory(connectionString), logger, quickUnlockStore, VaultCryptography.ProductionArgon2Parameters)
    {
    }

    public InternalVaultManager(SqliteConnectionFactory factory, ILogger<InternalVaultManager>? logger = null, IDeviceQuickUnlockStore? quickUnlockStore = null)
        : this(factory, logger, quickUnlockStore, VaultCryptography.ProductionArgon2Parameters)
    {
    }

    internal InternalVaultManager(SqliteConnectionFactory factory, ILogger<InternalVaultManager>? logger, IDeviceQuickUnlockStore? quickUnlockStore, Argon2Parameters argon2Parameters)
    {
        _factory = factory;
        _logger = logger ?? NullLogger<InternalVaultManager>.Instance;
        _quickUnlockStore = quickUnlockStore;
        _argon2Parameters = argon2Parameters;
    }

    // 无主密码 = 明文模式；默认（未初始化）按明文处理
    public bool IsPlainMode => !_hasMasterPassword;

    // 明文模式恒解锁；加密模式需数据密钥存在
    public bool IsUnlocked
    {
        get
        {
            lock (_keyStateLock) return !_hasMasterPassword || _vaultKey != null;
        }
    }

    private Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct) => _factory.OpenAsync(ct);

    // 惰性初始化：确保元数据表存在并加载元数据；不自动解锁
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
            {
                return;
            }

            // Schema 由迁移器统一维护（已是最新版本时仅一次 user_version 查询）
            await SchemaMigrator.MigrateAsync(_factory, ct);
            using var conn = await CreateConnectionAsync(ct);

            var meta = await LoadMetadataAsync(conn, ct);
            if (meta.TryGetValue(KeyPlainMode, out var plain))
            {
                _hasMasterPassword = plain != "1";
            }

            if (meta.TryGetValue(KeyKdfSalt, out var saltB64))
            {
                _kdfSalt = Convert.FromBase64String(saltB64);
            }

            if (meta.TryGetValue(KeyKdf, out var kdfId))
            {
                _kdfId = kdfId;
            }

            if (meta.TryGetValue(KeyVaultId, out var vaultIdB64))
            {
                _vaultId = Convert.FromBase64String(vaultIdB64);
            }

            _rotationPending = meta.GetValueOrDefault(KeyRotationPending) == "1";
            _cleanupPending = meta.GetValueOrDefault(KeyCleanupPending) == "1";

            _initialized = true;
            _logger.LogInformation("Vault 初始化完成 模式={Mode}", _hasMasterPassword ? "加密" : "明文");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public Task SetMasterPasswordAsync(string masterPassword, CancellationToken ct = default)
        => WithSecretWriteLockAsync(() => SetMasterPasswordCoreAsync(masterPassword, ct), ct);

    private async Task SetMasterPasswordCoreAsync(string masterPassword, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(masterPassword))
        {
            throw new ArgumentException("主密码不能为空", nameof(masterPassword));
        }

        await EnsureInitializedAsync(ct);
        EnsureUnlocked();
        string? previousQuickUnlockId = CurrentQuickUnlockKeyId();

        using SqliteConnection conn = await CreateConnectionAsync(ct);
        await CompletePendingCleanupAsync(conn, ct);

        // 清除更新行留下的旧页内容；VACUUM 与截断 WAL 另行处理历史空闲页和日志帧。
        using (SqliteCommand secureDelete = conn.CreateCommand())
        {
            secureDelete.CommandText = "PRAGMA secure_delete = ON;";
            await secureDelete.ExecuteNonQueryAsync(ct);
        }

        byte[]? oldKey;
        byte[]? vaultId;
        lock (_keyStateLock)
        {
            oldKey = _vaultKey;
            vaultId = _vaultId;
        }

        // 已是 Vault Key 的库换密码只换盐、重新包装同一个 Vault Key，不改条目密文
        bool reuseVaultKey = _hasMasterPassword && oldKey != null && vaultId != null;
        // 只有确实可能存在旧设备副本时才在换盐事务里写下轮换标记
        bool mayHaveDeviceCopy = reuseVaultKey && previousQuickUnlockId != null && _quickUnlockStore != null;
        byte[] vaultKey;
        if (reuseVaultKey)
        {
            vaultKey = oldKey!.ToArray();
        }
        else
        {
            vaultId = RandomNumberGenerator.GetBytes(VaultCryptography.VaultIdLength);
            vaultKey = RandomNumberGenerator.GetBytes(VaultCryptography.KeyLength);
        }

        byte[] salt = RandomNumberGenerator.GetBytes(VaultCryptography.SaltLength);
        string kdfId = VaultCryptography.FormatArgon2Id(_argon2Parameters);
        byte[] wrappingKey = VaultCryptography.DeriveArgon2Id(masterPassword, salt, _argon2Parameters);
        byte[] wrappedVaultKey = VaultCryptography.Seal(wrappingKey, VaultCryptography.BuildWrapAad(vaultId!), vaultKey);
        byte[] verifier = VaultCryptography.SealVerifier(vaultKey, vaultId!);

        int reEncrypted = 0;
        bool committed = false;
        try
        {
            using (SqliteTransaction tx = conn.BeginTransaction())
            {
                await UpsertMetadataAsync(conn, tx, KeyPlainMode, "0", ct);
                await UpsertMetadataAsync(conn, tx, KeyKdf, kdfId, ct);
                await UpsertMetadataAsync(conn, tx, KeyKdfSalt, Convert.ToBase64String(salt), ct);
                await UpsertMetadataAsync(conn, tx, KeyVaultId, Convert.ToBase64String(vaultId!), ct);
                await UpsertMetadataAsync(conn, tx, KeyWrappedVaultKey, Convert.ToBase64String(wrappedVaultKey), ct);
                await UpsertMetadataAsync(conn, tx, KeyVerifier, Convert.ToBase64String(verifier), ct);
                // 清理状态与新密钥元数据一起提交。进程中断后仍会在下次启动重试。
                await UpsertMetadataAsync(conn, tx, KeyCleanupPending, "1", ct);
                // 换盐只换查找名，旧设备副本的密钥字节仍可能解开当前库：
                // 在同一事务写下轮换标记，删除成功后再清回 "0"，删除失败或中断则下次口令解锁先轮换
                await UpsertMetadataAsync(conn, tx, KeyRotationPending, mayHaveDeviceCopy ? "1" : "0", ct);

                // 首次设置或从旧库升级：把全部旧密文换成带 AAD 的新算法
                if (!reuseVaultKey)
                {
                    reEncrypted = await ReEncryptAllSecretsAsync(conn, tx, oldKey, vaultKey, ct);
                }

                tx.Commit();
            }

            // 提交已完成，先使内存与磁盘一致。即使后续清理失败，新密码仍能解锁和重试。
            committed = true;
            lock (_keyStateLock)
            {
                ReplaceKeysLocked(vaultKey, wrappingKey, vaultId, salt, kdfId);
                _hasMasterPassword = true;
                _rotationPending = mayHaveDeviceCopy;
                _lockVersion++;
            }
            _cleanupPending = true;
            // 换盐只换了查找名，旧登记里的密钥字节仍可能解开当前库：删除失败必须轮换
            await RemovePreviousQuickUnlockOrRotateAsync(previousQuickUnlockId, ct);
            await CompletePendingCleanupAsync(conn, ct);
            _logger.LogInformation("设置主密码完成 重加密材料={Count} 项", reEncrypted);
        }
        finally
        {
            if (!committed)
            {
                CryptographicOperations.ZeroMemory(vaultKey);
                CryptographicOperations.ZeroMemory(wrappingKey);
            }
        }
    }

    public async Task<bool> TryAutoUnlockAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await WithSecretWriteLockAsync(async () =>
        {
            using SqliteConnection conn = await CreateConnectionAsync(ct);
            await CompletePendingCleanupAsync(conn, ct);
        }, ct);
        // 明文模式恒可用；加密模式走懒解锁，不在启动时自动发起系统认证。
        return !_hasMasterPassword;
    }

    public Task UnlockAsync(string masterPassword, bool rememberOnThisDevice, CancellationToken ct = default)
        => WithSecretWriteLockAsync(() => UnlockCoreAsync(masterPassword, ct), ct);

    private async Task UnlockCoreAsync(string masterPassword, CancellationToken ct)
    {
        long lockVersion;
        lock (_keyStateLock) lockVersion = _lockVersion;
        await EnsureInitializedAsync(ct);

        if (!_hasMasterPassword)
        {
            // 明文模式无锁定语义
            lock (_keyStateLock)
            {
                if (_vaultKey != null) CryptographicOperations.ZeroMemory(_vaultKey);
                if (_wrappingKey != null) CryptographicOperations.ZeroMemory(_wrappingKey);
                _vaultKey = null;
                _wrappingKey = null;
            }
            return;
        }

        if (_kdfSalt == null)
        {
            _logger.LogError("Vault 解锁失败：元数据缺失 kdf_salt");
            throw new InvalidOperationException("Vault 元数据缺失 kdf_salt");
        }

        var verifierB64 = await LoadValueAsync(KeyVerifier, ct);
        if (verifierB64 == null)
        {
            _logger.LogError("Vault 解锁失败：元数据缺失 verifier");
            throw new InvalidOperationException("Vault 元数据缺失 verifier");
        }

        var wrappedB64 = await LoadValueAsync(KeyWrappedVaultKey, ct);
        byte[] verifier = Convert.FromBase64String(verifierB64);

        // 未知或非法 KDF 标识抛 NotSupportedException，不能报成密码错误
        byte[] wrappingKey = VaultCryptography.DeriveWrappingKey(_kdfId, masterPassword, _kdfSalt);

        if (wrappedB64 == null)
        {
            // 旧库：口令派生值就是数据密钥，verifier 用 AES-256-GCM 保护
            if (!VaultCryptography.TryLegacyVerifierMatches(wrappingKey, verifier))
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
                _logger.LogWarning("Vault 解锁失败：主密码错误");
                throw new UnauthorizedAccessException("主密码错误");
            }

            // 在同一次解锁里迁移到 Vault Key + Argon2id；迁移失败不留下半新元数据
            await MigrateLegacyVaultAsync(wrappingKey, masterPassword, lockVersion, ct);
            _logger.LogInformation("Vault 由旧 PBKDF2 库迁移到 Vault Key");
            return;
        }

        byte[] vaultId = _vaultId ?? throw new InvalidOperationException("Vault 元数据缺失 vault_id");
        byte[] vaultKey;
        try
        {
            vaultKey = VaultCryptography.Open(wrappingKey, VaultCryptography.BuildWrapAad(vaultId), Convert.FromBase64String(wrappedB64));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            _logger.LogWarning("Vault 解锁失败：主密码错误");
            throw new UnauthorizedAccessException("主密码错误");
        }

        if (!VaultCryptography.VerifierMatches(vaultKey, vaultId, verifier))
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(vaultKey);
            _logger.LogWarning("Vault 解锁失败：主密码错误");
            throw new UnauthorizedAccessException("主密码错误");
        }

        bool installed = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_keyStateLock)
            {
                if (lockVersion != _lockVersion) throw new OperationCanceledException("保管库已重新锁定。");
                ReplaceKeysLocked(vaultKey, wrappingKey);
                installed = true;
            }
        }
        finally
        {
            if (!installed)
            {
                CryptographicOperations.ZeroMemory(vaultKey);
                CryptographicOperations.ZeroMemory(wrappingKey);
            }
        }

        // 上次回滚因缺少包装密钥只记了标记：现在有口令派生的包装密钥，先轮换再投入使用
        if (_rotationPending)
        {
            await RotateVaultKeyLockedAsync(ct);
        }

        using SqliteConnection conn = await CreateConnectionAsync(ct);
        await CompletePendingCleanupAsync(conn, ct);
        _logger.LogInformation("Vault 解锁成功");
    }

    // 旧库迁移：新盐 + Argon2id 包装随机 Vault Key，并把全部旧密文换成带 AAD 的新算法。
    // 调用方持有 _secretWriteLock 且已完成旧 verifier 校验（mek 即旧数据密钥）。
    private async Task MigrateLegacyVaultAsync(byte[] mek, string masterPassword, long lockVersion, CancellationToken ct)
    {
        string? previousQuickUnlockId = CurrentQuickUnlockKeyId();

        byte[] salt = RandomNumberGenerator.GetBytes(VaultCryptography.SaltLength);
        byte[] vaultId = RandomNumberGenerator.GetBytes(VaultCryptography.VaultIdLength);
        byte[] vaultKey = RandomNumberGenerator.GetBytes(VaultCryptography.KeyLength);
        string kdfId = VaultCryptography.FormatArgon2Id(_argon2Parameters);
        byte[] wrappingKey = VaultCryptography.DeriveArgon2Id(masterPassword, salt, _argon2Parameters);
        byte[] wrappedVaultKey = VaultCryptography.Seal(wrappingKey, VaultCryptography.BuildWrapAad(vaultId), vaultKey);
        byte[] verifier = VaultCryptography.SealVerifier(vaultKey, vaultId);

        bool installed = false;
        bool committed = false;
        try
        {
            using (SqliteConnection conn = await CreateConnectionAsync(ct))
            using (SqliteTransaction tx = conn.BeginTransaction())
            {
                await ReEncryptAllSecretsAsync(conn, tx, mek, vaultKey, ct);
                await UpsertMetadataAsync(conn, tx, KeyPlainMode, "0", ct);
                await UpsertMetadataAsync(conn, tx, KeyKdf, kdfId, ct);
                await UpsertMetadataAsync(conn, tx, KeyKdfSalt, Convert.ToBase64String(salt), ct);
                await UpsertMetadataAsync(conn, tx, KeyVaultId, Convert.ToBase64String(vaultId), ct);
                await UpsertMetadataAsync(conn, tx, KeyWrappedVaultKey, Convert.ToBase64String(wrappedVaultKey), ct);
                await UpsertMetadataAsync(conn, tx, KeyVerifier, Convert.ToBase64String(verifier), ct);
                await UpsertMetadataAsync(conn, tx, KeyCleanupPending, "1", ct);
                await UpsertMetadataAsync(conn, tx, KeyRotationPending, "0", ct);
                tx.Commit();
                committed = true;
            }

            lock (_keyStateLock)
            {
                if (lockVersion != _lockVersion) throw new OperationCanceledException("保管库已重新锁定。");
                ReplaceKeysLocked(vaultKey, wrappingKey, vaultId, salt, kdfId);
                _hasMasterPassword = true;
                _rotationPending = false;
                _lockVersion++;
                installed = true;
            }
        }
        finally
        {
            // 迁移失败必须清掉候选密钥，磁盘上不能留下半新元数据（事务已回滚）
            if (!installed)
            {
                if (committed)
                {
                    // 事务已提交但被并发锁定抢先：Lock() 清掉原先留在内存里的旧 MEK；
                    // 下次解锁按磁盘新格式重载
                    _logger.LogWarning("Vault 迁移已提交但未安装到内存，锁定并清掉旧数据密钥");
                    Lock();
                }
                CryptographicOperations.ZeroMemory(vaultKey);
                CryptographicOperations.ZeroMemory(wrappingKey);
            }
            CryptographicOperations.ZeroMemory(mek);
        }

        _cleanupPending = true;
        // 迁移已生成新 Vault Key，留给旧设备的字节必然打不开新 verifier，无需为迁移强制轮换
        if (previousQuickUnlockId != null && _quickUnlockStore != null)
        {
            await TryDeleteQuickUnlockAsync(previousQuickUnlockId, CancellationToken.None);
        }
        using SqliteConnection cleanupConn = await CreateConnectionAsync(ct);
        await CompletePendingCleanupAsync(cleanupConn, ct);
    }

    public void Lock()
    {
        lock (_keyStateLock)
        {
            _lockVersion++;
            if (_vaultKey != null) CryptographicOperations.ZeroMemory(_vaultKey);
            if (_wrappingKey != null) CryptographicOperations.ZeroMemory(_wrappingKey);
            _vaultKey = null;
            _wrappingKey = null;
        }
    }

    public async Task<Dictionary<string, SecretPayload>> GetSecretsAsync(Guid identityId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secrets_blob, encryption_algorithm FROM identity_secrets WHERE identity_id = $id;";
        cmd.Parameters.AddWithValue("$id", identityId.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new Dictionary<string, SecretPayload>();
        }

        var blob = (byte[])reader["secrets_blob"];
        var algorithm = reader.GetString(1);

        byte[] json = DecryptItem(identityId, blob, algorithm);
        return JsonSerializer.Deserialize<Dictionary<string, SecretPayload>>(json) ?? new();
    }

    public Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default)
        => WithSecretWriteLockAsync(() => SaveSecretsCoreAsync(identityId, secrets, ct), ct);

    private async Task SaveSecretsCoreAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        var json = JsonSerializer.SerializeToUtf8Bytes(secrets);
        var (blob, algorithm, nonce, tag) = EncryptItem(identityId, json);

        using var conn = await CreateConnectionAsync(ct);
        await CompletePendingCleanupAsync(conn, ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO identity_secrets (identity_id, secrets_blob, encryption_algorithm, nonce, tag)
            VALUES ($id, $blob, $alg, $nonce, $tag)
            ON CONFLICT(identity_id) DO UPDATE SET
                secrets_blob = $blob,
                encryption_algorithm = $alg,
                nonce = $nonce,
                tag = $tag;
        ";
        cmd.Parameters.AddWithValue("$id", identityId.ToString());
        cmd.Parameters.Add("$blob", SqliteType.Blob).Value = blob;
        cmd.Parameters.AddWithValue("$alg", algorithm);
        cmd.Parameters.AddWithValue("$nonce", (object?)nonce ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetPasswordAsync(Guid proxyId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secrets_blob, encryption_algorithm FROM proxy_secrets WHERE proxy_id = $id;";
        cmd.Parameters.AddWithValue("$id", proxyId.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var blob = (byte[])reader["secrets_blob"];
        var algorithm = reader.GetString(1);
        byte[] json = DecryptItem(proxyId, blob, algorithm);
        ProxySecretPayload? payload = JsonSerializer.Deserialize<ProxySecretPayload>(json);
        return string.IsNullOrEmpty(payload?.Password) ? null : payload.Password;
    }

    public Task SetPasswordAsync(Guid proxyId, string? password, CancellationToken ct = default)
        => WithSecretWriteLockAsync(() => SetPasswordCoreAsync(proxyId, password, ct), ct);

    private async Task SetPasswordCoreAsync(Guid proxyId, string? password, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        if (string.IsNullOrWhiteSpace(password))
        {
            using var deleteConn = await CreateConnectionAsync(ct);
            var delete = deleteConn.CreateCommand();
            delete.CommandText = "DELETE FROM proxy_secrets WHERE proxy_id = $id;";
            delete.Parameters.AddWithValue("$id", proxyId.ToString());
            await delete.ExecuteNonQueryAsync(ct);
            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new ProxySecretPayload { Password = password });
        var (blob, algorithm, nonce, tag) = EncryptItem(proxyId, json);

        using var conn = await CreateConnectionAsync(ct);
        await CompletePendingCleanupAsync(conn, ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO proxy_secrets (proxy_id, secrets_blob, encryption_algorithm, nonce, tag)
            VALUES ($id, $blob, $alg, $nonce, $tag)
            ON CONFLICT(proxy_id) DO UPDATE SET
                secrets_blob = $blob,
                encryption_algorithm = $alg,
                nonce = $nonce,
                tag = $tag;
        ";
        cmd.Parameters.AddWithValue("$id", proxyId.ToString());
        cmd.Parameters.Add("$blob", SqliteType.Blob).Value = blob;
        cmd.Parameters.AddWithValue("$alg", algorithm);
        cmd.Parameters.AddWithValue("$nonce", (object?)nonce ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> HasPasswordAsync(Guid proxyId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM proxy_secrets WHERE proxy_id = $id;";
        cmd.Parameters.AddWithValue("$id", proxyId.ToString());
        var value = await cmd.ExecuteScalarAsync(ct);
        return value != null && value != DBNull.Value;
    }

    public async Task DeleteSecretsAsync(Guid identityId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM identity_secrets WHERE identity_id = $id;";
        cmd.Parameters.AddWithValue("$id", identityId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private void EnsureUnlocked()
    {
        if (_hasMasterPassword && _vaultKey == null)
        {
            throw new InvalidOperationException("Vault 已锁定，请先解锁");
        }
    }

    // 明文直通；加密块按算法分别解；新算法 AAD 绑定行主键
    private byte[] DecryptItem(Guid itemId, byte[] blob, string algorithm)
    {
        if (algorithm == VaultCryptography.PlainAlgorithm)
        {
            return blob;
        }

        EnsureUnlocked();
        if (algorithm == VaultCryptography.SealedAlgorithm)
        {
            return VaultCryptography.Open(_vaultKey!, VaultCryptography.BuildItemAad(itemId), blob);
        }

        if (algorithm == VaultCryptography.LegacyAlgorithm)
        {
            return VaultCryptography.LegacyOpen(_vaultKey!, blob);
        }

        throw new NotSupportedException($"不支持的 Vault 条目算法: {algorithm}");
    }

    private (byte[] Blob, string Algorithm, byte[]? Nonce, byte[]? Tag) EncryptItem(Guid itemId, byte[] plaintext)
    {
        if (!_hasMasterPassword)
        {
            // 明文模式：原始 JSON 字节落库（UI 已警示不加密）
            return (plaintext, VaultCryptography.PlainAlgorithm, null, null);
        }

        EnsureUnlocked();
        if (_vaultId != null)
        {
            byte[] sealedBlock = VaultCryptography.Seal(_vaultKey!, VaultCryptography.BuildItemAad(itemId), plaintext);
            return (sealedBlock, VaultCryptography.SealedAlgorithm, VaultCryptography.SplitNonce(sealedBlock), VaultCryptography.SplitTag(sealedBlock));
        }

        // 尚未迁移的旧库仍写 AES-256-GCM
        byte[] legacyBlock = VaultCryptography.LegacySeal(_vaultKey!, plaintext);
        return (legacyBlock, VaultCryptography.LegacyAlgorithm,
            legacyBlock.AsSpan(0, VaultCryptography.LegacyNonceLength).ToArray(),
            legacyBlock.AsSpan(VaultCryptography.LegacyNonceLength, VaultCryptography.LegacyTagLength).ToArray());
    }

    // 把两种秘密表的全部行从 oldKey 换成 newKey；旧算法行与 PLAIN 行一并升级为带 AAD 的新算法
    private static async Task<int> ReEncryptAllSecretsAsync(SqliteConnection conn, SqliteTransaction tx, byte[]? oldKey, byte[] newKey, CancellationToken ct)
    {
        int count = await ReEncryptTableAsync(conn, tx, oldKey, newKey, "identity_secrets", "identity_id", ct);
        count += await ReEncryptTableAsync(conn, tx, oldKey, newKey, "proxy_secrets", "proxy_id", ct);
        return count;
    }

    // 表名与主键列是内部常量，不接受外部输入
    private static async Task<int> ReEncryptTableAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        byte[]? oldKey,
        byte[] newKey,
        string table,
        string idColumn,
        CancellationToken ct)
    {
        var pending = new List<(string id, string algorithm, byte[] blob)>();
        using (var select = conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = $"SELECT {idColumn}, encryption_algorithm, secrets_blob FROM {table};";
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                pending.Add((reader.GetString(0), reader.GetString(1), (byte[])reader["secrets_blob"]));
            }
        }

        foreach (var (id, algorithm, blob) in pending)
        {
            Guid itemId = Guid.Parse(id);
            byte[] aad = VaultCryptography.BuildItemAad(itemId);
            byte[] plaintext = algorithm switch
            {
                VaultCryptography.PlainAlgorithm => blob,
                VaultCryptography.LegacyAlgorithm => oldKey != null
                    ? VaultCryptography.LegacyOpen(oldKey, blob)
                    : throw new InvalidOperationException("保管库材料迁移缺少旧密钥"),
                VaultCryptography.SealedAlgorithm => oldKey != null
                    ? VaultCryptography.Open(oldKey, aad, blob)
                    : throw new InvalidOperationException("保管库材料迁移缺少旧密钥"),
                _ => throw new NotSupportedException($"不支持的 Vault 条目算法: {algorithm}"),
            };

            byte[] wrapped;
            try
            {
                wrapped = VaultCryptography.Seal(newKey, aad, plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = $@"
                UPDATE {table}
                SET secrets_blob = $blob, encryption_algorithm = $alg, nonce = $nonce, tag = $tag
                WHERE {idColumn} = $id;
            ";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.Add("$blob", SqliteType.Blob).Value = wrapped;
            update.Parameters.AddWithValue("$alg", VaultCryptography.SealedAlgorithm);
            update.Parameters.AddWithValue("$nonce", VaultCryptography.SplitNonce(wrapped));
            update.Parameters.AddWithValue("$tag", VaultCryptography.SplitTag(wrapped));
            await update.ExecuteNonQueryAsync(ct);
        }

        return pending.Count;
    }

    // 生成新 Vault Key，重加密全部条目并重写 verifier；调用方必须持有 _secretWriteLock 且处于解锁态
    private async Task RotateVaultKeyLockedAsync(CancellationToken ct)
    {
        byte[]? oldKey;
        byte[]? wrappingKey;
        byte[]? vaultId;
        lock (_keyStateLock)
        {
            if (_vaultKey == null || _wrappingKey == null || _vaultId == null) return;
            oldKey = _vaultKey;
            wrappingKey = _wrappingKey;
            vaultId = _vaultId;
        }

        byte[] newVaultKey = RandomNumberGenerator.GetBytes(VaultCryptography.KeyLength);
        byte[] wrappedVaultKey = VaultCryptography.Seal(wrappingKey, VaultCryptography.BuildWrapAad(vaultId), newVaultKey);
        byte[] verifier = VaultCryptography.SealVerifier(newVaultKey, vaultId);

        bool installed = false;
        try
        {
            using SqliteConnection conn = await CreateConnectionAsync(ct);
            using (SqliteTransaction tx = conn.BeginTransaction())
            {
                await ReEncryptAllSecretsAsync(conn, tx, oldKey, newVaultKey, ct);
                await UpsertMetadataAsync(conn, tx, KeyWrappedVaultKey, Convert.ToBase64String(wrappedVaultKey), ct);
                await UpsertMetadataAsync(conn, tx, KeyVerifier, Convert.ToBase64String(verifier), ct);
                await UpsertMetadataAsync(conn, tx, KeyRotationPending, "0", ct);
                tx.Commit();
            }

            lock (_keyStateLock)
            {
                // 轮换提交期间若被并发 Lock，内存不安装，下次解锁按磁盘新格式重载
                if (ReferenceEquals(_vaultKey, oldKey))
                {
                    CryptographicOperations.ZeroMemory(oldKey);
                    _vaultKey = newVaultKey;
                    _rotationPending = false;
                    _lockVersion++;
                    installed = true;
                }
            }

            _logger.LogInformation("Vault Key 已轮换");
        }
        finally
        {
            if (!installed) CryptographicOperations.ZeroMemory(newVaultKey);
        }
    }

    // 删除旧快速解锁项；删除失败说明换盐只换了查找名，必须轮换 Vault Key 使遗留副本真正失效
    private async Task RemovePreviousQuickUnlockOrRotateAsync(string? keyId, CancellationToken ct)
    {
        if (keyId == null || _quickUnlockStore == null) return;
        if (await TryDeleteQuickUnlockAsync(keyId, ct))
        {
            // 删除成功，旧副本已不存在：另起一次写入把换盐时下的轮换标记清回 "0"
            await ClearRotationPendingAsync(ct);
            return;
        }

        _logger.LogWarning("删除旧快速解锁登记失败，轮换 Vault Key 以使遗留副本失效");
        // 轮换成功会在其事务里把标记写成 "0"；若因内存里已无密钥而直接返回，标记保持 "1"
        await RotateVaultKeyLockedAsync(ct);
        // 轮换后再删一次；即使再失败，旧字节已解不开当前库
        await TryDeleteQuickUnlockAsync(keyId, ct);
    }

    // 单独一次写入把轮换标记清回 "0"；调用方持有 _secretWriteLock
    private async Task ClearRotationPendingAsync(CancellationToken ct)
    {
        using SqliteConnection conn = await CreateConnectionAsync(ct);
        using SqliteTransaction tx = conn.BeginTransaction();
        await UpsertMetadataAsync(conn, tx, KeyRotationPending, "0", ct);
        tx.Commit();
        lock (_keyStateLock) { _rotationPending = false; }
    }

    private async Task<bool> TryDeleteQuickUnlockAsync(string keyId, CancellationToken ct)
    {
        try
        {
            await _quickUnlockStore!.DeleteKeyAsync(keyId, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("无法删除本机快速解锁项，错误类型={ErrorType}", ex.GetType().Name);
            return false;
        }
    }

    // 调用方持有 _keyStateLock；负责清零被替换的旧密钥
    private void ReplaceKeysLocked(byte[] vaultKey, byte[]? wrappingKey, byte[]? vaultId = null, byte[]? salt = null, string? kdfId = null)
    {
        if (_vaultKey != null) CryptographicOperations.ZeroMemory(_vaultKey);
        if (_wrappingKey != null) CryptographicOperations.ZeroMemory(_wrappingKey);
        _vaultKey = vaultKey;
        _wrappingKey = wrappingKey;
        if (vaultId != null) _vaultId = vaultId;
        if (salt != null) _kdfSalt = salt;
        if (kdfId != null) _kdfId = kdfId;
    }

    private sealed class ProxySecretPayload
    {
        public string? Password { get; set; }
    }

    private static async Task<Dictionary<string, string>> LoadMetadataAsync(SqliteConnection conn, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM vault_metadata;";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private async Task<string?> LoadValueAsync(string key, CancellationToken ct)
    {
        using var conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM vault_metadata WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value as string;
    }

    private static async Task UpsertMetadataAsync(SqliteConnection conn, SqliteTransaction tx, string key, string value, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO vault_metadata (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
        ";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
