namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class TendouKeiGuiPreset
{
    public const string Id = "builtin-gui-tendou-kei";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Tendou Kei (天童凯伊)",
        IsBuiltIn = true,
        WindowBackground = "#14171E",
        PanelBackground = "#1D212B",
        PanelAltBackground = "#252B38",
        BorderBrush = "#3A4354",
        PrimaryText = "#F4F3F8",
        SecondaryText = "#8B96A8",
        AccentColor = "#E83674",
        AccentHover = "#FF5792"
    };
}
