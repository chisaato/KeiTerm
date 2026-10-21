namespace Kei.Term.Core.Sync;

// 启动、本地修改、定时和手动共用一轮后台同步。运行中再次触发只保留「结束后再跑一轮」。
public sealed class SyncScheduler : IDisposable
{
    public const string VaultLockedMessage = "保险库已锁定，同步暂停";
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ISyncClock _clock;
    private readonly ISyncLocationSource _locations;
    private readonly ISyncPassphraseStore _secrets;
    private readonly ISyncChangeSignal _changes;
    private readonly Func<ISyncLocation, CancellationToken, Task<SyncResult>> _sync;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _wake = new();
    private readonly List<TaskCompletionSource<SyncResult>> _waiters = new();
    private int _generation;
    private bool _started;
    private bool _running;
    private bool _immediate;
    private bool _again;
    private bool _catchUp;
    private bool _unlocked;
    private DateTime? _debounceDue;
    private DateTime _nextInterval;
    private Task? _loop;

    public SyncScheduler(ISyncClock clock, ISyncLocationSource locations, ISyncPassphraseStore secrets,
        ISyncChangeSignal changes, Func<ISyncLocation, CancellationToken, Task<SyncResult>> sync)
    {
        _clock = clock;
        _locations = locations;
        _secrets = secrets;
        _changes = changes;
        _sync = sync;
        _unlocked = secrets.IsUnlocked;
    }

    public string Status { get; private set; } = "";

    public event Action<string>? StatusChanged;

    // 一轮真正执行过后刷新界面；未配置或锁定而跳过的触发不刷新。
    public event Func<Task>? Synchronized;

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _changes.Changed += OnChanged;
            _unlocked = _secrets.IsUnlocked;
            _nextInterval = _clock.UtcNow + Interval;
            if (!_unlocked)
            {
                if (HasLocation())
                {
                    _catchUp = true;
                    SetStatus(VaultLockedMessage);
                }
            }
            else _immediate = true;
            _loop = Task.Run(LoopAsync);
            Wake();
        }
    }

    public void NotifyVault(bool unlocked)
    {
        lock (_gate)
        {
            if (_unlocked == unlocked) return;
            _unlocked = unlocked;
            if (!unlocked)
            {
                _immediate = false;
                _debounceDue = null;
                _again = false;
                if (HasLocation())
                {
                    _catchUp = true;
                    SetStatus(VaultLockedMessage);
                }
            }
            else if (_catchUp && HasLocation())
            {
                _catchUp = false;
                _immediate = true;
                // 锁定期间错过的多次定时只补一轮，下次定时从现在重新计算。
                if (_nextInterval == default || _nextInterval <= _clock.UtcNow) BumpInterval();
            }
            else _catchUp = false;
            Wake();
        }
    }

    public Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        var waiter = new TaskCompletionSource<SyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
                return Task.FromResult(new SyncResult(false, "同步已停止"));
            if (!_unlocked)
            {
                if (HasLocation())
                {
                    _catchUp = true;
                    SetStatus(VaultLockedMessage);
                }
                return Task.FromResult(new SyncResult(false, HasLocation() ? VaultLockedMessage : "请选择同步目录"));
            }
            _waiters.Add(waiter);
            if (_running) _again = true;
            else _immediate = true;
            Wake();
        }
        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        return waiter.Task;
    }

    public void Dispose()
    {
        List<TaskCompletionSource<SyncResult>> abandoned;
        lock (_gate)
        {
            _changes.Changed -= OnChanged;
            _stop.Cancel();
            abandoned = DrainWaiters();
            Wake();
        }
        foreach (TaskCompletionSource<SyncResult> waiter in abandoned)
            waiter.TrySetResult(new SyncResult(false, "同步已停止"));
    }

    private void OnChanged()
    {
        lock (_gate)
        {
            if (!_started || _stop.IsCancellationRequested) return;
            if (!_unlocked)
            {
                if (HasLocation())
                {
                    _catchUp = true;
                    SetStatus(VaultLockedMessage);
                }
                return;
            }
            if (!HasLocation()) return;
            // 空闲时重新计算 30 秒；正在同步时并入结束后的那一轮，不再另起防抖。
            if (_running) _again = true;
            else
            {
                _debounceDue = _clock.UtcNow + Debounce;
                Wake();
            }
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TimeSpan wait;
            int observed;
            CancellationToken wake;
            lock (_gate)
            {
                wait = ComputeWait();
                observed = _generation;
                wake = _wake.Token;
            }
            if (wait != TimeSpan.Zero)
            {
                lock (_gate)
                {
                    if (_generation != observed) continue;
                    wake = _wake.Token;
                }
                try { await _clock.DelayAsync(wait, wake).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { continue; }
                catch (OperationCanceledException) { break; }
                continue;
            }

            List<TaskCompletionSource<SyncResult>> waiters;
            lock (_gate)
            {
                if (_stop.IsCancellationRequested) break;
                _running = true;
                _immediate = false;
                _again = false;
                _debounceDue = null;
                if (_nextInterval <= _clock.UtcNow) BumpInterval();
                waiters = DrainWaiters();
            }

            SyncResult? result = null;
            OperationCanceledException? stopped = null;
            try { result = await RunOnceAsync().ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (_stop.IsCancellationRequested) { stopped = ex; }
            finally
            {
                lock (_gate)
                {
                    _running = false;
                    if (!_unlocked)
                    {
                        _again = false;
                        _debounceDue = null;
                        if (HasLocation())
                        {
                            _catchUp = true;
                            SetStatus(VaultLockedMessage);
                        }
                    }
                    else if (_again)
                    {
                        _again = false;
                        _immediate = true;
                        _debounceDue = null;
                    }
                }
            }

            if (stopped != null)
            {
                foreach (TaskCompletionSource<SyncResult> waiter in waiters)
                    waiter.TrySetCanceled(stopped.CancellationToken);
                break;
            }
            if (result != null)
            {
                foreach (TaskCompletionSource<SyncResult> waiter in waiters)
                    waiter.TrySetResult(result);
                SetStatus(Format(result));
                Func<Task>? refresh = Synchronized;
                if (refresh != null)
                {
                    try { await refresh().ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                }
            }
            else
            {
                SyncResult skipped = !_unlocked
                    ? new SyncResult(false, VaultLockedMessage)
                    : HasLocation()
                        ? new SyncResult(false, "请先设定或加入同步口令")
                        : new SyncResult(false, "请选择同步目录");
                foreach (TaskCompletionSource<SyncResult> waiter in waiters)
                    waiter.TrySetResult(skipped);
                if (waiters.Count > 0) SetStatus(skipped.Message);
            }
        }
    }

    private TimeSpan ComputeWait()
    {
        if (!_unlocked) return Timeout.InfiniteTimeSpan;
        DateTime now = _clock.UtcNow;
        DateTime due = _nextInterval;
        if (_immediate) due = now;
        if (_debounceDue is DateTime debounce && debounce < due) due = debounce;
        TimeSpan wait = due - now;
        return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private async Task<SyncResult?> RunOnceAsync()
    {
        // 未使用同步的明文 Vault 仍可正常工作，不显示同步前置条件错误。
        if (!_locations.HasConfiguration) return null;
        if (!_secrets.IsUnlocked)
        {
            lock (_gate) _unlocked = false;
            return null;
        }
        if (_secrets is ISyncSafetyStore safety)
        {
            try { await safety.EnsureSyncMasterPasswordAsync(_stop.Token).ConfigureAwait(false); }
            catch (InvalidOperationException ex) { return new(false, ex.Message); }
        }
        ISyncLocation? location = await _locations.OpenAsync(_stop.Token).ConfigureAwait(false);
        if (location == null) return null;
        char[]? passphrase;
        try { passphrase = await _secrets.GetPassphraseAsync(_stop.Token).ConfigureAwait(false); }
        catch (InvalidOperationException) { (location as IDisposable)?.Dispose(); return null; }
        bool configured = passphrase is { Length: > 0 };
        if (passphrase != null) Array.Clear(passphrase);
        if (!configured)
        {
            (location as IDisposable)?.Dispose();
            return null;
        }

        try
        {
            // 导入和墓碑清理都发生在这一轮里，整段抑制后写回不会再预约防抖。
            using (_changes.Suppress())
                return await _sync(location, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SyncResult(false, ex.Message);
        }
        finally { (location as IDisposable)?.Dispose(); }
    }

    private void BumpInterval()
    {
        DateTime now = _clock.UtcNow;
        if (_nextInterval == default) _nextInterval = now;
        do _nextInterval += Interval;
        while (_nextInterval <= now);
    }

    private bool HasLocation() => _locations.HasConfiguration;

    private List<TaskCompletionSource<SyncResult>> DrainWaiters()
    {
        var drained = new List<TaskCompletionSource<SyncResult>>(_waiters);
        _waiters.Clear();
        return drained;
    }

    private void Wake()
    {
        _generation++;
        CancellationTokenSource previous = _wake;
        _wake = new CancellationTokenSource();
        previous.Cancel();
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private string Format(SyncResult result)
    {
        string text = $"{_clock.UtcNow.ToLocalTime():g}：{result.Message}";
        if (result.WriterDeviceId == null) return text;
        return text + $"\n最后写入设备：{result.WriterDeviceId}\n写入时间：{result.WrittenAtUtc:u}";
    }
}
