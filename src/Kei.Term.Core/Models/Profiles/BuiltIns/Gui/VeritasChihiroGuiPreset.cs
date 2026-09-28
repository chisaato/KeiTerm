namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class VeritasChihiroGuiPreset
{
    public const string Id = "builtin-gui-veritas-chihiro";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Kagami Chihiro",
        IsBuiltIn = true,
        WindowBackground = "#303048",
        PanelBackground = "#334261",
        PanelAltBackground = "#465266",
        BorderBrush = "#526E9F",
        PrimaryText = "#FFFFFF",
        SecondaryText = "#FFFFFF",
        AccentColor = "#4DBCE9",
        AccentHover = "#3388C4"
    };
}
