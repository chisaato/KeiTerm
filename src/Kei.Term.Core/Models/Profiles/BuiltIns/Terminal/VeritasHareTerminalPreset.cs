namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class VeritasHareTerminalPreset
{
    public const string Id = "builtin-term-veritas-hare";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Omagari Hare",
        IsBuiltIn = true,
        Foreground = "#F4F6F8",
        Background = "#151922",
        CursorColor = "#DAEF00",
        SelectionBackground = "#DAEF0038",
        AnsiColors =
        [
            "#151922", // Black (Jacket dark)
            "#E04858", // Red
            "#A1C41C", // Green (Lime/olive)
            "#DAEF00", // Yellow (Cyber Lime accent)
            "#3B8EEA", // Blue (Millennium Blue)
            "#B666D2", // Magenta
            "#4EB5D9", // Cyan (Sensor blue)
            "#D8DEE9", // White
            "#54637D", // Bright Black (Muted slate)
            "#F15C6D", // Bright Red
            "#B8E022", // Bright Green
            "#E8FA2E", // Bright Yellow
            "#56A2F5", // Bright Blue
            "#CD84E8", // Bright Magenta
            "#6CD1F2", // Bright Cyan
            "#FFFFFF"  // Bright White (Hair white)
        ]
    };
}

