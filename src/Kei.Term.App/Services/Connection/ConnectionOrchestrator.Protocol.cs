using System;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Abstractions;
using Kei.Term.Core.Models;
using Kei.Term.App.Models;

namespace Kei.Term.App.Services.Connection;

// 非 SSH 的终端工厂不接收认证材料，也不要求文件系统；按协议和引擎显式声明支持范围。
public interface IProtocolSessionFactory
{
    bool Supports(string protocol, TransportEngine engine);
    Task<ITerminalSession> CreateSessionAsync(ResolvedSessionConfig config, TransportEngine engine, CancellationToken ct = default);
}

public sealed partial class ConnectionOrchestrator
{
    private async Task<ConnectOutcome> ConnectProtocolAsync(
        ConnectionRequest request, IConnectionHost host, ConnectionAttempt attempt, TransportEngine engine)
    {
        string protocol = request.Config.Protocol;
        if (!Enum.IsDefined(engine)
            || protocol is not (SessionProtocols.Telnet or SessionProtocols.Serial)
            || _protocolSessions?.Supports(protocol, engine) != true)
        {
            string message = $"不支持连接类型/传输引擎：{protocol} / {engine}";
            if (request.ReuseTarget is { } existing) existing.ReportError(message);
            else await _interaction().NotifyAsync(request.Config.SessionName, message, NotificationLevel.Error);
            return ConnectOutcome.Stopped;
        }

        IConnectionTarget target;
        target = request.ReuseTarget ?? host.OpenTab(request.Config);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation, target.BeginConnectionAttempt());
        attempt = new ConnectionAttempt(lifetime.Token, attempt.Automatic);
        if (request.ReuseTarget is { } reuse)
        {
            await reuse.ResetForReconnectAsync(request.Config, attempt.Cancellation);
            if (reuse.IsDisposed || attempt.Cancellation.IsCancellationRequested) return ConnectOutcome.Stopped;
            target = reuse;
        }

        ITerminalSession? session = null;
        var completionGate = new object();
        bool disconnected = false;
        // 在目标订阅前登记：终止事件与成功标记互斥；后到的断开由目标正常更新状态。
        void OnDisconnected(Exception? _)
        {
            lock (completionGate) disconnected = true;
        }
        try
        {
            session = await Task.Run(() => _protocolSessions.CreateSessionAsync(request.Config, engine, attempt.Cancellation));
            if (target.IsDisposed || attempt.Cancellation.IsCancellationRequested)
            {
                await session.DisposeAsync();
                return ConnectOutcome.Stopped;
            }
            session.Disconnected += OnDisconnected;
            target.AttachSession(session);
            await Task.Run(() => session.ConnectAsync(attempt.Cancellation));
            if (target.IsDisposed || attempt.Cancellation.IsCancellationRequested)
            {
                await target.DetachSessionAsync(session);
                return ConnectOutcome.Stopped;
            }
            lock (completionGate)
            {
                if (disconnected || !session.IsConnected) return ConnectOutcome.Stopped;
                target.MarkConnected();
            }
            target.SetRequiresInteractiveAuthentication(false);
            return ConnectOutcome.Connected;
        }
        catch (Exception ex)
        {
            if (session != null) await target.DetachSessionAsync(session);
            if (target.IsDisposed || attempt.Cancellation.IsCancellationRequested) return ConnectOutcome.Stopped;
            target.ReportError(DescribeFailure(ex));
            return ex is NotSupportedException or OperationCanceledException
                ? ConnectOutcome.Stopped : ConnectOutcome.RetryableFailure;
        }
        finally
        {
            if (session != null) session.Disconnected -= OnDisconnected;
        }
    }
}
