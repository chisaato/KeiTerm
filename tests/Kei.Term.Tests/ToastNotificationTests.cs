using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kei.Term.App.Models;
using Kei.Term.App.ViewModels.Notifications;
using Xunit;

namespace Kei.Term.Tests;

public class ToastNotificationTests
{
    // 测试手动时钟，精准推进时间与计时器触发
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        private readonly List<TestTimer> _timers = [];
        private readonly object _lock = new();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock) return _utcNow;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                TestTimer timer = new(this, callback, state, dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan delta)
        {
            List<TestTimer> copy;
            lock (_lock)
            {
                _utcNow += delta;
                copy = [.. _timers];
            }

            foreach (TestTimer timer in copy)
            {
                timer.CheckTrigger(_utcNow);
            }
        }

        public void Remove(TestTimer timer)
        {
            lock (_lock) _timers.Remove(timer);
        }

        public sealed class TestTimer : ITimer
        {
            private readonly TestTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private DateTimeOffset? _dueTime;
            private TimeSpan _period;
            private bool _disposed;
            private readonly object _lock = new();

            public TestTimer(TestTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _period = period;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    _dueTime = owner.GetUtcNow() + dueTime;
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_lock)
                {
                    if (_disposed) return false;
                    _period = period;
                    if (dueTime == Timeout.InfiniteTimeSpan)
                    {
                        _dueTime = null;
                    }
                    else
                    {
                        _dueTime = _owner.GetUtcNow() + dueTime;
                    }
                    return true;
                }
            }

            public void CheckTrigger(DateTimeOffset current)
            {
                lock (_lock)
                {
                    if (_disposed || _dueTime == null) return;
                    if (current >= _dueTime.Value)
                    {
                        _dueTime = _period == Timeout.InfiniteTimeSpan ? null : current + _period;
                        _callback(_state);
                    }
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    _disposed = true;
                    _dueTime = null;
                }
                _owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public void ToastManager_CapacityLimit_EvictsOldestToastWhenExceedingFour()
    {
        TestTimeProvider clock = new();
        ToastManagerViewModel manager = new(timeProvider: clock, dispatchToUi: a => a());

        // 添加 4 条通知（达到上限）
        manager.Show("T1", "Msg1", NotificationLevel.Info);
        manager.Show("T2", "Msg2", NotificationLevel.Info);
        manager.Show("T3", "Msg3", NotificationLevel.Info);
        manager.Show("T4", "Msg4", NotificationLevel.Info);
        Assert.Equal(4, manager.ActiveToasts.Count);
        Assert.Equal("T1", manager.ActiveToasts[0].Title);

        // 添加第 5 条通知，最旧的 T1 应被淘汰
        manager.Show("T5", "Msg5", NotificationLevel.Info);
        Assert.Equal(4, manager.ActiveToasts.Count);
        Assert.Equal("T2", manager.ActiveToasts[0].Title);
        Assert.Equal("T5", manager.ActiveToasts[3].Title);
    }

    [Fact]
    public void ToastManager_Deduplication_RefreshesExistingToastInsteadOfAddingDuplicate()
    {
        TestTimeProvider clock = new();
        ToastManagerViewModel manager = new(timeProvider: clock, dispatchToUi: a => a());

        ToastItemViewModel first = manager.Show("SameTitle", "SameMessage", NotificationLevel.Info);
        Assert.Single(manager.ActiveToasts);

        ToastItemViewModel second = manager.Show("SameTitle", "SameMessage", NotificationLevel.Info);
        Assert.Single(manager.ActiveToasts);
        Assert.Same(first, second);
    }

    [Fact]
    public void ToastItem_SuccessAndInfo_AutoDismissesAfterFourSeconds()
    {
        TestTimeProvider clock = new();
        ToastManagerViewModel manager = new(timeProvider: clock, dispatchToUi: a => a());

        manager.Show("Success", "Operation complete", NotificationLevel.Success);
        Assert.Single(manager.ActiveToasts);

        // 推进 3 秒，未超时
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Single(manager.ActiveToasts);

        // 再推进 1.5 秒（累计 4.5s），成功消失
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Empty(manager.ActiveToasts);
    }

    [Fact]
    public void ToastItem_Warning_AutoDismissesAfterEightSeconds()
    {
        TestTimeProvider clock = new();
        ToastManagerViewModel manager = new(timeProvider: clock, dispatchToUi: a => a());

        manager.Show("Warn", "Target might be busy", NotificationLevel.Warning);
        Assert.Single(manager.ActiveToasts);

        // 推进 5 秒，仍应存在
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(manager.ActiveToasts);

        // 再推进 3.5 秒（累计 8.5s），消失
        clock.Advance(TimeSpan.FromSeconds(3.5));
        Assert.Empty(manager.ActiveToasts);
    }

    [Fact]
    public void ToastItem_Error_DoesNotAutoDismiss_RequiresManualClose()
    {
        TestTimeProvider clock = new();
        ToastManagerViewModel manager = new(timeProvider: clock, dispatchToUi: a => a());

        ToastItemViewModel errorToast = manager.Show("Fatal", "Connection refused", NotificationLevel.Error);
        Assert.Single(manager.ActiveToasts);
        Assert.False(errorToast.HasAutoDismiss);

        // 即使推进 60 秒，Error 依然常驻
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Single(manager.ActiveToasts);

        // 手动关闭
        errorToast.DismissCommand.Execute(null);
        Assert.Empty(manager.ActiveToasts);
    }

    [Fact]
    public void ToastItem_HoverPausesTimer_AndResumeContinuesCountdown()
    {
        TestTimeProvider clock = new();
        ToastManagerViewModel manager = new(timeProvider: clock, dispatchToUi: a => a());

        ToastItemViewModel toast = manager.Show("Info", "Downloading file...", NotificationLevel.Info);
        Assert.Single(manager.ActiveToasts);

        // 经过 2 秒后悬停（原本 4s 消失，现剩 2s）
        clock.Advance(TimeSpan.FromSeconds(2));
        toast.PauseDismiss();

        // 悬停期间过去 10 秒，toast 不应消失
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(manager.ActiveToasts);

        // 鼠标移出，恢复倒计时
        toast.ResumeDismiss();

        // 恢复后只推进 1 秒，总剩余约 2s，故尚未消失
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(manager.ActiveToasts);

        // 再推进 1.5 秒，计时完毕自动消失
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Empty(manager.ActiveToasts);
    }

    [Fact]
    public void ToastItem_CanExpand_TrueOnlyForLongOrMultilineText()
    {
        ToastItemViewModel shortToast = new("Short", "Hello world", NotificationLevel.Info, _ => { });
        Assert.False(shortToast.CanExpand);

        ToastItemViewModel multilineToast = new("Multi", "Line 1\nLine 2", NotificationLevel.Info, _ => { });
        Assert.True(multilineToast.CanExpand);

        string longText = new('a', 120);
        ToastItemViewModel longToast = new("Long", longText, NotificationLevel.Warning, _ => { });
        Assert.True(longToast.CanExpand);
    }

    [Fact]
    public async Task ToastItem_CopyFullText_IncludesTitleAndMessage()
    {
        string copied = string.Empty;
        ToastItemViewModel toast = new(
            "TestTitle",
            "TestMessage",
            NotificationLevel.Error,
            _ => { },
            copyToClipboard: text =>
            {
                copied = text;
                return Task.CompletedTask;
            });

        await toast.CopyFullTextCommand.ExecuteAsync(null);
        Assert.Equal("TestTitle\nTestMessage", copied);
    }
}
