using Avalonia;
using Avalonia.Controls;

namespace Kei.Term.App.Views.Settings.Controls;

// 标题/说明在左、控件（Content）右对齐；模板见 SettingsStyles.axaml
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public static readonly DirectProperty<SettingRow, bool> HasDescriptionProperty =
        AvaloniaProperty.RegisterDirect<SettingRow, bool>(nameof(HasDescription), o => o.HasDescription);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    // 行宽低于该值（px）时切换为上下排列：标题说明在上，控件在下
    public static readonly StyledProperty<double> NarrowThresholdProperty =
        AvaloniaProperty.Register<SettingRow, double>(nameof(NarrowThreshold), 400);

    public double NarrowThreshold
    {
        get => GetValue(NarrowThresholdProperty);
        set => SetValue(NarrowThresholdProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 测量前按可用宽度切换 :narrow 伪类，样式里据此重排模板
        if (!double.IsInfinity(availableSize.Width))
            PseudoClasses.Set(":narrow", availableSize.Width < NarrowThreshold);
        return base.MeasureOverride(availableSize);
    }

    static SettingRow()
    {
        DescriptionProperty.Changed.AddClassHandler<SettingRow>((x, _) =>
            x.RaisePropertyChanged(HasDescriptionProperty, !string.IsNullOrEmpty(x.Description), x.HasDescription));
    }
}
