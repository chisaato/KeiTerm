namespace Kei.Term.Infrastructure.Vault;

using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Kei.Term.Core.Vault;
using Kei.Term.Infrastructure.Storage;

// 旧 identity_secrets（身份级整包）→ vault_secrets（材料库）的运行时迁移。
// 单事务：解密整包、规划拆分、写新表、回填 methods_json、写已迁移身份集合与完成标记。
// 写入中途 IO/约束失败仍整体回滚并向上抛；仅对「身份 Id 损坏 / methods_json 损坏 / 整包解密解析失败」
// 按身份容错跳过，存在跳过项时不写完成标记，旧数据保留，下次解锁重试补迁。
// 已迁移身份记录在 vault_metadata[MigratedIdentitiesKey]，重试时整体跳过，避免重复插入、
// 覆盖用户改绑的 SecretId 与刷新 updated_at。
internal enum SecretMigrationStatus
{
    // 已有完成标记，未执行
    AlreadyDone,
    // 全部身份迁移成功并写入完成标记
    Completed,
    // 有损坏身份被跳过：已提交其余迁移，但未写完成标记（下次解锁重试）
    Partial,
}

internal sealed class LegacySecretMigrator
{
    internal const string MarkerKey = "secret_library_migrated";
    internal const string MigratedIdentitiesKey = "secret_library_migrated_identities";

    private readonly SqliteConnectionFactory _factory;

    public LegacySecretMigrator(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    // 执行迁移；已有完成标记时返回 AlreadyDone。
    public async Task<SecretMigrationStatus> RunAsync(
        Func<Guid, byte[], string, byte[]> decryptItem,
        Func<Guid, byte[], (byte[] Blob, string Algorithm, byte[]? Nonce, byte[]? Tag)> encryptItem,
        ILogger logger,
        CancellationToken ct = default)
    {
        using SqliteConnection conn = await _factory.OpenAsync(ct);
        using SqliteTransaction tx = conn.BeginTransaction();

        string? marker = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM vault_metadata WHERE key = @key;",
            new { key = MarkerKey }, transaction: tx, cancellationToken: ct));
        if (marker == "1")
        {
            return SecretMigrationStatus.AlreadyDone;
        }

        // 已迁移身份集合：这些身份的整包/方法不再处理（重试幂等 + 不回退旧数据）
        HashSet<Guid> migrated = await ReadMigratedSetAsync(conn, tx, ct);

        List<IdentityRow> identityRows = (await conn.QueryAsync<IdentityRow>(new CommandDefinition(
            "SELECT id, name, methods_json FROM identities;", transaction: tx, cancellationToken: ct))).ToList();
        List<SecretRow> secretRows = (await conn.QueryAsync<SecretRow>(new CommandDefinition(
            "SELECT identity_id, secrets_blob, encryption_algorithm FROM identity_secrets;",
            transaction: tx, cancellationToken: ct))).ToList();

        // 本次跳过的损坏项：只要存在就不写完成标记（旧表保留、下次解锁重试）
        bool hasSkipped = false;

        var identities = new List<Identity>();
        foreach (IdentityRow row in identityRows)
        {
            if (!Guid.TryParse(row.Id, out Guid identityId))
            {
                // Id 列损坏：按损坏项跳过，不阻断其余身份
                logger.LogWarning("材料库迁移身份 Id 无效 错误类型=GuidParse");
                hasSkipped = true;
                continue;
            }

            if (migrated.Contains(identityId))
            {
                // 已迁移身份：不解析其方法、不回填（保留用户可能已在新库中改绑的 SecretId）
                continue;
            }

            List<AuthMethodEntry> methods;
            try
            {
                methods = JsonSerializer.Deserialize<List<AuthMethodEntry>>(row.MethodsJson ?? "[]") ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 只带出身份 Id 与错误类型，绝不含方法内容或密钥
                logger.LogWarning("材料库迁移读取身份方法失败 identity={IdentityId} 错误类型={ErrorType}", identityId, ex.GetType().Name);
                hasSkipped = true;
                continue;
            }

            identities.Add(new Identity { Id = identityId, Name = row.Name, Methods = methods });
        }

        var candidateIds = new HashSet<Guid>(identities.Select(identity => identity.Id));
        var bundles = new Dictionary<Guid, IReadOnlyDictionary<string, SecretPayload>>();
        foreach (SecretRow row in secretRows)
        {
            if (!Guid.TryParse(row.IdentityId, out Guid identityId))
            {
                logger.LogWarning("材料库迁移旧整包身份 Id 无效 错误类型=GuidParse");
                hasSkipped = true;
                continue;
            }

            // 已迁移身份或已被跳过（方法不可解析）的身份：不处理其整包
            if (migrated.Contains(identityId) || !candidateIds.Contains(identityId))
            {
                continue;
            }

            IReadOnlyDictionary<string, SecretPayload> bundle;
            try
            {
                byte[] json = decryptItem(identityId, row.SecretsBlob, row.EncryptionAlgorithm);
                bundle = JsonSerializer.Deserialize<Dictionary<string, SecretPayload>>(json) ?? new();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 只带出身份 Id 与错误类型，绝不记录解密后的材料
                logger.LogWarning("材料库迁移解密旧整包失败 identity={IdentityId} 错误类型={ErrorType}", identityId, ex.GetType().Name);
                hasSkipped = true;
                // 该身份整包不可用：连同其方法一起跳过，旧数据保留供未迁移回退路径读取
                identities.RemoveAll(identity => identity.Id == identityId);
                candidateIds.Remove(identityId);
                continue;
            }

            bundles[identityId] = bundle;
        }

        // 既有私钥条目的合并保留目标：（内容, 口令）→ 条目 Id。
        // 内容相同的旧私钥优先复用既有条目，使已迁移条目的 Id 选择在重试间保持稳定（不会另建新条目）。
        Dictionary<(string Content, string Passphrase), Guid> existingPrivateKeys =
            await ReadExistingPrivateKeysAsync(conn, tx, decryptItem, logger, ct);

        SecretMigrationPlan plan = LegacySecretMigration.Plan(identities, bundles, existingPrivateKeys);
        LogMerges(logger, identities, bundles, plan);

        // 逐条写新表：Id 冲突（半途重试或已存在）时保留既有行
        foreach (MigratedSecret secret in plan.Secrets)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(secret.Payload);
            var (blob, algorithm, nonce, tag) = encryptItem(secret.Id, json);
            DateTime now = DateTime.UtcNow;
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO vault_secrets
                    (id, name, kind, meta_json, secret_blob, encryption_algorithm, nonce, tag, created_at, updated_at)
                VALUES
                    (@Id, @Name, @Kind, NULL, @Blob, @Algorithm, @Nonce, @Tag, @CreatedAt, @UpdatedAt)
                ON CONFLICT(id) DO NOTHING;",
                new
                {
                    Id = secret.Id.ToString(),
                    secret.Name,
                    secret.Kind,
                    Blob = blob,
                    Algorithm = algorithm,
                    Nonce = nonce,
                    Tag = tag,
                    CreatedAt = SqliteValue.FormatUtc(now),
                    UpdatedAt = SqliteValue.FormatUtc(now)
                },
                transaction: tx,
                cancellationToken: ct));
        }

        // 回填 Vault* 方法的 SecretId：仅在为空时写入（不覆盖用户已改绑的值），未变化不 UPDATE
        foreach (Identity identity in identities)
        {
            var bindings = plan.Bindings
                .Where(b => b.IdentityId == identity.Id)
                .ToDictionary(b => b.MethodId, b => b.SecretId);
            if (bindings.Count == 0)
            {
                continue;
            }

            bool changed = false;
            foreach (AuthMethodEntry method in identity.Methods)
            {
                if (!bindings.TryGetValue(method.Id, out Guid secretId))
                {
                    continue;
                }

                switch (method)
                {
                    case VaultPasswordMethod password when password.SecretId == null:
                        password.SecretId = secretId;
                        changed = true;
                        break;
                    case VaultPrivateKeyMethod key when key.SecretId == null:
                        key.SecretId = secretId;
                        changed = true;
                        break;
                }
            }

            if (!changed)
            {
                continue;
            }

            // 同时刷新 updated_at：回填改变了方法数据，应反映为身份的一次更新
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE identities SET methods_json = @json, updated_at = @updatedAt WHERE id = @id;",
                new
                {
                    json = JsonSerializer.Serialize(identity.Methods),
                    updatedAt = SqliteValue.FormatUtc(DateTime.UtcNow),
                    id = identity.Id.ToString()
                },
                transaction: tx,
                cancellationToken: ct));
        }

        // 已迁移身份集合：与写表/回填在同一事务内原子提交
        foreach (Identity identity in identities)
        {
            migrated.Add(identity.Id);
        }

        if (migrated.Count > 0)
        {
            string idsJson = JsonSerializer.Serialize(
                migrated.Select(id => id.ToString()).OrderBy(value => value, StringComparer.Ordinal));
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO vault_metadata (key, value) VALUES (@key, @value)
                ON CONFLICT(key) DO UPDATE SET value = @value;",
                new { key = MigratedIdentitiesKey, value = idsJson },
                transaction: tx,
                cancellationToken: ct));
        }

        bool complete = !hasSkipped;
        if (complete)
        {
            // 只有完整迁移才写完成标记：标记抑制旧表回退，必须确保无未迁移项
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO vault_metadata (key, value) VALUES (@key, '1')
                ON CONFLICT(key) DO UPDATE SET value = '1';",
                new { key = MarkerKey },
                transaction: tx,
                cancellationToken: ct));
        }

        tx.Commit();
        return complete ? SecretMigrationStatus.Completed : SecretMigrationStatus.Partial;
    }

    // 读取已迁移身份集合；键缺失或损坏时按空集处理（不可反悔为「未迁移」以外的状态）
    private static async Task<HashSet<Guid>> ReadMigratedSetAsync(
        SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        string? json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM vault_metadata WHERE key = @key;",
            new { key = MigratedIdentitiesKey }, transaction: tx, cancellationToken: ct));
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
            // 集合损坏：按空集处理，宁可重复处理也不漏迁移
        }

        return set;
    }

    // 扫描既有 vault_secrets 中的私钥条目，建立（内容, 口令）→ 条目 Id 的保留目标表。
    // 单个既有条目解密失败只跳过该条目，不阻断迁移。
    private static async Task<Dictionary<(string Content, string Passphrase), Guid>> ReadExistingPrivateKeysAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        Func<Guid, byte[], string, byte[]> decryptItem,
        ILogger logger,
        CancellationToken ct)
    {
        var result = new Dictionary<(string Content, string Passphrase), Guid>();
        List<ExistingSecretRow> rows = (await conn.QueryAsync<ExistingSecretRow>(new CommandDefinition(
            "SELECT id, kind, secret_blob, encryption_algorithm FROM vault_secrets ORDER BY created_at, id;",
            transaction: tx, cancellationToken: ct))).ToList();

        foreach (ExistingSecretRow row in rows)
        {
            if (row.Kind != SecretKind.SshPrivateKey || !Guid.TryParse(row.Id, out Guid entryId))
            {
                continue;
            }

            try
            {
                byte[] json = decryptItem(entryId, row.SecretBlob, row.EncryptionAlgorithm);
                SecretPayload? payload = JsonSerializer.Deserialize<SecretPayload>(json);
                string? content = payload?.PrivateKeyContent;
                if (!string.IsNullOrEmpty(content))
                {
                    // 首次出现的既有条目作为保留目标（按 created_at, id 顺序确定）
                    result.TryAdd((content, payload!.Passphrase ?? string.Empty), entryId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("材料库迁移读取既有条目失败 条目Id={SecretId} 错误类型={ErrorType}", entryId, ex.GetType().Name);
            }
        }

        return result;
    }

    // 合并发生时记录条目数（绝不记录私钥内容）
    private static void LogMerges(
        ILogger logger,
        List<Identity> identities,
        Dictionary<Guid, IReadOnlyDictionary<string, SecretPayload>> bundles,
        SecretMigrationPlan plan)
    {
        int privateKeyPayloads = 0;
        foreach (Identity identity in identities)
        {
            if (!bundles.TryGetValue(identity.Id, out IReadOnlyDictionary<string, SecretPayload>? bundle))
            {
                continue;
            }

            foreach (AuthMethodEntry method in identity.Methods)
            {
                if (method is VaultPrivateKeyMethod &&
                    bundle.TryGetValue(method.Id.ToString(), out SecretPayload? payload) &&
                    !string.IsNullOrEmpty(payload?.PrivateKeyContent))
                {
                    privateKeyPayloads++;
                }
            }
        }

        int merged = privateKeyPayloads - plan.Secrets.Count(s => s.Kind == SecretKind.SshPrivateKey);
        if (merged > 0)
        {
            logger.LogInformation("材料库迁移合并相同私钥 条目数={Count}", merged);
        }
    }

    private sealed class IdentityRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? MethodsJson { get; set; }
    }

    private sealed class SecretRow
    {
        public string IdentityId { get; set; } = string.Empty;
        public byte[] SecretsBlob { get; set; } = [];
        public string EncryptionAlgorithm { get; set; } = string.Empty;
    }

    private sealed class ExistingSecretRow
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public byte[] SecretBlob { get; set; } = [];
        public string EncryptionAlgorithm { get; set; } = string.Empty;
    }
}
