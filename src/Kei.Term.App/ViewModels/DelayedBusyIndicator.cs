namespace Kei.Term.App.ViewModels;

using System;
using System.Threading;

// 防闪烁的“忙碌”指示：忙碌持续超过 showDelay 才显示，显示后至少保持 minVisible。
// 快速完成的加载从不显示；连续加载在停留期内不会先隐藏再重新显示。
public sealed class DelayedBusyIndicator : IDisposable
{
    public static readonly TimeSpan DefaultShowDelay = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan DefaultMinVisible = TimeSpan.FromMilliseconds(300);

    private readonly TimeProvider _time;
    private readonly TimeSpan _showDelay;
    private readonly TimeSpan _minVisible;
    private readonly Action<Action> _marshal;
    private readonly object _gate = new();
    private ITimer? _timer;
    // 每次换定时器就自增，已排队但过期的回调据此丢弃。
    private int _generation;
    private bool _busy;
    private bool _shown;
    private long _shownAt;
    private bool _disposed;

    // 显示状态变化通知；定时器线程上的变化会先经 marshal 送回调用方指定的线程。
    public event Action<bool>? Changed;

    public bool IsShown
    {
        get { lock (_gate) return _shown; }
    }

    public DelayedBusyIndicator(TimeProvider? timeProvider = null, TimeSpan? showDelay = null, TimeSpan? minVisible = null, Action<Action>? marshal = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _showDelay = showDelay ?? DefaultShowDelay;
        _minVisible = minVisible ?? DefaultMinVisible;
        _marshal = marshal ?? (action => action());
    }

    public void SetBusy(bool busy)
    {
        bool? changed = null;
        lock (_gate)
        {
            if (_disposed || _busy == busy) return;
            _busy = busy;
            if (busy)
            {
                // 停留期内再次忙碌：取消待执行的隐藏，保持显示。
                if (_shown) CancelTimer();
                else StartTimer(_showDelay);
            }
            else if (!_shown)
            {
                // 还没来得及显示就结束：永不显示。
                CancelTimer();
            }
            else
            {
                TimeSpan remaining = _minVisible - _time.GetElapsedTime(_shownAt);
                if (remaining <= TimeSpan.Zero)
                {
                    CancelTimer();
                    _shown = false;
                    changed = false;
                }
                else StartTimer(remaining);
            }
        }
        if (changed is { } value) Raise(value);
    }

    private void StartTimer(TimeSpan due)
    {
        CancelTimer();
        int generation = ++_generation;
        _timer = _time.CreateTimer(_ => OnTimer(generation), null, due, Timeout.InfiniteTimeSpan);
    }

    private void CancelTimer()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTimer(int generation)
    {
        bool? changed = null;
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            _timer?.Dispose();
            _timer = null;
            if (_busy && !_shown)
            {
                _shown = true;
                _shownAt = _time.GetTimestamp();
                changed = true;
            }
            else if (!_busy && _shown)
            {
                _shown = false;
                changed = false;
            }
        }
        if (changed is { } value) Raise(value);
    }

    private void Raise(bool value) => _marshal(() => Changed?.Invoke(value));

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CancelTimer();
        }
    }
}
