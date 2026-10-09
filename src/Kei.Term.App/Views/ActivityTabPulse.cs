using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Kei.Term.App.Views;

// 只淡入淡出标题，强调色由主题样式提供；密集通知合并到正在播放的两次提醒中。
internal sealed class ActivityTabPulse(Control header)
{
    private CancellationTokenSource? _animation;

    public void Start()
    {
        if (_animation != null) return;
        header.Opacity = 1;
        Animation animation = new()
        {
            Duration = TimeSpan.FromMilliseconds(960),
            Easing = new SineEaseInOut(),
            Children =
            {
                Frame(0, 1), Frame(0.2, 0.35), Frame(0.4, 1),
                Frame(0.6, 0.35), Frame(0.8, 1), Frame(1, 1)
            }
        };
        CancellationTokenSource cancellation = new();
        _animation = cancellation;
        _ = RunAsync(animation, cancellation);
    }

    private async Task RunAsync(Animation animation, CancellationTokenSource cancellation)
    {
        try { await animation.RunAsync(header, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_animation, cancellation))
            {
                _animation = null;
                header.Opacity = 1;
            }
            cancellation.Dispose();
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation = _animation;
        _animation = null;
        cancellation?.Cancel();
        header.Opacity = 1;
    }

    private static KeyFrame Frame(double cue, double opacity) => new()
    {
        Cue = new Cue(cue),
        Setters = { new Setter(Visual.OpacityProperty, opacity) }
    };
}
