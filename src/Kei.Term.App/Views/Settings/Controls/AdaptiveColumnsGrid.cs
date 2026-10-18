using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Kei.Term.App.Views.Settings.Controls;

// 双栏设置页容器：宽度足够时左右两栏（第 0 列设置卡片、第 1 列预览），
// 宽度低于阈值时改为上下排列：设置区占剩余高度并独立滚动，分隔条可拖动预览高度，预览停靠底部。
public class AdaptiveColumnsGrid : Grid
{
    public const string PreviewMinHeightKey = "Kei.Settings.PreviewMinHeight";
    public const string PreviewMaxHeightKey = "Kei.Settings.PreviewMaxHeight";
    public const string PreviewDefaultHeightKey = "Kei.Settings.PreviewDefaultHeight";

    public static readonly StyledProperty<double> NarrowThresholdProperty =
        AvaloniaProperty.Register<AdaptiveColumnsGrid, double>(nameof(NarrowThreshold), 740);

    private bool? _isNarrow;
    private bool _applying;
    private bool _previewConstrained;
    private GridSplitter? _splitter;
    private double _savedMinHeight;
    private double _savedMaxHeight;
    private VerticalAlignment _savedVerticalAlignment;

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
        if (_applying || _isNarrow == narrow) return;
        Control? settings = ContentChild(first: true);
        Control? preview = ContentChild(first: false);
        // 子元素尚未挂上时不记状态，下一帧再切
        if (settings == null || preview == null || ReferenceEquals(settings, preview)) return;

        _applying = true;
        try
        {
            _isNarrow = narrow;
            if (narrow) ApplyNarrow(settings, preview);
            else ApplyWide(settings, preview);
        }
        finally
        {
            _applying = false;
        }
    }

    // 窄模式：第 0 行设置区（有限高度内滚动），第 1 行分隔条，第 2 行预览
    private void ApplyNarrow(Control settings, Control preview)
    {
        double minHeight = ResourceOr(PreviewMinHeightKey, 160);
        double maxHeight = ResourceOr(PreviewMaxHeightKey, 480);
        double defaultHeight = ResourceOr(PreviewDefaultHeightKey, 240);
        if (defaultHeight < minHeight) defaultHeight = minHeight;
        if (defaultHeight > maxHeight) defaultHeight = maxHeight;

        EnsureSplitter(preview);
        ColumnDefinitions = ColumnDefinitions.Parse("*");
        RowDefinitions = new RowDefinitions
        {
            new RowDefinition(1, GridUnitType.Star),
            new RowDefinition(GridLength.Auto),
            new RowDefinition(defaultHeight, GridUnitType.Pixel)
            {
                MinHeight = minHeight,
                MaxHeight = maxHeight,
            },
        };
        SetColumn(settings, 0);
        SetRow(settings, 0);
        SetColumn(_splitter!, 0);
        SetRow(_splitter!, 1);
        SetColumn(preview, 0);
        SetRow(preview, 2);
        ConstrainPreview(preview, minHeight, maxHeight);
        RowSpacing = 0;
    }

    // 宽模式：左侧设置栏单独滚动，右侧预览固定；不留分隔条，也不留窄模式的行分配
    private void ApplyWide(Control settings, Control preview)
    {
        RemoveSplitter();
        ColumnDefinitions = ColumnDefinitions.Parse("1.1*,*");
        RowDefinitions = new RowDefinitions();
        SetColumn(settings, 0);
        SetRow(settings, 0);
        SetColumn(preview, 1);
        SetRow(preview, 0);
        RestorePreview(preview);
        RowSpacing = 0;
    }

    // 插到预览之前，使预览仍是最后一个子元素（既有测试按 Children[^1] 取预览）
    private void EnsureSplitter(Control preview)
    {
        _splitter ??= new GridSplitter { Classes = { "settingsPreviewSplitter" } };
        if (_splitter.Parent == this) return;
        int index = Children.IndexOf(preview);
        if (index < 0) Children.Add(_splitter);
        else Children.Insert(index, _splitter);
    }

    private void RemoveSplitter()
    {
        if (_splitter == null) return;
        Children.Remove(_splitter);
        SetRow(_splitter, 0);
        SetColumn(_splitter, 0);
    }

    private void ConstrainPreview(Control preview, double minHeight, double maxHeight)
    {
        if (!_previewConstrained)
        {
            _savedMinHeight = preview.MinHeight;
            _savedMaxHeight = preview.MaxHeight;
            _savedVerticalAlignment = preview.VerticalAlignment;
            _previewConstrained = true;
        }

        preview.MinHeight = minHeight;
        preview.MaxHeight = maxHeight;
        // 预览填满可拖动的行，高度跟着分隔条走
        preview.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void RestorePreview(Control preview)
    {
        if (!_previewConstrained) return;
        preview.MinHeight = _savedMinHeight;
        preview.MaxHeight = _savedMaxHeight;
        preview.VerticalAlignment = _savedVerticalAlignment;
        _previewConstrained = false;
    }

    private Control? ContentChild(bool first)
    {
        Control? found = null;
        foreach (Control child in Children)
        {
            if (child == _splitter) continue;
            found = child;
            if (first) return found;
        }

        return first ? null : found;
    }

    private double ResourceOr(string key, double fallback)
    {
        if (this.TryFindResource(key, out object? value) && value is double number && number > 0)
            return number;
        return fallback;
    }
}
