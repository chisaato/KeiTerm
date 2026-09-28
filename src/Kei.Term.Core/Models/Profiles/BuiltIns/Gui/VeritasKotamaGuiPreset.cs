namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class VeritasKotamaGuiPreset
{
    public const string Id = "builtin-gui-veritas-kotama";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Otose Kotama",
        IsBuiltIn = true,
        WindowBackground = "#302D2C",
        PanelBackground = "#5F4E48",
        PanelAltBackground = "#57504E",
        BorderBrush = "#3B312A",
        PrimaryText = "#FFFFFF",
        SecondaryText = "#BBBBBB",
        AccentColor = "#EDA52F",
        AccentHover = "#EDA52F8D"
    };
}
