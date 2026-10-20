using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kei.Term.App.Models;

namespace Kei.Term.App.ViewModels.Notifications;

// 单条 Toast 通知 ViewModel
public partial class ToastItemViewModel : ObservableObject
{
    private readonly Action<ToastItemViewModel> _onDismiss;
    private readonly Func<string, Task>? _copyToClipboard;
    private readonly TimeProvider _timeProvider;
    private ITimer? _timer;
    private TimeSpan _remainingTime;
    private DateTimeOffset _lastResumeTime;
    private bool _isPaused;
    private readonly object _timerLock = new();

    public Guid Id { get; } = Guid.NewGuid();

    public string Title { get; }

    public string Message { get; }

    public NotificationLevel Level { get; }

    // 默认展示截断预览；超过长文本阈值时可展开/收起
    public bool CanExpand { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isCopyFeedbackVisible;

    // 是否有自动倒计时（Error 不自动消失）
    public bool HasAutoDismiss => Level != NotificationLevel.Error;

    public ToastItemViewModel(
        string title,
        string message,
        NotificationLevel level,
        Action<ToastItemViewModel> onDismiss,
        Func<string, Task>? copyToClipboard = null,
        TimeProvider? timeProvider = null)
    {
        Title = title ?? string.Empty;
        Message = message ?? string.Empty;
        Level = level;
        _onDismiss = onDismiss;
        _copyToClipboard = copyToClipboard;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // 超过 100 字符或含多行文本支持展开与收起
        CanExpand = Message.Length > 100 || Message.Contains('\n');

        TimeSpan duration = level switch
        {
            NotificationLevel.Success or NotificationLevel.Info => TimeSpan.FromSeconds(4),
            NotificationLevel.Warning => TimeSpan.FromSeconds(8),
            _ => TimeSpan.Zero,
        };

        _remainingTime = duration;
        if (duration > TimeSpan.Zero)
        {
            _lastResumeTime = _timeProvider.GetUtcNow();
            _timer = _timeProvider.CreateTimer(OnTimerTick, null, duration, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerTick(object? state)
    {
        lock (_timerLock)
        {
            if (_isPaused) return;
            _timer?.Dispose();
            _timer = null;
        }

        _onDismiss(this);
    }

    // 鼠标悬停时暂停倒计时
    public void PauseDismiss()
    {
        lock (_timerLock)
        {
            if (_timer == null || _isPaused || !HasAutoDismiss) return;
            _isPaused = true;
            DateTimeOffset now = _timeProvider.GetUtcNow();
            TimeSpan elapsed = now - _lastResumeTime;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            _remainingTime -= elapsed;
            if (_remainingTime < TimeSpan.Zero) _remainingTime = TimeSpan.Zero;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    // 鼠标移出时恢复倒计时
    public void ResumeDismiss()
    {
        lock (_timerLock)
        {
            if (_timer == null || !_isPaused || !HasAutoDismiss) return;
            _isPaused = false;
            _lastResumeTime = _timeProvider.GetUtcNow();
            // 若剩余时间已耗尽，稍作缓冲（100ms）后触发消除
            TimeSpan delay = _remainingTime > TimeSpan.FromMilliseconds(50) ? _remainingTime : TimeSpan.FromMilliseconds(100);
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    [RelayCommand]
    public void ToggleExpand()
    {
        if (CanExpand)
        {
            IsExpanded = !IsExpanded;
        }
    }

    [RelayCommand]
    public void Dismiss()
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }

        _onDismiss(this);
    }

    [RelayCommand]
    public async Task CopyFullTextAsync()
    {
        string full = string.IsNullOrWhiteSpace(Title) ? Message : $"{Title}\n{Message}";
        if (_copyToClipboard != null)
        {
            await _copyToClipboard(full);
            IsCopyFeedbackVisible = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                IsCopyFeedbackVisible = false;
            });
        }
    }
}
