using System;
using Kei.Term.App.Helpers;
using Kei.Term.Core.Vault;

namespace Kei.Term.App.ViewModels;

// 材料库列表行：仅承载公开元数据 + 引用计数，载荷不进入列表层。
public sealed class VaultSecretListItemViewModel
{
    public VaultSecretListItemViewModel(VaultSecret secret, int referenceCount)
    {
        Secret = secret;
        ReferenceCount = referenceCount;
    }

    public Guid Id => Secret.Id;

    public string Name => Secret.Name;

    // 底层条目（重命名等元数据变更以本对象为基础做 with 拷贝）
    public VaultSecret Secret { get; }

    // 引用它的身份方法数（每次加载时由 SecretReferences 扫描 methods_json 得出）
    public int ReferenceCount { get; }

    // 类型显示名；未知 Kind 原样展示，保证新旧版本可容忍新增类型
    public string KindLabel => Secret.Kind switch
    {
        SecretKind.Password => Strings.Get("VaultSecretLibrary.Kind.Password"),
        SecretKind.SshPrivateKey => Strings.Get("VaultSecretLibrary.Kind.PrivateKey"),
        SecretKind.Passphrase => Strings.Get("Secret.Kind.Passphrase"),
        _ => Secret.Kind,
    };

    // 指纹（可为空：迁移产出或口令条目无指纹时展示空串）
    public string Fingerprint => Secret.Fingerprint ?? string.Empty;
}
