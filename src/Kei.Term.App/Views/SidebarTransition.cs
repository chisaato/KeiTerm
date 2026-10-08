using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Kei.Term.App.Views;

// 两侧面板只动画内层表面，外层布局/命中保持稳定；每次反向从当前画面继续。
internal sealed class SidebarTransition(Control surface) : IDisposable
{
    private readonly TranslateTransform _translation = GetTranslation(surface);
    private CancellationTokenSource? _animation;
    private int _generation;
    private bool _disposed;

    public void SetImmediate(bool visible)
    {
        Cancel();
        surface.Opacity = visible ? 1 : 0;
        _translation.X = 0;
    }

    public void Start(bool visible, double hiddenOffset, bool fromHidden, Action completed)
    {
        if (_disposed) return;
        double opacity = fromHidden ? 0 : surface.Opacity;
        double offset = fromHidden ? hiddenOffset : _translation.X;
        Cancel();
        int generation = _generation;
        // 动画结束或取消后仍有确定的基础值，不依赖 FillMode 遗留动画优先级。
        surface.Opacity = visible ? 1 : 0;
        _translation.X = visible ? 0 : hiddenOffset;
        Animation animation = new()
        {
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = visible ? new CubicEaseOut() : new CubicEaseIn(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(Visual.OpacityProperty, opacity), new Setter(TranslateTransform.XProperty, offset) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(Visual.OpacityProperty, visible ? 1d : 0d), new Setter(TranslateTransform.XProperty, visible ? 0d : hiddenOffset) }
                }
            }
        };
        CancellationTokenSource cancellation = new();
        _animation = cancellation;
        _ = RunAnimationAsync(animation, cancellation, generation, completed);
    }

    private async Task RunAnimationAsync(Animation animation, CancellationTokenSource cancellation, int generation, Action completed)
    {
        try
        {
            await animation.RunAsync(surface, cancellation.Token);
            if (_disposed || generation != _generation) return;
            _animation = null;
            completed();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { cancellation.Dispose(); }
    }

    public void Cancel()
    {
        // 先改变代次，避免 Dispose 期间排队的完成回调再修改可见性或列宽。
        _generation++;
        _animation?.Cancel();
        _animation = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }

    private static TranslateTransform GetTranslation(Control control)
    {
        if (control.RenderTransform is TranslateTransform translation) return translation;
        TranslateTransform created = new();
        control.RenderTransform = created;
        return created;
    }

}
