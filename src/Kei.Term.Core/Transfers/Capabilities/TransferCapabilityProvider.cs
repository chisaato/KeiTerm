namespace Kei.Term.Core.Transfers;

using Kei.Term.Core.Settings;
using Kei.Term.Core.Storage;

// 每个连接一个。有会话行时每次都读仓储，清除后下一次重新探测；没有会话行的临时连接只放内存。
public interface ITransferCapabilityProvider
{
    Task<TransferProbeResult> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class TransferCapabilityProvider : ITransferCapabilityProvider
{
    private readonly Guid? _sessionId;
    private readonly ISessionMetadataRepository? _repository;
    private readonly Func<FileTransferSettings> _settings;
    private readonly Func<CancellationToken, Task<IRemoteShell?>> _acquireShell;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<TransferProbeResult>? _inflight;
    private TransferProbeResult? _memory;
    private int _memoryOnly;

    public TransferCapabilityProvider(
        Guid? sessionId,
        ISessionMetadataRepository? repository,
        Func<FileTransferSettings> settings,
        Func<CancellationToken, Task<IRemoteShell?>> acquireShell,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(acquireShell);
        _sessionId = sessionId;
        _repository = repository;
        _settings = settings;
        _acquireShell = acquireShell;
        _time = time ?? TimeProvider.System;
    }

    public async Task<TransferProbeResult> GetAsync(CancellationToken cancellationToken = default)
    {
        if (UsesRepository)
        {
            TransferProbeResult? stored = await _repository!.GetTransferProbeAsync(_sessionId!.Value, cancellationToken).ConfigureAwait(false);
            if (stored != null)
            {
                return stored;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<TransferProbeResult> task;
        try
        {
            if (Volatile.Read(ref _memoryOnly) != 0 && _memory != null)
            {
                return _memory;
            }

            if (UsesRepository)
            {
                TransferProbeResult? stored = await _repository!.GetTransferProbeAsync(_sessionId!.Value, cancellationToken).ConfigureAwait(false);
                if (stored != null)
                {
                    return stored;
                }
            }

            if (_inflight is { IsCompleted: true })
            {
                _inflight = null;
            }

            _inflight ??= ProbeAndStoreAsync();
            task = _inflight;
        }
        finally
        {
            _gate.Release();
        }

        // 取消只停下这次等待。共享的探测继续跑完并落库，避免一个调用方取消就把别人的结果丢掉。
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool UsesRepository
        => Volatile.Read(ref _memoryOnly) == 0
           && _sessionId is Guid id
           && id != Guid.Empty
           && _repository != null;

    private async Task<TransferProbeResult> ProbeAndStoreAsync()
    {
        TransferProbeResult result = await ProbeCoreAsync().ConfigureAwait(false);
        bool wrote = false;
        if (Volatile.Read(ref _memoryOnly) == 0
            && _sessionId is Guid id
            && id != Guid.Empty
            && _repository != null)
        {
            wrote = await _repository.SetTransferProbeAsync(id, result, CancellationToken.None).ConfigureAwait(false);
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (wrote)
            {
                _memory = null;
                Volatile.Write(ref _memoryOnly, 0);
            }
            else
            {
                _memory = result;
                Volatile.Write(ref _memoryOnly, 1);
            }
        }
        finally
        {
            _gate.Release();
        }

        return result;
    }

    private async Task<TransferProbeResult> ProbeCoreAsync()
    {
        IRemoteShell? shell;
        try
        {
            shell = await _acquireShell(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TransferProbeResult.Unavailable(_time);
        }

        if (shell == null)
        {
            return TransferProbeResult.Unavailable(_time);
        }

        FileTransferSettings settings = _settings() ?? new FileTransferSettings();
        return await TransferCapabilityProbe.ProbeAsync(shell, settings, _time, CancellationToken.None).ConfigureAwait(false);
    }
}
