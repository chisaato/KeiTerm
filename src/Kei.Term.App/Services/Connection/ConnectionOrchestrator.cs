using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Helpers;
using Kei.Term.App.ViewModels;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.Core.Security;
using Kei.Term.Core.Services;
using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;
using Kei.Term.Ssh.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kei.Term.App.Services.Connection;

// 「怎么连」：收集认证 → 解析跳板 → 开标签 → 后台建连；认证失败回弹重试、主机密钥握手外确认、成功后挂文件侧栏。
// 不依赖 Avalonia：UI 线程切换经注入的 uiDispatch 完成，可在测试中同步执行。
public sealed class ConnectionOrchestrator
{
    // 认证失败后单次弹窗重试的最大次数
    public const int MaxAuthRetries = 3;

    // 同一次连接中主机密钥人工确认的上限（防止服务器每次呈现不同密钥导致无限弹窗）
    public const int MaxHostKeyConfirmations = 2;

    private readonly ISshSessionFactory _sshFactory;
    private readonly AuthMaterialCollector _collector;
    private readonly HostKeyTrustService? _hostKeyTrust;
    private readonly ISettingsService _settings;
    private readonly Func<IInteractionService> _interaction;
    private readonly Action<Action> _uiDispatch;
    private readonly ILogger _logger;
    private readonly IProxyRepository? _proxies;
    private readonly IPortForwardRepository? _portForwards;

    public ConnectionOrchestrator(
        ISshSessionFactory sshFactory,
        AuthMaterialCollector collector,
        HostKeyTrustService? hostKeyTrust,
        ISettingsService settings,
        Func<IInteractionService> interaction,
        Action<Action> uiDispatch,
        ILogger? logger = null,
        IProxyRepository? proxies = null,
        IPortForwardRepository? portForwards = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sshFactory = sshFactory;
        _collector = collector;
        _hostKeyTrust = hostKeyTrust;
        _settings = settings;
        _interaction = interaction;
        _uiDispatch = uiDispatch;
        _logger = logger ?? NullLogger.Instance;
        _proxies = proxies;
        _portForwards = portForwards;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _lastConnectSucceeded;
    private bool _interactiveThisAttempt;

    // 上一次成功连接是否依赖当场输入（口令重试或 KI / 2FA）
    public bool LastAttemptRequiredInteraction { get; private set; }

    // 意外断开后的下一次尝试。等待被取消则不再连接。必须复用 ReuseTarget。
    public async Task ScheduleReconnectAsync(
        ConnectionRequest request,
        IConnectionHost host,
        ReconnectFacts facts,
        CancellationToken ct)
    {
        ReconnectDecision decision = ReconnectPolicy.Decide(facts);
        if (!decision.ShouldReconnect || decision.DelaySeconds is not int seconds)
        {
            return;
        }

        request.ReuseTarget?.WriteLocalStatus(ReconnectMessages.Waiting(seconds, decision.Attempt));
        try
        {
            await _delay(TimeSpan.FromSeconds(seconds), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || request.ReuseTarget is { IsDisposed: true })
        {
            return;
        }

        await ConnectAsync(request, host);
        if (_lastConnectSucceeded)
        {
            request.ReuseTarget?.WriteLocalStatus(ReconnectMessages.Reconnected);
        }
    }

    public async Task ConnectAsync(ConnectionRequest request, IConnectionHost host)
    {
        _lastConnectSucceeded = false;
        _interactiveThisAttempt = false;
        // 出口不可用时不收集认证、不建标签、不拨号
        PreparedExit exit = await PrepareExitAsync(request.Config);
        if (exit.Error != null)
        {
            _logger.LogWarning("出口不可用 会话={Session} 原因={Reason}", request.Config.SessionName, exit.Error);
            await _interaction().NotifyAsync(request.Config.SessionName, exit.Error);
            return;
        }

        ConnectionRequest adjusted = request with { Config = exit.Config };

        // 1. 认证材料；用户取消兜底弹窗则不建标签
        CollectedAuth? auth = await _collector.CollectTargetAsync(adjusted);
        if (auth == null)
        {
            return;
        }

        // 2. 跳板链：逐跳解析配置并物化认证；跳板自己的 SOCKS5 只挂在那一跳上
        IReadOnlyList<SshHop> jumpHops;
        try
        {
            jumpHops = await _collector.CollectJumpHopsAsync(auth.Config, host.FindSession);
            jumpHops = await DecorateHopsAsync(jumpHops, host.FindSession);
        }
        catch (JumpChainException ex)
        {
            _logger.LogWarning("跳板链解析失败 会话={Session} 原因={Reason}", auth.Config.SessionName, ex.Message);
            await _interaction().NotifyAsync(auth.Config.SessionName, ex.Message);
            return;
        }

        // 3. 先建标签（Connecting）或复位待重连的标签，再后台建连；
        //    复位放在认证收集之后：用户取消认证时旧标签保持原状
        IConnectionTarget target;
        if (request.ReuseTarget is { } reuse)
        {
            if (reuse.IsDisposed)
            {
                return;
            }

            await reuse.ResetForReconnectAsync(auth.Config);
            target = reuse;
        }
        else
        {
            target = host.OpenTab(auth.Config);
        }

        await ConnectWithRetryAsync(target, auth, jumpHops, exit.Socks5Host, exit.Socks5Port);
    }

    private async Task ConnectWithRetryAsync(
        IConnectionTarget target,
        CollectedAuth auth,
        IReadOnlyList<SshHop> jumpHops,
        string? socks5Host,
        int socks5Port)
    {
        ResolvedSessionConfig resolved = auth.Config;
        var current = new List<MaterializedAuthMethod>(auth.Materials);
        IReadOnlyList<PortForward> forwards = await LoadForwardsAsync(resolved.SessionId);
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, resolved.ConnectTimeoutSeconds));
        int promptCount = 0;
        int hostKeyConfirmations = 0;

        while (true)
        {
            if (target.IsDisposed)
            {
                return;
            }

            _logger.LogInformation(
                "创建 SSH 会话 host={Host}:{Port} 会话={Session} 材料数={Count}",
                resolved.Host,
                resolved.Port,
                resolved.SessionName,
                current.Count);

            ISshSession? session = null;
            Exception? failure = null;
            try
            {
                // 连接流程整体运行于后台线程，避免阻塞 UI
                SshConnectOptions options = BuildConnectOptions(timeout, jumpHops, socks5Host, socks5Port, forwards);
                ResolvedSessionConfig attemptConfig = resolved;
                List<MaterializedAuthMethod> attemptMaterials = current;
                session = await Task.Run(() => _sshFactory.CreateSessionAsync(attemptConfig, attemptMaterials, options, CancellationToken.None));
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (session != null)
            {
                if (target.IsDisposed)
                {
                    await session.DisposeAsync();
                    return;
                }

                // 端点挂载必须在 UI 线程完成（await 续体回到调用方上下文）
                target.AttachSession(session);

                ISshSession connecting = session;
                failure = await Task.Run(async () =>
                {
                    try
                    {
                        await connecting.ConnectAsync();
                        return null;
                    }
                    catch (Exception ex)
                    {
                        return ex;
                    }
                });
            }

            if (failure == null)
            {
                _logger.LogInformation("SSH 会话连接成功 host={Host}:{Port}", resolved.Host, resolved.Port);
                target.MarkConnected();
                _lastConnectSucceeded = true;
                LastAttemptRequiredInteraction = _interactiveThisAttempt;
                if (session.ForwardStartErrors.Count > 0)
                {
                    target.ReportWarning(string.Join("；", session.ForwardStartErrors));
                }
                // 文件通道必须用最终认证成功的材料（重试后的 current），而非首轮材料
                StartFileSystem(target, resolved, current.ToList(), session!, BuildConnectOptions(timeout, jumpHops, socks5Host, socks5Port, forwards));
                return;
            }

            _logger.LogError(
                failure,
                "SSH 会话创建或连接失败 host={Host}:{Port} 会话={Session}",
                resolved.Host,
                resolved.Port,
                resolved.SessionName);

            await target.DetachSessionAsync();

            // 主机密钥需人工确认：在握手之外弹窗（不受连接超时约束），放行后直接重连
            if (failure is HostKeyRejectedException { Outcome.RequiresConfirmation: true } hostKeyFailure
                && _hostKeyTrust is { CanConfirm: true }
                && hostKeyConfirmations < MaxHostKeyConfirmations)
            {
                hostKeyConfirmations++;
                if (await _hostKeyTrust.ConfirmAsync(hostKeyFailure.Outcome.Evaluation))
                {
                    _logger.LogInformation("主机密钥已人工确认，重新连接 host={Host}:{Port}", resolved.Host, resolved.Port);
                    continue;
                }
            }

            // 弹窗次数达上限或非认证类失败：终端显示失败原因
            if (promptCount >= MaxAuthRetries || !IsAuthenticationFailure(failure))
            {
                _logger.LogError(
                    "认证失败终止 host={Host}:{Port} 重试次数={Retries} 原因={Reason}",
                    resolved.Host,
                    resolved.Port,
                    promptCount,
                    DescribeFailure(failure));
                target.ReportError(DescribeFailure(failure));
                return;
            }

            promptCount++;
            _interactiveThisAttempt = true;
            _logger.LogInformation("认证失败，第 {Attempt} 次弹出认证重试 host={Host}:{Port}", promptCount, resolved.Host, resolved.Port);
            var retry = await _collector.PromptRetryAsync(resolved.Username, auth.Identity);
            if (retry == null)
            {
                _logger.LogInformation("用户取消认证重试，连接中止 host={Host}:{Port}", resolved.Host, resolved.Port);
                target.ReportError(DescribeFailure(failure));
                return;
            }

            (AuthPromptMethod method, MaterializedAuthMethod? newMaterial, string? newUsername) = retry.Value;
            if (!string.IsNullOrWhiteSpace(newUsername))
            {
                resolved = resolved with { Username = newUsername.Trim() };
            }

            ReplaceMaterials(current, method, newMaterial);
        }
    }

    // 按用户新选的方法重建材料：同类材料被替换；交互式 = 零材料重连（靠 KI 桥应答）
    internal static void ReplaceMaterials(List<MaterializedAuthMethod> current, AuthPromptMethod method, MaterializedAuthMethod? newMaterial)
    {
        switch (method)
        {
            case AuthPromptMethod.Password:
                current.RemoveAll(m => m.Kind == AuthMaterialKind.Password);
                break;
            case AuthPromptMethod.PublicKeyFile:
            case AuthPromptMethod.PublicKeyVault:
                current.RemoveAll(m => m.Kind == AuthMaterialKind.PrivateKey);
                break;
            default:
                current.Clear();
                break;
        }

        if (newMaterial != null)
        {
            current.Add(newMaterial);
        }
    }

    // 异步预准备并挂载文件侧栏；失败只记日志，不影响终端
    private void StartFileSystem(
        IConnectionTarget target,
        ResolvedSessionConfig config,
        IReadOnlyList<MaterializedAuthMethod> materials,
        ISshSession session,
        SshConnectOptions options)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                IRemoteFileSystem fileSystem = await _sshFactory.CreateFileSystemAsync(config, materials, session, options);
                await target.AttachFileSystemAsync(fileSystem);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "初始化远程文件系统侧栏失败");
            }
        });
    }

    private SshConnectOptions BuildConnectOptions(
        TimeSpan timeout,
        IReadOnlyList<SshHop> jumpHops,
        string? socks5Host,
        int socks5Port,
        IReadOnlyList<PortForward> forwards)
    {
        AppSettings settings = _settings.Current;
        return new SshConnectOptions
        {
            ConnectTimeout = timeout,
            KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(0, settings.KeepAliveIntervalSeconds)),
            InteractivePrompt = BuildInteractivePrompt(),
            HostKeyVerifier = _hostKeyTrust,
            AgentSocketPath = settings.CustomAgentSocketPath,
            JumpHosts = jumpHops,
            Socks5Host = socks5Host,
            Socks5Port = socks5Port,
            PortForwards = forwards
        };
    }

    private async Task<IReadOnlyList<PortForward>> LoadForwardsAsync(Guid sessionId)
    {
        if (_portForwards == null || sessionId == Guid.Empty)
        {
            return [];
        }

        return await _portForwards.GetBySessionAsync(sessionId);
    }

    // 目标自己的出口。kind=proxy 的会话型档案改写成跳板，SOCKS5 只留给本会话最外层。
    private async Task<PreparedExit> PrepareExitAsync(ResolvedSessionConfig config)
    {
        if (config.ProxyProfileId is not Guid proxyId)
        {
            return new PreparedExit(config, null, null, 0);
        }

        SessionProxyDecision decision = await ResolveProxyAsync(proxyId);
        return decision switch
        {
            FailedDecision failed => new PreparedExit(config, failed.Message, null, 0),
            Socks5Decision socks => new PreparedExit(
                config with { ProxyProfileId = null, JumpHostSessionId = null },
                null,
                socks.Host,
                socks.Port),
            JumpDecision jump => new PreparedExit(
                config with { ProxyProfileId = null, JumpHostSessionId = jump.SessionId },
                null,
                null,
                0),
            _ => new PreparedExit(config, SessionProxyExit.NotWiredMessage, null, 0)
        };
    }

    // 每一跳自己的 SOCKS5 挂在该跳上；HTTP / 已删除在拨号前失败。会话型档案继续展开跳板链。
    private async Task<IReadOnlyList<SshHop>> DecorateHopsAsync(
        IReadOnlyList<SshHop> hops,
        Func<Guid, SessionNode?> findSession,
        int depth = 0)
    {
        var result = new List<SshHop>();
        foreach (SshHop hop in hops)
        {
            if (hop.Config.ProxyProfileId is not Guid proxyId)
            {
                result.Add(hop);
                continue;
            }

            SessionProxyDecision decision = await ResolveProxyAsync(proxyId);
            switch (decision)
            {
                case Socks5Decision socks:
                    result.Add(hop with { Socks5Host = socks.Host, Socks5Port = socks.Port });
                    break;
                case JumpDecision jump when depth < JumpChainResolver.MaxDepth:
                    ResolvedSessionConfig via = hop.Config with
                    {
                        JumpHostSessionId = jump.SessionId,
                        ProxyProfileId = null
                    };
                    IReadOnlyList<SshHop> extra = await _collector.CollectJumpHopsAsync(via, findSession);
                    result.AddRange(await DecorateHopsAsync(extra, findSession, depth + 1));
                    result.Add(hop);
                    break;
                case FailedDecision failed:
                    throw new JumpChainException(failed.Message);
                default:
                    throw new JumpChainException(SessionProxyExit.NotWiredMessage);
            }
        }

        return result;
    }

    private async Task<SessionProxyDecision> ResolveProxyAsync(Guid proxyId)
    {
        if (_proxies == null)
        {
            return SessionProxyDecision.Failed(SessionProxyExit.DeletedMessage);
        }

        ProxyProfile? proxy = await _proxies.GetByIdAsync(proxyId);
        return SessionProxyExit.Resolve(proxyId, id => proxy != null && proxy.Id == id ? proxy : null);
    }

    private sealed record PreparedExit(ResolvedSessionConfig Config, string? Error, string? Socks5Host, int Socks5Port);

    // keyboard-interactive 真交互回调：SSH 后台线程 → UI 线程弹提示窗（2FA 可用）
    private Func<string, Task<string?>> BuildInteractivePrompt()
    {
        return prompt =>
        {
            // 出现 KI / 2FA 弹窗的连接不要在后台自动重连
            _interactiveThisAttempt = true;
            // 提示文本来自服务器，可记录；应答内容可能含密码/OTP，绝不记录
            _logger.LogInformation("KI 认证提示弹出 提示={Prompt}", prompt);
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _uiDispatch(async () =>
            {
                try
                {
                    string? response = await _interaction().PromptKeyboardInteractiveAsync(prompt);
                    _logger.LogInformation("KI 认证提示应答 已应答={Answered}", !string.IsNullOrEmpty(response));
                    tcs.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "KI 认证提示处理异常");
                    tcs.TrySetResult(null);
                }
            });
            return tcs.Task;
        };
    }

    // SSH.NET 的认证失败类型名均含 Authentication（SshAuthenticationException 等）
    private static bool IsAuthenticationFailure(Exception ex)
        => ex.GetType().Name.Contains("Authentication", StringComparison.Ordinal);

    private static string DescribeFailure(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? Strings.Get("Status.Auth.Failed") : ex.Message;
}
