namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class VeritasHareTerminalPreset
{
    public const string Id = "builtin-term-veritas-hare";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Omagari Hare",
        IsBuiltIn = true,
        Foreground = "#FFFFFF",
        Background = "#18202C",
        CursorColor = "#DAEF00",
        SelectionBackground = "#7991B21E",
        AnsiColors =
        [
            "#000000", // Black
            "#CD3131", // Red
            "#0DBC79", // Green
            "#E5E510", // Yellow
            "#2472C8", // Blue
            "#BC3FBC", // Magenta
            "#11A8CD", // Cyan
            "#E5E5E5", // White
            "#666666", // Bright Black
            "#F14C4C", // Bright Red
            "#23D18B", // Bright Green
            "#F5F543", // Bright Yellow
            "#3B8EEA", // Bright Blue
            "#D670D6", // Bright Magenta
            "#29B8DB", // Bright Cyan
            "#FFFFFF"  // Bright White
        ]
    };
}
