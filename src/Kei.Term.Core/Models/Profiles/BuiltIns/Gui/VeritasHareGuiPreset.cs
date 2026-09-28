namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class VeritasHareGuiPreset
{
    public const string Id = "builtin-gui-veritas-hare";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Omagari Hare",
        IsBuiltIn = true,
        WindowBackground = "#18202C",
        PanelBackground = "#293241",
        PanelAltBackground = "#22262C",
        BorderBrush = "#475977",
        PrimaryText = "#FFFFFF",
        SecondaryText = "#BDCF3E",
        AccentColor = "#DAEF00",
        AccentHover = "#A0B000"
    };
}
