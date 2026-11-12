namespace Kei.Term.Ssh.Services;

using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using SshNet.Agent;
using Kei.Term.Core.Abstractions;

// 物化材料 → SSH.NET 认证方法。终端会话、SFTP、SCP、跳板机共用同一套规则。
internal static class SshAuthMethodBuilder
{
    public const string PageantAgentName = "pageant";

    public static AuthenticationMethod[] Build(
        string username,
        IReadOnlyList<MaterializedAuthMethod>? methods,
        Func<string, Task<string?>>? interactivePrompt,
        string? agentSocketPath,
        ILogger logger)
    {
        List<AuthenticationMethod> result = [];
        string? fallbackPassword = null;
        bool hasInteractive = false;

        // 按物化顺序注册，顺序即认证尝试优先级
        foreach (MaterializedAuthMethod material in methods ?? [])
        {
            switch (material.Kind)
            {
                case AuthMaterialKind.Password:
                    // 允许空密码（如 OpenWrt 免密直接回车）
                    fallbackPassword ??= material.Secret?.Password ?? string.Empty;
                    result.Add(new PasswordAuthenticationMethod(username, material.Secret?.Password ?? string.Empty));
                    break;

                case AuthMaterialKind.PrivateKey when !string.IsNullOrEmpty(material.Secret?.PrivateKeyContent):
                    using (var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(material.Secret.PrivateKeyContent)))
                    {
                        PrivateKeyFile keyFile = string.IsNullOrEmpty(material.Secret.Passphrase)
                            ? new PrivateKeyFile(keyStream)
                            : new PrivateKeyFile(keyStream, material.Secret.Passphrase);
                        result.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
                    }

                    break;

                case AuthMaterialKind.Interactive:
                    if (!hasInteractive && interactivePrompt != null)
                    {
                        result.Add(BuildInteractive(username, interactivePrompt, null));
                        hasInteractive = true;
                    }
                    break;

                case AuthMaterialKind.Agent:
                    // SshNet.Agent 无法对 SK/FIDO 身份取公钥算指纹，Fingerprint 字段预留、暂不过滤
                    AuthenticationMethod? agent = TryBuildAgentMethod(username, agentSocketPath, logger);
                    if (agent != null)
                    {
                        result.Add(agent);
                    }

                    break;
            }
        }

        // 密码材料允许 KI 应答；零材料加回调表示用户明确选择纯交互认证。
        // 密钥/Agent 材料不能仅因应用提供了通用回调，就额外启用未配置的认证方式。
        if (!hasInteractive && (fallbackPassword != null || (interactivePrompt != null && (methods == null || methods.Count == 0))))
        {
            result.Add(BuildInteractive(username, interactivePrompt, fallbackPassword));
        }

        return result.ToArray();
    }

    private static KeyboardInteractiveAuthenticationMethod BuildInteractive(
        string username, Func<string, Task<string?>>? interactivePrompt, string? fallbackPassword)
    {
        var method = new KeyboardInteractiveAuthenticationMethod(username);
        method.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                // SSH 后台线程等待 UI 回传；创建认证列表时不触发任何弹窗。
                prompt.Response = interactivePrompt != null
                    ? interactivePrompt(prompt.Request).GetAwaiter().GetResult() ?? string.Empty
                    : fallbackPassword ?? string.Empty;
            }
        };
        return method;
    }

    private static AuthenticationMethod? TryBuildAgentMethod(string username, string? agentSocketPath, ILogger logger)
    {
        try
        {
            SshAgent agent = CreateAgent(agentSocketPath);
            SshAgentPrivateKey[] identities = agent.RequestIdentities();
            if (identities.Length == 0)
            {
                logger.LogInformation("SSH Agent 无可用身份 通道={Agent}", DescribeAgent(agentSocketPath));
                return null;
            }

            return new PrivateKeyAuthenticationMethod(username, identities);
        }
        catch (Exception ex)
        {
            // Agent 不可用或未启动时跳过，继续后续方法
            logger.LogInformation("SSH Agent 不可用 通道={Agent} 原因={Reason}", DescribeAgent(agentSocketPath), ex.Message);
            return null;
        }
    }

    private static SshAgent CreateAgent(string? agentSocketPath)
    {
        if (string.IsNullOrWhiteSpace(agentSocketPath))
        {
            return new SshAgent(null);
        }

        return string.Equals(agentSocketPath.Trim(), PageantAgentName, StringComparison.OrdinalIgnoreCase)
            ? new Pageant(null)
            : new SshAgent(agentSocketPath.Trim(), null);
    }

    private static string DescribeAgent(string? agentSocketPath)
        => string.IsNullOrWhiteSpace(agentSocketPath) ? "系统默认" : agentSocketPath.Trim();
}
