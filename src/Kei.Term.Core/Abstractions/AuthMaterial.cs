namespace Kei.Term.Core.Abstractions;

using Kei.Term.Core.Vault;

// 物化后的认证材料种类
public enum AuthMaterialKind
{
    Password,
    PrivateKey,
    Agent
}

// 经过物化（取材料/问口令）后可直接注册到 SSH 会话的认证方法；
// Secret 仅内存流转，绝不持久化或记录明文日志
public sealed record MaterializedAuthMethod(
    AuthMaterialKind Kind,
    SecretPayload? Secret,
    string? AgentFingerprint = null);
