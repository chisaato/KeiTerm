namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class DraculaTerminalPreset
{
    public const string Id = "builtin-term-dracula";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Dracula",
        IsBuiltIn = true,
        Foreground = "#F8F8F2",
        Background = "#282A36",
        CursorColor = "#F8F8F2",
        SelectionBackground = "#44475A",
        AnsiColors =
        [
            "#21222C", // Black
            "#FF5555", // Red
            "#50FA7B", // Green
            "#F1FA8C", // Yellow
            "#BD93F9", // Blue
            "#FF79C6", // Magenta
            "#8BE9FD", // Cyan
            "#F8F8F2", // White
            "#6272A4", // Bright Black
            "#FF6E6E", // Bright Red
            "#69FF94", // Bright Green
            "#FFFFA5", // Bright Yellow
            "#D6ACFF", // Bright Blue
            "#FF92DF", // Bright Magenta
            "#A4FFFF", // Bright Cyan
            "#FFFFFF"  // Bright White
        ]
    };
}
