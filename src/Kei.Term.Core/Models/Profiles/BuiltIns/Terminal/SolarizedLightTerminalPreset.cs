namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class SolarizedLightTerminalPreset
{
    public const string Id = "builtin-term-solarized-light";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Solarized Light",
        IsBuiltIn = true,
        Foreground = "#586E75",
        Background = "#FDF6E3",
        CursorColor = "#586E75",
        SelectionBackground = "#EEE8D5",
        AnsiColors =
        [
            "#073642", // Black
            "#DC322F", // Red
            "#859900", // Green
            "#B58900", // Yellow
            "#268BD2", // Blue
            "#D33682", // Magenta
            "#2AA198", // Cyan
            "#EEE8D5", // White
            "#002B36", // Bright Black
            "#CB4B16", // Bright Red
            "#586E75", // Bright Green
            "#657B83", // Bright Yellow
            "#839496", // Bright Blue
            "#6C71C4", // Bright Magenta
            "#93A1A1", // Bright Cyan
            "#FDF6E3"  // Bright White
        ]
    };
}
