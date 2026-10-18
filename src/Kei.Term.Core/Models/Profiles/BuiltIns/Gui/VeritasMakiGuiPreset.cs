namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class VeritasMakiGuiPreset
{
    public const string Id = "builtin-gui-veritas-maki";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Konuri Maki",
        IsBuiltIn = true,
        WindowBackground = "#1C1B26",
        PanelBackground = "#282536",
        PanelAltBackground = "#343048",
        BorderBrush = "#4C4666",
        PrimaryText = "#F6F5F8",
        SecondaryText = "#A099B5",
        AccentColor = "#C83E44",
        AccentHover = "#A53136"
    };
}

