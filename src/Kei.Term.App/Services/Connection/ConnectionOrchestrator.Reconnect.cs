using System;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.Core.Services;

namespace Kei.Term.App.Services.Connection;

public sealed partial class ConnectionOrchestrator
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // 每条连接独占本次结果和交互状态；并发标签不能覆盖彼此的重连资格。
    private sealed class ConnectionAttempt(CancellationToken cancellation, bool automatic = false)
    {
        public CancellationToken Cancellation { get; } = cancellation;
        public bool Automatic { get; } = automatic;
        public bool RequiredInteraction { get; set; }
    }

    private enum ConnectOutcome { Connected, RetryableFailure, Stopped }

    public async Task ScheduleReconnectAsync(
        ConnectionRequest request,
        IConnectionHost host,
        ReconnectFacts facts,
        CancellationToken ct)
    {
        if (request.ReuseTarget == null) return;
        for (int next = facts.NextAttempt; ; next++)
        {
            ReconnectDecision decision = ReconnectPolicy.Decide(facts with
            {
                Enabled = facts.Enabled && _settings.Current.AutoReconnectOnDisconnect,
                InteractiveRequired = facts.InteractiveRequired || request.ReuseTarget.RequiresInteractiveAuthentication,
                NextAttempt = next
            });
            if (!decision.ShouldReconnect || decision.DelaySeconds is not int seconds
                || ct.IsCancellationRequested || request.ReuseTarget.IsDisposed) return;

            request.ReuseTarget.WriteLocalStatus(ReconnectMessages.Waiting(seconds, next));
            try { await _delay(TimeSpan.FromSeconds(seconds), ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested || request.ReuseTarget.IsDisposed || !_settings.Current.AutoReconnectOnDisconnect) return;

            ConnectOutcome outcome;
            try
            {
                outcome = await ConnectCoreAsync(request, host, new ConnectionAttempt(ct, automatic: true));
            }
            catch (OperationCanceledException)
            {
                // 自动认证可能因凭据库锁定而中止，不弹窗或继续重试。
                return;
            }
            if (outcome == ConnectOutcome.Connected)
            {
                request.ReuseTarget.WriteLocalStatus(ReconnectMessages.Reconnected);
                return;
            }
            if (outcome != ConnectOutcome.RetryableFailure) return;
        }
    }
}
