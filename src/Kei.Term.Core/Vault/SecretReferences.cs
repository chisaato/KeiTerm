namespace Kei.Term.Core.Vault;

// 引用某材料的身份 + 其命中方法：用于删除保护与「引用数」展示
public readonly record struct SecretReference(Guid IdentityId, string IdentityName, Guid MethodId);

// 材料引用统计：不落库计数器，按需扫描所有身份的 methods_json，避免与同步/迁移脱节。
// 纯函数、无 IO。
public static class SecretReferences
{
    // 命中规则：条目 Id 为 Guid.Empty 视为「不统计」，直接返回空
    public static IReadOnlyList<SecretReference> Find(IEnumerable<Identity> identities, Guid secretId)
    {
        if (secretId == Guid.Empty)
        {
            return [];
        }

        var hits = new List<SecretReference>();
        foreach (Identity identity in identities)
        {
            foreach (AuthMethodEntry method in identity.Methods)
            {
                if (SecretIdOf(method) == secretId)
                {
                    hits.Add(new SecretReference(identity.Id, identity.Name, method.Id));
                }
            }
        }

        return hits;
    }

    // 方法引用的材料条目 Id：
    // - Vault 密码/私钥：方法自身的 SecretId（null = 尚未绑定）
    // - 文件私钥：仅持久化口令模式下按其方法 Id 计（与迁移期「条目 Id = 旧 methodId」一致）
    // - 其余（Agent / 交互 / 非持久化文件私钥）：不引用材料
    public static Guid? SecretIdOf(AuthMethodEntry method) => method switch
    {
        VaultPasswordMethod password => password.SecretId,
        VaultPrivateKeyMethod privateKey => privateKey.SecretId,
        FilePrivateKeyMethod fileKey when fileKey.PassphraseMode == PassphrasePersistence.Persistent => fileKey.Id,
        _ => null
    };
}
