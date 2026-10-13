namespace Kei.Term.Core.Vault;

// 迁移规划结果：待落库的新条目 + 方法 → 条目 的 SecretId 回填绑定
public sealed record SecretMigrationPlan(IReadOnlyList<MigratedSecret> Secrets, IReadOnlyList<MethodSecretBinding> Bindings);

// 一条待落库的材料条目；Id 通常 = 旧 methodId，使方法 Id 与 SecretId 一致
public sealed record MigratedSecret(Guid Id, string Kind, string Name, SecretPayload Payload);

// Vault* 方法对其材料条目的引用回填（文件私钥按方法 Id 直接引用，不产生绑定）
public sealed record MethodSecretBinding(Guid IdentityId, Guid MethodId, Guid SecretId);

// 旧身份级整包（Dictionary<methodId, SecretPayload>）→ 材料库条目的纯规划函数：不触 IO，便于单测。
public static class LegacySecretMigration
{
    private const string PrivateKeyLabel = "私钥";
    private const string PasswordLabel = "密码";
    private const string PassphraseLabel = "口令";

    // secrets：identityId →（methodId 字符串 → 载荷）。遍历身份与方法建条目，
    // 仅 ssh-private-key 条目按（私钥内容, 口令）去重合并；未匹配任何方法的键也按命名规则建条目。
    // existingPrivateKeys：已存在于材料库的私钥条目（内容, 口令）→ 条目 Id。迁移重试时用它们
    // 作为合并保留目标，使已迁移条目的 Id 选择对后续重试保持稳定（不会改绑到别的新条目）。
    public static SecretMigrationPlan Plan(
        IEnumerable<Identity> identities,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, SecretPayload>> secrets,
        IReadOnlyDictionary<(string Content, string Passphrase), Guid>? existingPrivateKeys = null)
    {
        var entries = new List<MigratedSecret>();
        var bindings = new List<MethodSecretBinding>();
        // ssh-private-key 合并表：（私钥内容, 口令）→ 保留条目 Id
        var privateKeyCanonical = new Dictionary<(string Content, string Passphrase), Guid>();
        if (existingPrivateKeys != null)
        {
            foreach (KeyValuePair<(string Content, string Passphrase), Guid> pair in existingPrivateKeys)
            {
                privateKeyCanonical[pair.Key] = pair.Value;
            }
        }

        foreach (Identity identity in identities)
        {
            if (!secrets.TryGetValue(identity.Id, out IReadOnlyDictionary<string, SecretPayload>? bundle) || bundle == null)
            {
                continue;
            }

            var matched = new HashSet<string>(StringComparer.Ordinal);
            foreach (AuthMethodEntry method in identity.Methods)
            {
                string key = method.Id.ToString();
                if (!bundle.TryGetValue(key, out SecretPayload? payload) || payload == null)
                {
                    continue;
                }

                matched.Add(key);
                Guid entryId = AddOrReuseEntry(identity.Name, method.Id, payload, entries, privateKeyCanonical);
                // 仅 Vault 密码/私钥方法需要回填 SecretId；文件私钥按方法 Id 直接引用
                if (method is VaultPasswordMethod or VaultPrivateKeyMethod)
                {
                    bindings.Add(new MethodSecretBinding(identity.Id, method.Id, entryId));
                }
            }

            // 未匹配任何方法的键：仍按命名规则建条目，但不产生绑定
            foreach (KeyValuePair<string, SecretPayload> pair in bundle)
            {
                if (matched.Contains(pair.Key) || pair.Value == null)
                {
                    continue;
                }

                Guid id = Guid.TryParse(pair.Key, out Guid parsed) ? parsed : Guid.NewGuid();
                AddOrReuseEntry(identity.Name, id, pair.Value, entries, privateKeyCanonical);
            }
        }

        return new SecretMigrationPlan(entries, bindings);
    }

    // 建条目并返回其 Id：ssh-private-key 命中合并表时复用保留条目、不再新增
    private static Guid AddOrReuseEntry(
        string identityName,
        Guid candidateId,
        SecretPayload payload,
        List<MigratedSecret> entries,
        Dictionary<(string Content, string Passphrase), Guid> privateKeyCanonical)
    {
        string kind = KindOf(payload);
        if (kind == SecretKind.SshPrivateKey)
        {
            // null 与 "" 视为同一口令，避免同义空口令重复建条目
            var mergeKey = (payload.PrivateKeyContent ?? string.Empty, payload.Passphrase ?? string.Empty);
            if (privateKeyCanonical.TryGetValue(mergeKey, out Guid kept))
            {
                return kept;
            }

            privateKeyCanonical[mergeKey] = candidateId;
        }

        entries.Add(new MigratedSecret(candidateId, kind, NameFor(identityName, kind), payload));
        return candidateId;
    }

    // 私钥内容 → 私钥；仅口令 → 口令；其余（含密码）→ 密码
    private static string KindOf(SecretPayload payload)
    {
        if (!string.IsNullOrEmpty(payload.PrivateKeyContent))
        {
            return SecretKind.SshPrivateKey;
        }

        if (!string.IsNullOrEmpty(payload.Password))
        {
            return SecretKind.Password;
        }

        if (!string.IsNullOrEmpty(payload.Passphrase))
        {
            return SecretKind.Passphrase;
        }

        return SecretKind.Password;
    }

    private static string NameFor(string identityName, string kind) => kind switch
    {
        SecretKind.SshPrivateKey => $"{identityName} · {PrivateKeyLabel}",
        SecretKind.Passphrase => $"{identityName} · {PassphraseLabel}",
        _ => $"{identityName} · {PasswordLabel}",
    };
}
