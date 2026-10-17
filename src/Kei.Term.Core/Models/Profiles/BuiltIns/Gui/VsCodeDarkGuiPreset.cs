namespace Kei.Term.Core.Models.Profiles.BuiltIns.Gui;

public static class VsCodeDarkGuiPreset
{
    public const string Id = "builtin-gui-vscode-dark";

    public static GuiProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "VS Code Dark",
        IsBuiltIn = true,
        WindowBackground = "#181818",
        PanelBackground = "#1F1F1F",
        PanelAltBackground = "#252526",
        BorderBrush = "#3E3E3E",
        PrimaryText = "#E0E0E0",
        SecondaryText = "#A0A0A0",
        AccentColor = "#0E639C",
        AccentHover = "#1177BB"
    };
}
