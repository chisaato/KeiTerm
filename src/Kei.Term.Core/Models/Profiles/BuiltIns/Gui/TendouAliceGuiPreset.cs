namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class TendouAliceGuiPreset
{
    public const string Id = "builtin-gui-tendou-alice";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Tendou Alice (天童爱丽丝)",
        IsBuiltIn = true,
        WindowBackground = "#131722",
        PanelBackground = "#1B2130",
        PanelAltBackground = "#242C40",
        BorderBrush = "#34425A",
        PrimaryText = "#EDF3FC",
        SecondaryText = "#7E8DA6",
        AccentColor = "#258DF0",
        AccentHover = "#44A1FF"
    };
}
