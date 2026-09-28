namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class SolarizedDarkTerminalPreset
{
    public const string Id = "builtin-term-solarized-dark";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Solarized Dark",
        IsBuiltIn = true,
        Foreground = "#93A1A1",
        Background = "#002B36",
        CursorColor = "#93A1A1",
        SelectionBackground = "#073642",
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
