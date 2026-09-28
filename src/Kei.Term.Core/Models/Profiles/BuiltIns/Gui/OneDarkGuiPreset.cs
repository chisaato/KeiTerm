namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class OneDarkGuiPreset
{
    public const string Id = "builtin-gui-one-dark";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "One Dark",
        IsBuiltIn = true,
        WindowBackground = "#282C34",
        PanelBackground = "#21252B",
        PanelAltBackground = "#2C313A",
        BorderBrush = "#181A1F",
        PrimaryText = "#ABB2BF",
        SecondaryText = "#5C6370",
        AccentColor = "#61AFEF",
        AccentHover = "#4D9CE6"
    };
}
