namespace Kei.Term.Infrastructure.Vault;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;
using Kei.Term.Infrastructure.Storage.Schema;

// 内置 Vault：主密码可选。明文模式下字节直通；加密模式用 AES-256-GCM。
//
// KDF 说明：规格要求 Argon2id（m=64MB, t=3, p=4），但已查证 .NET 10 内置加密库
// （Microsoft.NETCore.App.Ref 10.0.2 的 System.Security.Cryptography）未提供任何
// Argon2/Kryptos 类型，故一期以 PBKDF2-Rfc2898DeriveBytes(HMAC-SHA512, 600k 迭代,
// salt 16B, 派生 32B) 替代，差异记入实现报告；盐与密钥长度与规格一致，未来可无损切换 KDF。
public partial class InternalVaultManager : IVaultManager, IVaultSecretStore, IProxySecretStore, IQuickUnlockVault
{
    // vault_metadata 键
    private const string KeyPlainMode = "plain_mode";
    private const string KeyKdfSalt = "kdf_salt";
    private const string KeyVerifier = "verifier";
    // KDF 标识与参数：缺省（早期库未写入）即视为 Pbkdf2Sha512Id，为未来切换 Argon2id 留出版本位
    private const string KeyKdf = "kdf";
    private const string KeyCleanupPending = "plaintext_cleanup_pending";
    private const string Pbkdf2Sha512Id = "pbkdf2-sha512:600000";

    private const int Pbkdf2Iterations = 600_000;
    private const int MekLength = 32;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private const string PlainAlgorithm = "PLAIN";
    private const string EncryptedAlgorithm = "AES-256-GCM";

    // 解锁校验用已知明文：用候选 MEK 解密后比对
    private const string VerifierPlaintext = "keiterm-vault-verifier-v1";

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<InternalVaultManager> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    // 密钥轮换与秘密写入互斥，避免旧密钥密文在轮换提交后重新落库。
    private readonly SemaphoreSlim _secretWriteLock = new(1, 1);

    private bool _initialized;
    private bool _hasMasterPassword;
    private byte[]? _kdfSalt;
    private string _kdfId = Pbkdf2Sha512Id;
    private byte[]? _mek;
    private bool _cleanupPending;
    private readonly object _keyStateLock = new();
    private long _lockVersion;
    private readonly IDeviceQuickUnlockStore? _quickUnlockStore;

    public InternalVaultManager(string connectionString, ILogger<InternalVaultManager>? logger = null, IDeviceQuickUnlockStore? quickUnlockStore = null)
        : this(new SqliteConnectionFactory(connectionString), logger, quickUnlockStore)
    {
    }

    public InternalVaultManager(SqliteConnectionFactory factory, ILogger<InternalVaultManager>? logger = null, IDeviceQuickUnlockStore? quickUnlockStore = null)
    {
        _factory = factory;
        _logger = logger ?? NullLogger<InternalVaultManager>.Instance;
        _quickUnlockStore = quickUnlockStore;
    }

    // 无主密码 = 明文模式；默认（未初始化）按明文处理
    public bool IsPlainMode => !_hasMasterPassword;

    // 明文模式恒解锁；加密模式需 MEK 存在
    public bool IsUnlocked
    {
        get
        {
            lock (_keyStateLock) return !_hasMasterPassword || _mek != null;
        }
    }

    private Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct) => _factory.OpenAsync(ct);

    // 惰性初始化：确保元数据表存在并加载 plain_mode/kdf_salt；不自动解锁
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

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        // 新设/更换主密码一律采用当前默认 KDF
        var mek = DeriveKey(Pbkdf2Sha512Id, masterPassword, salt);
        var verifier = EncryptWithMek(mek, Encoding.UTF8.GetBytes(VerifierPlaintext));

        int reEncrypted = 0;
        bool committed = false;
        try
        {
            using (SqliteTransaction tx = conn.BeginTransaction())
            {
                await UpsertMetadataAsync(conn, tx, KeyPlainMode, "0", ct);
                await UpsertMetadataAsync(conn, tx, KeyKdfSalt, Convert.ToBase64String(salt), ct);
                await UpsertMetadataAsync(conn, tx, KeyVerifier, Convert.ToBase64String(verifier), ct);
                await UpsertMetadataAsync(conn, tx, KeyKdf, Pbkdf2Sha512Id, ct);
                // 清理状态与新密钥元数据一起提交。进程中断后仍会在下次启动重试。
                await UpsertMetadataAsync(conn, tx, KeyCleanupPending, "1", ct);

                // 先重包全部旧密文，再升级明文，避免把新密文当作旧密文处理。
                if (_mek != null)
                {
                    reEncrypted = await RewrapEncryptedSecretsAsync(conn, tx, _mek, mek, "identity_secrets", "identity_id", ct);
                    reEncrypted += await RewrapEncryptedSecretsAsync(conn, tx, _mek, mek, "proxy_secrets", "proxy_id", ct);
                }

                reEncrypted += await ReEncryptPlainSecretsAsync(conn, tx, mek, ct);
                tx.Commit();
            }

            // 提交已完成，先使内存与磁盘一致。即使后续清理失败，新密码仍能解锁和重试。
            committed = true;
            lock (_keyStateLock)
            {
                if (_mek != null) CryptographicOperations.ZeroMemory(_mek);
                _kdfSalt = salt;
                _kdfId = Pbkdf2Sha512Id;
                _mek = mek;
                _hasMasterPassword = true;
                _lockVersion++;
            }
            _cleanupPending = true;
            await RemovePreviousQuickUnlockAsync(previousQuickUnlockId);
            await CompletePendingCleanupAsync(conn, ct);
            _logger.LogInformation("设置主密码完成 重加密材料={Count} 项", reEncrypted);
        }
        finally
        {
            if (!committed) CryptographicOperations.ZeroMemory(mek);
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
            lock (_keyStateLock) _mek = null;
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

        byte[] candidate = DeriveKey(_kdfId, masterPassword, _kdfSalt);
        bool installed = false;
        try
        {
            byte[] plain = DecryptWithMek(candidate, Convert.FromBase64String(verifierB64));
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(plain, Encoding.UTF8.GetBytes(VerifierPlaintext)))
                {
                    _logger.LogWarning("Vault 解锁失败：主密码错误");
                    throw new UnauthorizedAccessException("主密码错误");
                }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }

            ct.ThrowIfCancellationRequested();
            lock (_keyStateLock)
            {
                if (lockVersion != _lockVersion) throw new OperationCanceledException("保管库已重新锁定。");
                if (_mek != null) CryptographicOperations.ZeroMemory(_mek);
                _mek = candidate;
                installed = true;
            }
        }
        catch (CryptographicException)
        {
            // GCM 校验失败即密码错误
            _logger.LogWarning("Vault 解锁失败：主密码错误");
            throw new UnauthorizedAccessException("主密码错误");
        }
        finally
        {
            if (!installed) CryptographicOperations.ZeroMemory(candidate);
        }

        using SqliteConnection conn = await CreateConnectionAsync(ct);
        await CompletePendingCleanupAsync(conn, ct);
        _logger.LogInformation("Vault 解锁成功");
        // 保留旧参数的兼容性；本机快速解锁须通过独立的主密码确认流程启用。
    }

    public void Lock()
    {
        lock (_keyStateLock)
        {
            _lockVersion++;
            if (_mek != null) CryptographicOperations.ZeroMemory(_mek);
            _mek = null;
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

        byte[] json;
        if (algorithm == PlainAlgorithm)
        {
            json = blob;
        }
        else
        {
            EnsureUnlocked();
            json = DecryptWithMek(_mek!, blob);
        }

        return JsonSerializer.Deserialize<Dictionary<string, SecretPayload>>(json) ?? new();
    }

    public Task SaveSecretsAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct = default)
        => WithSecretWriteLockAsync(() => SaveSecretsCoreAsync(identityId, secrets, ct), ct);

    private async Task SaveSecretsCoreAsync(Guid identityId, Dictionary<string, SecretPayload> secrets, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        var json = JsonSerializer.SerializeToUtf8Bytes(secrets);

        byte[] blob;
        string algorithm;
        byte[]? nonce = null;
        byte[]? tag = null;

        if (_hasMasterPassword)
        {
            EnsureUnlocked();
            blob = EncryptWithMek(_mek!, json);
            algorithm = EncryptedAlgorithm;
            // 同步拆分出 nonce/tag 列，兼容规格表结构
            nonce = blob.AsSpan(0, NonceLength).ToArray();
            tag = blob.AsSpan(NonceLength, TagLength).ToArray();
        }
        else
        {
            // 明文模式：原始 JSON 字节落库（UI 已警示不加密）
            blob = json;
            algorithm = PlainAlgorithm;
        }

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
        return ReadPassword(blob, algorithm);
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
        byte[] blob;
        string algorithm;
        byte[]? nonce = null;
        byte[]? tag = null;

        if (_hasMasterPassword)
        {
            EnsureUnlocked();
            blob = EncryptWithMek(_mek!, json);
            algorithm = EncryptedAlgorithm;
            nonce = blob.AsSpan(0, NonceLength).ToArray();
            tag = blob.AsSpan(NonceLength, TagLength).ToArray();
        }
        else
        {
            blob = json;
            algorithm = PlainAlgorithm;
        }

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
        if (_hasMasterPassword && _mek == null)
        {
            throw new InvalidOperationException("Vault 已锁定，请先解锁");
        }
    }

    private string? ReadPassword(byte[] blob, string algorithm)
    {
        byte[] json;
        if (algorithm == PlainAlgorithm)
        {
            json = blob;
        }
        else
        {
            EnsureUnlocked();
            json = DecryptWithMek(_mek!, blob);
        }

        ProxySecretPayload? payload = JsonSerializer.Deserialize<ProxySecretPayload>(json);
        return string.IsNullOrEmpty(payload?.Password) ? null : payload.Password;
    }

    // 明文升级为加密：逐行把 PLAIN 材料块用新 MEK 重新加密；返回重加密行数
    private static async Task<int> ReEncryptPlainSecretsAsync(SqliteConnection conn, SqliteTransaction tx, byte[] mek, CancellationToken ct)
    {
        int count = await ReEncryptPlainTableAsync(conn, tx, mek, "identity_secrets", "identity_id", ct);
        count += await ReEncryptPlainTableAsync(conn, tx, mek, "proxy_secrets", "proxy_id", ct);
        return count;
    }

    // 表名与主键列是内部常量，不接受外部输入
    private static async Task<int> ReEncryptPlainTableAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        byte[] mek,
        string table,
        string idColumn,
        CancellationToken ct)
    {
        var pending = new List<(string id, byte[] plaintext)>();
        using (var select = conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = $"SELECT {idColumn}, secrets_blob FROM {table} WHERE encryption_algorithm = $alg;";
            select.Parameters.AddWithValue("$alg", PlainAlgorithm);
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                pending.Add((reader.GetString(0), (byte[])reader["secrets_blob"]));
            }
        }

        foreach (var (id, plaintext) in pending)
        {
            var blob = EncryptWithMek(mek, plaintext);
            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = $@"
                UPDATE {table}
                SET secrets_blob = $blob, encryption_algorithm = $alg, nonce = $nonce, tag = $tag
                WHERE {idColumn} = $id;
            ";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.Add("$blob", SqliteType.Blob).Value = blob;
            update.Parameters.AddWithValue("$alg", EncryptedAlgorithm);
            update.Parameters.AddWithValue("$nonce", blob.AsSpan(0, NonceLength).ToArray());
            update.Parameters.AddWithValue("$tag", blob.AsSpan(NonceLength, TagLength).ToArray());
            await update.ExecuteNonQueryAsync(ct);
        }

        return pending.Count;
    }

    // 两种秘密表都必须用旧 MEK 解开、新 MEK 重包；表名和主键仅接受内部常量。
    private static async Task<int> RewrapEncryptedSecretsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        byte[] oldMek,
        byte[] newMek,
        string table,
        string idColumn,
        CancellationToken ct)
    {
        var pending = new List<(string id, byte[] plaintext)>();
        using (var select = conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = $"SELECT {idColumn}, secrets_blob FROM {table} WHERE encryption_algorithm = $alg;";
            select.Parameters.AddWithValue("$alg", EncryptedAlgorithm);
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                pending.Add((reader.GetString(0), (byte[])reader["secrets_blob"]));
            }
        }

        foreach (var (id, blob) in pending)
        {
            byte[] plaintext;
            try
            {
                plaintext = DecryptWithMek(oldMek, blob);
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("保管库材料重包失败");
            }

            var wrapped = EncryptWithMek(newMek, plaintext);
            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = $@"
                UPDATE {table}
                SET secrets_blob = $blob, encryption_algorithm = $alg, nonce = $nonce, tag = $tag
                WHERE {idColumn} = $id;
            ";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.Add("$blob", SqliteType.Blob).Value = wrapped;
            update.Parameters.AddWithValue("$alg", EncryptedAlgorithm);
            update.Parameters.AddWithValue("$nonce", wrapped.AsSpan(0, NonceLength).ToArray());
            update.Parameters.AddWithValue("$tag", wrapped.AsSpan(NonceLength, TagLength).ToArray());
            await update.ExecuteNonQueryAsync(ct);
        }

        return pending.Count;
    }

    private sealed class ProxySecretPayload
    {
        public string? Password { get; set; }
    }

    private static byte[] DeriveKey(string kdfId, string password, byte[] salt)
    {
        // 未知 KDF 标识说明库由更新版本写入：拒绝猜测，避免用错误算法反复"密码错误"
        if (!string.Equals(kdfId, Pbkdf2Sha512Id, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"不支持的 Vault KDF: {kdfId}");
        }

        // PBKDF2-HMAC-SHA512：替代 Argon2id（.NET 10 无内置 Argon2）
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, MekLength);
    }

    // blob = [nonce 12B][tag 16B][ciphertext]
    private static byte[] EncryptWithMek(byte[] mek, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var tag = new byte[TagLength];
        var cipher = new byte[plaintext.Length];

        using (var aes = new AesGcm(mek, TagLength))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag);
        }

        var blob = new byte[NonceLength + TagLength + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceLength);
        cipher.CopyTo(blob, NonceLength + TagLength);
        return blob;
    }

    private static byte[] DecryptWithMek(byte[] mek, byte[] blob)
    {
        if (blob.Length < NonceLength + TagLength)
        {
            throw new CryptographicException("Vault blob 长度非法");
        }

        var nonce = blob.AsSpan(0, NonceLength);
        var tag = blob.AsSpan(NonceLength, TagLength);
        var cipher = blob.AsSpan(NonceLength + TagLength);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(mek, TagLength);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
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
