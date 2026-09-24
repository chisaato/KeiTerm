namespace Kei.Term.Core.Services;

using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Vault;
using Microsoft.Extensions.Logging;

// 文件私钥读取结果：内容 + 是否带口令。
// 口令探测依赖 SSH 私钥解析，Core 不引入 SSH 依赖，由读取方在 Ssh 层解析后回传。
public sealed record FileKeyReadResult(string Content, bool RequiresPassphrase);

// 口令弹窗结果：口令 + 是否持久化（Persistent 模式且用户勾选记住时为 true）
public sealed record PassphrasePromptResult(string Passphrase, bool Remember);

// 方法物化所需的委托集合：App 层注入具体 IO/UI 实现，Core 保持纯逻辑可测
public sealed class AuthMaterializerContext
{
    // 读取私钥文件；不存在/不可读返回 null
    public required Func<string, CancellationToken, Task<FileKeyReadResult?>> ReadPrivateKeyFileAsync { get; init; }

    // SessionOnly 口令内存缓存查找（按 methodId 键控）
    public required Func<Guid, string?> GetSessionPassphrase { get; init; }

    // Persistent 口令 / Vault 方法材料从 identity_secrets 整包取出；Vault 锁定时返回 null
    public required Func<Guid, SecretPayload?> GetVaultSecret { get; init; }

    // 口令弹窗（三态选择）；返回 null 表示用户取消
    public required Func<FilePrivateKeyMethod, CancellationToken, Task<PassphrasePromptResult?>> PromptPassphraseAsync { get; init; }

    // Persistent 口令确认记住后写回 identity_secrets
    public Func<Guid, SecretPayload, Task>? SaveVaultSecretAsync { get; init; }

    // Interactive 方法弹窗；返回 null 表示取消
    public Func<InteractiveMethod, string, CancellationToken, Task<SecretPayload?>>? PromptInteractiveAsync { get; init; }

    // 可选诊断日志（App 注入）；Core 只依赖日志抽象
    public ILogger? Logger { get; init; }
}

// 认证方法物化：取材料 / 问口令 → MaterializedAuthMethod；返回 null 表示跳过该方法顺延
public static class AuthMaterializer
{
    public static async Task<MaterializedAuthMethod?> MaterializeAsync(
        AuthMethodEntry method,
        string username,
        AuthMaterializerContext context,
        CancellationToken ct = default)
    {
        var logger = context.Logger;
        switch (method)
        {
            case VaultPasswordMethod:
            {
                var secret = context.GetVaultSecret(method.Id);
                if (string.IsNullOrEmpty(secret?.Password))
                {
                    logger?.LogInformation("认证方法物化跳过 方法=VaultPassword 原因=无保管库密码材料");
                    return null;
                }

                logger?.LogInformation("认证方法物化成功 方法=VaultPassword");
                return new MaterializedAuthMethod(AuthMaterialKind.Password, secret);
            }

            case VaultPrivateKeyMethod:
            {
                var secret = context.GetVaultSecret(method.Id);
                if (string.IsNullOrEmpty(secret?.PrivateKeyContent))
                {
                    logger?.LogInformation("认证方法物化跳过 方法=VaultPrivateKey 原因=无保管库私钥材料");
                    return null;
                }

                logger?.LogInformation("认证方法物化成功 方法=VaultPrivateKey");
                return new MaterializedAuthMethod(AuthMaterialKind.PrivateKey, secret);
            }

            case FilePrivateKeyMethod fileKey:
                return await MaterializeFileKeyAsync(fileKey, context, ct);

            case AgentMethod agent:
                // 一期不做指纹过滤（全量身份）；字段透传供 Ssh 层与二期使用
                logger?.LogInformation("认证方法物化成功 方法=Agent");
                return new MaterializedAuthMethod(AuthMaterialKind.Agent, null, agent.AgentFingerprint);

            case InteractiveMethod interactive:
            {
                if (context.PromptInteractiveAsync == null)
                {
                    logger?.LogInformation("认证方法物化跳过 方法=Interactive 原因=无交互回调");
                    return null;
                }

                var secret = await context.PromptInteractiveAsync(interactive, username, ct);
                if (secret == null)
                {
                    logger?.LogInformation("认证方法物化跳过 方法=Interactive 原因=用户取消交互认证");
                    return null;
                }

                logger?.LogInformation("认证方法物化成功 方法=Interactive");
                return new MaterializedAuthMethod(AuthMaterialKind.Password, secret);
            }

            default:
                logger?.LogInformation("认证方法物化跳过 方法={Method} 原因=不支持的方法类型", method.GetType().Name);
                return null;
        }
    }

    private static async Task<MaterializedAuthMethod?> MaterializeFileKeyAsync(
        FilePrivateKeyMethod method,
        AuthMaterializerContext context,
        CancellationToken ct)
    {
        var logger = context.Logger;

        if (string.IsNullOrWhiteSpace(method.KeyFilePath))
        {
            logger?.LogInformation("认证方法物化跳过 方法=FilePrivateKey 原因=未设置文件路径");
            return null;
        }

        // SessionOnly：本次运行内存命中则复用口令，不再弹窗
        if (method.PassphraseMode == PassphrasePersistence.SessionOnly)
        {
            var cached = context.GetSessionPassphrase(method.Id);
            if (!string.IsNullOrEmpty(cached))
            {
                var cachedFile = await ReadFileAsync(method, context, ct);
                if (cachedFile == null)
                {
                    logger?.LogInformation("认证方法物化跳过 方法=FilePrivateKey 原因=文件不存在或不可读");
                    return null;
                }

                logger?.LogInformation("认证方法物化成功 方法=FilePrivateKey 口令=会话缓存");
                return new MaterializedAuthMethod(
                    AuthMaterialKind.PrivateKey,
                    new SecretPayload { PrivateKeyContent = cachedFile.Content, Passphrase = cached });
            }
        }

        var file = await ReadFileAsync(method, context, ct);
        if (file == null)
        {
            // 文件不存在/不可读 -> 跳过该方法
            logger?.LogInformation("认证方法物化跳过 方法=FilePrivateKey 原因=文件不存在或不可读");
            return null;
        }

        if (!file.RequiresPassphrase)
        {
            // 无口令私钥：直接物化
            logger?.LogInformation("认证方法物化成功 方法=FilePrivateKey 口令=无");
            return new MaterializedAuthMethod(
                AuthMaterialKind.PrivateKey,
                new SecretPayload { PrivateKeyContent = file.Content });
        }

        // 有口令：Persistent 先尝试从 Vault 整包材料命中（按 methodId 键控）
        string? passphrase = null;
        var fromVault = false;
        if (method.PassphraseMode == PassphrasePersistence.Persistent)
        {
            passphrase = context.GetVaultSecret(method.Id)?.Passphrase;
            fromVault = !string.IsNullOrEmpty(passphrase);
        }

        if (string.IsNullOrEmpty(passphrase))
        {
            // AlwaysAsk 每次询问；Persistent 未命中同样弹窗
            var prompt = await context.PromptPassphraseAsync(method, ct);
            if (prompt == null)
            {
                // 用户取消 -> 跳过该方法顺延
                logger?.LogInformation("认证方法物化跳过 方法=FilePrivateKey 原因=用户取消口令输入");
                return null;
            }

            passphrase = prompt.Passphrase;

            // Persistent 且用户确认记住 -> 回调保存回 identity_secrets
            if (method.PassphraseMode == PassphrasePersistence.Persistent
                && prompt.Remember
                && context.SaveVaultSecretAsync != null)
            {
                await context.SaveVaultSecretAsync(method.Id, new SecretPayload { Passphrase = passphrase });
            }
        }

        logger?.LogInformation(
            "认证方法物化成功 方法=FilePrivateKey 口令来源={Source}",
            fromVault ? "保管库" : "用户输入");
        return new MaterializedAuthMethod(
            AuthMaterialKind.PrivateKey,
            new SecretPayload { PrivateKeyContent = file.Content, Passphrase = passphrase });
    }

    private static async Task<FileKeyReadResult?> ReadFileAsync(
        FilePrivateKeyMethod method,
        AuthMaterializerContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(method.KeyFilePath))
        {
            return null;
        }

        return await context.ReadPrivateKeyFileAsync(method.KeyFilePath, ct);
    }
}
