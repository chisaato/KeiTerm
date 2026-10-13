using Kei.Term.Core.Vault;

namespace Kei.Term.App.Models;

// 会话解锁/上锁后通知管理器刷新；携带 Vault 实例，避免其他会话串扰。
public sealed record VaultLockStateChangedMessage(IVaultManager Vault);
