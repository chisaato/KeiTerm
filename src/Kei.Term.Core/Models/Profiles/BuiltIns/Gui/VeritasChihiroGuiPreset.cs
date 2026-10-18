namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class VeritasChihiroGuiPreset
{
    public const string Id = "builtin-gui-veritas-chihiro";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Kagami Chihiro",
        IsBuiltIn = true,
        WindowBackground = "#1A1D2C",
        PanelBackground = "#24293D",
        PanelAltBackground = "#30374F",
        BorderBrush = "#434E70",
        PrimaryText = "#EFF2F8",
        SecondaryText = "#8D9AB2",
        AccentColor = "#309FD9",
        AccentHover = "#2382B4"
    };
}

