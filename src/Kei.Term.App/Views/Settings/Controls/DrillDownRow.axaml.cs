using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Kei.Term.App.Views.Settings.Controls;

public partial class DrillDownRow : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DrillDownRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<DrillDownRow, string?>(nameof(Description));

    public static readonly DirectProperty<DrillDownRow, bool> HasDescriptionProperty =
        AvaloniaProperty.RegisterDirect<DrillDownRow, bool>(nameof(HasDescription), o => o.HasDescription);

    public static readonly StyledProperty<string?> SummaryProperty =
        AvaloniaProperty.Register<DrillDownRow, string?>(nameof(Summary));

    public static readonly StyledProperty<string> ActionLabelProperty =
        AvaloniaProperty.Register<DrillDownRow, string>(nameof(ActionLabel), "Edit ›");

    public static readonly StyledProperty<bool> HasSwitchProperty =
        AvaloniaProperty.Register<DrillDownRow, bool>(nameof(HasSwitch), false);

    public static readonly StyledProperty<bool> SwitchCheckedProperty =
        AvaloniaProperty.Register<DrillDownRow, bool>(nameof(SwitchChecked), true, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsActionEnabledProperty =
        AvaloniaProperty.Register<DrillDownRow, bool>(nameof(IsActionEnabled), true);

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<DrillDownRow, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<DrillDownRow, object?>(nameof(CommandParameter));

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

    public string? Summary
    {
        get => GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public string ActionLabel
    {
        get => GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    public bool HasSwitch
    {
        get => GetValue(HasSwitchProperty);
        set => SetValue(HasSwitchProperty, value);
    }

    public bool SwitchChecked
    {
        get => GetValue(SwitchCheckedProperty);
        set => SetValue(SwitchCheckedProperty, value);
    }

    public bool IsActionEnabled
    {
        get => GetValue(IsActionEnabledProperty);
        set => SetValue(IsActionEnabledProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    static DrillDownRow()
    {
        DescriptionProperty.Changed.AddClassHandler<DrillDownRow>((x, _) =>
            x.RaisePropertyChanged(HasDescriptionProperty, !string.IsNullOrEmpty(x.Description), x.HasDescription));
    }

    public DrillDownRow()
    {
        InitializeComponent();
        ActionLabel = Helpers.Strings.Get("Common.EditArrow");
    }
}
