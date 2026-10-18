namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class VeritasHareGuiPreset
{
    public const string Id = "builtin-gui-veritas-hare";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Omagari Hare",
        IsBuiltIn = true,
        WindowBackground = "#151922",
        PanelBackground = "#1E2532",
        PanelAltBackground = "#283244",
        BorderBrush = "#3A4A62",
        PrimaryText = "#F4F6F8",
        SecondaryText = "#96A3B6",
        AccentColor = "#DAEF00",
        AccentHover = "#B8C900"
    };
}

