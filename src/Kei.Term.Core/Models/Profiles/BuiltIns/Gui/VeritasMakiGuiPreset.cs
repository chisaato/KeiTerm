namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class VeritasMakiGuiPreset
{
    public const string Id = "builtin-gui-veritas-maki";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Konuri Maki",
        IsBuiltIn = true,
        WindowBackground = "#303048",
        PanelBackground = "#312E42",
        PanelAltBackground = "#2A3049",
        BorderBrush = "#FF00003C",
        PrimaryText = "#FFFFFF",
        SecondaryText = "#FF5260",
        AccentColor = "#CB1919",
        AccentHover = "#941212"
    };
}
