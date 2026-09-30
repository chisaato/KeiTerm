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

        // 任一密码材料或存在交互回调 → 注册 keyboard-interactive
        // （纯交互式认证：零密码材料 + interactivePrompt 也必须注册，否则 KI 永远不触发）
        if (fallbackPassword != null || interactivePrompt != null)
        {
            var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(username);
            keyboardInteractive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    // 连接运行于非 UI 线程；事件处理器同步阻塞等待 UI 经 Dispatcher 回传输入。
                    // 无交互回调时用预收集密码应答全部提示，避免无 UI 环境等待输入造成死锁
                    prompt.Response = interactivePrompt != null
                        ? interactivePrompt(prompt.Request).GetAwaiter().GetResult() ?? string.Empty
                        : fallbackPassword ?? string.Empty;
                }
            };
            result.Add(keyboardInteractive);
        }

        return result.ToArray();
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
