using Avalonia;
using Avalonia.Controls;

namespace Kei.Term.App.Views.Settings.Controls;

// 双栏设置页容器：宽度足够时左右两栏（第 0 列设置卡片、第 1 列预览），
// 宽度低于阈值时自动改为单栏，预览面板落到设置卡片下方，避免设置行被挤压。
public class AdaptiveColumnsGrid : Grid
{
    public static readonly StyledProperty<double> NarrowThresholdProperty =
        AvaloniaProperty.Register<AdaptiveColumnsGrid, double>(nameof(NarrowThreshold), 740);


    private bool? _isNarrow;

    // 低于该宽度（px）切为单栏
    public double NarrowThreshold
    {
        get => GetValue(NarrowThresholdProperty);
        set => SetValue(NarrowThresholdProperty, value);
    }

    public bool IsNarrow => _isNarrow == true;

    protected override Size MeasureOverride(Size availableSize)
    {
        // 在测量前按可用宽度切换布局，保证同一帧内就是最终形态
        if (!double.IsInfinity(availableSize.Width)) Apply(availableSize.Width < NarrowThreshold);
        return base.MeasureOverride(availableSize);
    }

    private void Apply(bool narrow)
    {
        if (_isNarrow == narrow) return;
        _isNarrow = narrow;
        ColumnDefinitions = narrow
            ? ColumnDefinitions.Parse("*")
            : ColumnDefinitions.Parse("1.1*,*");
        RowDefinitions = narrow ? RowDefinitions.Parse("Auto,Auto") : new RowDefinitions();
        for (int i = 0; i < Children.Count; i++)
        {
            Control child = Children[i];
            // 第二个子元素（预览）在窄布局下落到第二行
            SetColumn(child, narrow ? 0 : i);
            SetRow(child, narrow ? i : 0);
        }
        // 单栏时行间距沿用 ColumnSpacing 的数值
        RowSpacing = narrow ? ColumnSpacing : 0;
    }
}
