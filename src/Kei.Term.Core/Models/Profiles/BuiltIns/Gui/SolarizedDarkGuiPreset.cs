namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class SolarizedDarkGuiPreset
{
    public const string Id = "builtin-gui-solarized-dark";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Solarized Dark",
        IsBuiltIn = true,
        WindowBackground = "#002B36",
        PanelBackground = "#073642",
        PanelAltBackground = "#0F4654",
        BorderBrush = "#586E75",
        PrimaryText = "#93A1A1",
        SecondaryText = "#657B83",
        AccentColor = "#268BD2",
        AccentHover = "#2AA198"
    };
}
