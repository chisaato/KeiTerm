using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kei.Term.App.Services;

namespace Kei.Term.App.Controls;

/// <summary>
/// 色彩选择板（KColorChooser 经典大色板）：
/// 横轴为色相 Hue (0..359)，纵轴为饱和度 Sat (上淡下浓: y=0 -> Sat=0, y=H -> Sat=255)。
/// 内部渲染基底色谱，并绘制定位十字指示器。
/// </summary>
public class ColorPlaneControl : Control
{
    public static readonly StyledProperty<int> HueProperty =
        AvaloniaProperty.Register<ColorPlaneControl, int>(nameof(Hue), defaultValue: 0);

    public static readonly StyledProperty<int> SatProperty =
        AvaloniaProperty.Register<ColorPlaneControl, int>(nameof(Sat), defaultValue: 255);

    public int Hue
    {
        get => GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    public int Sat
    {
        get => GetValue(SatProperty);
        set => SetValue(SatProperty, value);
    }

    public event Action<int, int>? HueSatChanged;

    private bool _isPointerPressed;
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth = -1;
    private int _bitmapHeight = -1;

    static ColorPlaneControl()
    {
        AffectsRender<ColorPlaneControl>(HueProperty, SatProperty);
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
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        double xClamped = Math.Clamp(pos.X, 0, w - 1);
        double yClamped = Math.Clamp(pos.Y, 0, h - 1);

        int hue = (int)Math.Clamp(Math.Round((xClamped / w) * 360.0), 0, 359);
        int sat = (int)Math.Clamp(Math.Round((yClamped / h) * 255.0), 0, 255);

        Hue = hue;
        Sat = sat;
        HueSatChanged?.Invoke(hue, sat);
    }

    private void EnsureBitmap(int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        // 色谱底图固定按标准采样渲染，保持流畅
        int targetW = Math.Clamp(width, 60, 360);
        int targetH = Math.Clamp(height, 60, 256);

        if (_bitmap != null && _bitmapWidth == targetW && _bitmapHeight == targetH)
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmapWidth = targetW;
        _bitmapHeight = targetH;
        _bitmap = new WriteableBitmap(new PixelSize(targetW, targetH), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        using var frameBuffer = _bitmap.Lock();
        int rowBytes = frameBuffer.RowBytes;
        IntPtr address = frameBuffer.Address;
        byte[] rowData = new byte[targetW * 4];

        for (int y = 0; y < targetH; y++)
        {
            int sat = (int)Math.Clamp(Math.Round(((double)y / targetH) * 255.0), 0, 255);
            for (int x = 0; x < targetW; x++)
            {
                int hue = (int)Math.Clamp(Math.Round(((double)x / targetW) * 360.0), 0, 359);
                var (r, g, b) = TerminalColorMath.HsvToRgb(hue, sat, 255);
                // Bgra8888 格式: B, G, R, A
                int offset = x * 4;
                rowData[offset] = b;
                rowData[offset + 1] = g;
                rowData[offset + 2] = r;
                rowData[offset + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(rowData, 0, address + (y * rowBytes), rowData.Length);
        }
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        int renderW = (int)Math.Round(w);
        int renderH = (int)Math.Round(h);
        EnsureBitmap(renderW, renderH);

        if (_bitmap != null)
        {
            context.DrawImage(_bitmap, new Rect(0, 0, w, h));
        }

        // 绘制十字指示器
        double markerX = Math.Clamp((Hue / 360.0) * w, 0, w);
        double markerY = Math.Clamp((Sat / 255.0) * h, 0, h);

        var whitePen = new Pen(Brushes.White, 1.5);
        var blackPen = new Pen(Brushes.Black, 1.0);

        // 外层微阴影/反差描边，确保在任何颜色背景下清晰可见
        context.DrawLine(blackPen, new Point(markerX - 8, markerY), new Point(markerX + 8, markerY));
        context.DrawLine(blackPen, new Point(markerX, markerY - 8), new Point(markerX, markerY + 8));

        context.DrawEllipse(null, blackPen, new Point(markerX, markerY), 4, 4);
        context.DrawEllipse(null, whitePen, new Point(markerX, markerY), 5, 5);
    }
}
