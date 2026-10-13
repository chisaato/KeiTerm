namespace Kei.Term.Infrastructure.Vault;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;

// 材料库（IVaultSecretLibrary）实现：vault_secrets 行的加解密，以及旧 identity_secrets 整包的运行时迁移挂载。
// 载荷复用主类 EncryptItem/DecryptItem（AAD 绑定条目 Id），明文模式直通。
public partial class InternalVaultManager
{
    private const string KeySecretLibraryMigrated = "secret_library_migrated";
    private const string KeySecretLibraryMigratedIdentities = "secret_library_migrated_identities";

    // 惰性构造：仅在首次访问材料库时建仓储
    private SqliteVaultSecretRepository? _secretRepository;
    private SqliteVaultSecretRepository SecretRepository => _secretRepository ??= new SqliteVaultSecretRepository(_factory);

    // 只读元数据列表：未解锁也能列出与选择
    public async Task<IReadOnlyList<VaultSecret>> ListAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await SecretRepository.ListAsync(ct);
    }

    // 取载荷需解锁；未命中的条目仅在「未迁移期间」回退旧整包（迁移前的只读兼容路径）。
    // 迁移完成后旧数据不再复活：删除过的材料不得经旧表重新读出。
    public async Task<SecretPayload?> GetPayloadAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        VaultSecretEncrypted? encrypted = await SecretRepository.GetAsync(id, ct);
        if (encrypted != null)
        {
            byte[] json = DecryptItem(id, encrypted.Envelope.Blob, encrypted.Envelope.Algorithm);
            return JsonSerializer.Deserialize<SecretPayload>(json);
        }

        if (!await IsLegacyFallbackEnabledAsync(ct))
        {
            return null;
        }

        return await ReadLegacyPayloadAsync(id, ct);
    }

    // 完成标记为 "1" 表示旧表已完整迁移，读取路径不再回退旧整包。
    // 部分迁移（存在损坏项）不写标记，回退保持开启以供未迁移材料继续可读。
    private async Task<bool> IsLegacyFallbackEnabledAsync(CancellationToken ct)
        => await LoadValueAsync(KeySecretLibraryMigrated, ct) != "1";

    public Task SaveAsync(VaultSecret secret, SecretPayload? payload, CancellationToken ct = default)
        => WithSecretWriteLockAsync(() => SaveSecretCoreAsync(secret, payload, ct), ct);

    private async Task SaveSecretCoreAsync(VaultSecret secret, SecretPayload? payload, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        if (secret.Id == Guid.Empty)
        {
            throw new ArgumentException("材料条目 Id 不能为空", nameof(secret));
        }

        VaultSecretEnvelope envelope;
        if (payload != null)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
            var (blob, algorithm, nonce, tag) = EncryptItem(secret.Id, json);
            envelope = new VaultSecretEnvelope(blob, algorithm, nonce, tag);
        }
        else
        {
            // 只改元数据：复用已存密文封套，避免用空载荷覆盖
            VaultSecretEncrypted? existing = await SecretRepository.GetAsync(secret.Id, ct);
            if (existing == null)
            {
                throw new ArgumentException("payload=null 只更新元数据，条目必须已存在", nameof(payload));
            }

            envelope = existing.Envelope;
        }

        await SecretRepository.UpsertAsync(secret, envelope, ct);
    }

    // 删除不检查引用，由上层服务先校验
    public Task DeleteAsync(Guid id, CancellationToken ct = default)
        => WithSecretWriteLockAsync(async () =>
        {
            await EnsureInitializedAsync(ct);
            await SecretRepository.DeleteAsync(id, ct);
        }, ct);

    // 旧整包回退读：仅对「尚未迁移」身份的整包开放（跳过已迁移集合内的身份，
    // 避免用户已删除的材料经旧表复活）；逐整包独立容错，损坏整包跳过、未命中返回 null。
    private async Task<SecretPayload?> ReadLegacyPayloadAsync(Guid id, CancellationToken ct)
    {
        string key = id.ToString();
        HashSet<Guid> migrated = await ReadMigratedIdentitySetAsync(ct);
        using SqliteConnection conn = await CreateConnectionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT identity_id, secrets_blob, encryption_algorithm FROM identity_secrets;";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!Guid.TryParse(reader.GetString(0), out Guid identityId) || migrated.Contains(identityId))
            {
                continue;
            }

            try
            {
                byte[] blob = (byte[])reader["secrets_blob"];
                string algorithm = reader.GetString(2);
                byte[] json = DecryptItem(identityId, blob, algorithm);
                Dictionary<string, SecretPayload>? bundle = JsonSerializer.Deserialize<Dictionary<string, SecretPayload>>(json);
                if (bundle != null && bundle.TryGetValue(key, out SecretPayload? payload))
                {
                    return payload;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个整包损坏不阻断其余整包的回退读取；只记身份 Id 与异常类型
                _logger.LogWarning("材料库回退读取旧整包失败 identity={IdentityId} 错误类型={ErrorType}", identityId, ex.GetType().Name);
            }
        }

        return null;
    }

    // 读取「已迁移身份 Id 集合」；键缺失或损坏时按空集处理（宁可回退也不误判为已迁移）
    private async Task<HashSet<Guid>> ReadMigratedIdentitySetAsync(CancellationToken ct)
    {
        string? json = await LoadValueAsync(KeySecretLibraryMigratedIdentities, ct);
        var set = new HashSet<Guid>();
        if (string.IsNullOrEmpty(json))
        {
            return set;
        }

        try
        {
            List<string>? ids = JsonSerializer.Deserialize<List<string>>(json);
            if (ids != null)
            {
                foreach (string value in ids)
                {
                    if (Guid.TryParse(value, out Guid identityId))
                    {
                        set.Add(identityId);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 集合损坏：按空集处理
        }

        return set;
    }

    // 迁移挂载点调用：明文模式或已解锁时运行一次；
    // 损坏的旧整包/身份按「跳过」容错（不阻断其余迁移），失败不阻断解锁。
    private async Task TryMigrateLegacySecretsAsync(CancellationToken ct)
    {
        // 加密模式未解锁（无数据密钥）时不迁移；明文模式 IsUnlocked 恒真
        if (!IsUnlocked)
        {
            return;
        }

        if (await LoadValueAsync(KeySecretLibraryMigrated, ct) == "1")
        {
            return;
        }

        try
        {
            SecretMigrationStatus status =
                await new LegacySecretMigrator(_factory).RunAsync(DecryptItem, EncryptItem, _logger, ct);
            switch (status)
            {
                case SecretMigrationStatus.Completed:
                    _logger.LogInformation("Vault 材料库迁移完成");
                    break;
                case SecretMigrationStatus.Partial:
                    // 存在被跳过的损坏项：不写完成标记，旧表保留，下次解锁重试补迁
                    _logger.LogWarning("Vault 材料库迁移部分完成：跳过损坏的旧材料，保留旧表并在下次解锁重试");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 迁移整体回滚，旧 identity_secrets 保留；本次解锁照常成功
            _logger.LogWarning("材料库迁移失败，保留旧数据：{ErrorType}", ex.GetType().Name);
        }
    }
}
