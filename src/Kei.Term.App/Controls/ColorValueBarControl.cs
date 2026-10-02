using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Kei.Term.App.Controls;

/// <summary>
/// 明度选择条（KColorChooser 右侧竖条）：
/// 纵轴为明度 Value (上白下黑: y=0 -> Val=255, y=H -> Val=0)。
/// 背景由纯白渐变到当前基底纯色 (H, S, 255)，再渐变到底部纯黑；
/// 按照 KColorChooser 经典设计：上白(255)到中纯色再到下黑(0)，或者简单的白到纯色到黑。
/// 在 HSV 空间中，上白下黑即：顶部为纯白(V=255, S=0)或 V=255，中间为当前基色，底部为纯黑(V=0)。
/// 或者直接以当前 Hue/Sat 为底：顶部 (H, S, 255)，底部 (H, S, 0)。
/// KColorChooser 的明度条：顶部是白，中间是当前纯色，底部是黑（即 HLS 的 L，或 HSV 的白-纯色-黑）。
/// 对于 HSV 明度条，上白下黑：顶部是纯白 (255)，中间是当前纯色，底部是纯黑 (0)。
/// </summary>
public class ColorValueBarControl : Control
{
    public static readonly StyledProperty<int> ValProperty =
        AvaloniaProperty.Register<ColorValueBarControl, int>(nameof(Val), defaultValue: 255);

    public static readonly StyledProperty<Color> BaseColorProperty =
        AvaloniaProperty.Register<ColorValueBarControl, Color>(nameof(BaseColor), defaultValue: Colors.Red);

    public int Val
    {
        get => GetValue(ValProperty);
        set => SetValue(ValProperty, value);
    }

    public Color BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public event Action<int>? ValChanged;

    private bool _isPointerPressed;

    static ColorValueBarControl()
    {
        AffectsRender<ColorValueBarControl>(ValProperty, BaseColorProperty);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            _isPointerPressed = true;
            e.Pointer.Capture(this);
            UpdateFromPoint(point.Position);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isPointerPressed)
        {
            var point = e.GetCurrentPoint(this);
            UpdateFromPoint(point.Position);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPointerPressed)
        {
            _isPointerPressed = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isPointerPressed = false;
    }

    private void UpdateFromPoint(Point pos)
    {
        double h = Bounds.Height;
        if (h <= 0) return;

        double yClamped = Math.Clamp(pos.Y, 0, h - 1);
        // 上 255，下 0
        int val = (int)Math.Clamp(Math.Round((1.0 - (yClamped / h)) * 255.0), 0, 255);

        Val = val;
        ValChanged?.Invoke(val);
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // 竖向渐变：顶部白色 (1.0)，中间基底色 (0.5)，底部黑色 (0.0) —— 经典 KColorChooser 风格
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Colors.White, 0.0),
                new GradientStop(BaseColor, 0.5),
                new GradientStop(Colors.Black, 1.0)
            }
        };

        var rect = new Rect(0, 0, w, h);
        context.DrawRectangle(gradient, null, rect);

        // 绘制左右两侧的箭头/三角游标指示器
        double markerY = Math.Clamp((1.0 - (Val / 255.0)) * h, 1, h - 1);

        var arrowBrush = Brushes.White;
        var arrowPen = new Pen(Brushes.Black, 1.0);

        // 左三角指示器 (指向右)
        var leftArrow = new StreamGeometry();
        using (var ctx = leftArrow.Open())
        {
            ctx.BeginFigure(new Point(0, markerY - 5), true);
            ctx.LineTo(new Point(5, markerY));
            ctx.LineTo(new Point(0, markerY + 5));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(arrowBrush, arrowPen, leftArrow);

        // 右三角指示器 (指向左)
        var rightArrow = new StreamGeometry();
        using (var ctx = rightArrow.Open())
        {
            ctx.BeginFigure(new Point(w, markerY - 5), true);
            ctx.LineTo(new Point(w - 5, markerY));
            ctx.LineTo(new Point(w, markerY + 5));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(arrowBrush, arrowPen, rightArrow);

        // 穿过指示线
        context.DrawLine(new Pen(Brushes.Black, 1.0), new Point(4, markerY), new Point(w - 4, markerY));
        context.DrawLine(new Pen(Brushes.White, 1.0, lineCap: PenLineCap.Flat), new Point(4, markerY), new Point(w - 4, markerY));
    }
}
