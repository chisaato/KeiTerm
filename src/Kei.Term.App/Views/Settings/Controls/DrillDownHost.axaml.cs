using Avalonia;
using Avalonia.Controls;
using Avalonia.Metadata;

namespace Kei.Term.App.Views.Settings.Controls;

public partial class DrillDownHost : UserControl
{
    public static readonly StyledProperty<object?> SubPageContentProperty =
        AvaloniaProperty.Register<DrillDownHost, object?>(nameof(SubPageContent));

    public static readonly DirectProperty<DrillDownHost, bool> HasSubPageProperty =
        AvaloniaProperty.RegisterDirect<DrillDownHost, bool>(nameof(HasSubPage), o => o.HasSubPage);

    public static new readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<DrillDownHost, object?>(nameof(Content));

    public object? SubPageContent
    {
        get => GetValue(SubPageContentProperty);
        set => SetValue(SubPageContentProperty, value);
    }

    public bool HasSubPage => SubPageContent != null;

    [Content]
    public new object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    static DrillDownHost()
    {
        SubPageContentProperty.Changed.AddClassHandler<DrillDownHost>((x, _) =>
            x.RaisePropertyChanged(HasSubPageProperty, x.SubPageContent != null, x.HasSubPage));
    }

    public DrillDownHost()
    {
        InitializeComponent();
    }
}
