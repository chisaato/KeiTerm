using Avalonia;
using Avalonia.Controls;
using Avalonia.Metadata;

namespace Kei.Term.App.Views.Settings.Controls;

public partial class SettingsCard : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Title));

    public static readonly DirectProperty<SettingsCard, bool> HasTitleProperty =
        AvaloniaProperty.RegisterDirect<SettingsCard, bool>(nameof(HasTitle), o => o.HasTitle);

    public static readonly StyledProperty<string> AdvancedHeaderProperty =
        AvaloniaProperty.Register<SettingsCard, string>(nameof(AdvancedHeader), "Advanced Options");

    public static readonly StyledProperty<object?> AdvancedContentProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(AdvancedContent));

    public static readonly DirectProperty<SettingsCard, bool> HasAdvancedContentProperty =
        AvaloniaProperty.RegisterDirect<SettingsCard, bool>(nameof(HasAdvancedContent), o => o.HasAdvancedContent);

    public static readonly StyledProperty<bool> IsAdvancedExpandedProperty =
        AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsAdvancedExpanded), false);

    public static new readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Content));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool HasTitle => !string.IsNullOrEmpty(Title);

    public string AdvancedHeader
    {
        get => GetValue(AdvancedHeaderProperty);
        set => SetValue(AdvancedHeaderProperty, value);
    }

    public object? AdvancedContent
    {
        get => GetValue(AdvancedContentProperty);
        set => SetValue(AdvancedContentProperty, value);
    }

    public bool HasAdvancedContent => AdvancedContent != null;

    public bool IsAdvancedExpanded
    {
        get => GetValue(IsAdvancedExpandedProperty);
        set => SetValue(IsAdvancedExpandedProperty, value);
    }

    [Content]
    public new object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    static SettingsCard()
    {
        TitleProperty.Changed.AddClassHandler<SettingsCard>((x, _) =>
            x.RaisePropertyChanged(HasTitleProperty, !string.IsNullOrEmpty(x.Title), x.HasTitle));

        AdvancedContentProperty.Changed.AddClassHandler<SettingsCard>((x, _) =>
            x.RaisePropertyChanged(HasAdvancedContentProperty, x.AdvancedContent != null, x.HasAdvancedContent));
    }

    public SettingsCard()
    {
        InitializeComponent();
        AdvancedHeader = Helpers.Strings.Get("Settings.Common.ShowAdvancedOptions");
    }
}
