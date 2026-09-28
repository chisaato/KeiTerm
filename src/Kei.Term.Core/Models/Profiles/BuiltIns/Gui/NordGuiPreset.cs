namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class NordGuiPreset
{
    public const string Id = "builtin-gui-nord";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Nord",
        IsBuiltIn = true,
        WindowBackground = "#2E3440",
        PanelBackground = "#3B4252",
        PanelAltBackground = "#434C5E",
        BorderBrush = "#4C566A",
        PrimaryText = "#ECEFF4",
        SecondaryText = "#D8DEE9",
        AccentColor = "#88C0D0",
        AccentHover = "#81A1C1"
    };
}
