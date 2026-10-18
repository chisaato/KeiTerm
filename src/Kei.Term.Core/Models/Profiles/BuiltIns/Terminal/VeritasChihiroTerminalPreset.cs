namespace Kei.Term.Core.Models.Profiles.BuiltIns.Terminal;

public static class VeritasChihiroTerminalPreset
{
    public const string Id = "builtin-term-veritas-chihiro";

    public static TerminalProfile Instance { get; } = new()
    {
        Id = Id,
        Name = "Veritas - Kagami Chihiro",
        IsBuiltIn = true,
        Foreground = "#EFF2F8",
        Background = "#1A1D2C",
        CursorColor = "#309FD9",
        SelectionBackground = "#309FD938",
        AnsiColors =
        [
            "#1A1D2C", // Black (Deep Navy)
            "#E05265", // Red
            "#3EB489", // Green (Terminal green)
            "#FFE354", // Yellow (Hairpin bright yellow)
            "#309FD9", // Blue (Tie cyan-blue)
            "#9C6CE4", // Magenta
            "#4D9BA9", // Cyan (Glasses teal)
            "#DAE0EC", // White
            "#505B7D", // Bright Black (Muted navy)
            "#F06A7C", // Bright Red
            "#54CEA3", // Bright Green
            "#FFEB75", // Bright Yellow
            "#4EB5EE", // Bright Blue
            "#B68DF2", // Bright Magenta
            "#68B4C2", // Bright Cyan
            "#FFFFFF"  // Bright White
        ]
    };
}

