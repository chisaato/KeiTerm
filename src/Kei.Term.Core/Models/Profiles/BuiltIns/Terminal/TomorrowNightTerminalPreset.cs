namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class TomorrowNightTerminalPreset
{
    public const string Id = "builtin-term-tomorrow-night";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Tomorrow Night",
        IsBuiltIn = true,
        Foreground = "#C5C8C6",
        Background = "#1D1F21",
        CursorColor = "#C5C8C6",
        SelectionBackground = "#373B41",
        AnsiColors =
        [
            "#1D1F21", // Black
            "#CC6666", // Red
            "#B5BD68", // Green
            "#F0C674", // Yellow
            "#81A2BE", // Blue
            "#B294BB", // Magenta
            "#8ABEB7", // Cyan
            "#C5C8C6", // White
            "#969896", // Bright Black
            "#CC6666", // Bright Red
            "#B5BD68", // Bright Green
            "#F0C674", // Bright Yellow
            "#81A2BE", // Bright Blue
            "#B294BB", // Bright Magenta
            "#8ABEB7", // Bright Cyan
            "#FFFFFF"  // Bright White
        ]
    };
}
