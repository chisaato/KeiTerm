using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Kei.Term.App.Views.Settings.Controls;

public partial class SettingsPageHeader : UserControl
{
    public static readonly StyledProperty<string?> GroupProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(Group));

    public static readonly DirectProperty<SettingsPageHeader, bool> HasGroupProperty =
        AvaloniaProperty.RegisterDirect<SettingsPageHeader, bool>(nameof(HasGroup), o => o.HasGroup);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(Description));

    public static readonly StyledProperty<string?> SubPageTitleProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(SubPageTitle));

    public static readonly DirectProperty<SettingsPageHeader, bool> HasSubPageProperty =
        AvaloniaProperty.RegisterDirect<SettingsPageHeader, bool>(nameof(HasSubPage), o => o.HasSubPage);

    public static readonly DirectProperty<SettingsPageHeader, string?> CurrentDisplayTitleProperty =
        AvaloniaProperty.RegisterDirect<SettingsPageHeader, string?>(nameof(CurrentDisplayTitle), o => o.CurrentDisplayTitle);

    public static readonly DirectProperty<SettingsPageHeader, bool> ShouldShowDescriptionProperty =
        AvaloniaProperty.RegisterDirect<SettingsPageHeader, bool>(nameof(ShouldShowDescription), o => o.ShouldShowDescription);

    public static readonly StyledProperty<ICommand?> BackCommandProperty =
        AvaloniaProperty.Register<SettingsPageHeader, ICommand?>(nameof(BackCommand));

    public string? Group
    {
        get => GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    public bool HasGroup => !string.IsNullOrEmpty(Group);

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

    public string? SubPageTitle
    {
        get => GetValue(SubPageTitleProperty);
        set => SetValue(SubPageTitleProperty, value);
    }

    public bool HasSubPage => !string.IsNullOrEmpty(SubPageTitle);

    public string? CurrentDisplayTitle => HasSubPage ? SubPageTitle : Title;

    public bool ShouldShowDescription => !HasSubPage && !string.IsNullOrEmpty(Description);

    public ICommand? BackCommand
    {
        get => GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    static SettingsPageHeader()
    {
        GroupProperty.Changed.AddClassHandler<SettingsPageHeader>((x, _) =>
            x.RaisePropertyChanged(HasGroupProperty, !string.IsNullOrEmpty(x.Group), x.HasGroup));

        TitleProperty.Changed.AddClassHandler<SettingsPageHeader>((x, _) =>
            x.RaisePropertyChanged(CurrentDisplayTitleProperty, x.CurrentDisplayTitle, x.CurrentDisplayTitle));

        SubPageTitleProperty.Changed.AddClassHandler<SettingsPageHeader>((x, _) =>
        {
            x.RaisePropertyChanged(HasSubPageProperty, !string.IsNullOrEmpty(x.SubPageTitle), x.HasSubPage);
            x.RaisePropertyChanged(CurrentDisplayTitleProperty, x.CurrentDisplayTitle, x.CurrentDisplayTitle);
            x.RaisePropertyChanged(ShouldShowDescriptionProperty, x.ShouldShowDescription, x.ShouldShowDescription);
        });

        DescriptionProperty.Changed.AddClassHandler<SettingsPageHeader>((x, _) =>
            x.RaisePropertyChanged(ShouldShowDescriptionProperty, x.ShouldShowDescription, x.ShouldShowDescription));
    }

    public SettingsPageHeader()
    {
        InitializeComponent();
    }
}
