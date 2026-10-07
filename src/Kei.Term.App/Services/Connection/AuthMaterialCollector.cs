using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Core.Vault;
using Kei.Term.Ssh.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.Services.Connection;

// 一跳（目标或跳板）的认证准备结果
public sealed record CollectedAuth(
    ResolvedSessionConfig Config,
    Identity? Identity,
    IReadOnlyList<MaterializedAuthMethod> Materials);

// 「用什么认证」：身份解析 → 认证计划 → 材料物化（Vault / 文件 / 口令 / 交互）→ 无材料时的兜底弹窗。
// 连接前在 UI 上下文中完成；「怎么连」由 ConnectionOrchestrator 负责。
public sealed class AuthMaterialCollector
{
    private readonly IIdentityRepository _identities;
    private readonly ISettingsService _settings;
    private readonly VaultSessionService _vault;
    private readonly Func<IInteractionService> _interaction;
    private readonly ILogger _logger;

    public AuthMaterialCollector(
        IIdentityRepository identities,
        ISettingsService settings,
        VaultSessionService vault,
        Func<IInteractionService> interaction,
        ILogger? logger = null)
    {
        _identities = identities;
        _settings = settings;
        _vault = vault;
        _interaction = interaction;
        _logger = logger ?? NullLogger.Instance;
    }

    // 目标主机：计划物化 → 无材料则弹统一认证窗；返回 null 表示用户取消（不应建标签）
    public async Task<CollectedAuth?> CollectTargetAsync(ConnectionRequest request)
    {
        ResolvedSessionConfig config = request.Config;
        Identity? identity = request.UseIdentity ? await ResolveIdentityAsync(config) : null;

        var materials = new List<MaterializedAuthMethod>();
        if (request.Preloaded != null)
        {
            materials.Add(request.Preloaded);
        }

        materials.AddRange(await MaterializePlanAsync(config, identity));
        _logger.LogInformation(
            "认证材料物化完成 材料数={Count} 类型={Kinds}",
            materials.Count,
            string.Join(",", materials.Select(m => m.Kind)));

        // 已配置身份方法却没有可用材料时中止，不能推断为允许空密码登录。
        // 未配置方法且有用户名的连接仍允许路由器免密登录；缺少用户名则询问单次认证。
        if (materials.Count == 0)
        {
            if (identity is { Methods.Count: > 0 })
            {
                await _interaction().NotifyAsync(config.SessionName, Strings.Get("Status.Auth.ConfiguredMethodsUnavailable"));
                return null;
            }

            string? effectiveUser = identity?.Username ?? config.Username;
            if (!string.IsNullOrWhiteSpace(effectiveUser))
            {
                _logger.LogInformation("未配置认证材料，先尝试直连免密登录 用户名={Username}", effectiveUser);
                materials.Add(new MaterializedAuthMethod(
                    AuthMaterialKind.Password,
                    new SecretPayload { Password = string.Empty }));
            }
            else
            {
                AuthPromptResult? fallback = await PromptAuthAsync(string.Empty, identity);
                if (fallback == null)
                {
                    return null;
                }

                (MaterializedAuthMethod? material, string? username) = await ResolvePromptResultAsync(fallback, identity);
                config = WithUsername(config, username);
                if (material != null)
                {
                    materials.Add(material);
                }
            }
        }

        return new CollectedAuth(config, identity, materials);
    }

    // 跳板链逐跳物化（由外到内）；某跳无材料且用户取消弹窗时抛 JumpChainException 中止整条连接
    public async Task<IReadOnlyList<SshHop>> CollectJumpHopsAsync(ResolvedSessionConfig target, Func<Guid, SessionNode?> findSession)
    {
        if (target.JumpHostSessionId == null)
        {
            return [];
        }

        var targetNode = new SessionNode
        {
            Id = target.SessionId,
            Name = target.SessionName,
            JumpHostSessionId = target.JumpHostSessionId
        };
        IReadOnlyList<SessionNode> chain = JumpChainResolver.Resolve(targetNode, findSession);

        var hops = new List<SshHop>();
        foreach (SessionNode jumpNode in chain)
        {
            ResolvedSessionConfig hopConfig = SessionConfigBuilder.Build(jumpNode, _settings.Current);
            Identity? hopIdentity = await ResolveIdentityAsync(hopConfig);
            List<MaterializedAuthMethod> hopMaterials = await MaterializePlanAsync(hopConfig, hopIdentity);

            if (hopMaterials.Count == 0)
            {
                if (hopIdentity is { Methods.Count: > 0 })
                {
                    throw new JumpChainException($"{jumpNode.Name}: {Strings.Get("Status.Auth.ConfiguredMethodsUnavailable")}");
                }

                AuthPromptResult? prompt = await PromptAuthAsync(hopIdentity?.Username ?? hopConfig.Username, hopIdentity);
                if (prompt == null)
                {
                    throw new JumpChainException($"已取消跳板机认证: {jumpNode.Name}");
                }

                (MaterializedAuthMethod? material, string? username) = await ResolvePromptResultAsync(prompt, hopIdentity);
                hopConfig = WithUsername(hopConfig, username);
                if (material != null)
                {
                    hopMaterials.Add(material);
                }
            }

            _logger.LogInformation(
                "跳板物化完成 跳板={Jump} host={Host}:{Port} 材料数={Count}",
                jumpNode.Name,
                hopConfig.Host,
                hopConfig.Port,
                hopMaterials.Count);
            hops.Add(new SshHop(hopConfig, hopMaterials));
        }

        return hops;
    }

    // 认证失败后的单次回弹：返回用户新选的方法与材料；null = 取消
    public async Task<(AuthPromptMethod Method, MaterializedAuthMethod? Material, string? Username)?> PromptRetryAsync(
        string username,
        Identity? identity)
    {
        // 仅配置密钥的身份不能在失败后自动改用未配置的密码或交互认证。
        if (identity is { Methods.Count: > 0 }
            && identity.Methods.Where(method => method.Enabled)
                .All(method => method is FilePrivateKeyMethod or VaultPrivateKeyMethod or AgentMethod))
        {
            return null;
        }

        AuthPromptResult? prompt = await PromptAuthAsync(username, identity);
        if (prompt == null)
        {
            return null;
        }

        (MaterializedAuthMethod? material, string? newUsername) = await ResolvePromptResultAsync(prompt, identity);
        return (prompt.Method, material, newUsername);
    }

    // 解析认证主体：会话绑定 → 全局默认身份；找不到返回 null
    private async Task<Identity?> ResolveIdentityAsync(ResolvedSessionConfig resolved)
    {
        Guid? identityId = resolved.IdentityId ?? _settings.Current.DefaultIdentityId;
        if (identityId == null)
        {
            _logger.LogInformation("认证主体解析=无身份（未绑定且无全局默认）");
            return null;
        }

        Identity? identity = await _identities.GetByIdAsync(identityId.Value);
        _logger.LogInformation(
            "认证主体解析 来源={Source} 身份Id={IdentityId} 命中={Hit}",
            resolved.IdentityId != null ? "会话绑定" : "全局默认",
            identityId,
            identity != null);
        return identity;
    }

    // 逐步物化认证计划；SingleUsePromptStep 不在此预先打扰用户，留作失败回弹
    private async Task<List<MaterializedAuthMethod>> MaterializePlanAsync(ResolvedSessionConfig config, Identity? identity)
    {
        IReadOnlyList<AuthStep> steps = AuthPlanBuilder.Plan(identity?.Methods, _settings.Current.PreferSystemAgent, identity?.Username);
        _logger.LogInformation(
            "认证计划 host={Host}:{Port} 会话={Session} 身份={Identity} 步骤={Steps}",
            config.Host,
            config.Port,
            config.SessionName,
            identity?.Name ?? "(无)",
            string.Join(" -> ", steps.Select(DescribeAuthStep)));

        var results = new List<MaterializedAuthMethod>();

        // identity_secrets 整包材料缓存（按 methodId 字符串键控），首次用到时懒加载
        Dictionary<string, SecretPayload>? secrets = null;

        var context = new AuthMaterializerContext
        {
            ReadPrivateKeyFileAsync = (path, ct) => PrivateKeyImport.ReadAsync(path, ct),
            Logger = _logger,
            GetSessionPassphrase = _vault.GetSessionPassphrase,
            GetVaultSecret = methodId =>
                secrets != null && secrets.TryGetValue(methodId.ToString(), out SecretPayload? payload) ? payload : null,
            PromptPassphraseAsync = (method, _) => _vault.PromptPassphraseAsync(method),
            SaveVaultSecretAsync = async (methodId, payload) =>
            {
                if (identity == null)
                {
                    return;
                }

                await _vault.PersistSecretAsync(identity.Id, methodId, payload);
                // 同步内存副本，后续同一连接内读取命中
                secrets ??= new Dictionary<string, SecretPayload>();
                secrets[methodId.ToString()] = payload;
            },
            PromptInteractiveAsync = async (method, username, ct) =>
            {
                // Interactive 方法：以交互式为默认项弹完整认证窗，取消则跳过该方法
                AuthPromptResult? prompt = await PromptAuthAsync(username, identity, AuthPromptMethod.Interactive);
                return prompt == null
                    ? null
                    : new SecretPayload { Password = prompt.Password ?? string.Empty };
            },
        };

        foreach (AuthStep step in steps)
        {
            switch (step)
            {
                case MethodStep methodStep:
                    if (identity != null)
                    {
                        secrets ??= await _vault.LoadIdentitySecretsAsync(identity.Id);
                    }

                    MaterializedAuthMethod? material = await AuthMaterializer.MaterializeAsync(methodStep.Method, config.Username, context);
                    if (material != null)
                    {
                        results.Add(material);
                    }

                    break;

                case AgentFallbackStep:
                    // 无身份且全局允许时，注入 Agent 全量身份尝试
                    results.Add(new MaterializedAuthMethod(AuthMaterialKind.Agent, null));
                    break;

                case SingleUsePromptStep:
                    // 失败回弹兜底，连接阶段处理
                    break;
            }
        }

        return results;
    }

    // 弹统一认证窗（完整模式）；identity 提供保管库密钥下拉项
    private async Task<AuthPromptResult?> PromptAuthAsync(string prefillUsername, Identity? identity, AuthPromptMethod? defaultMethod = null)
    {
        AuthPromptResult? result = await _interaction().PromptAuthAsync(prefillUsername, BuildVaultKeyOptions(identity), defaultMethod);
        _logger.LogInformation(
            "认证窗结果 方法={Method} 用户名={Username}",
            result?.Method.ToString() ?? "取消",
            result?.Username ?? "(空)");
        return result;
    }

    // 当前身份中已配置的 Vault 私钥方法 → 认证窗「保管库密钥」下拉项
    private static IReadOnlyList<VaultKeyOption> BuildVaultKeyOptions(Identity? identity)
    {
        if (identity == null)
        {
            return [];
        }

        return identity.Methods
            .OfType<VaultPrivateKeyMethod>()
            .Select((method, i) => new VaultKeyOption(
                method.Id,
                string.Format(Strings.Get("Status.Vault.KeyOptionFormat"), identity.Name, i + 1)))
            .ToList();
    }

    // 认证窗结果 → 认证材料；Interactive 返回零材料（交由 KI 桥应答）
    private async Task<(MaterializedAuthMethod? Material, string? Username)> ResolvePromptResultAsync(AuthPromptResult result, Identity? identity)
    {
        switch (result.Method)
        {
            case AuthPromptMethod.Password:
                return (
                    new MaterializedAuthMethod(AuthMaterialKind.Password, new SecretPayload { Password = result.Password ?? string.Empty }),
                    result.Username);

            case AuthPromptMethod.PublicKeyFile:
            {
                if (string.IsNullOrWhiteSpace(result.KeyFilePath))
                {
                    return (null, result.Username);
                }

                FileKeyReadResult? file = await PrivateKeyImport.ReadAsync(result.KeyFilePath);
                if (file == null)
                {
                    return (null, result.Username);
                }

                return (
                    new MaterializedAuthMethod(
                        AuthMaterialKind.PrivateKey,
                        new SecretPayload { PrivateKeyContent = file.Content, Passphrase = result.Passphrase }),
                    result.Username);
            }

            case AuthPromptMethod.PublicKeyVault:
            {
                if (identity == null || result.VaultMethodId == null)
                {
                    return (null, result.Username);
                }

                Dictionary<string, SecretPayload> secrets = await _vault.LoadIdentitySecretsAsync(identity.Id);
                if (!secrets.TryGetValue(result.VaultMethodId.Value.ToString(), out SecretPayload? payload)
                    || string.IsNullOrEmpty(payload.PrivateKeyContent))
                {
                    return (null, result.Username);
                }

                return (new MaterializedAuthMethod(AuthMaterialKind.PrivateKey, payload), result.Username);
            }

            case AuthPromptMethod.Interactive:
            default:
                // 交互式：零材料，连接时由 KI 桥逐条问答
                return (null, result.Username);
        }
    }

    private static ResolvedSessionConfig WithUsername(ResolvedSessionConfig config, string? username)
        => string.IsNullOrWhiteSpace(username) ? config : config with { Username = username.Trim() };

    // 认证计划步骤的可读描述（仅类型，不含任何材料值）
    private static string DescribeAuthStep(AuthStep step) => step switch
    {
        MethodStep methodStep => $"Method:{methodStep.Method.GetType().Name}",
        AgentFallbackStep => "AgentFallback",
        SingleUsePromptStep => "SingleUsePrompt",
        _ => step.GetType().Name,
    };
}
