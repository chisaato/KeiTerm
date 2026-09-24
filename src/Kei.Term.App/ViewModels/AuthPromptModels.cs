using System;
using System.Collections.Generic;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels;

// 统一认证窗可选择的认证方式
public enum AuthPromptMethod
{
    Password,
    PublicKeyFile,
    PublicKeyVault,
    Interactive
}

// 公钥来源下拉中的「保管库密钥」项（当前连接身份已导入的 Vault 私钥方法）
public sealed record VaultKeyOption(Guid MethodId, string DisplayName);

// 统一认证窗完整模式的结果
public sealed record AuthPromptResult(
    AuthPromptMethod Method,
    string Username,
    string? Password,
    string? KeyFilePath,
    string? Passphrase,
    Guid? VaultMethodId);

// 身份编辑器暂存的 Vault 私钥变更：Remove=true 表示待删除，否则为待写入内容
public sealed record VaultKeyImport(Guid MethodId, string? PrivateKeyContent, string? Passphrase, bool Remove);

// Vault 私钥已存材料回显信息：字节数 + SSH 指纹（解析失败为 null）
public sealed record VaultKeyInfo(int Size, string? Fingerprint);

// 身份编辑结果：领域对象 + 需在身份落库后写入 Vault 的私钥变更
public sealed record IdentityEditResult(Identity Identity, IReadOnlyList<VaultKeyImport> VaultKeyImports);
