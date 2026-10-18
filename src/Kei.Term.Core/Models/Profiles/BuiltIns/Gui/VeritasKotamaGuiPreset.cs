namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class VeritasKotamaGuiPreset
{
    public const string Id = "builtin-gui-veritas-kotama";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Otose Kotama",
        IsBuiltIn = true,
        WindowBackground = "#1A1A1A",
        PanelBackground = "#252526",
        PanelAltBackground = "#323130",
        BorderBrush = "#4A4643",
        PrimaryText = "#F4F0E8",
        SecondaryText = "#9C948B",
        AccentColor = "#DB922C",
        AccentHover = "#B8761D"
    };
}

