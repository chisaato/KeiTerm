using Avalonia;
using Avalonia.Controls;
using Avalonia.Metadata;

namespace Kei.Term.App.Views.Settings.Controls;

public partial class SettingRow : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public static readonly DirectProperty<SettingRow, bool> HasDescriptionProperty =
        AvaloniaProperty.RegisterDirect<SettingRow, bool>(nameof(HasDescription), o => o.HasDescription);

    public static new readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<SettingRow, object?>(nameof(Content));

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

    [Content]
    public new object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    static SettingRow()
    {
        DescriptionProperty.Changed.AddClassHandler<SettingRow>((x, _) =>
            x.RaisePropertyChanged(HasDescriptionProperty, !string.IsNullOrEmpty(x.Description), x.HasDescription));
    }

    public SettingRow()
    {
        InitializeComponent();
    }
}
