namespace Kei.Term.Core.Security;

using System.Collections.Concurrent;
using Kei.Term.Core.Storage;
using Microsoft.Extensions.Logging;

// 主机密钥校验结论。Decision 为 null 表示策略要求人工确认（RequiresConfirmation）
public sealed record HostKeyCheckOutcome(bool Accepted, HostKeyEvaluation Evaluation, HostKeyDecision? Decision)
{
    public bool RequiresConfirmation => Decision == null;
}

// 连接期间主机密钥被拒绝：区别于认证失败，UI 不应回弹密码框
public sealed class HostKeyRejectedException : Exception
{
    public HostKeyRejectedException(HostKeyCheckOutcome outcome, Exception? inner = null)
        : base(BuildMessage(outcome), inner)
    {
        Outcome = outcome;
    }

    public HostKeyCheckOutcome Outcome { get; }

    private static string BuildMessage(HostKeyCheckOutcome outcome)
    {
        PresentedHostKey key = outcome.Evaluation.Presented;
        string endpoint = KnownHostMatcher.ToHostString(key.Host, key.Port);
        return outcome.Evaluation.Verdict switch
        {
            HostKeyVerdict.Changed => $"主机密钥已变更，连接已阻断（可能存在中间人攻击）: {endpoint} {key.KeyType} {key.FingerprintSha256}",
            HostKeyVerdict.Revoked => $"主机密钥已被吊销: {endpoint} {key.KeyType} {key.FingerprintSha256}",
            HostKeyVerdict.Unknown => $"未信任的主机密钥: {endpoint} {key.KeyType} {key.FingerprintSha256}",
            _ => $"主机密钥被拒绝: {endpoint}"
        };
    }
}

// 主机密钥信任编排，分两段：
// 1) VerifyAsync：在 SSH 密钥交换回调内同步调用，只做查库与策略裁决，绝不弹窗
//    （握手受连接超时约束，用户阅读指纹的时间不能计入超时）；
// 2) ConfirmAsync：需要人工确认时由上层在握手之外弹窗，确认后重连即可命中信任。
public sealed class HostKeyTrustService
{
    private readonly IKnownHostRepository _repository;
    private readonly Func<HostKeyPolicy> _policy;
    private readonly Func<HostKeyEvaluation, CancellationToken, Task<HostKeyDecision>>? _prompt;
    private readonly ILogger? _logger;

    // 「仅本次接受」的密钥：本进程内视为可信，不落库
    private readonly ConcurrentDictionary<string, byte> _acceptedOnce = new(StringComparer.Ordinal);

    public HostKeyTrustService(
        IKnownHostRepository repository,
        Func<HostKeyPolicy> policy,
        Func<HostKeyEvaluation, CancellationToken, Task<HostKeyDecision>>? prompt = null,
        ILogger? logger = null)
    {
        _repository = repository;
        _policy = policy;
        _prompt = prompt;
        _logger = logger;
    }

    // 宿主是否提供了确认 UI；未提供时 Ask 策略退化：未知主机 TOFU，变更拒绝
    public bool CanConfirm => _prompt != null;

    public async Task<HostKeyEvaluation> EvaluateAsync(PresentedHostKey presented, CancellationToken ct = default)
    {
        IReadOnlyList<KnownHostEntry> candidates = await _repository.GetCandidatesAsync(presented.Host, presented.Port, ct);
        List<KnownHostEntry> matched = candidates
            .Where(e => KnownHostMatcher.Matches(e, presented.Host, presented.Port))
            .ToList();
        HostKeyEvaluation evaluation = HostKeyVerifier.Evaluate(presented, matched);

        // 本次运行已人工放行过的同一把钥匙（吊销除外）
        if (evaluation.Verdict is HostKeyVerdict.Unknown or HostKeyVerdict.Changed
            && _acceptedOnce.ContainsKey(AllowanceKey(presented)))
        {
            return evaluation with { Verdict = HostKeyVerdict.Trusted };
        }

        return evaluation;
    }

    public async Task<HostKeyCheckOutcome> VerifyAsync(PresentedHostKey presented, CancellationToken ct = default)
    {
        HostKeyEvaluation evaluation = await EvaluateAsync(presented, ct);
        HostKeyPolicy policy = _policy();
        HostKeyDecision? decision = HostKeyVerifier.DecideWithoutPrompt(evaluation.Verdict, policy);

        if (decision == null && !CanConfirm)
        {
            // 无确认 UI：不能无人值守地放行变更；未知主机按 accept-new 记录
            decision = evaluation.Verdict == HostKeyVerdict.Unknown ? HostKeyDecision.AcceptAndRemember : HostKeyDecision.Reject;
        }

        Log(evaluation, policy, decision);

        if (decision is { } final)
        {
            await ApplyDecisionAsync(evaluation, final, prompted: false, ct);
            return new HostKeyCheckOutcome(final != HostKeyDecision.Reject, evaluation, final);
        }

        return new HostKeyCheckOutcome(false, evaluation, null);
    }

    // 握手外的人工确认；返回是否放行（放行后调用方应重连）
    public async Task<bool> ConfirmAsync(HostKeyEvaluation evaluation, CancellationToken ct = default)
    {
        if (_prompt == null || evaluation.Verdict == HostKeyVerdict.Revoked)
        {
            return false;
        }

        HostKeyDecision decision = await _prompt(evaluation, ct);
        Log(evaluation, _policy(), decision);
        await ApplyDecisionAsync(evaluation, decision, prompted: true, ct);
        return decision != HostKeyDecision.Reject;
    }

    private async Task ApplyDecisionAsync(HostKeyEvaluation evaluation, HostKeyDecision decision, bool prompted, CancellationToken ct)
    {
        PresentedHostKey presented = evaluation.Presented;
        DateTime now = DateTime.UtcNow;

        if (evaluation.Verdict == HostKeyVerdict.Trusted)
        {
            KnownHostEntry? hit = evaluation.KnownEntries.FirstOrDefault(e =>
                e.Status == KnownHostStatus.Trusted
                && string.Equals(e.PublicKeyBase64, presented.PublicKeyBase64, StringComparison.Ordinal));
            if (hit != null)
            {
                await _repository.TouchAsync(hit.Id, now, ct);
            }

            return;
        }

        if (decision == HostKeyDecision.AcceptOnce)
        {
            _acceptedOnce[AllowanceKey(presented)] = 0;
            return;
        }

        if (decision != HostKeyDecision.AcceptAndRemember)
        {
            return;
        }

        // 用户确认替换变更的密钥：移除该端点同算法的旧精确条目（模式条目来自导入，不做改写）
        if (evaluation.Verdict == HostKeyVerdict.Changed)
        {
            foreach (KnownHostEntry stale in evaluation.KnownEntries.Where(e =>
                         !e.IsPattern
                         && e.Status == KnownHostStatus.Trusted
                         && string.Equals(e.KeyType, presented.KeyType, StringComparison.Ordinal)))
            {
                await _repository.DeleteAsync(stale.Id, ct);
            }
        }

        await _repository.SaveAsync(new KnownHostEntry
        {
            Host = presented.Host.Trim().ToLowerInvariant(),
            Port = presented.Port,
            KeyType = presented.KeyType,
            PublicKeyBase64 = presented.PublicKeyBase64,
            FingerprintSha256 = presented.FingerprintSha256,
            Status = KnownHostStatus.Trusted,
            Source = prompted ? KnownHostSource.UserConfirmed : KnownHostSource.FirstUse,
            CreatedAt = now,
            LastSeenAt = now
        }, ct);
    }

    private void Log(HostKeyEvaluation evaluation, HostKeyPolicy policy, HostKeyDecision? decision)
    {
        PresentedHostKey presented = evaluation.Presented;
        _logger?.LogInformation(
            "主机密钥校验 endpoint={Endpoint} 算法={KeyType} 指纹={Fingerprint} 判定={Verdict} 策略={Policy} 决定={Decision}",
            KnownHostMatcher.ToHostString(presented.Host, presented.Port),
            presented.KeyType,
            presented.FingerprintSha256,
            evaluation.Verdict,
            policy,
            decision?.ToString() ?? "待确认");
    }

    private static string AllowanceKey(PresentedHostKey key)
        => $"{KnownHostMatcher.ToHostString(key.Host, key.Port)} {key.PublicKeyBase64}";
}
